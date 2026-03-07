# OneClickInstaller Package

## Quick Start
1. Copy the entire package folder to the target machine (USB or network share)
2. Copy the software installer media into the **SW\** folder (see "SW Folder" below)
3. **Right-click** `INSTALL.bat` and select **"Run as administrator"**
4. The bootstrapper installs .NET Framework 3.5/4.8 (reboots if needed, then auto-resumes)
5. OneClickInstaller GUI launches — click **Install** to begin
6. After all software installs, EikonConfigurator runs the 19-step kiosk lockdown
7. System reboots into locked-down kiosk mode

> **Important:** Always use `INSTALL.bat` — do NOT launch `OneClickInstaller.exe` directly,
> as it requires .NET Framework 3.5/4.8 which the batch file installs first.

## Package Variants
The build produces two pre-configured packages — use the one matching your target:

| Package | Hardware Checks | EikonConfigurator Args |
|---------|----------------|----------------------|
| **OneClickInstaller_Production** | Strict (i3/i5/i7/i9/Xeon, 4+ cores, 16 GB RAM, 1 TB disk, 2 NICs) | (none) |
| **OneClickInstaller_VM** | Relaxed (any CPU, 1 core, 2 GB RAM, 50 GB disk, 1 NIC) | `--bypass-hardware-check --log-path` |

## What's Included
- **INSTALL.bat** — Bootstrapper (installs .NET 3.5/4.8, then launches the installer)
- **OneClickInstaller.exe** — Main GUI installer (software packages, disk partitioning, MySQL)
- **installer-config.json** — Configuration (packages, partitions, OS lockdown settings)
- **EikonConfigurator/** — OS kiosk hardening tool (19 steps: accounts, AppLocker, UWF, WEKF, etc.)
- **SW/** — Software packages folder (copy installer media here before deployment)
- **UNINSTALL_DOTNET48.bat** — Utility to remove .NET 4.8 if needed

## SW Folder
Before deployment, copy the following installer media into the **SW\\** folder:
- .NET Framework 4.8 offline installer → `SW\.NET4.8\`
- .NET Framework 3.5 sxs source → `SW\dotnetfx35\sxs\`
- Microsoft Visual C++ Redistributables (2008, 2010, 2012, 2015-2022)
- 7-Zip, CH34x USB Driver, DirectX SDK, MySQL Server + Connector
- Notepad++, LUT calibration files, GVPPro Setup MSI

The exact folder names and paths are defined in `installer-config.json`.

## Requirements
- Windows 10 LTSC (build 17763+) or Windows 11
- Administrator privileges
- Single disk with sufficient space (for partitioning)

## Installation Process
1. **INSTALL.bat** installs .NET Framework 3.5 (DISM offline) and 4.8 (silent)
   - Reboots if needed, auto-resumes via RunOnce registry key
2. **OneClickInstaller** launches and performs:
   - Disk partitioning (C: 60 GB, D: 15 GB, E: remaining — configurable)
   - Silent installation of all software packages (VC++ redists, 7-Zip, drivers, MySQL, etc.)
   - Copy LUT files to D:\Luts
   - MySQL database configuration (root/root, dcmdetails DB)
   - GVPPro application install (final package)
3. **EikonConfigurator** runs automatically (if enabled in config):
   - Hardware validation (CPU, RAM, disk, NICs)
   - Create EikonAdmin + EikonUser accounts
   - Configure auto-logon, scheduled tasks, services
   - AppLocker policies, Keyboard Filter (WEKF), UWF overlay
   - Disable unnecessary Windows features
   - Seal the kiosk (UWF commit + reboot)

## Configuration
Edit **installer-config.json** to customize without recompiling:

- **diskPartitioning** — drive sizes, letters, labels, or disable entirely
- **softwarePackages** — add/remove MSI, EXE, INF, or COPY-type packages
- **finalInstallPackage** — the last application installed (GVPPro by default)
- **mysqlConfig** — database name, credentials, SQL script path
- **osConfiguration** — enable/disable kiosk lockdown, set EikonConfigurator args

## Package Structure
```
OneClickInstaller_Production\    (or OneClickInstaller_VM\)
├── INSTALL.bat                  ← Entry point (run as admin)
├── OneClickInstaller.exe
├── OneClickInstaller.exe.config
├── installer-config.json        ← Pre-configured for this variant
├── README.txt
├── UNINSTALL_DOTNET48.bat
├── EikonConfigurator\           ← OS kiosk hardening tool
│   ├── EikonConfigurator.exe
│   ├── hardware-config.json     ← Pre-configured for this variant
│   └── (runtime dependencies)
└── SW\                          ← Copy installer media here
    ├── .NET4.8\
    ├── dotnetfx35\sxs\
    ├── 7Zip\
    ├── CH34x_Install_Windows_v3_4\
    ├── DirectX-SDK\
    ├── GVPPro\
    ├── Luts\
    ├── Microsoft Visual C++ ...\
    ├── MySQL_JavaJRE_DB_Installer-...\
    └── Notepad++\
```

## Troubleshooting
- **"Requires .NET Framework"** → Use `INSTALL.bat` instead of running the exe directly
- **"Not running as administrator"** → Right-click INSTALL.bat → "Run as administrator"
- **"Installation file not found"** → Ensure SW\ folder contains all installer media
- **MySQL configuration fails** → Check if MySQL service started; verify credentials
- **Disk partitioning fails** → Ensure sufficient unallocated space; run as admin
- **Hardware check fails (VM)** → Use the OneClickInstaller_VM package variant