using System;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Security.AccessControl;
using Xunit;
using EikonConfigurator;

namespace EikonConfigurator.Tests
{
    public class RequirementTests : IDisposable
    {
        private List<string> _executedCommands;
        private List<string> _registryChanges;
        
        // Save originals to restore
        private Action<string, string> _originalCommandExecutor;
        private Action<RegistryKey, string, string, object, RegistryValueKind> _originalRegistrySetter;
        private Action<RegistryKey, string, object, RegistryValueKind> _originalKeySetter;
        private Action<string, string, object, RegistryValueKind> _originalStaticSetter;
        private Func<RegistryKey, string, bool, RegistryKey> _originalKeyOpener;
        private Func<RegistryKey, string, RegistryKeyPermissionCheck, RegistryRights, RegistryKey> _originalKeyOpenerWithRights;
        private Func<RegistryKey, string, RegistryKey> _originalKeyCreator;
        private Action<string> _originalServiceStopper;

        public RequirementTests()
        {
            _executedCommands = new List<string>();
            _registryChanges = new List<string>();

            // Save originals
            _originalCommandExecutor = Program.CommandExecutor;
            _originalRegistrySetter = Program.RegistrySetter;
            _originalKeySetter = Program.RegistryKeySetter;
            _originalStaticSetter = Program.RegistryStaticSetter;
            _originalKeyOpener = Program.KeyOpener;
            _originalKeyOpenerWithRights = Program.KeyOpenerWithRights;
            _originalKeyCreator = Program.KeyCreator;
            _originalServiceStopper = Program.ServiceStopper;

            // Hook into the static Program class
            Program.CommandExecutor = (fileName, args) =>
            {
                _executedCommands.Add($"{fileName} {args}");
            };

            // Hook for SetRegistryValue(root, subkey...)
            Program.RegistrySetter = (root, key, name, val, kind) =>
            {
                string rootName = root.Name; 
                _registryChanges.Add($"{rootName}\\{key}\\{name}={val}");
            };

            // Hook for SafeSet(key, name...)
            Program.RegistryKeySetter = (key, name, val, kind) => 
            {
                 // We don't have the subkey path here easily, just the key Name (or Mock)
                 string kName = key?.Name ?? "MockKey";
                 _registryChanges.Add($"{kName}\\{name}={val}");
            };

            // Hook for SafeSetStatic(path, name...)
            Program.RegistryStaticSetter = (path, name, val, kind) =>
            {
                 _registryChanges.Add($"{path}\\{name}={val}");
            };

            // Mock KeyOpener/Creator to return NULL (prevents real access, avoids disposing real keys)
            Program.KeyOpener = (root, sub, wr) => null;
            Program.KeyOpenerWithRights = (root, sub, check, rights) => null;
            Program.KeyCreator = (root, sub) => null;

            Program.ServiceStopper = (svc) => 
            {
                 // Mock: Do nothing (simulate stopped service)
            };
        }

        public void Dispose()
        {
            // Restore hooks
            Program.CommandExecutor = _originalCommandExecutor;
            Program.RegistrySetter = _originalRegistrySetter;
            Program.RegistryKeySetter = _originalKeySetter;
            Program.RegistryStaticSetter = _originalStaticSetter;
            Program.KeyOpener = _originalKeyOpener;
            Program.KeyOpenerWithRights = _originalKeyOpenerWithRights;
            Program.KeyCreator = _originalKeyCreator;
            Program.ServiceStopper = _originalServiceStopper;
        }

        [Fact]
        public void Req_1_2_15_5_BitLocker_ShouldBeEnabled()
        {
            // Act
            // BitLocker is configured in ConfigureCisBenchmarks (based on cache) or OsHardening?
            // Let's call ConfigureCisBenchmarks as it was seen there or OsHardening.
            // Actually it was in ConfigureOsHardening in previous read_file (lines 1481)
            Program.ConfigureCisBenchmarks(); 

            // Assert
            Assert.Contains("dism /online /enable-feature /featurename:BitLocker /NoRestart", _executedCommands);
        }

        [Fact]
        public void Req_1_2_14_EventLogSDDLs_ShouldBeHardened()
        {
            // Act
            Program.ConfigureCisBenchmarks();

            // Assert
            Assert.Contains("wevtutil sl Application /ca:O:BAG:SYD:(A;;0x3;;;IU)(A;;0x7;;;BA)(A;;0xf0007;;;SY)", _executedCommands);
            Assert.Contains("wevtutil sl Security /ca:O:BAG:SYD:(A;;0x1;;;IU)(A;;0x7;;;BA)(A;;0xf0005;;;SY)", _executedCommands);
            Assert.Contains("wevtutil sl System /ca:O:BAG:SYD:(A;;0x1;;;IU)(A;;0x7;;;BA)(A;;0xf0007;;;SY)", _executedCommands);
        }

        [Fact]
        public void Req_1_2_50_EventLog_Size_ShouldBe20MB()
        {
            // Act
            Program.ConfigureCisBenchmarks();

            // Assert
            // 20480 * 1024 = 20971520
            string expectedValue = "20971520"; 
            
            // Check Registry
            Assert.Contains($"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\MaxSize={expectedValue}", _registryChanges);
            Assert.Contains($"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\EventLog\\Security\\MaxSize={expectedValue}", _registryChanges);
            Assert.Contains($"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\EventLog\\System\\MaxSize={expectedValue}", _registryChanges);
        }

        [Fact]
        public void Req_1_2_13_48_UAC_ShouldBeEnabled()
        {
            // Act
            Program.ConfigureCisBenchmarks();

            // Assert
            string uacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
            Assert.Contains($"HKEY_LOCAL_MACHINE\\{uacKey}\\EnableLUA=1", _registryChanges);
            Assert.Contains($"HKEY_LOCAL_MACHINE\\{uacKey}\\ConsentPromptBehaviorAdmin=1", _registryChanges);
        }

        [Fact]
        public void Req_1_2_27_KeyboardFilter_ShouldBlockKeys()
        {
            // Act
            Program.ConfigureKeyboardFilter();

            // Assert
            // It runs a massive PowerShell script. We verify the script invocation.
            // Check for correct case "WEKF" and function name "Block-PredefinedKey"
            Assert.True(_executedCommands.Exists(cmd => cmd.Contains("WEKF_PredefinedKey")), "Should contain WEKF class usage");
            Assert.True(_executedCommands.Exists(cmd => cmd.Contains("Block-PredefinedKey")), "Should contain helper function usage");
            // Expect usage of sc config for MsKeyboardFilter
            Assert.True(_executedCommands.Exists(cmd => cmd.Contains("sc config \"MsKeyboardFilter\" start= auto")), "Should enable service");
        }

        [Fact]
        public void Req_1_1_15_Defender_Exclusions_ShouldBeSet()
        {
            // Act
            Program.ConfigureDefender();

            // Assert
            Assert.Contains("powershell -Command \"Add-MpPreference -ExclusionPath 'C:\\Eikon', 'C:\\ProgramData\\MySQL'\"", _executedCommands);
            Assert.Contains("powershell -Command \"Add-MpPreference -ExclusionProcess 'Launcher.exe', 'GVPPro.exe', 'mysqld.exe'\"", _executedCommands);
        }

        [Fact]
        public void Req_Services_ShouldBeDisabled()
        {
            // Act
            Program.OptimizeServices(); 

            // Assert
            // Quotes are included in the command generation: config "{svcName}"
            Assert.True(_executedCommands.Exists(c => c.Contains("sc config \"RemoteRegistry\" start= disabled")), "RemoteRegistry should be disabled");
            Assert.True(_executedCommands.Exists(c => c.Contains("sc config \"WSearch\" start= disabled")), "Windows Search should be disabled");
        }

        [Fact]
        public void Req_Firewall_ShouldBlockPing()
        {
            // Act
            Program.ConfigureFirewall();

            // Assert
            Assert.True(_executedCommands.Exists(c => c.Contains("Get-NetFirewallRule -DisplayName '*Echo Request*ICMPv4*' | Disable-NetFirewallRule")));
        }

        [Fact]
        public void Req_AppLocker_ShouldEnforceRules()
        {
            // Act
            Program.ConfigureAppLocker();

            // Assert
            // Checks for service start
            Assert.Contains("sc config AppIDSvc start= auto", _executedCommands);
            // Checks for policy import
            Assert.True(_executedCommands.Exists(c => c.Contains("Set-AppLockerPolicy -XmlPolicy")));
        }
    }
}
