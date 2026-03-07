using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace OneClickInstaller
{
    public class InstallationManager
    {
        public event EventHandler<ProgressEventArgs> ProgressUpdated;
        public event EventHandler<StatusEventArgs> StatusUpdated;
        public event EventHandler<LogEventArgs> LogUpdated;

        private readonly List<SoftwarePackage> _packages;
        private readonly string _basePath;
        private readonly FinalPackageConfig _finalPackageConfig;
        private readonly DiskPartitioningConfig _diskPartitioningConfig;
        private readonly string _logFilePath;
        private readonly OsConfigurationConfig _osConfigurationConfig;
        private readonly PostInstallScriptsConfig _postInstallScriptsConfig;
        private int _mySqlReadyTimeoutSeconds = 60;

        public InstallationManager()
        {
            // Get the directory where the executable is located
            string exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            // SW folder is in the same directory as the exe
            _basePath = Path.Combine(exeDirectory, "SW");

            // Setup log file
            _logFilePath = Path.Combine(exeDirectory, "oneclickinstaller_log.txt");
            try
            {
                File.WriteAllText(_logFilePath, $"OneClickInstaller Log - Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
                File.AppendAllText(_logFilePath, $"=========================================================={Environment.NewLine}");
            }
            catch { /* If we can't create log file, continue without file logging */ }

            // Load all configuration from installer-config.json
            string configPath = Path.Combine(exeDirectory, "installer-config.json");
            if (File.Exists(configPath))
            {
                string jsonContent = File.ReadAllText(configPath);
                var config = SimpleJsonParser.Parse(jsonContent);
                _finalPackageConfig = config.FinalInstallPackage;
                _diskPartitioningConfig = config.DiskPartitioning;
                _osConfigurationConfig = config.OsConfiguration;
                _postInstallScriptsConfig = config.PostInstallScripts;

                // Build package list from JSON config (order in JSON = install order)
                _packages = new List<SoftwarePackage>();
                if (config.SoftwarePackages != null)
                {
                    foreach (var pkg in config.SoftwarePackages)
                    {
                        _packages.Add(new SoftwarePackage
                        {
                            Name = pkg.Name,
                            FilePath = Path.GetFullPath(Path.Combine(_basePath, pkg.RelativePath.Replace(@"\\", @"\"))),
                            InstallArgs = pkg.InstallArgs ?? "",
                            InstallType = ParseInstallType(pkg.InstallType),
                            CopyDestination = (pkg.CopyDestination ?? "").Replace(@"\\", @"\")
                        });

                        if (pkg.PostInstall != null && pkg.PostInstall.ConfigureMySQL && pkg.PostInstall.TimeoutSeconds > 0)
                            _mySqlReadyTimeoutSeconds = pkg.PostInstall.TimeoutSeconds;
                    }
                }
                LogMessage($"Loaded {_packages.Count} packages from installer-config.json");

                // Override MySQL password from secrets.json if present
                string secretsPath = Path.Combine(exeDirectory, "secrets.json");
                if (File.Exists(secretsPath))
                {
                    string secretsJson = File.ReadAllText(secretsPath);
                    string overridePassword = SimpleJsonParser.ParseMySQLPassword(secretsJson);
                    if (!string.IsNullOrEmpty(overridePassword))
                    {
                        config.MySQLConfig.Password = overridePassword;
                        LogMessage("MySQL password loaded from secrets.json");
                    }
                }
            }
            else
            {
                _packages = new List<SoftwarePackage>();
                LogMessage("WARNING: installer-config.json not found! No packages to install.");
            }
        }

        private static InstallationType ParseInstallType(string type)
        {
            if (string.IsNullOrEmpty(type)) return InstallationType.EXE;
            switch (type.ToUpperInvariant())
            {
                case "MSI": return InstallationType.MSI;
                case "EXE": return InstallationType.EXE;
                case "COPY": return InstallationType.COPY;
                case "DISM": return InstallationType.DISM;
                case "INF": return InstallationType.INF;
                case "MYSQL_CONSOLE": return InstallationType.MYSQL_CONSOLE;
                default: return InstallationType.EXE;
            }
        }

        public async Task StartInstallationAsync()
        {
            LogMessage("Starting installation process...");
            
            // Check if running as administrator
            if (!IsRunningAsAdministrator())
            {
                throw new UnauthorizedAccessException("This installer must be run as Administrator.");
            }

            // Verify all files exist - collect missing ones to skip
            var missingPackages = await VerifyFilesAsync();

            // Calculate total steps for accurate overall progress
            int totalSteps = _packages.Count; // software packages
            bool hasDiskPartitioning = _diskPartitioningConfig != null && _diskPartitioningConfig.Enabled;
            if (hasDiskPartitioning) totalSteps++;
            totalSteps++; // DB configuration
            if (_finalPackageConfig != null && !string.IsNullOrEmpty(_finalPackageConfig.RelativePath)) totalSteps++;
            if (_postInstallScriptsConfig != null && _postInstallScriptsConfig.Enabled) totalSteps++;
            if (_osConfigurationConfig != null && _osConfigurationConfig.Enabled) totalSteps++;
            int completedSteps = 0;

            // Disk partitioning (before any installs)
            if (hasDiskPartitioning)
            {
                LogMessage("Starting disk partitioning...");
                UpdateStatus("Partitioning disk...", "Disk Partitioning", 0, "Installing");
                UpdateProgress((double)completedSteps / totalSteps * 100, 0);
                
                try
                {
                    await PartitionDiskAsync();
                    completedSteps++;
                    UpdateStatus("Disk partitioning completed", "Disk Partitioning", 0, "Completed");
                    UpdateProgress((double)completedSteps / totalSteps * 100, 100);
                    LogMessage("Disk partitioning completed successfully");
                }
                catch (Exception ex)
                {
                    UpdateStatus("Disk partitioning failed", "Disk Partitioning", 0, "Failed");
                    LogMessage($"Disk partitioning failed: {ex.Message}");
                    throw;
                }
            }

            // Install each package
            int failedCount = 0;
            for (int i = 0; i < _packages.Count; i++)
            {
                var package = _packages[i];
                // UI index offset: 0 = Disk Partitioning, so packages start at index 1
                int uiIndex = hasDiskPartitioning ? i + 1 : i;
                
                // Skip missing packages
                if (missingPackages.Contains(package.Name))
                {
                    UpdateStatus($"Skipped {package.Name} - file not found", package.Name, uiIndex, "Skipped");
                    LogMessage($"SKIPPED: {package.Name} - installer file not found");
                    completedSteps++;
                    UpdateProgress((double)completedSteps / totalSteps * 100, 100);
                    failedCount++;
                    continue;
                }

                UpdateStatus($"Installing {package.Name}...", package.Name, uiIndex, "Installing");
                UpdateProgress((double)completedSteps / totalSteps * 100, 0);
                
                LogMessage($"Installing {package.Name}...");
                
                try
                {
                    await InstallPackageAsync(package, uiIndex);
                    UpdateStatus($"Successfully installed {package.Name}", package.Name, uiIndex, "Completed");
                    LogMessage($"Successfully installed {package.Name}");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Failed to install {package.Name}", package.Name, uiIndex, "Failed");
                    LogMessage($"FAILED: {package.Name}: {ex.Message}");
                    failedCount++;
                    // Continue with next package instead of aborting
                }
                
                completedSteps++;
                UpdateProgress((double)completedSteps / totalSteps * 100, 100);
                await Task.Delay(500); // Brief pause between installations
            }

            // UI index accounting for disk partitioning offset
            int diskOffset = hasDiskPartitioning ? 1 : 0;
            int dbConfigUiIndex = _packages.Count + diskOffset;
            int finalUiIndex = dbConfigUiIndex + 1;

            // Configure MySQL and run SQL script
            LogMessage("Configuring MySQL database...");
            UpdateStatus("Configuring MySQL database...", "Database Configuration", dbConfigUiIndex, "Installing");
            UpdateProgress((double)completedSteps / totalSteps * 100, 0);
            
            try
            {
                await ConfigureMySQLAsync();
                UpdateStatus("Database configuration completed", "Database Configuration", dbConfigUiIndex, "Completed");
                LogMessage("Database configuration completed successfully");
            }
            catch (Exception ex)
            {
                UpdateStatus("Database configuration failed", "Database Configuration", dbConfigUiIndex, "Failed");
                LogMessage($"Database configuration failed: {ex.Message}");
                failedCount++;
                LogMessage("Continuing despite MySQL configuration failure...");
            }
            completedSteps++;
            UpdateProgress((double)completedSteps / totalSteps * 100, 100);
            
            // Install final package (configurable) as the last step
            if (_finalPackageConfig != null && !string.IsNullOrEmpty(_finalPackageConfig.RelativePath))
            {
                string finalName = _finalPackageConfig.Name;
                LogMessage($"Installing {finalName}...");
                UpdateStatus($"Installing {finalName}...", finalName, finalUiIndex, "Installing");
                UpdateProgress((double)completedSteps / totalSteps * 100, 0);
                
                try
                {
                    var finalPackage = new SoftwarePackage
                    {
                        Name = finalName,
                        FilePath = Path.GetFullPath(Path.Combine(_basePath, _finalPackageConfig.RelativePath.Replace(@"\\", @"\"))),
                        InstallArgs = _finalPackageConfig.InstallArgs,
                        InstallType = ParseInstallType(_finalPackageConfig.InstallType),
                        CopyDestination = _finalPackageConfig.CopyDestination
                    };
                    await InstallPackageAsync(finalPackage, finalUiIndex);
                    UpdateStatus($"Successfully installed {finalName}", finalName, finalUiIndex, "Completed");
                    LogMessage($"Successfully installed {finalName}");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Failed to install {finalName}", finalName, finalUiIndex, "Failed");
                    LogMessage($"Failed to install {finalName}: {ex.Message}");
                    failedCount++;
                }
                completedSteps++;
                UpdateProgress((double)completedSteps / totalSteps * 100, 100);
            }

            // Post-install scripts (optional, after GVPPro)
            int postScriptsUiIndex = finalUiIndex + 1;
            if (_postInstallScriptsConfig != null && _postInstallScriptsConfig.Enabled)
            {
                string stepName = !string.IsNullOrEmpty(_postInstallScriptsConfig.Description) ? _postInstallScriptsConfig.Description : "Post-Install Scripts";
                LogMessage($"Running {stepName}...");
                UpdateStatus($"Running {stepName}...", stepName, postScriptsUiIndex, "Installing");
                UpdateProgress((double)completedSteps / totalSteps * 100, 0);

                try
                {
                    await RunPostInstallScriptsAsync();
                    UpdateStatus($"{stepName} completed", stepName, postScriptsUiIndex, "Completed");
                    LogMessage($"{stepName} completed successfully");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"{stepName} failed", stepName, postScriptsUiIndex, "Failed");
                    LogMessage($"{stepName} failed: {ex.Message}");
                    failedCount++;
                }
                completedSteps++;
                UpdateProgress((double)completedSteps / totalSteps * 100, 100);
            }

            // OS Configuration (EikonConfigurator - kiosk lockdown)
            // Only proceed if ALL previous steps succeeded (0 failures)
            if (_osConfigurationConfig != null && _osConfigurationConfig.Enabled)
            {
                int osConfigUiIndex = postScriptsUiIndex + 1;

                if (failedCount > 0)
                {
                    LogMessage($"SKIPPING OS configuration: {failedCount} failure(s) detected in previous steps.");
                    LogMessage("Fix the failed packages and re-run the installer before kiosk lockdown.");
                    UpdateStatus("Skipped - previous errors", "OS Configuration", osConfigUiIndex, "Skipped");
                    completedSteps++;
                    UpdateProgress((double)completedSteps / totalSteps * 100, 100);
                }
                else
                {
                    LogMessage("All packages installed successfully. Proceeding to OS configuration...");
                    UpdateStatus("Configuring OS for kiosk mode...", "OS Configuration", osConfigUiIndex, "Installing");
                    UpdateProgress((double)completedSteps / totalSteps * 100, 0);
                    
                    try
                    {
                        await RunOsConfigurationAsync();
                        UpdateStatus("OS configuration completed", "OS Configuration", osConfigUiIndex, "Completed");
                        LogMessage("OS configuration completed. System will restart automatically.");
                    }
                    catch (Exception ex)
                    {
                        UpdateStatus("OS configuration failed", "OS Configuration", osConfigUiIndex, "Failed");
                        LogMessage($"OS configuration failed: {ex.Message}");
                        failedCount++;
                    }
                    completedSteps++;
                    UpdateProgress((double)completedSteps / totalSteps * 100, 100);
                }
            }

            UpdateProgress(100, 100);
            LogMessage($"Installation process completed! ({failedCount} failure(s))");
        }

        private async Task<HashSet<string>> VerifyFilesAsync()
        {
            var missing = new HashSet<string>();
            LogMessage("Verifying installation files...");
            
            foreach (var package in _packages)
            {
                if (package.InstallType == InstallationType.COPY)
                {
                    if (!File.Exists(package.FilePath) && !Directory.Exists(package.FilePath))
                    {
                        LogMessage($"WARNING: {package.Name} - source not found: {package.FilePath}");
                        missing.Add(package.Name);
                    }
                    else
                    {
                        LogMessage($"Found: {Path.GetFileName(package.FilePath)} (copy)");
                    }
                }
                else if (package.InstallType == InstallationType.INF)
                {
                    if (!File.Exists(package.FilePath))
                    {
                        LogMessage($"WARNING: {package.Name} - INF file not found: {package.FilePath}");
                        missing.Add(package.Name);
                    }
                    else
                    {
                        LogMessage($"Found: {Path.GetFileName(package.FilePath)} (INF driver)");
                    }
                }
                else if (package.InstallType == InstallationType.DISM)
                {
                    // DISM packages can proceed without the source archive (will try online/built-in)
                    if (File.Exists(package.FilePath))
                    {
                        LogMessage($"Found: {Path.GetFileName(package.FilePath)} (DISM source archive)");
                    }
                    else
                    {
                        LogMessage($"Note: {package.Name} - source archive not found: {package.FilePath}. Will try DISM without source.");
                    }
                }
                else if (package.InstallType == InstallationType.MYSQL_CONSOLE)
                {
                    // MySQLInstallerConsole.exe is installed by the bootstrapper MSI in a previous step;
                    // it won't exist yet during pre-flight verification, so skip the check.
                    LogMessage($"Note: {package.Name} - will use MySQLInstallerConsole.exe (installed by bootstrapper)");
                }
                else
                {
                    if (!File.Exists(package.FilePath))
                    {
                        LogMessage($"WARNING: {package.Name} - file not found: {package.FilePath}");
                        missing.Add(package.Name);
                    }
                    else
                    {
                        LogMessage($"Found: {Path.GetFileName(package.FilePath)}");
                    }
                }
            }

            if (missing.Count > 0)
            {
                LogMessage($"{missing.Count} package(s) will be SKIPPED due to missing files.");
            }
            else
            {
                LogMessage("All installation files verified.");
            }

            // Check SQL file
            var sqlFile = Path.Combine(_basePath, @"MySQL_JavaJRE_DB_Installer-20231002T070835Z-001\MySQL_JavaJRE_DB_Installer\dcmdetails_20251217.sql");
            if (!File.Exists(sqlFile))
            {
                LogMessage($"Warning: SQL file not found: {sqlFile}");
            }
            else
            {
                LogMessage($"Found: {Path.GetFileName(sqlFile)}");
            }

            return missing;
        }

        private async Task InstallPackageAsync(SoftwarePackage package, int index)
        {
            if (package.InstallType == InstallationType.COPY)
            {
                await Task.Run(() =>
                {
                    LogMessage($"Copying {package.Name} to {package.CopyDestination}...");
                    CopyDirectory(package.FilePath, package.CopyDestination);
                    LogMessage($"Copied {package.Name} to {package.CopyDestination}");
                });
                return;
            }

            if (package.InstallType == InstallationType.DISM)
            {
                await InstallViaDismAsync(package);
                return;
            }

            if (package.InstallType == InstallationType.MYSQL_CONSOLE)
            {
                await InstallViaMySQLConsoleAsync(package);
                return;
            }

            if (package.InstallType == InstallationType.INF)
            {
                await Task.Run(() =>
                {
                    LogMessage($"Installing INF driver: {package.FilePath}");

                    // pnputil doesn't work reliably with network/mapped drives.
                    // Copy the driver folder to a local temp directory first.
                    string infPath = package.FilePath;
                    string tempDriverDir = null;

                    try
                    {
                        string driverDir = Path.GetDirectoryName(infPath);
                        tempDriverDir = Path.Combine(Path.GetTempPath(), "OneClickInstaller_Driver_" + Path.GetFileNameWithoutExtension(infPath));

                        if (Directory.Exists(tempDriverDir))
                            Directory.Delete(tempDriverDir, true);

                        LogMessage($"Copying driver files to local temp: {tempDriverDir}");
                        CopyDirectory(driverDir, tempDriverDir);

                        infPath = Path.Combine(tempDriverDir, Path.GetFileName(package.FilePath));
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Warning: Failed to copy driver to temp, trying original path. Error: {ex.Message}");
                        infPath = package.FilePath;
                    }

                    // On 64-bit OS, a 32-bit process can't find pnputil.exe in System32 (WOW64 redirect).
                    // Use SysNative path to access the real System32.
                    string pnputil = "pnputil.exe";
                    if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
                    {
                        string sysNative = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysNative", "pnputil.exe");
                        if (File.Exists(sysNative))
                            pnputil = sysNative;
                    }

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = pnputil,
                            Arguments = $"/add-driver \"{infPath}\" /install",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };

                    LogMessage($"Command: {pnputil} /add-driver \"{infPath}\" /install");

                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(output))
                        LogMessage($"pnputil: {output.Trim()}");

                    LogMessage($"{package.Name} exit code: {process.ExitCode}");

                    // Clean up temp folder
                    try
                    {
                        if (tempDriverDir != null && Directory.Exists(tempDriverDir))
                            Directory.Delete(tempDriverDir, true);
                    }
                    catch { }

                    // pnputil exit codes: 0 = success, 3010 = success + reboot required
                    if (process.ExitCode != 0 && process.ExitCode != 3010)
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            LogMessage($"pnputil error: {error.Trim()}");
                        throw new Exception($"pnputil failed with exit code {process.ExitCode}");
                    }

                    if (process.ExitCode == 3010)
                        LogMessage("Driver installed successfully (reboot recommended)");
                });
                return;
            }

            await Task.Run(() =>
            {
                Process process;
                
                if (package.InstallType == InstallationType.MSI)
                {
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "msiexec.exe",
                            Arguments = $"/i \"{package.FilePath}\" {package.InstallArgs}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            Verb = "runas"
                        }
                    };
                }
                else
                {
                    // EXE installers: use UseShellExecute=true so the installer can 
                    // properly run its own setup (some like DXSDK need a window context).
                    // We already run as admin so elevation is inherited.
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = package.FilePath,
                            Arguments = package.InstallArgs,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        }
                    };
                }

                process.Start();
                
                LogMessage($"Command: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                LogMessage($"Process started (PID: {process.Id}), waiting for exit...");
                
                // Monitor progress
                var startTime = DateTime.Now;
                while (!process.HasExited)
                {
                    var elapsed = DateTime.Now - startTime;
                    var progress = Math.Min(elapsed.TotalSeconds / 120.0 * 100, 95);
                    UpdateProgress(-1, progress);
                    
                    Task.Delay(1000).Wait();
                    
                    if (elapsed.TotalMinutes > 15)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch { }
                        throw new TimeoutException($"Installation of {package.Name} timed out after 15 minutes");
                    }
                }

                LogMessage($"{package.Name} exit code: {process.ExitCode}");

                if (process.ExitCode != 0)
                {
                    // For some installers, exit code 3010 means "restart required" which is acceptable
                    if (process.ExitCode == 3010)
                    {
                        LogMessage("Installation completed successfully (restart required)");
                        return;
                    }
                    
                    // Don't fail on all non-zero exit codes as some installers use them differently
                    LogMessage($"Installation completed with exit code {process.ExitCode}");
                }
            });
        }

        private async Task InstallViaMySQLConsoleAsync(SoftwarePackage package)
        {
            await Task.Run(() =>
            {
                string consolePath = package.FilePath;

                // Search for MySQLInstallerConsole.exe if not at the bundled path
                if (!File.Exists(consolePath))
                {
                    LogMessage($"MySQLInstallerConsole.exe not found at: {consolePath}");
                    var searchPaths = new[]
                    {
                        @"C:\Program Files (x86)\MySQL\MySQL Installer for Windows\MySQLInstallerConsole.exe",
                        @"C:\Program Files\MySQL\MySQL Installer for Windows\MySQLInstallerConsole.exe"
                    };

                    foreach (var path in searchPaths)
                    {
                        if (File.Exists(path))
                        {
                            consolePath = path;
                            LogMessage($"Found MySQLInstallerConsole.exe at: {consolePath}");
                            break;
                        }
                    }
                }

                if (!File.Exists(consolePath))
                {
                    throw new FileNotFoundException(
                        "MySQLInstallerConsole.exe not found. Ensure the MySQL Installer bootstrapper was installed first.");
                }

                LogMessage($"Running: {consolePath} {package.InstallArgs}");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = consolePath,
                        Arguments = package.InstallArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();

                LogMessage($"MySQLInstallerConsole started (PID: {process.Id}), waiting for exit...");

                var startTime = DateTime.Now;
                while (!process.HasExited)
                {
                    var elapsed = DateTime.Now - startTime;
                    var progress = Math.Min(elapsed.TotalSeconds / 180.0 * 100, 95);
                    UpdateProgress(-1, progress);
                    Task.Delay(2000).Wait();

                    if (elapsed.TotalMinutes > 15)
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException($"{package.Name} timed out after 15 minutes");
                    }
                }

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                if (!string.IsNullOrWhiteSpace(output))
                    LogMessage($"MySQLInstallerConsole output: {output.Trim()}");
                if (!string.IsNullOrWhiteSpace(error))
                    LogMessage($"MySQLInstallerConsole error: {error.Trim()}");

                LogMessage($"{package.Name} exit code: {process.ExitCode}");

                if (process.ExitCode != 0)
                {
                    LogMessage($"MySQLInstallerConsole completed with exit code {process.ExitCode}");
                }
            });
        }

        private async Task WaitForMySQLReadyAsync()
        {
            LogMessage($"Waiting for MySQL to become available on port 3306 (timeout: {_mySqlReadyTimeoutSeconds}s)...");
            var deadline = DateTime.Now.AddSeconds(_mySqlReadyTimeoutSeconds);
            while (DateTime.Now < deadline)
            {
                try
                {
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        var connectTask = client.ConnectAsync("127.0.0.1", 3306);
                        if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask && !connectTask.IsFaulted)
                        {
                            LogMessage("MySQL is ready.");
                            return;
                        }
                    }
                }
                catch { }
                await Task.Delay(1000);
            }
            throw new TimeoutException($"MySQL did not become available on port 3306 within {_mySqlReadyTimeoutSeconds} seconds.");
        }

        private async Task ConfigureMySQLAsync()
        {
            await WaitForMySQLReadyAsync();

            LogMessage("Configuring MySQL...");
            
            // Try to find MySQL installation path - search multiple locations and registry
            string mysqlPath = null;

            // Check common install paths
            var mysqlPaths = new[]
            {
                @"C:\Program Files\MySQL\MySQL Server 8.0\bin\mysql.exe",
                @"C:\Program Files (x86)\MySQL\MySQL Server 8.0\bin\mysql.exe",
                @"C:\Program Files\MySQL\MySQL Server 5.7\bin\mysql.exe",
                @"C:\MySQL\bin\mysql.exe"
            };

            foreach (var path in mysqlPaths)
            {
                if (File.Exists(path))
                {
                    mysqlPath = path;
                    break;
                }
            }

            // If not found, try to find via registry
            if (mysqlPath == null)
            {
                LogMessage("MySQL not found in standard paths, checking registry...");
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MySQL AB\MySQL Server 8.0"))
                    {
                        if (key != null)
                        {
                            var location = key.GetValue("Location") as string;
                            if (!string.IsNullOrEmpty(location))
                            {
                                var regPath = Path.Combine(location, "bin", "mysql.exe");
                                if (File.Exists(regPath))
                                {
                                    mysqlPath = regPath;
                                    LogMessage($"Found MySQL via registry: {mysqlPath}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { LogMessage($"Registry check failed: {ex.Message}"); }
            }

            // If still not found, search Program Files recursively
            if (mysqlPath == null)
            {
                LogMessage("Searching for mysql.exe in Program Files...");
                try
                {
                    var searchDirs = new[] { @"C:\Program Files\MySQL", @"C:\Program Files (x86)\MySQL" };
                    foreach (var dir in searchDirs)
                    {
                        if (Directory.Exists(dir))
                        {
                            var found = Directory.GetFiles(dir, "mysql.exe", SearchOption.AllDirectories);
                            if (found.Length > 0)
                            {
                                mysqlPath = found[0];
                                LogMessage($"Found MySQL via search: {mysqlPath}");
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) { LogMessage($"File search failed: {ex.Message}"); }
            }

            if (mysqlPath == null)
            {
                LogMessage("MySQL command line client not found in standard locations");
                throw new FileNotFoundException("MySQL command line client not found");
            }

            LogMessage($"Found MySQL at: {mysqlPath}");

            // Wait for MySQL service
            await WaitForMySQLServiceAsync();
            
            LogMessage("Executing database script...");
            
            // Create database
            try
            {
                var createDbProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = mysqlPath,
                        Arguments = "-u root -proot -e \"CREATE DATABASE IF NOT EXISTS dcmdetails;\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                createDbProcess.Start();
                createDbProcess.WaitForExit();
                
                if (createDbProcess.ExitCode == 0)
                {
                    LogMessage("Database 'dcmdetails' created successfully");
                }
                else
                {
                    var error = createDbProcess.StandardError.ReadToEnd();
                    LogMessage($"Database creation warning/error: {error}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Database creation failed: {ex.Message}");
                throw;
            }

            // Execute SQL script
            var sqlFile = Path.Combine(_basePath, @"MySQL_JavaJRE_DB_Installer-20231002T070835Z-001\MySQL_JavaJRE_DB_Installer\dcmdetails_20251217.sql");
            if (File.Exists(sqlFile))
            {
                try
                {
                    // Use mysql.exe directly with source command - avoids cmd.exe quoting issues
                    // Forward slashes are required in the source path for MySQL
                    string sourcePath = sqlFile.Replace(@"\", "/");
                    LogMessage($"Executing SQL script: {sqlFile}");

                    var sqlProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = mysqlPath,
                            Arguments = $"-u root -proot dcmdetails -e \"source {sourcePath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };

                    sqlProcess.Start();
                    string output = sqlProcess.StandardOutput.ReadToEnd();
                    string error = sqlProcess.StandardError.ReadToEnd();
                    sqlProcess.WaitForExit();
                    
                    if (sqlProcess.ExitCode == 0)
                    {
                        LogMessage("SQL script executed successfully");
                    }
                    else
                    {
                        LogMessage($"SQL script execution warning (exit {sqlProcess.ExitCode}): {error}");
                    }

                    if (!string.IsNullOrWhiteSpace(output))
                        LogMessage($"SQL output: {output.Trim()}");
                }
                catch (Exception ex)
                {
                    LogMessage($"SQL script execution failed: {ex.Message}");
                    throw;
                }
            }
        }

        private async Task WaitForMySQLServiceAsync()
        {
            var maxWait = TimeSpan.FromMinutes(5);
            var startTime = DateTime.Now;
            
            LogMessage("Checking for MySQL service...");
            
            while (DateTime.Now - startTime < maxWait)
            {
                try
                {
                    var serviceProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "sc.exe",
                            Arguments = "query MySQL80",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };

                    serviceProcess.Start();
                    var output = serviceProcess.StandardOutput.ReadToEnd();
                    serviceProcess.WaitForExit();
                    
                    if (output.Contains("RUNNING"))
                    {
                        LogMessage("MySQL service is running");
                        return;
                    }
                    
                    LogMessage("MySQL service not yet running, waiting...");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error checking MySQL service: {ex.Message}");
                }
                
                await Task.Delay(5000); // Wait 5 seconds before checking again
            }
            
            LogMessage("MySQL service check timed out, attempting to continue anyway...");
        }

        private static bool IsRunningAsAdministrator()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private void UpdateProgress(double overallProgress, double currentProgress)
        {
            ProgressUpdated?.Invoke(this, new ProgressEventArgs 
            { 
                OverallProgress = overallProgress >= 0 ? overallProgress : -1,
                CurrentProgress = currentProgress 
            });
        }

        private void UpdateStatus(string message, string currentItem, int itemIndex, string status)
        {
            StatusUpdated?.Invoke(this, new StatusEventArgs 
            { 
                Message = message,
                CurrentItem = currentItem,
                ItemIndex = itemIndex,
                Status = status
            });
        }

        private void LogMessage(string message)
        {
            LogUpdated?.Invoke(this, new LogEventArgs { Message = message });
            WriteToLogFile(message);
        }

        private void WriteToLogFile(string message)
        {
            try
            {
                if (!string.IsNullOrEmpty(_logFilePath))
                {
                    File.AppendAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch { /* Silently ignore file write errors */ }
        }

        private void CleanupGetAdminVbs()
        {
            try
            {
                // Kill any lingering wscript.exe that may hold a lock on getadmin.vbs
                foreach (var proc in Process.GetProcessesByName("wscript"))
                {
                    try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                }
                // Delete the stale temp file
                string getAdminPath = Path.Combine(Path.GetTempPath(), "getadmin.vbs");
                if (File.Exists(getAdminPath))
                {
                    File.Delete(getAdminPath);
                }
            }
            catch { /* best-effort cleanup */ }
        }

        private async Task RunPostInstallScriptsAsync()
        {
            bool allScriptsOk = true;

            if (_postInstallScriptsConfig.Scripts != null)
            {
                foreach (string scriptPath in _postInstallScriptsConfig.Scripts)
                {
                    string resolvedPath = scriptPath.Replace(@"\\", @"\");
                    LogMessage($"Running post-install script: {resolvedPath}");

                    if (!File.Exists(resolvedPath))
                    {
                        LogMessage($"WARNING: Script not found: {resolvedPath} - skipping");
                        continue;
                    }

                    // Clean up stale getadmin.vbs from previous script's self-elevation to prevent file lock errors
                    CleanupGetAdminVbs();

                    try
                    {
                        // Use 64-bit cmd.exe to avoid WOW64 filesystem redirection.
                        // Vendor scripts check admin via cacls.exe %SYSTEMROOT%\system32\config\system
                        // which fails under 32-bit cmd because system32 redirects to SysWOW64.
                        string cmd64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysNative", "cmd.exe");
                        if (!File.Exists(cmd64)) cmd64 = "cmd.exe";
                        var psi = new ProcessStartInfo
                        {
                            FileName = cmd64,
                            Arguments = $"/c \"\"{resolvedPath}\"\"",
                            WorkingDirectory = Path.GetDirectoryName(resolvedPath),
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using (var process = Process.Start(psi))
                        {
                            LogMessage($"Script started (PID: {process.Id}), waiting for exit...");
                            string stdout = await process.StandardOutput.ReadToEndAsync();
                            string stderr = await process.StandardError.ReadToEndAsync();
                            process.WaitForExit(300000); // 5 minute timeout per script
                            
                            if (!string.IsNullOrWhiteSpace(stdout)) LogMessage($"Script output: {stdout.Trim()}");
                            if (!string.IsNullOrWhiteSpace(stderr)) LogMessage($"Script errors: {stderr.Trim()}");
                            LogMessage($"Script exit code: {process.ExitCode}");

                            if (process.ExitCode != 0)
                            {
                                LogMessage($"WARNING: Script returned non-zero exit code: {process.ExitCode}");
                                allScriptsOk = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Script execution failed: {ex.Message}");
                        allScriptsOk = false;
                    }
                }
            }

            // Run cleanup script only if all scripts succeeded
            if (allScriptsOk && !string.IsNullOrEmpty(_postInstallScriptsConfig.CleanupScript))
            {
                string cleanupPath = _postInstallScriptsConfig.CleanupScript.Replace(@"\\", @"\");
                LogMessage($"All scripts succeeded. Running cleanup: {cleanupPath}");

                if (File.Exists(cleanupPath))
                {
                    CleanupGetAdminVbs();

                    try
                    {
                        string cmd64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysNative", "cmd.exe");
                        if (!File.Exists(cmd64)) cmd64 = "cmd.exe";
                        var psi = new ProcessStartInfo
                        {
                            FileName = cmd64,
                            Arguments = $"/c \"\"{cleanupPath}\"\"",
                            WorkingDirectory = Path.GetDirectoryName(cleanupPath),
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using (var process = Process.Start(psi))
                        {
                            string stdout = await process.StandardOutput.ReadToEndAsync();
                            string stderr = await process.StandardError.ReadToEndAsync();
                            process.WaitForExit(300000);
                            if (!string.IsNullOrWhiteSpace(stdout)) LogMessage($"Cleanup output: {stdout.Trim()}");
                            if (!string.IsNullOrWhiteSpace(stderr)) LogMessage($"Cleanup errors: {stderr.Trim()}");
                            LogMessage($"Cleanup exit code: {process.ExitCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Cleanup script failed: {ex.Message}");
                    }
                }
                else
                {
                    LogMessage($"WARNING: Cleanup script not found: {cleanupPath} - skipping");
                }
            }
            else if (!allScriptsOk)
            {
                LogMessage("Skipping cleanup script because one or more post-install scripts failed.");
                throw new Exception("One or more post-install scripts failed.");
            }
        }

        private async Task RunOsConfigurationAsync()
        {
            string exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string osConfigExePath = Path.GetFullPath(Path.Combine(exeDirectory, _osConfigurationConfig.RelativePath.Replace(@"\\", @"\")));
            string osConfigSourceDir = Path.GetDirectoryName(osConfigExePath);
            string exeName = Path.GetFileName(osConfigExePath);

            if (!Directory.Exists(osConfigSourceDir))
            {
                throw new DirectoryNotFoundException($"OS configurator folder not found: {osConfigSourceDir}");
            }

            // Copy EikonConfigurator to C:\ so it runs from a local fixed drive.
            // EikonConfigurator auto-migrates from network/removable drives, which would
            // break our stdout capture. Pre-copying to C:\ avoids this.
            string localDir = @"C:\EikonConfigurator";
            LogMessage($"Copying OS configurator to {localDir}...");
            if (Directory.Exists(localDir))
            {
                try { Directory.Delete(localDir, true); } catch { }
            }
            CopyDirectory(osConfigSourceDir, localDir);

            string localExePath = Path.Combine(localDir, exeName);
            if (!File.Exists(localExePath))
            {
                throw new FileNotFoundException($"OS configurator not found: {localExePath}");
            }

            LogMessage($"Launching: {localExePath}");

            // Resolve log path: config value takes precedence; otherwise default to the install folder.
            string eikonLogPath = !string.IsNullOrEmpty(_osConfigurationConfig.LogPath)
                ? _osConfigurationConfig.LogPath
                : Path.Combine(exeDirectory, "installer_log.txt");

            // Fresh install — remove any leftover log from a previous attempt so the file starts clean.
            try { if (File.Exists(eikonLogPath)) File.Delete(eikonLogPath); } catch { }

            var argsBuilder = new System.Text.StringBuilder();
            if (_osConfigurationConfig.BypassHardwareCheck)
                argsBuilder.Append("--bypass-hardware-check ");
            argsBuilder.Append($"--log-path \"{eikonLogPath}\" ");
            if (_osConfigurationConfig.DryRun)
                argsBuilder.Append("--dry-run ");
            if (!string.IsNullOrEmpty(_osConfigurationConfig.AdminPassword))
                argsBuilder.Append($"--admin-password \"{_osConfigurationConfig.AdminPassword}\" ");
            string osConfigArgs = argsBuilder.ToString().TrimEnd();
            LogMessage($"Arguments: {(string.IsNullOrEmpty(osConfigArgs) ? "(none)" : osConfigArgs)}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = localExePath,
                    Arguments = osConfigArgs,
                    WorkingDirectory = localDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            LogMessage($"EikonConfigurator started (PID: {process.Id}), running 19 configuration steps...");

            // Stream stdout/stderr to the WPF log in real time
            var outputTask = Task.Run(() =>
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        LogMessage($"[OS Config] {line}");
                }
            });

            var errorTask = Task.Run(() =>
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        LogMessage($"[OS Config ERROR] {line}");
                }
            });

            // Monitor progress while waiting
            var startTime = DateTime.Now;
            while (!process.HasExited)
            {
                var elapsed = DateTime.Now - startTime;
                var progress = Math.Min(elapsed.TotalSeconds / 600.0 * 100, 95); // ~10 min estimate
                UpdateProgress(-1, progress);
                await Task.Delay(2000);
                // EikonConfigurator may trigger a reboot (shutdown.exe) and then exit.
                // No timeout - the reboot will terminate everything.
            }

            await Task.WhenAll(outputTask, errorTask);
            LogMessage($"EikonConfigurator exit code: {process.ExitCode}");

            // Exit code 0 = success (may have triggered a system reboot)
            // EikonConfigurator handles its own reboot logic via shutdown.exe
            if (process.ExitCode != 0)
            {
                throw new Exception($"OS configuration failed (exit code {process.ExitCode})");
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            if (File.Exists(sourceDir))
            {
                // Single file copy
                Directory.CreateDirectory(Path.GetDirectoryName(destDir));
                File.Copy(sourceDir, destDir, true);
                return;
            }

            // Directory copy
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private async Task InstallViaDismAsync(SoftwarePackage package)
        {
            string featureName = package.InstallArgs; // e.g., "NetFx3"

            await Task.Run(() =>
            {
                // Check if the feature is already enabled
                LogMessage($"Checking if {featureName} is already enabled...");
                var checkProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Online /Get-FeatureInfo /FeatureName:{featureName}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                checkProcess.Start();
                string checkOutput = checkProcess.StandardOutput.ReadToEnd();
                checkProcess.WaitForExit();
                LogMessage($"DISM feature check output (excerpt): {(checkOutput.Length > 200 ? checkOutput.Substring(0, 200) : checkOutput)}");

                if (checkOutput.Contains("State : Enabled"))
                {
                    LogMessage($"{featureName} is already enabled. Skipping.");
                    return;
                }

                // Extract cab files from 7z archive using 7-Zip
                string archivePath = package.FilePath;
                string extractDir = Path.Combine(Path.GetTempPath(), "NetFx3_sxs");

                if (Directory.Exists(extractDir))
                {
                    try { Directory.Delete(extractDir, true); } catch { }
                }
                Directory.CreateDirectory(extractDir);

                // Try to find 7-Zip (we just installed it)
                string sevenZipPath = @"C:\Program Files\7-Zip\7z.exe";
                if (!File.Exists(sevenZipPath))
                {
                    sevenZipPath = @"C:\Program Files (x86)\7-Zip\7z.exe";
                }

                if (File.Exists(sevenZipPath) && File.Exists(archivePath))
                {
                    LogMessage($"Extracting .NET 3.5 source files from {Path.GetFileName(archivePath)}...");
                    var extractProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = sevenZipPath,
                            Arguments = $"x \"{archivePath}\" -o\"{extractDir}\" -y",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    extractProcess.Start();
                    extractProcess.WaitForExit();
                    LogMessage($"7-Zip extraction exit code: {extractProcess.ExitCode}");
                }
                else
                {
                    LogMessage("WARNING: 7-Zip not found or archive missing. Trying DISM without source...");
                }

                // Find the sxs/source folder (cab files may be in a subfolder)
                string sourceDir = extractDir;
                var cabFiles = Directory.GetFiles(extractDir, "*.cab", SearchOption.AllDirectories);
                if (cabFiles.Length > 0)
                {
                    sourceDir = Path.GetDirectoryName(cabFiles[0]);
                    LogMessage($"Found {cabFiles.Length} cab file(s) in: {sourceDir}");
                }
                else
                {
                    LogMessage("WARNING: No .cab files found in extracted archive.");
                }

                // Run DISM to enable the feature
                string dismArgs;
                if (cabFiles.Length > 0)
                {
                    dismArgs = $"/Online /Enable-Feature /FeatureName:{featureName} /All /Source:\"{sourceDir}\" /LimitAccess /NoRestart";
                }
                else
                {
                    // Try without source (will need internet or Windows source)
                    dismArgs = $"/Online /Enable-Feature /FeatureName:{featureName} /All /NoRestart";
                }

                LogMessage($"Running: dism.exe {dismArgs}");
                var dismProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = dismArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                dismProcess.Start();

                // Monitor with timeout (DISM can take a while)
                var startTime = DateTime.Now;
                while (!dismProcess.HasExited)
                {
                    var elapsed = DateTime.Now - startTime;
                    var progress = Math.Min(elapsed.TotalSeconds / 180.0 * 100, 95);
                    UpdateProgress(-1, progress);
                    Task.Delay(2000).Wait();

                    if (elapsed.TotalMinutes > 20)
                    {
                        try { dismProcess.Kill(); } catch { }
                        throw new TimeoutException($"DISM {featureName} timed out after 20 minutes");
                    }
                }

                string dismOutput = dismProcess.StandardOutput.ReadToEnd();
                string dismError = dismProcess.StandardError.ReadToEnd();
                LogMessage($"DISM exit code: {dismProcess.ExitCode}");
                if (!string.IsNullOrWhiteSpace(dismOutput))
                {
                    LogMessage($"DISM output: {(dismOutput.Length > 500 ? dismOutput.Substring(0, 500) : dismOutput)}");
                }
                if (!string.IsNullOrWhiteSpace(dismError))
                {
                    LogMessage($"DISM error: {dismError}");
                }

                // Clean up temp dir
                try { Directory.Delete(extractDir, true); } catch { }

                if (dismProcess.ExitCode != 0 && dismProcess.ExitCode != 3010)
                {
                    throw new Exception($"DISM failed with exit code {dismProcess.ExitCode}");
                }

                if (dismProcess.ExitCode == 3010)
                {
                    LogMessage($"{featureName} enabled successfully (restart may be required)");
                }
                else
                {
                    LogMessage($"{featureName} enabled successfully");
                }
            });
        }

        private async Task PartitionDiskAsync()
        {
            await Task.Run(() =>
            {
                int diskNumber = _diskPartitioningConfig.DiskNumber;
                var partitions = _diskPartitioningConfig.Partitions;

                if (partitions == null || partitions.Count == 0)
                {
                    LogMessage("No partitions configured, skipping.");
                    return;
                }

                // Find the primary partition (C:) to shrink
                var primaryPartition = partitions.Find(p => p.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase));
                if (primaryPartition == null)
                {
                    LogMessage("No C: partition configuration found, skipping.");
                    return;
                }

                // Check if additional partitions already exist
                var newPartitions = partitions.FindAll(p => !p.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase));
                bool allExist = true;
                foreach (var np in newPartitions)
                {
                    string drivePath = np.DriveLetter + ":\\";
                    if (!Directory.Exists(drivePath))
                    {
                        allExist = false;
                        break;
                    }
                }

                if (allExist && newPartitions.Count > 0)
                {
                    LogMessage("All configured partitions already exist. Skipping partitioning.");
                    return;
                }

                // Step 1: Shrink C: to the configured size
                long targetSizeBytes = (long)primaryPartition.SizeGB * 1024L * 1024L * 1024L;
                LogMessage($"Shrinking C: to {primaryPartition.SizeGB} GB...");

                string shrinkScript = $@"
                    $partition = Get-Partition -DriveLetter C
                    $currentSize = $partition.Size
                    $targetSize = {targetSizeBytes}
                    if ($currentSize -le $targetSize) {{
                        Write-Output 'C: is already at or below target size. Skipping shrink.'
                    }} else {{
                        $supported = Get-PartitionSupportedSize -DriveLetter C
                        if ($targetSize -lt $supported.SizeMin) {{
                            throw 'Target size is below minimum supported size for C:'
                        }}
                        Resize-Partition -DriveLetter C -Size $targetSize
                        Write-Output 'C: partition resized successfully.'
                    }}
                ";

                RunPowerShellScript(shrinkScript);

                // Step 2: Create new partitions from unallocated space
                // Create without drive letter first, format, then assign letter to avoid Windows "Format disk" popup
                foreach (var partConfig in newPartitions)
                {
                    LogMessage($"Creating {partConfig.DriveLetter}: partition ({(partConfig.UseRemainingSpace ? "remaining space" : partConfig.SizeGB + " GB")})...");

                    string createScript;
                    if (partConfig.UseRemainingSpace)
                    {
                        createScript = $@"
                            $newPart = New-Partition -DiskNumber {diskNumber} -UseMaximumSize
                            $partNumber = $newPart.PartitionNumber
                            Format-Volume -Partition $newPart -FileSystem NTFS -NewFileSystemLabel '{partConfig.Label}' -Confirm:$false
                            Set-Partition -DiskNumber {diskNumber} -PartitionNumber $partNumber -NewDriveLetter '{partConfig.DriveLetter}'
                            Get-Volume -DriveLetter '{partConfig.DriveLetter}' | Format-Table -AutoSize
                            Write-Output '{partConfig.DriveLetter}: partition created with remaining space.'
                        ";
                    }
                    else
                    {
                        long partSizeBytes = (long)partConfig.SizeGB * 1024L * 1024L * 1024L;
                        createScript = $@"
                            $newPart = New-Partition -DiskNumber {diskNumber} -Size {partSizeBytes}
                            $partNumber = $newPart.PartitionNumber
                            Format-Volume -Partition $newPart -FileSystem NTFS -NewFileSystemLabel '{partConfig.Label}' -Confirm:$false
                            Set-Partition -DiskNumber {diskNumber} -PartitionNumber $partNumber -NewDriveLetter '{partConfig.DriveLetter}'
                            Get-Volume -DriveLetter '{partConfig.DriveLetter}' | Format-Table -AutoSize
                            Write-Output '{partConfig.DriveLetter}: partition created ({partConfig.SizeGB} GB).'
                        ";
                    }

                    RunPowerShellScript(createScript);
                }

                LogMessage("All partitions configured successfully.");
            });
        }

        private void RunPowerShellScript(string script)
        {
            // Write script to a temp file to avoid quoting issues
            string tempScript = Path.Combine(Path.GetTempPath(), "oci_partition_" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(tempScript, script);

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    LogMessage(output.Trim());
                }

                if (process.ExitCode != 0)
                {
                    LogMessage($"PowerShell error: {error}");
                    throw new Exception($"PowerShell script failed (exit code {process.ExitCode}): {error}");
                }
            }
            finally
            {
                try { File.Delete(tempScript); } catch { }
            }
        }
    }

    public class SoftwarePackage
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public string InstallArgs { get; set; }
        public InstallationType InstallType { get; set; }
        public string CopyDestination { get; set; }
    }

    public enum InstallationType
    {
        MSI,
        EXE,
        COPY,
        DISM,
        INF,
        MYSQL_CONSOLE
    }
}