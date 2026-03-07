# Technical Specification: Eikon Secure Closed-Loop Environment

## 1. Introduction
This document provides the low-level technical configuration details for the Eikon Closed-Loop Windows OS. It covers the end-to-end deployment pipeline: the **OneClickInstaller** (WPF application for automated software provisioning) and the **EikonConfigurator** (19-step OS hardening and kiosk lockdown tool).

All configuration is JSON-driven. Software packages are defined in `installer-config.json`. Hardware and OS policies are defined in `hardware-config.json`.

## 2. OneClickInstaller — Software Provisioning

### 2.1 Overview
**Technology**: .NET Framework 4.8 WPF application.
**UI**: Real-time progress display with per-package status indicators, overall/current progress bars, elapsed/estimated time, and scrollable installation log.
**Error Strategy**: Best-effort — individual package failures are logged but do not abort subsequent steps. OS configuration is skipped if any prior step failed.

### 2.2 Installation Pipeline
The installer executes the following stages in order:

| Stage | Description |
| :--- | :--- |
| 1. Admin Check | Verifies the process is running with Administrator privileges. |
| 2. File Verification | Pre-flight check for all package files. Missing packages are skipped. |
| 3. Disk Partitioning | Shrinks C:, creates D: and E: partitions (configurable). |
| 4. Software Packages | Installs packages in JSON-defined order (VCRedists, drivers, SDK, MySQL, utilities). |
| 5. MySQL Configuration | Creates database, executes SQL seed script. |
| 6. Final Package | Installs GVPPro application (after database is ready). |
| 7. Post-Install Scripts | Runs configurable batch scripts from the application directory. |
| 8. OS Configuration | Launches EikonConfigurator for 19-step kiosk lockdown (only if 0 prior failures). |

### 2.3 Disk Partitioning
**Configuration**: `installer-config.json` → `diskPartitioning`
*   **Disk**: 0 (system disk).
*   **C:**: Shrink to **60 GB** (label: "OS").
*   **D:**: Create **15 GB** (label: "App").
*   **E:**: Remaining space (label: "Data", `useRemainingSpace: true`).
*   Skips if target partitions already exist.

### 2.4 Supported Installation Types
| Type | Mechanism |
| :--- | :--- |
| `MSI` | `msiexec.exe /i ... /quiet /norestart ALLUSERS=1` |
| `EXE` | Direct execution with configured `/quiet` arguments |
| `COPY` | File/directory copy to target path |
| `DISM` | 7-Zip extraction + `dism.exe /Online /Add-Package` |
| `INF` | `pnputil.exe /add-driver ... /install` |
| `MYSQL_CONSOLE` | `MySQLInstallerConsole.exe community install ...` |

Exit code `3010` (reboot required) is treated as success for MSI/EXE/INF packages.

### 2.5 MySQL Configuration
**Configuration**: `installer-config.json` → `mysqlConfig`
1.  Installs MySQL Installer bootstrapper (MSI).
2.  Runs `MySQLInstallerConsole.exe` to install MySQL Server 8.0.21 + Workbench (silent).
3.  Waits for `MySQL80` service to reach RUNNING state (up to 5 minutes).
4.  Creates database: `CREATE DATABASE IF NOT EXISTS dcmdetails`.
5.  Executes SQL seed script: `dcmdetails_20251217.sql`.

### 2.6 Post-Install Scripts
**Configuration**: `installer-config.json` → `postInstallScripts`
```json
{
  "enabled": true,
  "scripts": [
    "D:\\GVP-Pro\\App\\PostInstallScript _IP.bat",
    "D:\\GVP-Pro\\App\\PostInstallScript.bat"
  ],
  "cleanupScript": "D:\\GVP-Pro\\App\\PostInstallScript _RemoveIPFiles.bat"
}
```
*   Scripts are executed using **64-bit cmd.exe** (`SysNative\cmd.exe`) to avoid WOW64 filesystem redirection.
*   Missing scripts are skipped with a warning.
*   Cleanup script runs only if all scripts succeeded.
*   Timeout: 5 minutes per script.

### 2.7 OS Configuration Launch
**Configuration**: `installer-config.json` → `osConfiguration`
*   **Precondition**: Only runs if **all** prior stages completed with 0 failures.
*   Copies `EikonConfigurator` folder to `C:\EikonConfigurator\`.
*   Launches `EikonConfigurator.exe` with configurable arguments (e.g., `--bypass-hardware-check`).
*   Streams stdout/stderr to the installation log in real-time.
*   EikonConfigurator manages its own reboot/shutdown cycle.

### 2.8 Shutdown Handling
*   When OS configuration is active and completes successfully, the UI sets `_systemShuttingDown = true`.
*   Exit button changes to "Restarting..." and is disabled.
*   Status: *"Installation complete! The system will restart to finalize OS configuration, then shut down automatically."*
*   EikonConfigurator triggers `shutdown.exe` internally (the WPF app does not call shutdown).

---

## 3. EikonConfigurator — OS Hardening (19 Steps)

### 3.1 Hardware & System Validation (Step 1)
**Validation Logic** (`ValidateHardwareOrDie`), configurable via `hardware-config.json`:
*   **CPU Model**: Checks processor name against allowed families list (e.g., i3, i5, i7, Xeon).
*   **CPU Cores**: `Win32_Processor.NumberOfLogicalProcessors` >= configurable minimum (default: 4).
*   **RAM**: `Win32_ComputerSystem.TotalPhysicalMemory` >= configurable minimum (default: 16 GB, with 5% tolerance for firmware reserves).
*   **Network**: `Win32_NetworkAdapter` (MediaTypeId=0, Physical=True) Count >= configurable minimum (default: 2).
*   **Disk Size**: Total capacity across fixed disks >= configurable minimum (default: 1000 GB).
*   **Bypass**: `--bypass-hardware-check` flag skips all validation (for VM testing).

### 3.2 Windows Features (Step 2)
**DISM** (`/Online /Enable-Feature /NoRestart`):
*   `Client-KeyboardFilter` — WEKF support
*   `Client-UnifiedWriteFilter` — UWF support
*   `Client-EmbeddedLogon` — Login customization
*   `Client-EmbeddedShellLauncher` — Per-user shell mapping
*   `Printing-Foundation-Features` — Print spooler
*   `Printing-XPSServices-Features` — XPS printing

Feature activation may require reboot; detected and handled in Step 17 (UWF).

### 3.3 Hardware Configuration (Step 3)
**Network** (`ApplyNetworkSettings`):
*   Renames adapters by keyword matching (e.g., "ImageLink", "Realtek").
*   Applies static IP or DHCP per adapter from `hardware-config.json`.
*   Sets advanced driver parameters (Jumbo Packet=9014, EEE disabled for imaging adapter).

**Device Hardening** (`ConfigureDeviceHardening`):
*   Increases wireless adapter metric to 500 (deprioritizes WiFi).
*   Disables Bluetooth and audio devices via `Disable-PnpDevice`.

## 4. OS Configuration & Tuning

### 4.1 Scheduled Tasks (Step 4)
**Task Scheduler** (`schtasks /Change /TN "..." /Disable`):
*   `Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser` (Telemetry)
*   `Microsoft\Windows\Customer Experience Improvement Program\Consolidator` (Telemetry)
*   `Microsoft\Windows\Defrag\ScheduledDefrag` (Maintenance)
*   `Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector` (Maintenance)
*   `Microsoft\Windows\Maintenance\WinSAT` (Performance Assessment)
*   `Microsoft\Windows\NlaSvc\WiFiTask` (Network)
*   `Microsoft\Windows\Windows Media Sharing\UpdateLibrary` (Media)

### 4.2 OS Tuning (Step 5)
**Power Management** (`powercfg`):
*   **Scheme**: Ultimate Performance (`e9a42b02-d5df-448d-aa00-03f14749eb61`).
    *   *Command*: `powercfg -duplicatescheme e9a42b02...` followed by `powercfg -setactive ...`.
    *   *Fallback*: High Performance (`8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`).
*   **AC Standby/Monitor**:
    *   *Command*: `powercfg -change -standby-timeout-ac 0` (Never).
    *   *Command*: `powercfg -change -monitor-timeout-ac 0` (Never).
*   **Hibernation**:
    *   *Command*: `powercfg -h off`.

**Registry Optimization**:

| Path | Key | Value | Type | Description |
| :--- | :--- | :--- | :--- | :--- |
| `HKLM\...\Memory Management` | `PagingFiles` | (Empty) | MultiString | **Disable PageFile** |
| `HKLM\...\FileSystem` | `NtfsDisable8dot3NameCreation` | 1 | DWord | **Performance** |
| `HKLM\...\FileSystem` | `NtfsDisableLastAccessUpdate` | 1 | DWord | **Performance** |
| `HKLM\...\TimeZoneInformation` | `RealTimeIsUniversal` | 1 | DWord | **UTC BIOS Time** |
| `HKLM\...\WindowsUpdate\AU` | `NoAutoUpdate` | 1 | DWord | **Disable WU** |
| `HKLM\...\Winlogon` | `EnableFirstLogonAnimation` | 0 | DWord | **Disable First Logon Animation** |

**Logon Audit**: `auditpol /set /subcategory:"Logon" /success:enable /failure:enable`

## 5. Security Hardening

### 5.1 User Accounts & Groups (Step 6)
**Account Policies** (`net accounts`):
*   *Command*: `net accounts /minpwlen:14 /maxpwage:unlimited /minpwage:0 /uniquepw:0 /lockoutthreshold:5 /lockoutduration:15 /lockoutwindow:15`

**User Accounts**:
*   **EikonUser**: Primary kiosk user. Created via `UserPrincipal`, added to `Administrators`.
    *   **Password**: Random 32-character secure password (regenerated at seal).
    *   **PasswordNeverExpires**: True.
    *   **AutoLogon**: Configured via Winlogon registry keys.
*   **EikonAdmin**: Dedicated administration account. Added to `Administrators` group.
    *   **Password**: `N1viH@rd0$Secure!`
    *   **State**: Enabled. Not visible on logon screen (`DontDisplayLastUserName=1`).
    *   **Access**: Ctrl+Alt+Del → Sign Out → type EikonAdmin credentials.
*   **Built-in Administrator**: Disabled (SID-500).
*   **Guest**: Disabled (`net user Guest /active:no`).
*   **Administrators Group Sanitization**: Removes all unauthorized members; only EikonAdmin and EikonUser remain.

### 5.2 Registry Hardening (Step 7)

#### 5.2.1 System Security (`ConfigureOsHardening`)
*   Applies privilege rights via SecEdit (SetValue, SetDebug, SetSystemTime for Admins/SYSTEM).
*   Temporarily disables UAC (`EnableLUA=0`) for headless execution; re-enabled at seal.
*   Hides last username on logon (`DontDisplayLastUserName=1`).
*   Enforces Ultimate Performance power plan.
*   Restricts HTMLHelp zone to 0.

#### 5.2.2 IE Hardening (`ConfigureIEHardening`)
*   `Restrictions\NoBrowserContextMenu`: **1**
*   `Control Panel\*Tab`: **1** (Hide all tabs)
*   `Toolbars\Restrictions\NoNavBar`: **1**
*   `Main\AlwaysShowMenus`: **0**

#### 5.2.3 User Hive Hardening (`ApplyUserHardening`)
Applied to `C:\Users\Default\NTUSER.DAT` (offline hive mount as `HKU\EikonTemp`). All new user profiles inherit these policies.

**Explorer Shell Restrictions** (`HKCU\...\Policies\Explorer`):

| Value Name | Data | Effect |
| :--- | :--- | :--- |
| `NoDrives` | `0x04` | Hide C: drive |
| `NoViewOnDrive` | `0x04` | Block browsing C: |
| `NoRun` | 1 | Disable Run Command |
| `NoWinKeys` | 1 | Disable Win+X Hotkeys |
| `NoFind` | 1 | Disable Search |
| `NoTrayContextMenu` | 1 | Disable Taskbar Tray Right-Click |
| `NoClose` | 1 | Remove Shutdown/Restart options |
| `HideSCAVolume` | 1 | Hide audio status icon |
| `HideSCANetwork` | 1 | Hide network status icon |
| `NoControlPanel` | 1 | Block Control Panel |
| `NoSetFolders` | 1 | Block Settings folders |
| `NoSetTaskbar` | 1 | Block taskbar settings |
| `NoNetworkConnections` | 1 | Block network connections UI |
| `DisallowRun` | 1 | Enable App Blocklist |

**Start Menu Lockdown** (`HKCU\...\Policies\Explorer`):
*   `NoStartMenuMorePrograms`, `NoCommonStartMenu`, `NoSMHelp`, `NoRecentDocsHistory`
*   `ClearRecentDocsOnExit`, `NoChangeStartMenu`, `NoFavoritesMenu`
*   `NoSMMyDocs`, `NoSMMyPictures`, `NoStartMenuMyMusic`, `NoUserFolderInStartMenu`

**Taskbar Lockdown**:
*   Hides Task View button, People band, Search box, Notification Center.
*   `TaskbarSizeMove=0`, `TaskbarNoDragToolbar=1`, `NoToolbarsOnTaskbar=1`, `NoPinningToTaskbar=1`.

**System Policies** (`HKCU\...\Policies\System`):
*   `DisableTaskMgr=1` — Task Manager disabled.
*   `DisableRegistryTools=1` — Registry editing disabled.

**Start Layout Lockdown**:
*   Deploys empty `LayoutModificationTemplate` XML to `C:\Windows\StartLayouts\EikonStartLayout.xml`.
*   Sets `LockedStartLayout=1` with layout path in HKCU policy.
*   RunOnce task re-imports empty layout at first login.

**Start Sidebar Folder Hiding (HKLM)**:
*   Applies `AllowPinnedFolder*=0` via PolicyManager CSP (Documents, Downloads, FileExplorer, Music, Network, PersonalFolder, Pictures, Settings, Videos).

#### 5.2.4 DisallowRun App Blocklist (43 Executables)
Applied via `HKCU\...\Policies\Explorer\DisallowRun` subkey:

| Category | Blocked Executables |
| :--- | :--- |
| **Shells/Interpreters** | `cmd.exe`, `powershell.exe`, `powershell_ise.exe`, `cscript.exe`, `mshta.exe` |
| **Registry/Editing** | `regedit.exe`, `regedit32.exe` |
| **Control Panel** | `control.exe`, `SystemSettings.exe` |
| **System Management** | `mmc.exe`, `taskmgr.exe`, `msconfig.exe`, `msinfo32.exe` |
| **MMC Snap-ins** | `compmgmt.msc`, `devmgmt.msc`, `diskmgmt.msc`, `services.msc`, `eventvwr.msc`, `gpedit.msc`, `lusrmgr.msc`, `secpol.msc`, `comexp.msc`, `certmgr.msc` |
| **Network** | `ncpa.cpl`, `ipconfig.exe`, `firewall.cpl` |
| **Utilities** | `notepad.exe`, `write.exe`, `wordpad.exe`, `calc.exe`, `mspaint.exe`, `SnippingTool.exe` |
| **Diagnostics** | `perfmon.exe`, `resmon.exe`, `dxdiag.exe` |
| **Remote Access** | `mstsc.exe` |
| **CPL Applets** | `appwiz.cpl`, `inetcpl.cpl`, `sysdm.cpl`, `timedate.cpl`, `main.cpl`, `intl.cpl` |
| **Other** | `winver.exe` |

**Not Blocked**: `wscript.exe` (required for KioskLauncher.vbs scheduled task).

### 5.3 Application Crash Dumps (Step 8)
**Configuration**: `hardware-config.json` → `software.crashDumps`
*   Registry: `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<process>`
*   **DumpFolder**: Configurable (default: `E:\CrashDumps`).
*   **DumpCount**: 5.
*   **DumpType**: 2 (full mini-dump).
*   **Target Processes**: Configurable list (e.g., `GVPPro.exe`, imaging services).

### 5.4 Services Optimization (Step 9)
**Service Control Manager** (`sc config` / `Set-Service`):

#### 5.4.1 Enabled Services
| Service Name | Display Name / Function | Action |
| :--- | :--- | :--- |
| **vds** | Virtual Disk Service | **Auto** |
| **MsKeyboardFilter** | WEKF Keyboard Filter | **Auto** |
| **Spooler** | Print Spooler | **Auto** |
| **W32Time** | Windows Time | **Auto** |
| **WlanSvc** | WLAN AutoConfig | **Auto** |
| **UsoSvc** | Update Orchestrator | **Manual (Demand)** |

#### 5.4.2 Full List of DISABLED Services (98 Services)
The following services are strictly **DISABLED** (`start= disabled` and Stopped):

**Networking & Remote Access**
*   `SSDPSRV` (SSDP Discovery)
*   `RemoteRegistry`
*   `RemoteAccess`
*   `upnphost` (UPnP Device Host)
*   `fdPHost` (Function Discovery Provider Host)
*   `FDResPub` (Function Discovery Resource Publication)
*   `WMPNetworkSvc` (Windows Media Player Network Sharing)
*   `PeerDistSvc` (BranchCache)
*   `RasMan` (Remote Access Connection Manager)
*   `UmRdpService` (Remote Desktop Services UserMode Port Redirector)
*   `TermService` (Remote Desktop Services)
*   `SessionEnv` (Remote Desktop Configuration)
*   `WinRM` (Windows Remote Management)
*   `WebClient`
*   `AJRouter` (AllJoyn Router Service)
*   `ALG` (Application Layer Gateway Service)
*   `icssvc` (Windows Mobile Hotspot Service)
*   `LanmanServer` (Server — temporarily enabled during seal for user management, then re-disabled)
*   `QWAVE` (Quality Windows Audio Video Experience)
*   `wcncsvc` (Windows Connect Now)
*   `WwanSvc` (WWAN AutoConfig)
*   `HomeGroupListener`
*   `HomeGroupProvider`
*   `SharedAccess` (Internet Connection Sharing - ICS)

**Updates, Telemetry & Diagnostics**
*   `wuauserv` (Windows Update)
*   `WSearch` (Windows Search)
*   `DiagTrack` (Connected User Experiences and Telemetry)
*   `WerSvc` (Windows Error Reporting Service)
*   `wercplsupport` (Problem Reports and Solutions Control Panel Support)
*   `diagsvc` (Diagnostic Execution Service)
*   `DPS` (Diagnostic Policy Service)
*   `WdiServiceHost` (Diagnostic Service Host)
*   `WdiSystemHost` (Diagnostic System Host)
*   `TroubleshootingSvc` (Program Compatibility Assistant Service)
*   `PcaSvc` (Program Compatibility Assistant Service)
*   `DoSvc` (Delivery Optimization)
*   `AppVClient` (Microsoft App-V Client)
*   `UevAgentService` (User Experience Virtualization Service)
*   `PushToInstall` (Windows PushToInstall Service)
*   `InstallService` (Microsoft Store Install Service)
*   `tzautoupdate` (Auto Time Zone Updater)
*   `WaaSMedicSvc` (Windows Update Medic Service)

**Hardware, Audio & Bluetooth**
*   `hidserv` (Human Interface Device Service)
*   `stisvc` (Windows Image Acquisition)
*   `WbioSrvc` (Windows Biometric Service)
*   `Fax`
*   `TapiSrv` (Telephony)
*   `WPDBusEnum` (Portable Device Enumerator Service)
*   `BthAvctpSvc` (AVCTP Service)
*   `bthserv` (Bluetooth Support Service)
*   `BTAGService` (Bluetooth Audio Gateway Service)
*   `CertPropSvc` (Certificate Propagation)
*   `Audiosrv` (Windows Audio)
*   `AudioEndpointBuilder` (Windows Audio Endpoint Builder)
*   `VacSvc` (Volumetric Audio Compositor Service)
*   `FrameServer` (Windows Camera Frame Server)
*   `shpamsvc` (Shared PC Account Manager)

**Virtualization (Hyper-V Guest)**
*   `vmicvmsession`
*   `vmicguestinterface`
*   `vmicheartbeat`
*   `vmictimesync`
*   `vmickvpexchange`
*   `vmicrdv`
*   `vmicshutdown`
*   `HvHost` (HV Host Service)

**Storage, Backup & File System**
*   `swprv` (Microsoft Software Shadow Copy Provider)
*   `VSS` (Volume Shadow Copy)
*   `wbengine` (Block Level Backup Engine Service)
*   `SDRSVC` (Windows Backup)
*   `MSiSCSI` (Microsoft iSCSI Initiator Service)
*   `CscService` (Offline Files)
*   `defragsvc` (Optimize drives)
*   `TieringEngineService` (Storage Tiers Management)
*   `TrkWks` (Distributed Link Tracking Client)
*   `SEMgrSvc` (Payments and NFC/SE Manager)

**User Experience, Xbox & Shell**
*   `Themes`
*   `SysMain` (Superfetch)
*   `WpcMonSvc` (Parental Controls)
*   `SharedRealitySvc` (Windows Mixed Reality)
*   `MapsBroker` (Downloaded Maps Manager)
*   `RetailDemo` (Retail Demo Service)
*   `WalletService`
*   `PhoneSvc`
*   `AssignedAccessManagerSvc`
*   `autotimesvc` (Cellular Time)
*   `pla` (Performance Logs & Alerts)
*   `DusmSvc` (Data Usage)
*   `XblAuthManager` (Xbox Live Auth Manager)
*   `XblGameSave` (Xbox Live Game Save)
*   `XboxGipSvc` (Xbox Accessory Management)
*   `XboxNetApiSvc` (Xbox Live Networking Service)
*   `RmSvc` (Radio Management Service)
*   `wisvc` (Windows Insider Service)
*   `wlidsvc` (Microsoft Account Sign-in Assistant)
*   `BITS` (Background Intelligent Transfer Service)
*   `AxInstSV` (ActiveX Installer)
*   `SNMPTRAP` (SNMP Trap)
*   `Wecsvc` (Windows Event Collector)

### 5.5 Firewall Configuration (Step 10)
**netsh / Set-NetFirewallRule**:
*   **Global**: `advfirewall reset`, Block Inbound, Allow Outbound.
*   **Allowed Groups**: "Core Networking", "File and Printer Sharing", "Remote Assistance".
*   **Blocked Keywords** (Regex matching DisplayName):
    *   `*IGMP*`, `*Multicast Listener*`, `*Neighbor Discovery*`, `*Teredo*`
    *   `*NB-Datagram*`, `*NB-Name*`, `*NB-Session*`, `*SMB-In*`
    *   `*mDNS*`, `*Cast to Device*`
*   **ICMP**: Block Echo Requests (Ping) for v4 and v6.
*   **Custom Rules**:
    *   Allow TCP 22 Inbound (OpenSSH)
    *   Allow TCP 104 Inbound (DICOM)
    *   Allow TCP 104, 2761 Outbound (DICOM/HL7)

### 5.6 Local Policies (Step 11)
*   **USBSTOR**: `HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR\Start` = 3 (device disabled, prevents USB storage enumeration).

### 5.7 CIS Benchmarks (Step 12)
| Category | Registry Key (HKLM) | Value Name | Data | Description |
| :--- | :--- | :--- | :--- | :--- |
| LSA | `SYSTEM\CurrentControlSet\Control\Lsa` | `LimitBlankPasswordUse` | 1 | No Blank PW |
| LSA | `SYSTEM\CurrentControlSet\Control\Lsa` | `LmCompatibilityLevel` | 5 | NTLMv2 Only |
| UAC | `SOFTWARE\...\Policies\System` | `EnableLUA` | 1 | Admin Approval (re-enabled at seal) |
| UAC | `SOFTWARE\...\Policies\System` | `ConsentPromptBehaviorAdmin` | 1 | Secure Desktop |
| Net | `SYSTEM\...\Tcpip\Parameters` | `DisableIPSourceRouting` | 2 | Highest Protection |
| Net | `SOFTWARE\...\HardenedPaths` | `\\*\NETLOGON` | `RequireMutualAuthentication=1,RequireIntegrity=1` | Hardened UNC |

### 5.8 Advanced Audit Policies (Step 13)
**auditpol**:
*   `auditpol /set /subcategory:"Logon" /success:enable /failure:enable`
*   `auditpol /set /subcategory:"Process Creation" /success:enable /failure:enable` (CmdLine args enabled via Registry).
*   `auditpol /set /subcategory:"Account Lockout" /failure:enable`
*   `auditpol /set /subcategory:"Removable Storage" /success:enable /failure:enable`

**Event Logs** (`wevtutil`):
*   **Command**: `wevtutil sl [LogName] /ms:20971520 /rt:false /ca:[SDDL]`
*   **Size**: 20MB (20971520 bytes).
*   **Retention**: Circular (overwrite as needed).
*   **SDDLs**:
    *   *Application*: `O:BAG:SYD:(A;;0x3;;;IU)(A;;0x7;;;BA)(A;;0xf0007;;;SY)` (Interactive User Read-Only).
    *   *Security/System*: Same SDDL pattern.

### 5.9 AppLocker (Step 14 — Deferred to Seal)
**Implementation**: XML Policy imported via `Set-AppLockerPolicy -XmlPolicy <tempFile> -Merge`.
**Enforcement**: Deferred to Final Seal Phase (not applied in Step 14) to ensure installer resilience across reboots.

**Rules**:
1.  **EikonAdmin Bypass** (Per-User SID):
    *   Uses `GetUserSid("EikonAdmin")` to retrieve the specific user SID at runtime.
    *   Rule: `FilePathRule Path="*" Action="Allow"` scoped to EikonAdmin's SID.
    *   **Why per-user SID**: Both EikonUser and EikonAdmin are in `Administrators`. Using group SID `S-1-5-32-544` would bypass AppLocker for both users.
    *   Fallback: `S-1-5-32-544` if SID lookup fails.
2.  **System Defaults** (Everyone `S-1-1-0`):
    *   `%PROGRAMFILES%\*`
    *   `%WINDIR%\*`
3.  **Configurable Paths** (`hardware-config.json` → `software.appLocker.allowedPaths`):
    *   e.g., `D:\GVP-Pro\App\*` — allows clinical application executables.
4.  **Installer Directory**: Automatically allows `<EikonConfigurator base dir>\*` to support resume/reseal.

**Service**: `AppIDSvc` set to `start=auto` and started before policy merge.

### 5.10 Windows Defender Exclusions (Step 15)
**PowerShell** (`Add-MpPreference`):
*   **Exclusion Paths**: Configured via `hardware-config.json` → `software.defender.exclusionPaths`.
*   **Exclusion Processes**: Configured via `hardware-config.json` → `software.defender.exclusionProcesses`.

### 5.11 Printer Configuration (Step 16)
**Configuration**: `hardware-config.json` → `printers[]`
*   **Driver Installation**: Supports `.INF` (pnputil), `.MSI` (msiexec), `.EXE` (direct invocation).
*   **Port Creation**: TCP ports by IP address; USB ports.
*   **Printer Queue**: `Add-Printer` with configured driver and port.
*   **Default Printer**: Set via `isDefault=true`.
*   Skipped if no printers defined in config.

## 6. Kiosk & Shell Hardening

### 6.1 Unified Write Filter — UWF (Step 17)
**`uwfmgr` Configuration**:
*   **Reboot Detection**: Checks for `uwfmgr.exe` availability and `RebootPending` registry key. If features require reboot, sets `_uwfFailedPendingReboot=true` and triggers reboot (see §7 Two-Pass Design).
*   **Overlay**: `uwfmgr overlay set-warningthreshold 1024` (warn at 1GB).
*   **Volume**: `uwfmgr volume protect C:` (write-protect C: drive).
*   **Overlay Size**: `uwfmgr overlay set-size <overlaySizeMb>` (configurable via `hardware-config.json`).
*   **File Exclusions** (mandatory + configurable):
    *   `C:\ProgramData\Microsoft\Crypto`
    *   `C:\ProgramData\Microsoft\wlansvc`
    *   `C:\Windows\WindowsUpdate.log`
    *   Additional from `hardware-config.json` → `uwf.fileExclusions`
*   **Registry Exclusions** (mandatory + configurable):
    *   `HKLM\SYSTEM\CurrentControlSet\Control\ComputerName`
    *   `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters`
    *   `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones`
    *   Additional from `hardware-config.json` → `uwf.registryExclusions`
*   **Activation**: `uwfmgr filter enable`

### 6.2 Keyboard Filter — WEKF (Step 18)
**Namespace**: `root\standardcimv2\embedded`

**Per-Account Filtering** (`WEKF_Account` WMI class):

| Account | Enabled | Effect |
| :--- | :--- | :--- |
| **EikonUser** | `true` | All keyboard shortcuts blocked |
| **EikonAdmin** | `false` | Full keyboard access for maintenance |

*Note: Both users are in `Administrators` group. Per-account WMI filtering is used because `DisableKeyboardFilterForAdministrators` would bypass WEKF for both users.*

**Blocked Predefined Keys** (`WEKF_PredefinedKey`):
*   `Alt+F4`, `Alt+Tab`, `Alt+Esc`, `Ctrl+Esc`, `Ctrl+F4`
*   `Win+L` (Lock), `Win+R` (Run), `Win+E` (Explorer), `Win+M` (Minimize), `Win+D` (Desktop)
*   `Win` (Start Menu), `Win+U`, `Win+Enter`
*   `Win+Up`, `Win+Down`, `Win+Left`, `Win+Right` (Snap Assist)

**Blocked Custom Scancodes** (`WEKF_CustomKey`):

| Shortcut | Modifiers | Scancode |
| :--- | :--- | :--- |
| `Ctrl+Alt+Tab` | 6 (Ctrl+Alt) | 15 |
| `Ctrl+Alt+Esc` | 6 (Ctrl+Alt) | 1 |
| `Win+X` | 8 (Win) | 45 |
| `Win+Space` | 8 (Win) | 57 |

### 6.3 Kiosk Startup — KioskLauncher (Step 19)
**Scheduled Task** (`EikonKioskLauncher`):
*   **Trigger**: `AtLogOn -User EikonUser` (fires only when EikonUser logs in, not EikonAdmin).
*   **Action**: `wscript.exe "<KioskLauncher.vbs path>"`.
*   **Principal**: `EikonUser`, Interactive session, Limited run level.
*   **Settings**: `AllowStartIfOnBatteries`, `DontStopIfGoingOnBatteries`, `StartWhenAvailable`.
*   **ExecutionTimeLimit**: `00:00:00` (no timeout — runs indefinitely).

**Desktop Shortcut** (`Start GVP-Pro.lnk`):
*   Placed in `C:\Users\Default\Desktop\` (inherited by all new profiles).
*   Target: `wscript.exe` with `KioskLauncher.vbs` path.
*   Allows manual restart of the kiosk application after a crash.

**Custom Shell** (configurable via `hardware-config.json` → `software.shell.customShellPath`):
*   **Registry**: `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell`
*   *Default*: `explorer.exe`.
*   *Example*: `D:\GVP-Pro\App\GVPPro.exe`.

## 7. Seal Installation — Two-Pass Design

### 7.1 Pass 1: Reboot for Pending Features
When UWF/DISM features require a reboot (`_uwfFailedPendingReboot=true`):
1.  Configures AutoLogon for **EikonAdmin** (Winlogon keys + `ForceAutoLogon=1`).
2.  Sets `DevicePasswordLessBuildVersion=0` (disables Windows 10 2004+ passwordless feature that silently ignores AutoLogon keys).
3.  Registers RunOnce resilience task: `*EikonConfiguratorResume` → `EikonConfigurator.exe --resume`.
4.  Triggers `shutdown /r /t 30` with message.
5.  On reboot: EikonAdmin auto-logs in, RunOnce fires, EikonConfigurator resumes from last completed step.

### 7.2 Pass 2: Final Seal
When no reboot is pending:
1.  **Cleanup Resilience**: Deletes RunOnce entry and state registry key.
2.  **Reset EikonUser Password**: Generates new 32-character random password.
3.  **Configure Final AutoLogon**:
    *   Production: `DefaultUserName=EikonUser` (boots directly to kiosk).
    *   VM/Test (`--keep-admin`): `DefaultUserName=EikonAdmin`.
    *   `AutoAdminLogon=1`, `ForceAutoLogon=0` (Sign Out returns to login screen for technician access).
4.  **Re-disable LanmanServer**: Was temporarily enabled for user management.
5.  **Apply AppLocker Policy**: Calls `ConfigureAppLockerFinal()` (§5.9).
6.  **Re-enable UAC**: `EnableLUA=1`, `ConsentPromptBehaviorAdmin=1`.
7.  **Re-enable Security**: `DontDisplayLastUserName=1` (final — hides username on logon screen).
8.  **Shutdown**: `shutdown /s /t 30` — *"Installation complete. Power the PC on 30 seconds after power off."*

### 7.3 Reseal Mode (`--reseal-only`)
Allows re-locking a system after technician maintenance without full re-installation:
*   Re-enables UWF, WEKF, AppLocker.
*   Resets EikonUser credentials.
*   Sets AutoLogon back to EikonUser.
*   Shuts down the system.

## 8. Logging & Auditing

### 8.1 OneClickInstaller Log
*   **File**: `oneclickinstaller_log.txt` in the installer directory.
*   **Format**: `[yyyy-MM-dd HH:mm:ss] message`
*   OS configuration output prefixed with `[OS Config]`.

### 8.2 EikonConfigurator Log
*   **File**: `C:\ProgramData\EikonConfigurator\OS_ClosedLoop.log`
*   Persists across reboots for two-pass debugging.
