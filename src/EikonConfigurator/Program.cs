using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.ServiceProcess;
using Microsoft.Win32;
using System.Text.Json;
using System.Management;
using System.Security.Cryptography;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Linq;

namespace EikonConfigurator
{
    internal class Program
    {
        // --- SAFE HELPERS & HOOKS ---
        internal static Action<string, string> CommandExecutor;
        internal static Action<RegistryKey, string, object, RegistryValueKind> RegistryKeySetter;
        internal static Action<string, string, object, RegistryValueKind> RegistryStaticSetter;
        internal static Action<RegistryKey, string, string, object, RegistryValueKind> RegistrySetter;

        internal static Action<string> ServiceStopper = (svcName) =>
        {
             try 
             {
                 using (var sc = new ServiceController(svcName))
                 {
                     if (sc.Status != ServiceControllerStatus.Stopped)
                     {
                         try { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(2)); } catch {}
                     }
                 }
             }
             catch { /* Service might not exist */ }
        };

        internal static Func<RegistryKey, string, bool, RegistryKey> KeyOpener = (root, sub, writable) => root.OpenSubKey(sub, writable);
        internal static Func<RegistryKey, string, RegistryKeyPermissionCheck, RegistryRights, RegistryKey> KeyOpenerWithRights = (root, sub, check, rights) => root.OpenSubKey(sub, check, rights);
        internal static Func<RegistryKey, string, RegistryKey> KeyCreator = (root, sub) => root.CreateSubKey(sub);

        private static void SafeSet(RegistryKey key, string name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
        {
            if (RegistryKeySetter != null) RegistryKeySetter(key, name, value, kind);
            else
            {
                if (IsDryRun) { Console.WriteLine($"[DRY-RUN] Key.SetValue: {key.Name}\\{name} = {value}"); return; }
                if (kind == RegistryValueKind.Unknown) key.SetValue(name, value);
                else key.SetValue(name, value, kind);
            }
        }
        
        private static void SafeSetStatic(string keyPath, string name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
        {
             if (RegistryStaticSetter != null) RegistryStaticSetter(keyPath, name, value, kind);
             else
             {
                 if (IsDryRun) { Console.WriteLine($"[DRY-RUN] Registry.SetValue: {keyPath}\\{name} = {value}"); return; }
                 if (kind == RegistryValueKind.Unknown) Registry.SetValue(keyPath, name, value);
                 else Registry.SetValue(keyPath, name, value, kind);
             }
        }

        // --- CONSTANTS ---
        private const string TargetUser = "EikonUser";
        private const string ResumeTaskName = "EikonConfiguratorResume";
        private static string _randomPassword;
        private static bool IsDryRun = false;
        private static string _adminPasswordArg = null;
        private static int _lastCompletedStep = 0;
        private const string RegStateKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\EikonConfigurator";
        static bool _uwfFailedPendingReboot = false;
        private static string[] _args = Array.Empty<string>();

        // --- JSON CONFIGURATION MODELS ---
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions 
        { 
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public class HardwareConfig
        {
            public HardwareRequirementsConfig hardwareRequirements { get; set; }
            public List<StartupFileConfig> startupFiles { get; set; }
            public List<NetworkAdapterConfig> networkAdapaters { get; set; }
            public List<SerialPortConfig> serialPorts { get; set; }
            public UwFConfig uwf { get; set; }
            public List<PrinterConfig> printers { get; set; }
            public SoftwareConfig software { get; set; }
        }

        public class HardwareRequirementsConfig
        {
            public int minCpuCores { get; set; } = 4;
            public int minRamGB { get; set; } = 16;
            public int minDiskGB { get; set; } = 1000;
            public int minNetworkAdapters { get; set; } = 2;
            public List<string> allowedCpuFamilies { get; set; }
        }

        public class StartupFileConfig { public string source { get; set; } public string destination { get; set; } public string description { get; set; } }
        public class PrinterConfig { public string name { get; set; } public string driverSource { get; set; } public string driverName { get; set; } public string portType { get; set; } public string ipAddress { get; set; } public bool isDefault { get; set; } }

        public class UwFConfig { public bool enabled { get; set; } public int overlaySizeMb { get; set; } public List<string> fileExclusions { get; set; } public List<string> registryExclusions { get; set; } }
        public class NetworkAdapterConfig { public Identification identification { get; set; } public NetSettings settings { get; set; } }
        public class Identification { public List<string> descriptionKeywords { get; set; } public string macAddressPrefix { get; set; } public string hardwareIdDetails { get; set; } }
        public class NetSettings { public string newName { get; set; } public Ipv4Config ipv4 { get; set; } public Dictionary<string, string> advanced { get; set; } public string forceComPort { get; set; } public int baudRate { get; set; } }
        public class Ipv4Config { public string mode { get; set; } public string ipAddress { get; set; } public string subnetMask { get; set; } public string gateway { get; set; } }
        public class SerialPortConfig { public Identification identification { get; set; } public NetSettings settings { get; set; } }

        public class SoftwareConfig { public ShellConfig shell { get; set; } public AppLockerConfig appLocker { get; set; } public CrashDumpConfig crashDumps { get; set; } public DefenderConfig defender { get; set; } }
        public class ShellConfig { public string customShellPath { get; set; } }
        public class AppLockerConfig { public List<string> allowedPaths { get; set; } }
        public class CrashDumpConfig { public bool enabled { get; set; } public string dumpFolder { get; set; } public List<string> targetProcesses { get; set; } }
        public class DefenderConfig { public List<string> exclusionPaths { get; set; } public List<string> exclusionProcesses { get; set; } }

        private static HardwareConfig LoadConfig()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hardware-config.json");
            if (File.Exists(configPath))
            {
                try { return JsonSerializer.Deserialize<HardwareConfig>(File.ReadAllText(configPath), JsonOptions); }
                catch (Exception ex) { Console.WriteLine($"JSON Parse Error: {ex.Message}"); }
            }
            return new HardwareConfig();
        }

        // --- MULTI-WRITER LOGGING ---
        public class MultiWriter : TextWriter
        {
            private TextWriter _consoleOut;
            private StreamWriter _fileOut;

            public MultiWriter(TextWriter consoleOut, string filePath)
            {
                _consoleOut = consoleOut;
                _fileOut = new StreamWriter(filePath, true) { AutoFlush = true };
            }

            public override Encoding Encoding => _consoleOut.Encoding;
            public override void Write(char value) { _consoleOut.Write(value); _fileOut.Write(value); }
            public override void Write(string value) { _consoleOut.Write(value); _fileOut.Write(value); }
            public override void WriteLine(string value) { _consoleOut.WriteLine(value); _fileOut.WriteLine(value); }
            protected override void Dispose(bool disposing) { if (disposing) _fileOut?.Dispose(); base.Dispose(disposing); }
        }

        static void Main(string[] args)
        {
            // 1. Immediately ensure we are running from a safe, persistent local drive
            EnsureLocalExecution(args);

            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer_log.txt");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--log-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    logPath = args[i + 1];
                    try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)); } catch {}
                }
            }

            var originalOut = Console.Out;
            try 
            {
                using (var logWriter = new MultiWriter(originalOut, logPath)) 
                {
                    Console.SetOut(logWriter);
                    Console.WriteLine($"\n--- Session Start: {DateTime.Now} ---");
                    if (args.Contains("--resume")) Console.WriteLine(">>> RESUMING AFTER REBOOT <<<");

                    try { RunInstaller(args); }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\nFATAL ERROR: " + ex.Message);
                        Console.WriteLine(ex.StackTrace);
                        Console.ResetColor();
                        Environment.Exit(1);
                    }
                    finally { Console.SetOut(originalOut); }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Log init failed: {ex.Message}"); RunInstaller(args); }
        }

        static void EnsureLocalExecution(string[] args)
        {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive"); 
            bool requiresTransfer = false;

            if (currentDir.StartsWith(@"\\")) requiresTransfer = true;
            else 
            {
                try 
                {
                    string driveLetter = Path.GetPathRoot(currentDir);
                    DriveInfo drive = new DriveInfo(driveLetter);
                    if (drive.DriveType == DriveType.Network || drive.DriveType == DriveType.Removable || !driveLetter.Equals(systemDrive + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        requiresTransfer = true;
                    }
                } 
                catch { requiresTransfer = true; } 
            }

            if (requiresTransfer)
            {
                string targetDir = Path.Combine(systemDrive, "EikonTempInstaller");
                Console.WriteLine($"Detected volatile execution source ({currentDir}). Migrating to {targetDir}...");
                TransferDirectory(currentDir, targetDir);

                string targetExe = Path.Combine(targetDir, Path.GetFileName(currentExe));
                var psi = new ProcessStartInfo(targetExe, string.Join(" ", args)) { UseShellExecute = false, WorkingDirectory = targetDir };
                Process.Start(psi);
                Environment.Exit(0); 
            }
        }

        static void TransferDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists) throw new DirectoryNotFoundException($"Source missing: {dir.FullName}");
            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles()) { file.CopyTo(Path.Combine(destinationDir, file.Name), true); }
            foreach (DirectoryInfo subDir in dir.GetDirectories()) { TransferDirectory(subDir.FullName, Path.Combine(destinationDir, subDir.Name)); }
        }

        private static void LoadState() { try { _lastCompletedStep = (int)(Registry.GetValue(RegStateKey, "LastStep", 0) ?? 0); } catch { } }

        private static void RunStep(int stepId, string name, Action action)
        {
            // FIX: Halt immediately if a previous step triggered a reboot
            if (_uwfFailedPendingReboot) return; 

            if (_lastCompletedStep >= stepId) { Console.WriteLine($"Skipping Step {stepId}: {name}"); return; }

            Console.WriteLine($"\n--- Step {stepId}: {name} ---");
            action();

            if (!IsDryRun)
            {
                try { Registry.SetValue(RegStateKey, "LastStep", stepId, RegistryValueKind.DWord); } catch { }
                _lastCompletedStep = stepId;
            }
        }

        static void RunInstaller(string[] args)
        {
            _args = args;
            if (args.Contains("--dry-run")) { IsDryRun = true; Console.WriteLine("!!! DRY RUN MODE !!!"); }
            for (int i = 0; i < args.Length; i++) { if (args[i] == "--admin-password" && i + 1 < args.Length) _adminPasswordArg = args[i + 1]; }

            // --- RESEAL-ONLY MODE ---
            // Skips all 18 configuration steps and goes directly to re-sealing.
            // Used by service engineers after maintenance to re-lock the kiosk.
            if (args.Contains("--reseal-only"))
            {
                Console.WriteLine("\n=== RESEAL-ONLY MODE ===");
                Console.WriteLine("Skipping all configuration steps. Re-sealing Clinical Mode...\n");
                ResealOnly();
                return;
            }

            Console.WriteLine("Initializing Configuration...");
            LoadState(); 
            
            SetResilienceRunOnce();

            // Deploy startup files (idempotent - runs every time before steps)
            DeployStartupFiles();

            if (!args.Contains("--bypass-hardware-check")) RunStep(1, "Hardware Validation", () => ValidateHardwareOrDie());
            RunStep(2, "Windows Features", () => ConfigureWindowsFeatures());
            RunStep(3, "Hardware Config", () => ConfigureHardware());
            RunStep(4, "Scheduled Tasks", () => ConfigureScheduledTasks());
            RunStep(5, "OS Tuning", () => ConfigureOsTuning());
            RunStep(6, "Create User", () => ConfigureUser());
            RunStep(7, "Registry Hardening", () => ConfigureRegistry());
            
            // Shifted everything up by one to close the gap
            RunStep(8, "Crash Dumps", () => ConfigureCrashDumps());
            RunStep(9, "Services Optimization", () => OptimizeServices());
            RunStep(10, "Firewall Configuration", () => ConfigureFirewall());
            RunStep(11, "Local Policies", () => ConfigureLocalPolicies());
            RunStep(12, "CIS Benchmarks", () => ConfigureCisBenchmarks());
            RunStep(13, "Advanced Audit Policies", () => ConfigureAdvancedAudit());
            RunStep(14, "AppLocker", () => ConfigureAppLocker());
            RunStep(15, "Defender Exclusions", () => ConfigureDefender());
            RunStep(16, "Printer Configuration", () => ConfigurePrinters());
            RunStep(17, "UWF Configuration", () => ConfigureUWF());
            RunStep(18, "Keyboard Filter", () => ConfigureKeyboardFilter()); 
            
            RunStep(19, "Kiosk Startup Config", () => ConfigureKioskStartup()); 
            
            SealInstallation();
        }

        /// <summary>
        /// Reseal-only mode: re-locks the kiosk after service maintenance.
        /// Re-enables UWF, Keyboard Filter, AppLocker; resets user credentials;
        /// disables EikonAdmin; reboots into sealed kiosk.
        /// </summary>
        static void ResealOnly()
        {
            Console.WriteLine("--- Reseal Phase: Re-enabling Lockdown Components ---");
            string winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

            // 0. Clean up the auto-reseal safety net task (engineer chose to reseal manually)
            Console.WriteLine("Removing auto-reseal safety net...");
            ExecuteSystemCommand("schtasks", "/Delete /TN \"EikonAutoReseal\" /F");

            // 1. Re-enable Keyboard Filter
            Console.WriteLine("Re-enabling Keyboard Filter...");
            ExecuteSystemCommand("sc", "config MsKeyboardFilter start= auto");

            // 2. Re-enable UWF
            Console.WriteLine("Re-enabling Unified Write Filter...");
            string uwfMgr = Path.Combine(Environment.SystemDirectory, "uwfmgr.exe");
            if (File.Exists(uwfMgr))
            {
                var config = LoadConfig();
                if (config.uwf != null && config.uwf.enabled)
                {
                    ExecuteSystemCommand(uwfMgr, "volume protect C:");
                    ExecuteSystemCommand(uwfMgr, "filter enable");
                    Console.WriteLine(" -> UWF filter will be active after reboot.");
                }
            }
            else
            {
                Console.WriteLine(" !! uwfmgr.exe not found — UWF cannot be re-enabled.");
            }

            // 3. Re-enable AppLocker
            Console.WriteLine("Re-enabling AppLocker...");
            ExecuteSystemCommand("sc", "config AppIDSvc start= auto");
            ExecuteSystemCommand("sc", "start AppIDSvc");

            // 4. Temporarily enable LanmanServer for user management
            ExecuteSystemCommand("sc", "config LanmanServer start= demand");
            ExecuteSystemCommand("net", "start LanmanServer /y");

            // 5. Reset EikonUser password and configure AutoLogon
            string finalUserPassword = GenerateSecurePassword(32);
            try
            {
                ExecuteSystemCommand("net", "accounts /minpwage:0");

                using (var ctx = new PrincipalContext(ContextType.Machine))
                {
                    Console.WriteLine($"Resetting {TargetUser} password and enabling account...");
                    var user = UserPrincipal.FindByIdentity(ctx, TargetUser);
                    if (user != null) { user.SetPassword(finalUserPassword); user.Enabled = true; user.Save(); }
                    else { Console.WriteLine($" !! WARNING: {TargetUser} not found. AutoLogon may fail."); }
                }

                Console.WriteLine($"Setting final AutoLogon credentials for {TargetUser}...");
                SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device", "DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);

                SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultDomainName", Environment.MachineName, RegistryValueKind.String);
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultUserName", TargetUser, RegistryValueKind.String);
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultPassword", finalUserPassword, RegistryValueKind.String);
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "AutoAdminLogon", "1", RegistryValueKind.String);
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "ForceAutoLogon", "1", RegistryValueKind.String);
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "LastUsedUsername", TargetUser, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"CRITICAL ERROR setting final credentials: {ex.Message}");
                Console.ResetColor();
            }

            // 6. Disable EikonAdmin
            Console.WriteLine("Disabling EikonAdmin account...");
            ExecuteSystemCommand("net", "user EikonAdmin /active:no");

            // 7. Re-disable LanmanServer
            Console.WriteLine("Re-disabling LanmanServer...");
            ServiceStopper("LanmanServer");
            ExecuteSystemCommand("sc", "config LanmanServer start= disabled");

            // 8. Re-apply AppLocker policy
            Console.WriteLine("Re-applying AppLocker policy...");
            ConfigureAppLockerFinal();

            // 9. Re-enable UAC and security settings
            Console.WriteLine("Re-enabling UAC...");
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 1, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DontDisplayLastUserName", 1, RegistryValueKind.DWord);

            // 10. Clean up any lingering state from previous installs
            Console.WriteLine("Cleaning up state tracking...");
            ExecuteSystemCommand("schtasks", $"/Delete /TN \"{ResumeTaskName}\" /F");
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    if (key != null) key.DeleteValue("*" + ResumeTaskName, false);
                }
            } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\EikonConfigurator", false); } catch { }

            Console.WriteLine("\n=== RESEAL COMPLETE ===");
            Console.WriteLine("Shutting down the PC. Power the PC on 30 seconds after power off.");
            ExecuteSystemCommand("shutdown", "/s /t 10 /f /c \"Eikon Clinical Mode Re-sealed. Shutting down. Power the PC on 30 seconds after power off.\"");
        }

        static void ValidateHardwareOrDie()
        {
            Console.WriteLine("Validating Hardware Requirements...");
            var config = LoadConfig();
            var req = config.hardwareRequirements ?? new HardwareRequirementsConfig();
            var errors = new List<string>();

            // --- CPU Model (i3/i5/i7/i9/Xeon) ---
            string cpuName = "";
            foreach (ManagementObject obj in new ManagementObjectSearcher("SELECT Name FROM Win32_Processor").Get())
            {
                cpuName = obj["Name"]?.ToString() ?? "";
            }
            Console.WriteLine($"  CPU: {cpuName}");

            if (req.allowedCpuFamilies != null && req.allowedCpuFamilies.Count > 0)
            {
                bool cpuMatch = false;
                foreach (var family in req.allowedCpuFamilies)
                {
                    if (cpuName.IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        cpuMatch = true;
                        break;
                    }
                }
                if (!cpuMatch)
                {
                    errors.Add($"CPU model '{cpuName}' not in allowed list: [{string.Join(", ", req.allowedCpuFamilies)}]");
                }
            }

            // --- CPU Cores ---
            int coreCount = 0;
            foreach (ManagementObject obj in new ManagementObjectSearcher("SELECT NumberOfLogicalProcessors FROM Win32_Processor").Get())
            {
                coreCount = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
            }
            Console.WriteLine($"  CPU Cores: {coreCount} (min: {req.minCpuCores})");
            if (coreCount < req.minCpuCores)
            {
                errors.Add($"Insufficient CPU cores. Found {coreCount}, required {req.minCpuCores}.");
            }

            // --- RAM ---
            ulong totalRamBytes = 0;
            foreach (ManagementObject obj in new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").Get())
            {
                totalRamBytes = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
            }
            double ramGB = totalRamBytes / (1024.0 * 1024.0 * 1024.0);
            Console.WriteLine($"  RAM: {ramGB:F1} GB (min: {req.minRamGB} GB)");
            if (totalRamBytes < (ulong)req.minRamGB * 1024UL * 1024UL * 1024UL * 95UL / 100UL) // 5% tolerance for firmware-reserved
            {
                errors.Add($"Insufficient RAM. Found {ramGB:F1} GB, required {req.minRamGB} GB.");
            }

            // --- Network Adapters ---
            int netCount = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE AdapterTypeId = 0 AND PhysicalAdapter = True").Get().Count;
            Console.WriteLine($"  Network Adapters: {netCount} (min: {req.minNetworkAdapters})");
            if (netCount < req.minNetworkAdapters)
            {
                errors.Add($"Insufficient network adapters. Found {netCount}, required {req.minNetworkAdapters}.");
            }

            // --- Disk Size ---
            long totalDiskBytes = 0;
            foreach (ManagementObject obj in new ManagementObjectSearcher("SELECT Size FROM Win32_DiskDrive WHERE MediaType='Fixed hard disk media'").Get())
            {
                totalDiskBytes += Convert.ToInt64(obj["Size"]);
            }
            double diskGB = totalDiskBytes / (1024.0 * 1024.0 * 1024.0);
            Console.WriteLine($"  Disk: {diskGB:F0} GB (min: {req.minDiskGB} GB)");
            if (totalDiskBytes < (long)req.minDiskGB * 1024L * 1024L * 1024L)
            {
                errors.Add($"Insufficient disk space. Found {diskGB:F0} GB, required {req.minDiskGB} GB.");
            }

            // --- Report ---
            if (errors.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nHardware validation FAILED ({errors.Count} issue(s)):");
                foreach (var err in errors) Console.WriteLine($"  ✗ {err}");
                Console.ResetColor();
                throw new Exception($"Hardware validation failed: {string.Join("; ", errors)}");
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Hardware validation PASSED.");
            Console.ResetColor();
        }

        static void SetResilienceRunOnce()
        {
            string myPath = Process.GetCurrentProcess().MainModule.FileName;
            var argsList = Environment.GetCommandLineArgs().Skip(1).ToList();
            if (!argsList.Contains("--resume")) argsList.Add("--resume");
            
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EikonConfigurator");
            try { Directory.CreateDirectory(logDir); } catch { }
            string logPath = Path.Combine(logDir, "OS_ClosedLoop.log");
            if (!argsList.Any(a => a.Contains("--log-path"))) 
            {
                argsList.Add("--log-path"); argsList.Add(logPath);
            }

            // FIX: Build the command without nested quotes that break schtasks and RunOnce
            // Quote only the exe path; arguments with spaces get individual quotes
            var sanitizedArgs = argsList.Select(a => a.Contains(" ") ? $"\"{a}\"" : a);
            string command = $"\"{myPath}\" {string.Join(" ", sanitizedArgs)}";
            Console.WriteLine($"Registering Resilience Task: {command}");

            try
            {
                // FIX: Remove the schtasks approach entirely.
                // - /SC ONLOGON + /RU SYSTEM + /IT is a broken combination: SYSTEM is never
                //   "interactively logged on", so the /IT flag can silently prevent execution.
                // - Having BOTH schtasks AND RunOnce creates a race condition (double execution).
                // 
                // Strategy: Use ONLY RunOnce with the "*" prefix.
                // The "*" prefix means "run even in Safe Mode", ensuring maximum resilience.
                // RunOnce runs in the context of the logging-on user, which is exactly what we need.
                ExecuteSystemCommand("schtasks", $"/Delete /TN \"{ResumeTaskName}\" /F");
                
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    if (key != null) key.SetValue("*" + ResumeTaskName, command);
                }
            }
            catch (Exception ex) { Console.WriteLine($"Persistence Warning: {ex.Message}"); }
        }

        static void ConfigureHardware()
        {
            var config = LoadConfig();
            if (config.networkAdapaters != null) foreach (var netConfig in config.networkAdapaters) ApplyNetworkSettings(netConfig);
            ConfigureDeviceHardening();
        }

        static void ConfigureDeviceHardening()
        {
            Console.WriteLine("Configuring Device Hardening...");
            RunPowerShell("Get-NetAdapter | Where-Object { $_.MediaType -like '*802.3*' -or $_.Name -like '*Wi-Fi*' -or $_.Description -like '*Wireless*' } | Set-NetIPInterface -InterfaceMetric 500 -ErrorAction SilentlyContinue");
            RunPowerShell("Get-PnpDevice -Class Bluetooth -Status OK | Disable-PnpDevice -Confirm:$false -ErrorAction SilentlyContinue");
            RunPowerShell("Get-PnpDevice -FriendlyName '*High Definition Audio*' -Status OK | Disable-PnpDevice -Confirm:$false -ErrorAction SilentlyContinue");
        }

        static void ApplyNetworkSettings(NetworkAdapterConfig config)
        {
            Console.WriteLine($"Looking for adapter matching: {string.Join(",", config.identification.descriptionKeywords ?? new List<string>())}...");
            var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionStatus=2 OR NetConnectionStatus=7 OR NetConnectionStatus=0");
            
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (config.identification.descriptionKeywords?.Any(k => name.Contains(k, StringComparison.InvariantCultureIgnoreCase)) ?? false)
                {
                    string oldId = obj["NetConnectionID"]?.ToString();
                    string finalName = oldId;
                    if (!string.IsNullOrEmpty(config.settings.newName) && oldId != config.settings.newName)
                    {
                        obj["NetConnectionID"] = config.settings.newName;
                        try { obj.Put(); finalName = config.settings.newName; } catch {}
                    }

                    if (finalName.Equals("ImageLink", StringComparison.OrdinalIgnoreCase))
                    {
                        if (config.settings.advanced == null) config.settings.advanced = new Dictionary<string, string>();
                        config.settings.advanced["*JumboPacket"] = "9014";
                        config.settings.advanced["*ReceiveBuffers"] = "2048";
                        config.settings.advanced["*TransmitBuffers"] = "2048";
                        config.settings.advanced["*EEE"] = "0";
                        RunPowerShell($"Set-NetIPInterface -InterfaceAlias '{finalName}' -InterfaceMetric 1");
                    }

                    if (config.settings.ipv4?.mode?.ToLower() == "static")
                        RunNetSh($"interface ip set address \"{finalName}\" static {config.settings.ipv4.ipAddress} {config.settings.ipv4.subnetMask} {config.settings.ipv4.gateway}");
                    else if (config.settings.ipv4?.mode?.ToLower() == "dhcp")
                        RunNetSh($"interface ip set address \"{finalName}\" dhcp");

                    if (config.settings.advanced != null && config.settings.advanced.Count > 0)
                    {
                        string pnpInstance = obj["PNPDeviceID"].ToString();
                        string driverKeyPath = GetDriverKeyPath(pnpInstance);
                        if (!string.IsNullOrEmpty(driverKeyPath))
                        {
                            foreach (var advSetting in config.settings.advanced) Registry.SetValue(driverKeyPath, advSetting.Key, advSetting.Value);
                            RunNetSh($"interface set interface \"{finalName}\" disable");
                            System.Threading.Thread.Sleep(2000);
                            RunNetSh($"interface set interface \"{finalName}\" enable");
                        }
                    }
                }
            }
        }

        static string GetDriverKeyPath(string pnpInstanceId)
        {
             using (var key = KeyOpener(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Enum\{pnpInstanceId}", false))
             {
                 string driver = key?.GetValue("Driver")?.ToString();
                 if (!string.IsNullOrEmpty(driver)) return $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{driver}";
             }
             return null;
        }

        /// <summary>
        /// Copies startup files defined in hardware-config.json to their target paths.
        /// Runs before all configuration steps. Source paths are relative to the EikonConfigurator directory.
        /// </summary>
        static void DeployStartupFiles()
        {
            var config = LoadConfig();
            if (config.startupFiles == null || config.startupFiles.Count == 0) return;

            Console.WriteLine("\n--- Deploying Startup Files ---");
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            foreach (var file in config.startupFiles)
            {
                try
                {
                    string sourcePath = Path.IsPathRooted(file.source) 
                        ? file.source 
                        : Path.Combine(baseDir, file.source);
                    string destPath = file.destination;

                    string desc = string.IsNullOrEmpty(file.description) ? "" : $" ({file.description})";
                    Console.WriteLine($" -> Copying: {sourcePath} => {destPath}{desc}");

                    if (!File.Exists(sourcePath))
                    {
                        Console.WriteLine($" !! WARNING: Source file not found: {sourcePath}");
                        continue;
                    }

                    string destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                        Console.WriteLine($"    Created directory: {destDir}");
                    }

                    if (IsDryRun)
                    {
                        Console.WriteLine($"    [DRY-RUN] Would copy {sourcePath} -> {destPath}");
                    }
                    else
                    {
                        File.Copy(sourcePath, destPath, true);
                        Console.WriteLine($"    OK: {Path.GetFileName(file.source)} deployed.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" !! ERROR deploying {file.source}: {ex.Message}");
                }
            }
        }

        internal static void ConfigureShellLauncher()
        {
            Console.WriteLine("Configuring Shell Launcher...");
            var config = LoadConfig();
            string userSid = GetUserSid(TargetUser);
            if (string.IsNullOrEmpty(userSid)) 
            {
                Console.WriteLine($" !! CRITICAL: Could not resolve SID for {TargetUser}. Shell Launcher NOT configured.");
                return;
            }
            Console.WriteLine($" -> Resolved {TargetUser} SID: {userSid}");

            string customShell = config.software?.shell?.customShellPath ?? "explorer.exe";
            Console.WriteLine($" -> Mapping Shell for {TargetUser} -> {customShell}");

            // =====================================================================
            // Actual WMI schema discovered from target machine (Win10 LTSC 17763):
            //
            //   SetEnabled(Enabled:Boolean)                          → void (no ReturnValue)
            //   SetDefaultShell(DefaultAction:SInt32, Shell:String)  → void (no ReturnValue)
            //   SetCustomShell(CustomReturnCodes:SInt32,             → throws on failure
            //                  CustomReturnCodesAction:SInt32,
            //                  DefaultAction:SInt32,
            //                  Shell:String,
            //                  Sid:String)
            //   IsEnabled()  → returns Enabled:Boolean
            //   GetCustomShell(Sid:String) → returns shell info
            //
            // Action codes: 0=RestartShell, 1=RestartDevice, 2=Shutdown, 3=DoNothing
            // =====================================================================

            try
            {
                var scope = new ManagementScope(@"\\.\root\standardcimv2\embedded");
                scope.Connect();
                var shellClass = new ManagementClass(scope, new ManagementPath("WESL_UserSetting"), null);

                // Step 1: Enable Shell Launcher
                // SetEnabled is void — no ReturnValue to check. Throws on failure.
                Console.WriteLine(" -> Calling SetEnabled(true)...");
                var enableParams = shellClass.GetMethodParameters("SetEnabled");
                enableParams["Enabled"] = true;
                shellClass.InvokeMethod("SetEnabled", enableParams, null);
                Console.WriteLine(" -> Shell Launcher ENABLED successfully.");

                // Step 2: Set default shell (fallback for all other users)
                // SetDefaultShell(DefaultAction:SInt32, Shell:String) — void
                Console.WriteLine(" -> Calling SetDefaultShell(explorer.exe, action=0)...");
                var defaultParams = shellClass.GetMethodParameters("SetDefaultShell");
                defaultParams["Shell"] = "explorer.exe";
                defaultParams["DefaultAction"] = 0; // RestartShell
                shellClass.InvokeMethod("SetDefaultShell", defaultParams, null);
                Console.WriteLine(" -> Default shell set to explorer.exe.");

                // Step 3: Map custom shell for target user
                // SetCustomShell(CustomReturnCodes:SInt32, CustomReturnCodesAction:SInt32,
                //                DefaultAction:SInt32, Shell:String, Sid:String)
                // The CustomReturnCodes/Action params are optional mapping arrays.
                // We try multiple approaches since the exact type contract varies by build.
                Console.WriteLine($" -> Calling SetCustomShell for SID {userSid}...");
                
                bool customShellSet = false;
                string[] approaches = { "only_required", "null_optional", "simple_overload" };
                
                foreach (string approach in approaches)
                {
                    if (customShellSet) break;
                    try
                    {
                        Console.WriteLine($"    Trying approach: {approach}...");
                        
                        if (approach == "only_required")
                        {
                            // Approach 1: Set only the 3 required params, skip optional ones
                            var p = shellClass.GetMethodParameters("SetCustomShell");
                            p["Sid"] = userSid;
                            p["Shell"] = customShell;
                            p["DefaultAction"] = 0;
                            shellClass.InvokeMethod("SetCustomShell", p, null);
                        }
                        else if (approach == "null_optional")
                        {
                            // Approach 2: Set optional params to null explicitly
                            var p = shellClass.GetMethodParameters("SetCustomShell");
                            p["Sid"] = userSid;
                            p["Shell"] = customShell;
                            p["DefaultAction"] = 0;
                            p["CustomReturnCodes"] = null;
                            p["CustomReturnCodesAction"] = null;
                            shellClass.InvokeMethod("SetCustomShell", p, null);
                        }
                        else if (approach == "simple_overload")
                        {
                            // Approach 3: Use positional args via the simple overload
                            // Order from schema: CustomReturnCodes, CustomReturnCodesAction, 
                            //                    DefaultAction, Shell, Sid
                            shellClass.InvokeMethod("SetCustomShell", 
                                new object[] { null, null, 0, customShell, userSid });
                        }
                        
                        customShellSet = true;
                        Console.WriteLine($" -> Custom shell mapped successfully (approach: {approach}).");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    {approach} failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                
                if (!customShellSet)
                    Console.WriteLine(" !! CRITICAL: All SetCustomShell approaches failed!");

                // Step 4: Verify using IsEnabled and GetCustomShell
                Console.WriteLine(" -> Verifying configuration...");
                var isEnabledResult = shellClass.InvokeMethod("IsEnabled", null, null);
                if (isEnabledResult != null)
                {
                    bool enabled = (bool)(isEnabledResult["Enabled"] ?? false);
                    Console.WriteLine($"    IsEnabled = {enabled}");
                }

                var getParams = shellClass.GetMethodParameters("GetCustomShell");
                getParams["Sid"] = userSid;
                var getResult = shellClass.InvokeMethod("GetCustomShell", getParams, null);
                if (getResult != null)
                {
                    string verifyShell = getResult["Shell"]?.ToString() ?? "(null)";
                    Console.WriteLine($"    GetCustomShell: Shell={verifyShell}");
                    Console.WriteLine(" -> Shell Launcher verification PASSED.");
                }
                else
                {
                    Console.WriteLine(" !! WARNING: GetCustomShell returned null.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" !! SHELL LAUNCHER ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"    Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        }

        /// <summary>
        /// Configures kiosk startup for EikonUser only:
        /// 1. Creates a scheduled task that runs KioskLauncher.vbs at EikonUser logon
        /// 2. Creates a desktop shortcut for manual restart after crash
        /// EikonUser keeps explorer.exe as shell but is locked down by registry 
        /// policies (NoDrives, NoRun, AppLocker, Keyboard Filter).
        /// Uses Task Scheduler instead of Run keys to avoid fragile offline hive mounting.
        /// </summary>
        internal static void ConfigureKioskStartup()
        {
            Console.WriteLine("Configuring Clinical Startup (Scheduled Task + Desktop shortcut)...");
            var config = LoadConfig();
            string kioskVbs = config.software?.shell?.customShellPath ?? @"C:\Windows\System32\wscript.exe D:\GVP-Pro\App\KioskLauncher.vbs";
            
            // Parse out the VBS path for the shortcut target
            // customShellPath format: "C:\Windows\System32\wscript.exe D:\GVP-Pro\App\KioskLauncher.vbs"
            string wscriptExe = @"C:\Windows\System32\wscript.exe";
            string vbsPath = @"D:\GVP-Pro\App\KioskLauncher.vbs";
            if (kioskVbs.Contains("wscript.exe"))
            {
                int idx = kioskVbs.IndexOf("wscript.exe", StringComparison.OrdinalIgnoreCase) + "wscript.exe".Length;
                vbsPath = kioskVbs.Substring(idx).Trim();
            }

            // --- 1. Create scheduled task for EikonUser logon only ---
            // Task Scheduler is machine-level (C:\Windows\System32\Tasks) — no hive mounting needed.
            // Trigger: AtLogOn scoped to EikonUser. EikonAdmin is not affected.
            // Principal: Interactive session so the VBS script is visible on screen.
            Console.WriteLine($" -> Creating scheduled task 'EikonKioskLauncher' for {TargetUser}");
            string taskScript = $@"
                $trigger = New-ScheduledTaskTrigger -AtLogOn -User '{TargetUser}'
                $action = New-ScheduledTaskAction -Execute '{wscriptExe}' -Argument '""{vbsPath}""' -WorkingDirectory '{Path.GetDirectoryName(vbsPath)}'
                $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
                $principal = New-ScheduledTaskPrincipal -UserId '{TargetUser}' -LogonType Interactive -RunLevel Limited
                Register-ScheduledTask -TaskName 'EikonKioskLauncher' -Trigger $trigger -Action $action -Settings $settings -Principal $principal -Force | Out-Null
                Write-Host '    OK: Scheduled task created for {TargetUser}'
            ";
            RunPowerShell(taskScript);

            // --- 2. Create Desktop shortcut for EikonUser ---
            // Determine EikonUser's Desktop path
            string desktopPath = $@"C:\Users\{TargetUser}\Desktop";
            if (!Directory.Exists(desktopPath))
            {
                // User profile may not exist yet (first login creates it).
                // Use Default user's Desktop as fallback — gets copied on first login.
                desktopPath = @"C:\Users\Default\Desktop";
                if (!Directory.Exists(desktopPath)) Directory.CreateDirectory(desktopPath);
            }

            string shortcutPath = Path.Combine(desktopPath, "Start GVP-Pro.lnk");
            Console.WriteLine($" -> Creating desktop shortcut: {shortcutPath}");

            // Use PowerShell COM to create a proper .lnk shortcut
            string psScript = $@"
                $ws = New-Object -ComObject WScript.Shell
                $sc = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}')
                $sc.TargetPath = '{wscriptExe}'
                $sc.Arguments = '""{vbsPath}""'
                $sc.WorkingDirectory = '{Path.GetDirectoryName(vbsPath)}'
                $sc.Description = 'Start GVP-Pro Kiosk Application'
                $sc.Save()
                Write-Host 'Shortcut created.'
            ";
            RunPowerShell(psScript);
            Console.WriteLine("    OK: Desktop shortcut created.");

            Console.WriteLine(" -> Kiosk Startup configured successfully.");
        }

        static void ConfigureUser()
        {
            Console.WriteLine("Configuring Users and Account Policies...");
            
            Console.WriteLine(" -> Applying Account Policies (minpwlen:14, maxpwage:unlimited, lockout:5)");
            ExecuteSystemCommand("net", "accounts /minpwlen:14 /maxpwage:unlimited /minpwage:0 /uniquepw:0 /lockoutthreshold:5 /lockoutduration:15 /lockoutwindow:15");
            
            _randomPassword = GenerateSecurePassword(32);
            
            using (var ctx = new PrincipalContext(ContextType.Machine))
            {
                Console.WriteLine($" -> Creating/Updating primary user: {TargetUser}");
                var user = UserPrincipal.FindByIdentity(ctx, TargetUser) ?? new UserPrincipal(ctx) { Name = TargetUser, PasswordNeverExpires = true };
                user.SetPassword(_randomPassword);
                user.Save();

                Console.WriteLine(" -> Creating/Updating admin user: EikonAdmin");
                var eikonAdmin = UserPrincipal.FindByIdentity(ctx, "EikonAdmin") ?? new UserPrincipal(ctx, "EikonAdmin", "N1viH@rd0$Secure!", true) { PasswordNeverExpires = true };
                eikonAdmin.SetPassword("N1viH@rd0$Secure!"); eikonAdmin.Enabled = true; eikonAdmin.Save();

                Console.WriteLine(" -> Sanitizing Administrators Group...");
                var admins = GroupPrincipal.FindByIdentity(ctx, "Administrators");
                if (admins != null)
                {
                    if (!eikonAdmin.IsMemberOf(admins)) { Console.WriteLine("    + Adding EikonAdmin to Administrators"); admins.Members.Add(eikonAdmin); }
                    if (!user.IsMemberOf(admins)) { Console.WriteLine($"    + Adding {TargetUser} to Administrators"); admins.Members.Add(user); }
                    admins.Save();

                    foreach (var member in admins.Members.ToList())
                    {
                        if (member.SamAccountName.Equals("EikonAdmin", StringComparison.OrdinalIgnoreCase) || member.SamAccountName.Equals(TargetUser, StringComparison.OrdinalIgnoreCase) || member.Name.Contains("Domain Admins")) continue;
                        
                        if (member.Sid.Value.EndsWith("-500")) 
                        { 
                            Console.WriteLine("    - Disabling Built-in Administrator (SID-500)");
                            if (member is UserPrincipal up) { up.Enabled = false; up.Save(); } 
                            continue; 
                        }
                        try 
                        { 
                            Console.WriteLine($"    - Removing unauthorized admin: {member.SamAccountName}");
                            admins.Members.Remove(member); admins.Save(); 
                            if (member is UserPrincipal eu) { eu.Enabled = false; eu.Save(); } 
                        } catch {}
                    }
                }
                try { var guest = UserPrincipal.FindByIdentity(ctx, "Guest"); if(guest!=null) { Console.WriteLine(" -> Disabling Guest Account"); guest.Enabled = false; guest.Save(); } } catch {}

                Console.WriteLine($" -> Configuring AutoLogon for {TargetUser}");
                using (var key = KeyCreator(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                {
                    SafeSet(key, "DefaultDomainName", Environment.MachineName);
                    SafeSet(key, "DefaultUserName", TargetUser);
                    SafeSet(key, "DefaultPassword", _randomPassword);
                    SafeSet(key, "AutoAdminLogon", "1");
                    SafeSet(key, "ForceAutoLogon", "1");
                }
            }
        }
        
        static string GenerateSecurePassword(int length)
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[length]; rng.GetBytes(bytes);
                return Convert.ToBase64String(bytes).Substring(0, length) + "1!";
            }
        }

        static void ConfigureOsHardening()
        {
            Console.WriteLine(" -> Applying Security Privilege Rights (SecEdit)");
            string inf = "[Unicode]\r\nUnicode=yes\r\n[Version]\r\nsignature=\"$CHICAGO$\"\r\nRevision=1\r\n[Privilege Rights]\r\nSeDebugPrivilege = *S-1-5-32-544\r\nSeSecurityPrivilege = *S-1-5-32-544\r\nSeTimeZonePrivilege = *S-1-5-32-544,*S-1-5-32-545\r\nSeSystemtimePrivilege = *S-1-5-32-544,*S-1-5-19,*S-1-5-32-545\r\nSeServiceLogonRight = *S-1-5-80-0\r\nSeDenyInteractiveLogonRight = *S-1-5-32-546\r\nSeIncreaseQuotaPrivilege = *S-1-5-32-544,*S-1-5-20,*S-1-5-19\r\nSeInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-545\r\nSeNetworkLogonRight = *S-1-5-32-544,*S-1-5-32-545,*S-1-5-32-551\r\nSeDenyServiceLogonRight = *S-1-5-32-546\r\n";
            string tempInf = Path.Combine(Path.GetTempPath(), "sec_harden.inf");
            string tempDb = Path.Combine(Path.GetTempPath(), "sec_harden.sdb");
            File.WriteAllText(tempInf, inf);
            ExecuteSystemCommand("secedit", $"/configure /db \"{tempDb}\" /cfg \"{tempInf}\" /quiet");

            // FIX: Removed the "minpwage:1" command from here entirely.
            
            Console.WriteLine(" -> Temporarily Disabling LUA for headless execution");
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 0, RegistryValueKind.DWord);
            
            Console.WriteLine(" -> Hiding Last User Name on Logon Screen");
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DontDisplayLastUserName", 1, RegistryValueKind.DWord);
            
            Console.WriteLine(" -> Enforcing Ultimate Performance Power Plan");
            ExecuteSystemCommand("powercfg", "-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61"); 
            ExecuteSystemCommand("powercfg", "-setactive e9a42b02-d5df-448d-aa00-03f14749eb61");
            
            Console.WriteLine(" -> Restricting HTMLHelp Allowed Zone");
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\HTMLHelp\1.x\ItssRestrictions", "MaxAllowedZone", 0, RegistryValueKind.DWord);
        }

static void SealInstallation() 
        {
            Console.WriteLine("\n--- Initiating Seal Logic ---");
            string winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            
            // FIX: Temporarily enable LanmanServer so local account management doesn't crash
            ExecuteSystemCommand("sc", "config LanmanServer start= demand");
            ExecuteSystemCommand("net", "start LanmanServer /y");

            if (_uwfFailedPendingReboot)
            {
                Console.WriteLine("!!! PENDING REBOOT DETECTED (UWF/Features) !!!");
                Console.WriteLine("Aborting final 'Seal' process. The Installer Account will remain ACTIVE.");
                Console.WriteLine("Configuring AutoLogon for current Administrator to resume installation after reboot...");
                
                // FIX (ROOT CAUSE): Disable the Windows 10 2004+ PasswordLess feature BEFORE 
                // setting AutoLogon. Without this, Windows silently ignores the Winlogon 
                // AutoAdminLogon keys and shows the login screen instead.
                SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device", "DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
                
                // FIX: Temporarily ensure DontDisplayLastUserName doesn't interfere with AutoLogon.
                // On some Win10 LTSC builds, DontDisplayLastUserName=1 + AutoAdminLogon=1 shows a 
                // blank login screen where the user must type a username, defeating AutoLogon.
                SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DontDisplayLastUserName", 0, RegistryValueKind.DWord);

                SetRegistryValue(Registry.LocalMachine, winlogonPath, "AutoAdminLogon", "1");
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultUserName", "EikonAdmin");
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultDomainName", Environment.MachineName);
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultPassword", "N1viH@rd0$Secure!");
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "ForceAutoLogon", "1");
                SetRegistryValue(Registry.LocalMachine, winlogonPath, "LastUsedUsername", "EikonAdmin");
                SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\UserSwitch", "Enabled", 0, RegistryValueKind.DWord);

                SetResilienceRunOnce(); 
                Console.WriteLine();
                Console.WriteLine("============================================================");
                Console.WriteLine("  REBOOTING: Windows features require a restart.");
                Console.WriteLine("  The installer will resume automatically after reboot.");
                Console.WriteLine("  Restarting in 30 seconds...");
                Console.WriteLine("============================================================");
                ExecuteSystemCommand("shutdown", "/r /t 30 /f /c \"Eikon Installer: Restarting to complete Windows feature installation. Configuration will resume automatically.\"");
                Environment.Exit(0); 
                return;
            }

            Console.WriteLine("Installation Phase Complete. Finalizing Kiosk State...");

            // 1. Cleanup Resilience completely to prevent infinite loops
            Console.WriteLine("Removing Post-Reboot Resume Tasks and Registry Keys...");
            // Delete any stale scheduled task (from previous versions or manual runs)
            ExecuteSystemCommand("schtasks", $"/Delete /TN \"{ResumeTaskName}\" /F");
            try 
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    if (key != null) key.DeleteValue("*" + ResumeTaskName, false);
                }
            } catch { }
            // Also clean up the state tracking key to prevent issues on re-runs
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\EikonConfigurator", false); } catch { }

            string finalUserPassword = GenerateSecurePassword(32);
            bool keepAdmin = _args.Contains("--keep-admin");
            try 
            {
                // 1. FORCE override any lingering minimum password age policies
                ExecuteSystemCommand("net", "accounts /minpwage:0");

                using (var ctx = new PrincipalContext(ContextType.Machine))
                {
                    Console.WriteLine($"Resetting {TargetUser} password and enabling account...");
                    var user = UserPrincipal.FindByIdentity(ctx, TargetUser);
                    if (user != null) { user.SetPassword(finalUserPassword); user.Enabled = true; user.Save(); }
                }
                
                // 2. Force Windows 10 to respect standard AutoLogon
                SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device", "DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);

                if (keepAdmin)
                {
                    // VM / test mode: keep AutoLogon as EikonAdmin so we get an admin desktop
                    Console.WriteLine("Setting final AutoLogon credentials for EikonAdmin (--keep-admin)...");
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultDomainName", Environment.MachineName, RegistryValueKind.String);
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultUserName", "EikonAdmin", RegistryValueKind.String);
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultPassword", "N1viH@rd0$Secure!", RegistryValueKind.String);
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "AutoAdminLogon", "1", RegistryValueKind.String);
                    // ForceAutoLogon=0: Auto-logs EikonAdmin on boot, but Sign Out shows
                    // login screen so tester can switch to EikonUser or EikonAdmin.
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "ForceAutoLogon", "0", RegistryValueKind.String);
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "LastUsedUsername", "EikonAdmin", RegistryValueKind.String);
                }
                else
                {
                    // Production: switch AutoLogon to EikonUser (kiosk user)
                    Console.WriteLine($"Setting final AutoLogon credentials for {TargetUser}...");
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultDomainName", Environment.MachineName, RegistryValueKind.String); 
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultUserName", TargetUser, RegistryValueKind.String);
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "DefaultPassword", finalUserPassword, RegistryValueKind.String);
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "AutoAdminLogon", "1", RegistryValueKind.String);
                    // ForceAutoLogon=0: Auto-logs EikonUser on boot/restart, but Sign Out shows
                    // the login screen so a technician can log in as EikonAdmin when needed.
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "ForceAutoLogon", "0", RegistryValueKind.String);
                    SetRegistryValue(Registry.LocalMachine, winlogonPath, "LastUsedUsername", TargetUser, RegistryValueKind.String);
                }
            } 
            catch (Exception ex) 
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"CRITICAL ERROR setting final credentials: {ex.Message}");
                Console.ResetColor();
            }

            if (keepAdmin)
            {
                Console.WriteLine("Keeping EikonAdmin account ACTIVE (--keep-admin flag).");
            }
            else
            {
                // EikonAdmin stays ACTIVE for maintenance access (AnyDesk / Ctrl+Alt+Del → Sign Out → login).
                // Security: protected by strong password + DontDisplayLastUserName (username not shown on login screen).
                Console.WriteLine("EikonAdmin account remains ACTIVE for maintenance access.");
            }
            
            // FIX: Re-disable LanmanServer to comply with security requirements
            Console.WriteLine("Re-disabling LanmanServer (Server) service...");
            ServiceStopper("LanmanServer");
            ExecuteSystemCommand("sc", "config LanmanServer start= disabled");

            ConfigureAppLockerFinal();
            
            Console.WriteLine("Re-enabling UAC (EnableLUA & ConsentPromptBehaviorAdmin)...");
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 1, RegistryValueKind.DWord);
            
            // FIX: Re-enable DontDisplayLastUserName for final kiosk security (was temporarily 
            // disabled during intermediate reboot to prevent AutoLogon interference)
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DontDisplayLastUserName", 1, RegistryValueKind.DWord);

            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("  EIKON IMAGING SOFTWARE INSTALLATION COMPLETE!");
            Console.WriteLine("  The PC will shut down in 30 seconds.");
            Console.WriteLine("  Power the PC on 30 seconds after power off.");
            Console.WriteLine("  The application will launch automatically on next boot.");
            Console.WriteLine("============================================================");
            ExecuteSystemCommand("shutdown", "/s /t 30 /f /c \"Eikon Imaging Software Installation is complete. Shutting down the PC. Power the PC on 30 seconds after power off.\"");
        }

        static void ConfigureUWF()
        {
            Console.WriteLine("Configuring UWF...");
            var config = LoadConfig();
            if (config.uwf != null && config.uwf.enabled)
            {
                string uwfMgr = Path.Combine(Environment.SystemDirectory, "uwfmgr.exe");
                
                // FIX: Check for DISM pending reboot (features installed but not yet active)
                // in addition to uwfmgr.exe existence. After DISM enables UWF, the exe may 
                // exist but not function until after reboot.
                if (!File.Exists(uwfMgr)) 
                { 
                    Console.WriteLine(" -> uwfmgr.exe not found. UWF feature requires reboot.");
                    _uwfFailedPendingReboot = true; 
                    return; 
                }
                
                // Also check if a reboot is pending from DISM feature installation
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", false))
                    {
                        if (key != null) 
                        { 
                            Console.WriteLine(" -> CBS RebootPending detected. Deferring UWF configuration.");
                            _uwfFailedPendingReboot = true; 
                            return; 
                        }
                    }
                } catch { }
                
                ExecuteSystemCommand(uwfMgr, "overlay set-warningthreshold 1024");
                ExecuteSystemCommand(uwfMgr, "volume protect C:");
                ExecuteSystemCommand(uwfMgr, $"overlay set-size {config.uwf.overlaySizeMb}");
                
                var files = config.uwf.fileExclusions ?? new List<string>();
                string[] reqFiles = { @"C:\ProgramData\Microsoft\Crypto", @"C:\ProgramData\Microsoft\wlansvc", @"C:\Windows\WindowsUpdate.log" };
                foreach(var f in reqFiles) if(!files.Contains(f)) files.Add(f);
                foreach(var f in files) ExecuteSystemCommand(uwfMgr, $"file add-exclusion \"{f}\"");
                
                var regs = config.uwf.registryExclusions ?? new List<string>();
                string[] reqReg = { @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\ComputerName", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones" };
                foreach(var r in reqReg) if(!regs.Contains(r)) regs.Add(r);
                foreach(var r in regs) ExecuteSystemCommand(uwfMgr, $"registry add-exclusion \"{r}\"");
                
                ExecuteSystemCommand(uwfMgr, "filter enable");
            }
        }

        internal static void ConfigureAppLockerFinal()
        {
            var config = LoadConfig();
            try { ExecuteSystemCommand("sc", "config AppIDSvc start= auto"); ExecuteSystemCommand("sc", "start AppIDSvc"); } catch {}
            List<string> paths = new List<string> { AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\') + @"\*" };
            if (config.software?.appLocker?.allowedPaths != null) paths.AddRange(config.software.appLocker.allowedPaths);

            // Look up EikonAdmin's specific SID for the bypass rule.
            // Cannot use S-1-5-32-544 (Administrators) because EikonUser is also an admin.
            string adminSid = GetUserSid("EikonAdmin") ?? "S-1-5-32-544";
            Console.WriteLine($" -> EikonAdmin SID for AppLocker bypass: {adminSid}");

            StringBuilder sb = new StringBuilder($@"<AppLockerPolicy Version=""1""><RuleCollection Type=""Exe"" EnforcementMode=""Enabled"">
<FilePathRule Id=""fd686d83-a829-4351-8ff4-27c7de5755d2"" Name=""EikonAdmin: All files"" Description=""Allow EikonAdmin to run any executable"" UserOrGroupSid=""{adminSid}"" Action=""Allow""><Conditions><FilePathCondition Path=""*"" /></Conditions></FilePathRule>
<FilePathRule Id=""921cc481-6e17-4653-8f75-050b80acca20"" Name=""Program Files"" Description="""" UserOrGroupSid=""S-1-1-0"" Action=""Allow""><Conditions><FilePathCondition Path=""%PROGRAMFILES%\*"" /></Conditions></FilePathRule>
<FilePathRule Id=""a61c8b2c-a319-4cd0-9690-d2177cad7b51"" Name=""Windows"" Description="""" UserOrGroupSid=""S-1-1-0"" Action=""Allow""><Conditions><FilePathCondition Path=""%WINDIR%\*"" /></Conditions></FilePathRule>");
            foreach (string p in paths) { string sp = System.Security.SecurityElement.Escape(p); sb.Append($@"<FilePathRule Id=""{Guid.NewGuid()}"" Name=""Allow: {sp}"" Description="""" UserOrGroupSid=""S-1-1-0"" Action=""Allow""><Conditions><FilePathCondition Path=""{sp}"" /></Conditions></FilePathRule>"); }
            sb.Append(@"</RuleCollection></AppLockerPolicy>");

            string tempFile = Path.Combine(Path.GetTempPath(), "applocker.xml");
            File.WriteAllText(tempFile, sb.ToString());
            RunPowerShell($"Set-AppLockerPolicy -XmlPolicy '{tempFile}' -Merge");
        }

        static void ConfigureCrashDumps()
        {
            Console.WriteLine("Configuring Application Crash Dumps...");
            var config = LoadConfig();
            if (config.software?.crashDumps != null && config.software.crashDumps.enabled)
            {
                string werKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
                string dumpDir = config.software.crashDumps.dumpFolder ?? @"E:\CrashDumps";
                var apps = config.software.crashDumps.targetProcesses ?? new List<string>();
                
                Console.WriteLine($" -> Enabling LocalDumps at {dumpDir} for {apps.Count} specific processes.");
                foreach (var app in apps) 
                {
                    using (var k = KeyCreator(Registry.LocalMachine, $@"{werKey}\{app}"))
                    { SafeSet(k, "DumpFolder", dumpDir); SafeSet(k, "DumpCount", 5, RegistryValueKind.DWord); SafeSet(k, "DumpType", 2, RegistryValueKind.DWord); }
                }
            }
        }

        /// <summary>
        /// Installs printer drivers and configures network printers from hardware-config.json.
        /// Runs after Defender exclusions and before UWF is enabled (writes must persist).
        /// </summary>
        internal static void ConfigurePrinters()
        {
            Console.WriteLine("Configuring Printers...");
            var config = LoadConfig();
            var printers = config.printers;

            if (printers == null || printers.Count == 0)
            {
                Console.WriteLine(" -> No printers defined in hardware-config.json. Skipping.");
                return;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            foreach (var printer in printers)
            {
                try
                {
                    Console.WriteLine($"\n -> Configuring printer: {printer.name}");

                    // Step 1: Install the driver if driverSource is specified
                    if (!string.IsNullOrEmpty(printer.driverSource))
                    {
                        string driverPath = Path.IsPathRooted(printer.driverSource)
                            ? printer.driverSource
                            : Path.Combine(baseDir, printer.driverSource);

                        if (File.Exists(driverPath))
                        {
                            string ext = Path.GetExtension(driverPath).ToLowerInvariant();

                            if (ext == ".inf")
                            {
                                Console.WriteLine($"    Installing INF driver: {driverPath}");
                                ExecuteSystemCommand("pnputil.exe", $"/add-driver \"{driverPath}\" /install");
                                // Also stage the driver via rundll32 so it appears in the driver list
                                RunPowerShell($"pnputil.exe /add-driver '{driverPath}' /install");
                            }
                            else if (ext == ".msi")
                            {
                                Console.WriteLine($"    Installing MSI driver: {driverPath}");
                                ExecuteSystemCommand("msiexec.exe", $"/i \"{driverPath}\" /quiet /norestart ALLUSERS=1");
                            }
                            else if (ext == ".exe")
                            {
                                Console.WriteLine($"    Installing EXE driver: {driverPath}");
                                ExecuteSystemCommand(driverPath, "/quiet /norestart");
                            }
                            else
                            {
                                Console.WriteLine($"    !! Unknown driver format: {ext}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"    !! WARNING: Driver source not found: {driverPath}");
                        }
                    }

                    // Step 2: Add the printer driver to Windows (must match exactly what the INF/MSI installed)
                    if (!string.IsNullOrEmpty(printer.driverName))
                    {
                        Console.WriteLine($"    Adding printer driver: {printer.driverName}");
                        RunPowerShell($"Add-PrinterDriver -Name '{printer.driverName}' -ErrorAction Stop");
                    }

                    // Step 3: Create a printer port
                    string portName = null;
                    string portType = (printer.portType ?? "TCP").ToUpperInvariant();

                    if (portType == "TCP" && !string.IsNullOrEmpty(printer.ipAddress))
                    {
                        portName = $"TCP_{printer.ipAddress}";
                        Console.WriteLine($"    Creating TCP port: {portName} -> {printer.ipAddress}");
                        RunPowerShell($@"
                            $existing = Get-PrinterPort -Name '{portName}' -ErrorAction SilentlyContinue
                            if (-not $existing) {{
                                Add-PrinterPort -Name '{portName}' -PrinterHostAddress '{printer.ipAddress}'
                                Write-Host '    Port created.'
                            }} else {{
                                Write-Host '    Port already exists.'
                            }}
                        ");
                    }
                    else if (portType == "USB")
                    {
                        portName = "USB001";
                        Console.WriteLine($"    Using USB port: {portName}");
                    }

                    // Step 4: Add the printer queue
                    if (!string.IsNullOrEmpty(printer.driverName) && !string.IsNullOrEmpty(portName))
                    {
                        Console.WriteLine($"    Adding printer: {printer.name}");
                        RunPowerShell($@"
                            $existing = Get-Printer -Name '{printer.name}' -ErrorAction SilentlyContinue
                            if (-not $existing) {{
                                Add-Printer -Name '{printer.name}' -DriverName '{printer.driverName}' -PortName '{portName}'
                                Write-Host '    Printer added.'
                            }} else {{
                                Write-Host '    Printer already exists.'
                            }}
                        ");
                    }

                    // Step 5: Set as default if requested
                    if (printer.isDefault)
                    {
                        Console.WriteLine($"    Setting as default printer: {printer.name}");
                        RunPowerShell($@"
                            $p = Get-CimInstance -ClassName Win32_Printer -Filter ""Name='{printer.name}'""
                            if ($p) {{ Invoke-CimMethod -InputObject $p -MethodName SetDefaultPrinter | Out-Null; Write-Host '    Default printer set.' }}
                            else {{ Write-Host '    !! Printer not found for default.' }}
                        ");
                    }

                    Console.WriteLine($"    OK: {printer.name} configured.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    !! ERROR configuring printer '{printer.name}': {ex.Message}");
                }
            }
        }

        internal static void ConfigureDefender()
        {
            Console.WriteLine("Configuring Windows Defender Dynamic Exclusions...");
            var config = LoadConfig();
            var paths = config.software?.defender?.exclusionPaths ?? new List<string>();
            if (paths.Count > 0) 
            {
                Console.WriteLine($" -> Adding Exclusion Paths from JSON: {paths.Count} paths");
                RunPowerShell($"Add-MpPreference -ExclusionPath {string.Join(", ", paths.Select(p => $"'{p}'"))}");
            }
            
            var procs = config.software?.defender?.exclusionProcesses ?? new List<string>();
            if (procs.Count > 0) 
            {
                Console.WriteLine($" -> Adding Exclusion Processes from JSON: {procs.Count} processes");
                RunPowerShell($"Add-MpPreference -ExclusionProcess {string.Join(", ", procs.Select(p => $"'{p}'"))}");
            }
        }

        static void ConfigureOsTuning()
        {
            Console.WriteLine("Applying OS Tuning...");
            
            Console.WriteLine(" -> Disabling Paging Files");
            SetRegistryValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", new string[] {}, RegistryValueKind.MultiString);
            
            Console.WriteLine(" -> Disabling NTFS Last Access Update");
            SetRegistryValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 1, RegistryValueKind.DWord);
            
            Console.WriteLine(" -> Disabling 8dot3 Name Creation");
            ExecuteSystemCommand("fsutil", "behavior set disable8dot3 1");
            
            Console.WriteLine(" -> Setting RealTimeIsUniversal to UTC");
            SetRegistryValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "RealTimeIsUniversal", 1, RegistryValueKind.DWord);
            
            Console.WriteLine(" -> Disabling Windows Auto Update (NoAutoUpdate=1)");
            string wuKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
            SetRegistryValue(Registry.LocalMachine, wuKey, "NoAutoUpdate", 1, RegistryValueKind.DWord);
            
            Console.WriteLine(" -> Disabling First Logon Animation");
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "EnableFirstLogonAnimation", 0, RegistryValueKind.DWord);
            
            Console.WriteLine(" -> Enabling Logon Audit Policies");
            ExecuteSystemCommand("auditpol", "/set /subcategory:\"Logon\" /success:enable /failure:enable");
        }

        static void ConfigureRegistry()
        {
            Console.WriteLine("Applying System Registry Hardening...");
            ConfigureOsHardening();
            ConfigureIEHardening();
            
            Console.WriteLine(" -> Disabling Power Standby, Monitor Timeout, and Hibernation");
            ExecuteSystemCommand("powercfg", "-change -standby-timeout-ac 0");
            ExecuteSystemCommand("powercfg", "-change -monitor-timeout-ac 0");
            ExecuteSystemCommand("powercfg", "-h off");
            // NOTE: Machine-wide Scancode Map removed — WEKF Keyboard Filter (Step 18)
            // handles Win-key blocking per-user, with administrators excluded.

            ApplyUserHardening(); 
        }

        static void ConfigureIEHardening()
        {
            Console.WriteLine(" -> Applying Internet Explorer System Restrictions...");
            string ieBase = @"SOFTWARE\Policies\Microsoft\Internet Explorer";
            
            SetRegistryValue(Registry.LocalMachine, $@"{ieBase}\Restrictions", "NoBrowserContextMenu", 1, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, $@"{ieBase}\Control Panel", "*Tab", 1, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, $@"{ieBase}\Toolbars\Restrictions", "NoNavBar", 1, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, $@"{ieBase}\Main", "AlwaysShowMenus", 0, RegistryValueKind.DWord);
        }

        static void ApplyUserHardening()
        {
            string hivePath = @"C:\Users\Default\NTUSER.DAT";
            if (!File.Exists(hivePath)) 
            {
                Console.WriteLine($" -> Skipping HKCU offline hardening. {TargetUser} hive not found.");
                return;
            }
            
            Console.WriteLine($" -> Mounting HKCU Offline Hive for {TargetUser}");
            ExecuteSystemCommand("reg", $@"load HKU\EikonTemp ""{hivePath}""");
            try
            {
                using (var root = KeyOpener(Registry.Users, "EikonTemp", true))
                {
                    if (root == null) return;
                    string pKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
                    string advKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
                    string polExplorer = @"Software\Policies\Microsoft\Windows\Explorer";
                    
                    // ========== Explorer Shell Restrictions ==========
                    Console.WriteLine("    + Applying Explorer Shell Restrictions");
                    SetRegistryValue(root, pKey, "NoRun", 1, RegistryValueKind.DWord);
                    // NoDesktop intentionally omitted — EikonUser needs to see the KioskLauncher shortcut
                    SetRegistryValue(root, pKey, "NoDrives", 4, RegistryValueKind.DWord);        // Hide C: drive
                    SetRegistryValue(root, pKey, "NoViewOnDrive", 4, RegistryValueKind.DWord);    // Block browsing C:
                    SetRegistryValue(root, pKey, "NoWinKeys", 1, RegistryValueKind.DWord);        // Disable Win key combos
                    SetRegistryValue(root, pKey, "NoFind", 1, RegistryValueKind.DWord);           // No search
                    SetRegistryValue(root, pKey, "NoTrayContextMenu", 1, RegistryValueKind.DWord);// No right-click on tray
                    SetRegistryValue(root, pKey, "NoClose", 1, RegistryValueKind.DWord);          // No shutdown from Start
                    SetRegistryValue(root, pKey, "HideSCAVolume", 1, RegistryValueKind.DWord);    // Hide volume icon (audio disabled)
                    SetRegistryValue(root, pKey, "HideSCANetwork", 1, RegistryValueKind.DWord);   // Hide network icon
                    
                    // ========== Control Panel & Settings ==========
                    Console.WriteLine("    + Blocking Control Panel and Settings");
                    SetRegistryValue(root, pKey, "NoControlPanel", 1, RegistryValueKind.DWord);   // Blocks both Control Panel AND Settings app
                    SetRegistryValue(root, pKey, "NoSetFolders", 1, RegistryValueKind.DWord);     // Remove Settings folders from Start
                    SetRegistryValue(root, pKey, "NoSetTaskbar", 1, RegistryValueKind.DWord);     // Prevent taskbar settings changes
                    SetRegistryValue(root, pKey, "NoNetworkConnections", 1, RegistryValueKind.DWord); // Block Network Connections
                    
                    // ========== Start Menu lockdown ==========
                    Console.WriteLine("    + Locking down Start Menu");
                    SetRegistryValue(root, pKey, "NoStartMenuMorePrograms", 1, RegistryValueKind.DWord); // Hide All Programs list
                    SetRegistryValue(root, pKey, "NoCommonStartMenu", 1, RegistryValueKind.DWord);       // Remove shared program groups
                    SetRegistryValue(root, pKey, "NoSMHelp", 1, RegistryValueKind.DWord);                // Remove Help
                    SetRegistryValue(root, pKey, "NoRecentDocsHistory", 1, RegistryValueKind.DWord);     // No recent docs
                    SetRegistryValue(root, pKey, "ClearRecentDocsOnExit", 1, RegistryValueKind.DWord);   // Clear on logoff
                    SetRegistryValue(root, pKey, "NoChangeStartMenu", 1, RegistryValueKind.DWord);       // Prevent Start changes
                    SetRegistryValue(root, pKey, "NoFavoritesMenu", 1, RegistryValueKind.DWord);         // No Favorites
                    SetRegistryValue(root, pKey, "NoSMMyDocs", 1, RegistryValueKind.DWord);              // No Documents
                    SetRegistryValue(root, pKey, "NoSMMyPictures", 1, RegistryValueKind.DWord);          // No Pictures
                    SetRegistryValue(root, pKey, "NoStartMenuMyMusic", 1, RegistryValueKind.DWord);      // No Music
                    SetRegistryValue(root, pKey, "NoUserFolderInStartMenu", 1, RegistryValueKind.DWord); // No user folder
                    SetRegistryValue(root, pKey, "NoStartMenuPinnedList", 0, RegistryValueKind.DWord);   // Keep Start pinned (for kiosk tile if needed)
                    SetRegistryValue(root, advKey, "Start_TrackProgs", 0, RegistryValueKind.DWord);      // Hide "Most Used" apps
                    SetRegistryValue(root, advKey, "Start_TrackDocs", 0, RegistryValueKind.DWord);       // Hide "Recently added"
                    SetRegistryValue(root, advKey, "Start_AdminToolsRoot", 0, RegistryValueKind.DWord);  // Hide Admin Tools
                    
                    // ========== Start Menu sidebar icons (left rail) ==========
                    Console.WriteLine("    + Hiding Start Menu sidebar icons");
                    // Set VisiblePlaces to empty (HKCU fallback — may be overridden on first login)
                    SetRegistryValue(root, @"Software\Microsoft\Windows\CurrentVersion\Start", 
                        "VisiblePlaces", new byte[0], RegistryValueKind.Binary);
                    // Add RunOnce entry: uses reg.exe to re-import empty VisiblePlaces at first login
                    // (catches the case where Windows regenerates defaults during profile creation)
                    string regCleanupPath = @"C:\Windows\Temp\EikonStartCleanup.reg";
                    SetRegistryValue(root, @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
                        "EikonStartCleanup", $"reg import \"{regCleanupPath}\"", RegistryValueKind.String);
                    
                    // ========== Taskbar UI cleanup ==========
                    Console.WriteLine("    + Hiding Taskbar buttons (Task View, People, Search)");
                    SetRegistryValue(root, advKey, "ShowTaskViewButton", 0, RegistryValueKind.DWord);
                    SetRegistryValue(root, $@"{advKey}\People", "PeopleBand", 0, RegistryValueKind.DWord);
                    SetRegistryValue(root, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, RegistryValueKind.DWord);
                    SetRegistryValue(root, polExplorer, "DisableNotificationCenter", 1, RegistryValueKind.DWord);
                    // Lock the taskbar and prevent drag-drop
                    SetRegistryValue(root, advKey, "TaskbarSizeMove", 0, RegistryValueKind.DWord);
                    SetRegistryValue(root, pKey, "TaskbarNoDragToolbar", 1, RegistryValueKind.DWord);
                    SetRegistryValue(root, pKey, "NoToolbarsOnTaskbar", 1, RegistryValueKind.DWord);
                    SetRegistryValue(root, pKey, "TaskbarNoAddRemoveToolbar", 1, RegistryValueKind.DWord);
                    SetRegistryValue(root, pKey, "TaskbarLockAll", 1, RegistryValueKind.DWord);
                    SetRegistryValue(root, pKey, "TaskbarNoResize", 1, RegistryValueKind.DWord);
                    SetRegistryValue(root, pKey, "NoPinningToTaskbar", 1, RegistryValueKind.DWord);

                    // ========== App Blocklist (DisallowRun) ==========
                    // wscript.exe intentionally NOT blocked — KioskLauncher.vbs auto-start needs it
                    Console.WriteLine("    + Applying comprehensive App Blocklist");
                    SetRegistryValue(root, pKey, "DisallowRun", 1, RegistryValueKind.DWord);
                    using (var disallowKey = root.CreateSubKey($@"{pKey}\DisallowRun"))
                    {
                        if (disallowKey != null)
                        {
                            string[] blockedApps = new[] {
                                "iexplore.exe",          // Internet Explorer
                                "cmd.exe",               // Command Prompt
                                "powershell.exe",        // PowerShell
                                "powershell_ise.exe",    // PowerShell ISE
                                "cscript.exe",           // Console Script Host
                                "mshta.exe",             // HTML Application Host
                                "regedit.exe",           // Registry Editor
                                "control.exe",           // Control Panel
                                "mmc.exe",               // Microsoft Management Console
                                "notepad.exe",           // Notepad
                                "write.exe",             // WordPad
                                "wordpad.exe",           // WordPad
                                "SystemSettings.exe",    // Windows Settings
                                "taskmgr.exe",           // Task Manager (belt + suspenders)
                                "msconfig.exe",          // System Configuration
                                "msinfo32.exe",          // System Information
                                "compmgmt.msc",          // Computer Management
                                "devmgmt.msc",           // Device Manager
                                "diskmgmt.msc",          // Disk Management
                                "services.msc",          // Services
                                "eventvwr.msc",          // Event Viewer
                                "gpedit.msc",            // Group Policy Editor
                                "lusrmgr.msc",           // Local Users & Groups
                                "ncpa.cpl",              // Network Connections
                                "desk.cpl",              // Display Settings
                                "firewall.cpl",          // Firewall
                                "appwiz.cpl",            // Programs & Features
                                "inetcpl.cpl",           // Internet Options
                                "sysdm.cpl",             // System Properties
                                "timedate.cpl",          // Date/Time
                                "main.cpl",              // Mouse Properties
                                "intl.cpl",              // Region settings
                                "certmgr.msc",           // Certificate Manager
                                "secpol.msc",            // Local Security Policy
                                "comexp.msc",            // Component Services
                                "perfmon.exe",           // Performance Monitor
                                "resmon.exe",            // Resource Monitor
                                "winver.exe",            // Windows Version
                                "dxdiag.exe",            // DirectX Diagnostics
                                "calc.exe",              // Calculator
                                "SnippingTool.exe",      // Snipping Tool
                                "mstsc.exe",             // Remote Desktop
                                "mspaint.exe",           // Paint
                            };
                            for (int i = 0; i < blockedApps.Length; i++)
                            {
                                disallowKey.SetValue((i + 1).ToString(), blockedApps[i], RegistryValueKind.String);
                            }
                            Console.WriteLine($"      Blocked {blockedApps.Length} executables");
                        }
                    }

                    // ========== System Policies ==========
                    Console.WriteLine("    + Disabling Task Manager for User");
                    SetRegistryValue(root, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr", 1, RegistryValueKind.DWord);
                    // Disable registry editing tools
                    SetRegistryValue(root, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableRegistryTools", 1, RegistryValueKind.DWord);

                    // ========== Locked Start Layout (removes default tiles) ==========
                    // Applied per-user (HKCU) so EikonAdmin keeps a normal Start Menu.
                    // Deploy the empty layout XML first, then set HKCU policy keys.
                    Console.WriteLine("    + Deploying locked empty Start Layout (removes default tiles)");
                    string layoutDir = @"C:\Windows\StartLayouts";
                    if (!Directory.Exists(layoutDir)) Directory.CreateDirectory(layoutDir);
                    string layoutXml = @"<LayoutModificationTemplate xmlns:defaultlayout=""http://schemas.microsoft.com/Start/2014/FullDefaultLayout"" xmlns:start=""http://schemas.microsoft.com/Start/2014/StartLayout"" xmlns=""http://schemas.microsoft.com/Start/2014/LayoutModification"" Version=""1"">
  <LayoutOptions StartTileGroupCellWidth=""6"" />
  <DefaultLayoutOverride>
    <StartLayoutCollection>
      <defaultlayout:StartLayout GroupCellWidth=""6"" />
    </StartLayoutCollection>
  </DefaultLayoutOverride>
</LayoutModificationTemplate>";
                    string layoutPath = Path.Combine(layoutDir, "EikonStartLayout.xml");
                    File.WriteAllText(layoutPath, layoutXml);
                    // Apply via per-user Group Policy keys (EikonUser HKCU only — not HKLM)
                    SetRegistryValue(root, polExplorer, "LockedStartLayout", 1, RegistryValueKind.DWord);
                    SetRegistryValue(root, polExplorer, "StartLayoutFile", layoutPath, RegistryValueKind.ExpandString);
                    Console.WriteLine($"    OK: Locked Start Layout deployed to {layoutPath}");
                }
            }
            finally 
            { 
                Console.WriteLine(" -> Unmounting HKCU Offline Hive");
                GC.Collect(); 
                Process.Start("reg", "unload HKU\\EikonTemp")?.WaitForExit(); 
            }

            // ========== HKLM: Force-hide Start sidebar pinned folders ==========
            // Uses Windows 10 MDM CSP PolicyManager bridge (works on Enterprise/LTSC).
            // Machine-wide: EikonAdmin also loses sidebar folder shortcuts but retains
            // full Start Menu functionality (All Programs, search, tiles, etc.).
            Console.WriteLine("    + Hiding Start Menu sidebar folders (HKLM AllowPinnedFolder policies)");
            string cspStartPath = @"SOFTWARE\Microsoft\PolicyManager\current\device\Start";
            string[] pinnedFolders = { "Documents", "Downloads", "FileExplorer", "HomeGroup",
                "Music", "Network", "PersonalFolder", "Pictures", "Settings", "Videos" };
            foreach (string folder in pinnedFolders)
            {
                SetRegistryValue(Registry.LocalMachine, cspStartPath,
                    $"AllowPinnedFolder{folder}", 0, RegistryValueKind.DWord);
            }

            // Deploy a .reg file for the RunOnce import (clears VisiblePlaces at first login)
            string regContent = "Windows Registry Editor Version 5.00\r\n\r\n" +
                "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Start]\r\n" +
                "\"VisiblePlaces\"=hex:\r\n";
            File.WriteAllText(@"C:\Windows\Temp\EikonStartCleanup.reg", regContent);
            Console.WriteLine("    OK: Start sidebar policies applied.");
        }

        static void ConfigureScheduledTasks()
        {
            Console.WriteLine("Disabling Scheduled Tasks...");
            string[] tasks = { 
                @"Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", 
                @"Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                @"Microsoft\Windows\Defrag\ScheduledDefrag",
                @"Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
                @"Microsoft\Windows\Maintenance\WinSAT",
                @"Microsoft\Windows\NlaSvc\WiFiTask",
                @"Microsoft\Windows\Windows Media Sharing\UpdateLibrary"
            };
            
            foreach (var t in tasks) 
            {
                ExecuteSystemCommand("schtasks", $"/Change /TN \"{t}\" /Disable");
            }
        }
        internal static void OptimizeServices()
        {
            Console.WriteLine("Optimizing Services...");

            // 1. Services to Disable entirely
            string[] dis = {
                "SSDPSRV", "RemoteRegistry", "RemoteAccess", "upnphost", "fdPHost", "FDResPub", 
                "WMPNetworkSvc", "PeerDistSvc", "RasMan", "UmRdpService", "TermService", 
                "SessionEnv", "WinRM", "WebClient", "AJRouter", "ALG", "icssvc", "LanmanServer", 
                "QWAVE", "wcncsvc", "WwanSvc", "HomeGroupListener", "HomeGroupProvider", 
                "SharedAccess", "wuauserv", "WSearch", "DiagTrack", "WerSvc", "wercplsupport", 
                "diagsvc", "DPS", "WdiServiceHost", "WdiSystemHost", "TroubleshootingSvc", 
                "PcaSvc", "DoSvc", "AppVClient", "UevAgentService", "PushToInstall", 
                "InstallService", "tzautoupdate", "WaaSMedicSvc", "hidserv", "stisvc", 
                "WbioSrvc", "Fax", "TapiSrv", "WPDBusEnum", "BthAvctpSvc", "bthserv", 
                "BTAGService", "CertPropSvc", "Audiosrv", "AudioEndpointBuilder", "VacSvc", 
                "FrameServer", "shpamsvc", "vmicvmsession", "vmicguestinterface", "vmicheartbeat", 
                "vmictimesync", "vmickvpexchange", "vmicrdv", "vmicshutdown", "HvHost", 
                "swprv", "VSS", "wbengine", "SDRSVC", "MSiSCSI", "CscService", "defragsvc", 
                "TieringEngineService", "TrkWks", "SEMgrSvc", "Themes", "SysMain", "WpcMonSvc", 
                "SharedRealitySvc", "MapsBroker", "RetailDemo", "WalletService", "PhoneSvc", 
                "AssignedAccessManagerSvc", "autotimesvc", "pla", "DusmSvc", "XblAuthManager", 
                "XblGameSave", "XboxGipSvc", "XboxNetApiSvc", "RmSvc", "wisvc", "wlidsvc", 
                "BITS", "AxInstSV", "SNMPTRAP", "Wecsvc"
            };

            // 2. Services requiring Automatic Startup
            string[] auto = { "vds", "MsKeyboardFilter", "Spooler", "W32Time", "WlanSvc" };

            int total = dis.Length;
            for (int i = 0; i < total; i++) 
            { 
                ServiceStopper(dis[i]); 
                ExecuteSystemCommand("sc", $"config \"{dis[i]}\" start= disabled"); 
                Console.Write($"\r[{(i * 100) / total}%] Disabling Services ({i}/{total})");
            }
            Console.WriteLine();

            foreach (var s in auto) ExecuteSystemCommand("sc", $"config \"{s}\" start= auto");
            
            // 3. Services requiring Manual Startup
            ExecuteSystemCommand("sc", "config \"UsoSvc\" start= demand");
        }

        internal static void ConfigureFirewall()
        {
            Console.WriteLine("Configuring Windows Defender Firewall...");
            Console.WriteLine(" -> Resetting Firewall and enabling Mandatory Groups");
            RunNetSh("advfirewall reset");
            RunNetSh("advfirewall firewall set rule group=\"Core Networking\" new enable=Yes");
            RunNetSh("advfirewall firewall set rule group=\"File and Printer Sharing\" new enable=Yes");
            RunNetSh("advfirewall firewall set rule group=\"Remote Assistance\" new enable=Yes");
            RunPowerShell("Set-NetFirewallProfile -NotifyOnListen False -Enabled True");
            
            Console.WriteLine(" -> Blocking ICMP (Ping) & Allowing DICOM/SSH Ports");
            RunPowerShell("New-NetFirewallRule -DisplayName 'Block Ping IPv4' -Direction Inbound -Action Block -Protocol ICMPv4 | Out-Null");
            RunPowerShell("New-NetFirewallRule -DisplayName 'Block Ping IPv6' -Direction Inbound -Action Block -Protocol ICMPv6 | Out-Null");
            RunPowerShell("New-NetFirewallRule -DisplayName 'Eikon OpenSSH' -Direction Inbound -LocalPort 22 -Protocol TCP -Action Allow | Out-Null");
            RunPowerShell("New-NetFirewallRule -DisplayName 'Eikon DICOM In' -Direction Inbound -LocalPort 104 -Protocol TCP -Action Allow | Out-Null");
            RunPowerShell("New-NetFirewallRule -DisplayName 'Eikon DICOM/HL7 Out' -Direction Outbound -LocalPort 104,2761 -Protocol TCP -Action Allow | Out-Null");

            Console.WriteLine(" -> Applying Regex Blocks (mDNS, IGMP, SMB-In, etc.)");
            string firewallBlockScript = @"
            $blockedKeywords = @('IGMP', 'Multicast Listener', 'Neighbor Discovery', 'Teredo', 'NB-Datagram', 'NB-Name', 'NB-Session', 'SMB-In', 'mDNS', 'Cast to Device')
            $rules = Get-NetFirewallRule -Direction Inbound -Enabled True
            foreach ($rule in $rules) {
                foreach ($keyword in $blockedKeywords) {
                    if ($rule.DisplayName -match $keyword) {
                        Disable-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
                        break
                    }
                }
            }";
            RunPowerShell(firewallBlockScript);
        }

        static void ConfigureLocalPolicies() 
        { 
            Console.WriteLine("Applying Local Security Policies...");
            Console.WriteLine(" -> Enforcing USBSTOR Start Policy");
            SetRegistryValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\USBSTOR", "Start", 3, RegistryValueKind.DWord); 
        }

        internal static void ConfigureCisBenchmarks() 
        { 
            Console.WriteLine("Applying CIS Benchmarks...");
            
            // LSA Policies
            string lsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";
            SetRegistryValue(Registry.LocalMachine, lsaKey, "LimitBlankPasswordUse", 1, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, lsaKey, "LmCompatibilityLevel", 5, RegistryValueKind.DWord); // NTLMv2 Only
            
            // Network Protections
            SetRegistryValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DisableIPSourceRouting", 2, RegistryValueKind.DWord);
            SetRegistryValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\NetworkProvider\HardenedPaths", @"\\*\NETLOGON", "RequireMutualAuthentication=1,RequireIntegrity=1", RegistryValueKind.String);
        }

        internal static void ConfigureAdvancedAudit() 
        { 
            Console.WriteLine("Configuring Advanced Audit Policies & Event Logs...");

            // 1. Advanced Audit Policies (auditpol)
            ExecuteSystemCommand("auditpol", "/set /subcategory:\"Logon\" /success:enable /failure:enable");
            ExecuteSystemCommand("auditpol", "/set /subcategory:\"Process Creation\" /success:enable /failure:enable");
            ExecuteSystemCommand("auditpol", "/set /subcategory:\"Account Lockout\" /failure:enable");
            ExecuteSystemCommand("auditpol", "/set /subcategory:\"Removable Storage\" /success:enable /failure:enable");

            // 2. Event Log Tuning (wevtutil)
            // 20MB limit (20971520), No Retention (Overwrite), Custom Interactive User SDDL
            string sddl = "O:BAG:SYD:(A;;0x3;;;IU)(A;;0x7;;;BA)(A;;0xf0007;;;SY)";
            string[] targetLogs = { "Application", "Security", "System" };
            
            foreach (string log in targetLogs)
            {
                ExecuteSystemCommand("wevtutil", $"sl {log} /ms:20971520 /rt:false /ca:\"{sddl}\"");
            }
        }

        internal static void ConfigureAppLocker() 
        { 
            Console.WriteLine("Skipping intermediate AppLocker policy generation.");
            Console.WriteLine(" -> AppLocker enforcement is deferred to the Final Seal Phase to ensure reboot resilience.");
        }

        internal static void ConfigureKeyboardFilter()
        {
            Console.WriteLine("Configuring Windows Embedded Keyboard Filter (WEKF)...");

            // Per-account WEKF configuration:
            //   EikonUser  -> filter ON  (keyboard shortcuts blocked)
            //   EikonAdmin -> filter OFF (full keyboard access for maintenance)
            // Both accounts are in Administrators group, so we cannot use the
            // blanket DisableKeyboardFilterForAdministrators flag.
            Console.WriteLine(" -> Configuring per-account Keyboard Filter (WEKF_Account)");
            string accountScript = $@"
            $namespace = 'root\standardcimv2\embedded'
            # Enable filter for {TargetUser}
            $existing = Get-WmiObject -Namespace $namespace -Class WEKF_Account | Where-Object {{ $_.Account -eq '{TargetUser}' }}
            if ($existing) {{
                $existing.Enabled = $true
                $existing.Put() | Out-Null
            }} else {{
                Set-WmiInstance -Namespace $namespace -Class WEKF_Account -Arguments @{{ Account='{TargetUser}'; Enabled=$true }} | Out-Null
            }}
            Write-Host '    OK: Keyboard Filter enabled for {TargetUser}'

            # Disable filter for EikonAdmin
            $existingAdmin = Get-WmiObject -Namespace $namespace -Class WEKF_Account | Where-Object {{ $_.Account -eq 'EikonAdmin' }}
            if ($existingAdmin) {{
                $existingAdmin.Enabled = $false
                $existingAdmin.Put() | Out-Null
            }} else {{
                Set-WmiInstance -Namespace $namespace -Class WEKF_Account -Arguments @{{ Account='EikonAdmin'; Enabled=$false }} | Out-Null
            }}
            Write-Host '    OK: Keyboard Filter disabled for EikonAdmin'";
            RunPowerShell(accountScript);

            Console.WriteLine(" -> Applying WMI blocks for standard escape keys and custom scancodes");
            string script = @"
            $namespace = 'root\standardcimv2\embedded'
            $keys = @('Alt+F4', 'Alt+Tab', 'Alt+Esc', 'Ctrl+Esc', 'Ctrl+F4', 'Win+L', 'Win+R', 'Win+E', 'Win+M', 'Win+D', 'Win', 'Win+U', 'Win+Enter', 'Win+Up', 'Win+Down', 'Win+Left', 'Win+Right')
            foreach ($k in $keys) {
                $filter = Get-WmiObject -Namespace $namespace -Class WEKF_PredefinedKey | Where-Object { $_.Id -eq $k }
                if ($filter) { $filter.Enabled = $true; $filter.Put() | Out-Null }
            }
            
            # Custom Scancodes (Ctrl+Alt+Tab, Ctrl+Alt+Esc, Win+X, Win+Space)
            $custom = @(
                @{ Mod=6; Scan=15 }, @{ Mod=6; Scan=1 }, @{ Mod=8; Scan=45 }, @{ Mod=8; Scan=57 }
            )
            foreach ($c in $custom) {
                Set-WmiInstance -Namespace $namespace -Class WEKF_CustomKey -Arguments @{ Modifiers=$c.Mod; CustomScanCode=$c.Scan; Enabled=$true } | Out-Null
            }";
            RunPowerShell(script);
        }

        static void ConfigureWindowsFeatures()
        {
            Console.WriteLine("Configuring Windows Features (DISM)...");
            string[] features = {
                "Client-KeyboardFilter", 
                "Client-UnifiedWriteFilter",
                "Client-EmbeddedLogon", 
                "Client-EmbeddedShellLauncher",
                "Printing-Foundation-Features", 
                "Printing-XPSServices-Features"
            };

            foreach (var feature in features)
            {
                Console.WriteLine($"Enabling feature: {feature}...");
                ExecuteSystemCommand("dism", $"/Online /Enable-Feature /FeatureName:{feature} /All /NoRestart");
            }
        }

        static string GetUserSid(string username)
        {
            try { return ((SecurityIdentifier)new NTAccount($"{Environment.MachineName}\\{username}").Translate(typeof(SecurityIdentifier))).Value; }
            catch { return null; }
        }

        static void RunNetSh(string args) { ExecuteSystemCommand("netsh", args); }
        static void RunPowerShell(string command) { RunPowerShellScript(command); }
        
        /// <summary>
        /// Executes a PowerShell script using -EncodedCommand to avoid all quoting/escaping issues.
        /// Uses asynchronous stderr reading to prevent the classic pipe deadlock.
        /// </summary>
        static void RunPowerShellScript(string script)
        {
            if (IsDryRun) { Console.WriteLine("[DRY-RUN] PowerShell script skipped."); return; }
            
            // Prepend $ProgressPreference to kill CLIXML progress spam on stderr
            string fullScript = "$ProgressPreference = 'SilentlyContinue'\n" + script;
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(fullScript));
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                // CRITICAL: Read stderr asynchronously to avoid deadlock.
                // If both stdout and stderr buffers fill, synchronous ReadToEnd() on one
                // stream blocks while the child blocks writing to the other. 
                var stderrBuilder = new StringBuilder();
                proc.ErrorDataReceived += (sender, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };
                proc.BeginErrorReadLine();
                
                // Now synchronously read stdout (safe because stderr is draining async)
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                
                if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
                string stderr = stderrBuilder.ToString();
                if (!string.IsNullOrWhiteSpace(stderr) && !stderr.Contains("CLIXML")) 
                    Console.Write($"[PS-ERR] {stderr}");
            }
        }
        static void ExecuteSystemCommand(string fileName, string arguments)
        {
            if (IsDryRun) return;
            var psi = new ProcessStartInfo(fileName, arguments) { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi)?.WaitForExit();
        }
        static void SetRegistryValue(RegistryKey root, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
        {
            if (IsDryRun) return;
            using (var k = root.CreateSubKey(path)) k.SetValue(name, value, kind);
        }
    }
}