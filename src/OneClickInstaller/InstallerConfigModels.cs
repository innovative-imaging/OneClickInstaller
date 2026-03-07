using System.Collections.Generic;

namespace OneClickInstaller
{
    // Simple JSON parser for basic configuration (avoiding external dependencies)
    public class SimpleJsonParser
    {
        public static InstallerConfig Parse(string json)
        {
            var config = new InstallerConfig();
            config.InstallerConfigInfo = new InstallerInfo();
            config.SoftwarePackages = new List<PackageConfig>();
            config.MySQLConfig = new MySQLConfig();
            config.Settings = new SettingsConfig();
            
            // Extract installer config
            config.InstallerConfigInfo.Name = ExtractStringValue(json, "name");
            config.InstallerConfigInfo.Version = ExtractStringValue(json, "version");
            config.InstallerConfigInfo.Description = ExtractStringValue(json, "description");
            
            // Extract MySQL config
            config.MySQLConfig.Username = ExtractStringValue(json, "username");
            config.MySQLConfig.Password = ExtractStringValue(json, "password");
            config.MySQLConfig.Database = ExtractStringValue(json, "database");
            config.MySQLConfig.SqlScript = ExtractStringValue(json, "sqlScript");
            
            // Extract settings
            config.Settings.MaxInstallTimeMinutes = ExtractIntValue(json, "maxInstallTimeMinutes", 15);
            
            // Extract software packages
            string packagesSection = ExtractSection(json, "softwarePackages");
            config.SoftwarePackages = ParsePackages(packagesSection);

            // Extract final install package (installed last, after DB config)
            string finalSection = ExtractObjectSection(json, "finalInstallPackage");
            if (!string.IsNullOrEmpty(finalSection))
            {
                config.FinalInstallPackage = new FinalPackageConfig
                {
                    Name = ExtractStringValue(finalSection, "name"),
                    RelativePath = ExtractStringValue(finalSection, "relativePath"),
                    InstallType = ExtractStringValue(finalSection, "installType"),
                    InstallArgs = ExtractStringValue(finalSection, "installArgs"),
                    CopyDestination = ExtractStringValue(finalSection, "copyDestination"),
                    Description = ExtractStringValue(finalSection, "description")
                };
            }

            // Extract disk partitioning config
            string partSection = ExtractObjectSection(json, "diskPartitioning");
            if (!string.IsNullOrEmpty(partSection))
            {
                config.DiskPartitioning = new DiskPartitioningConfig
                {
                    Enabled = ExtractBoolValue(partSection, "enabled", false),
                    DiskNumber = ExtractIntValue(partSection, "diskNumber", 0),
                    Partitions = new List<PartitionConfig>()
                };

                // Parse partitions array
                string partitionsSection = ExtractSection(partSection, "partitions");
                if (!string.IsNullOrEmpty(partitionsSection))
                {
                    var partPattern = @"\{([^}]+)\}";
                    var partMatches = System.Text.RegularExpressions.Regex.Matches(partitionsSection, partPattern);
                    foreach (System.Text.RegularExpressions.Match pm in partMatches)
                    {
                        var pJson = pm.Groups[1].Value;
                        config.DiskPartitioning.Partitions.Add(new PartitionConfig
                        {
                            DriveLetter = ExtractStringValue(pJson, "driveLetter"),
                            SizeGB = ExtractIntValue(pJson, "sizeGB", 0),
                            Label = ExtractStringValue(pJson, "label"),
                            UseRemainingSpace = ExtractBoolValue(pJson, "useRemainingSpace", false)
                        });
                    }
                }
            }

            // Extract OS configuration config
            string osConfigSection = ExtractObjectSection(json, "osConfiguration");
            if (!string.IsNullOrEmpty(osConfigSection))
            {
                config.OsConfiguration = new OsConfigurationConfig
                {
                    Enabled = ExtractBoolValue(osConfigSection, "enabled", false),
                    RelativePath = ExtractStringValue(osConfigSection, "relativePath"),
                    Description = ExtractStringValue(osConfigSection, "description"),
                    AutoReboot = ExtractBoolValue(osConfigSection, "autoReboot", true),
                    BypassHardwareCheck = ExtractBoolValue(osConfigSection, "bypassHardwareCheck", false),
                    LogPath = ExtractNullableStringValue(osConfigSection, "logPath"),
                    DryRun = ExtractBoolValue(osConfigSection, "dryRun", false),
                    AdminPassword = ExtractNullableStringValue(osConfigSection, "adminPassword")
                };
            }

            // Extract post-install scripts config
            string postScriptsSection = ExtractObjectSection(json, "postInstallScripts");
            if (!string.IsNullOrEmpty(postScriptsSection))
            {
                config.PostInstallScripts = new PostInstallScriptsConfig
                {
                    Enabled = ExtractBoolValue(postScriptsSection, "enabled", false),
                    Description = ExtractStringValue(postScriptsSection, "description"),
                    CleanupScript = ExtractStringValue(postScriptsSection, "cleanupScript"),
                    Scripts = new List<string>()
                };

                // Parse the "scripts" array of strings
                string scriptsArray = ExtractSection(postScriptsSection, "scripts");
                if (!string.IsNullOrEmpty(scriptsArray))
                {
                    var strPattern = new System.Text.RegularExpressions.Regex("\"([^\"]+)\"");
                    foreach (System.Text.RegularExpressions.Match sm in strPattern.Matches(scriptsArray))
                    {
                        config.PostInstallScripts.Scripts.Add(sm.Groups[1].Value.Replace("\\\\", "\\"));
                    }
                }
            }

            return config;
        }
        
        private static string ExtractStringValue(string json, string key)
        {
            var pattern = "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            return match.Success ? match.Groups[1].Value : "";
        }

        // Returns null if the field is absent or explicitly set to null; empty string if set to "".
        private static string ExtractNullableStringValue(string json, string key)
        {
            var nullPattern = "\"" + key + "\"\\s*:\\s*null";
            if (System.Text.RegularExpressions.Regex.IsMatch(json, nullPattern))
                return null;
            var strPattern = "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"";
            var match = System.Text.RegularExpressions.Regex.Match(json, strPattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static string ParseMySQLPassword(string secretsJson)
        {
            return ExtractStringValue(secretsJson, "mysqlPassword");
        }
        
        private static int ExtractIntValue(string json, string key, int defaultValue)
        {
            var pattern = "\"" + key + "\"\\s*:\\s*(\\d+)";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            return match.Success ? int.Parse(match.Groups[1].Value) : defaultValue;
        }

        private static bool ExtractBoolValue(string json, string key, bool defaultValue)
        {
            var pattern = "\"" + key + "\"\\s*:\\s*(true|false)";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Equals("true", System.StringComparison.OrdinalIgnoreCase) : defaultValue;
        }
        
        private static string ExtractSection(string json, string sectionName)
        {
            var startPattern = "\"" + sectionName + "\"\\s*:\\s*\\[";
            var startMatch = System.Text.RegularExpressions.Regex.Match(json, startPattern);
            if (!startMatch.Success) return "";
            
            int startPos = startMatch.Index + startMatch.Length;
            int braceCount = 1;
            int pos = startPos;
            
            while (pos < json.Length && braceCount > 0)
            {
                if (json[pos] == '[' || json[pos] == '{') braceCount++;
                if (json[pos] == ']' || json[pos] == '}') braceCount--;
                pos++;
            }
            
            return json.Substring(startPos, pos - startPos - 1);
        }

        private static string ExtractObjectSection(string json, string sectionName)
        {
            var startPattern = "\"" + sectionName + "\"\\s*:\\s*\\{";
            var startMatch = System.Text.RegularExpressions.Regex.Match(json, startPattern);
            if (!startMatch.Success) return "";

            int startPos = startMatch.Index + startMatch.Length;
            int braceCount = 1;
            int pos = startPos;

            while (pos < json.Length && braceCount > 0)
            {
                if (json[pos] == '{') braceCount++;
                if (json[pos] == '}') braceCount--;
                pos++;
            }

            return json.Substring(startPos, pos - startPos - 1);
        }
        
        private static List<PackageConfig> ParsePackages(string packagesJson)
        {
            var packages = new List<PackageConfig>();
            // Parse top-level objects handling nested braces (e.g. postInstall)
            var objectBlocks = ExtractTopLevelObjects(packagesJson);
            
            foreach (var packageJson in objectBlocks)
            {
                string name = ExtractStringValue(packageJson, "name");
                if (string.IsNullOrEmpty(name)) continue; // skip empty/noise
                
                var package = new PackageConfig
                {
                    Name = name,
                    RelativePath = ExtractStringValue(packageJson, "relativePath"),
                    InstallType = ExtractStringValue(packageJson, "installType"),
                    InstallArgs = ExtractStringValue(packageJson, "installArgs"),
                    CopyDestination = ExtractStringValue(packageJson, "copyDestination"),
                    Required = packageJson.Contains("\"required\"") ? ExtractBoolValue(packageJson, "required", true) : true,
                    Description = ExtractStringValue(packageJson, "description")
                };
                
                if (packageJson.Contains("postInstall"))
                {
                    package.PostInstall = new PostInstallConfig
                    {
                        ConfigureMySQL = ExtractBoolValue(packageJson, "configureMySQL", false),
                        TimeoutSeconds = ExtractIntValue(packageJson, "timeoutSeconds", 60)
                    };
                }
                
                packages.Add(package);
            }
            
            return packages;
        }

        private static List<string> ExtractTopLevelObjects(string json)
        {
            var objects = new List<string>();
            int i = 0;
            while (i < json.Length)
            {
                if (json[i] == '{')
                {
                    int start = i + 1;
                    int depth = 1;
                    i++;
                    while (i < json.Length && depth > 0)
                    {
                        if (json[i] == '{') depth++;
                        else if (json[i] == '}') depth--;
                        i++;
                    }
                    objects.Add(json.Substring(start, i - start - 1));
                }
                else
                {
                    i++;
                }
            }
            return objects;
        }
    }

    public class InstallerConfig
    {
        public InstallerInfo InstallerConfigInfo { get; set; }
        public List<PackageConfig> SoftwarePackages { get; set; }
        public FinalPackageConfig FinalInstallPackage { get; set; }
        public DiskPartitioningConfig DiskPartitioning { get; set; }
        public MySQLConfig MySQLConfig { get; set; }
        public SettingsConfig Settings { get; set; }
        public PostInstallScriptsConfig PostInstallScripts { get; set; }
        public OsConfigurationConfig OsConfiguration { get; set; }
    }

    public class InstallerInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
    }

    public class PackageConfig
    {
        public string Name { get; set; }
        public string RelativePath { get; set; }
        public string InstallType { get; set; }
        public string InstallArgs { get; set; }
        public string CopyDestination { get; set; }
        public bool Required { get; set; }
        public string Description { get; set; }
        public PostInstallConfig PostInstall { get; set; }
    }

    public class PostInstallConfig
    {
        public bool ConfigureMySQL { get; set; }
        public int TimeoutSeconds { get; set; }
    }

    public class MySQLConfig
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string Database { get; set; }
        public string SqlScript { get; set; }
    }

    public class FinalPackageConfig
    {
        public string Name { get; set; }
        public string RelativePath { get; set; }
        public string InstallType { get; set; }
        public string InstallArgs { get; set; }
        public string CopyDestination { get; set; }
        public string Description { get; set; }
    }

    public class DiskPartitioningConfig
    {
        public bool Enabled { get; set; }
        public int DiskNumber { get; set; }
        public List<PartitionConfig> Partitions { get; set; }
    }

    public class PartitionConfig
    {
        public string DriveLetter { get; set; }
        public int SizeGB { get; set; }
        public string Label { get; set; }
        public bool UseRemainingSpace { get; set; }
    }

    public class SettingsConfig
    {
        public int MaxInstallTimeMinutes { get; set; }
        public int RetryAttempts { get; set; }
        public bool ShowDetailedProgress { get; set; }
        public bool CreateInstallLog { get; set; }
    }

    public class PostInstallScriptsConfig
    {
        public bool Enabled { get; set; }
        public string Description { get; set; }
        public List<string> Scripts { get; set; }
        public string CleanupScript { get; set; }
    }

    public class OsConfigurationConfig
    {
        public bool Enabled { get; set; }
        public string RelativePath { get; set; }
        public string Description { get; set; }
        public bool AutoReboot { get; set; }
        public bool BypassHardwareCheck { get; set; }
        public string LogPath { get; set; }
        public bool DryRun { get; set; }
        public string AdminPassword { get; set; }
    }
}
