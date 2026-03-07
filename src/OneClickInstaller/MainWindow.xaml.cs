using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OneClickInstaller
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private DateTime _startTime;
        private bool _isInstalling = false;
        private bool _systemShuttingDown = false;
        private readonly InstallationManager _installationManager;
        private InstallerConfig _config;
        
        public ObservableCollection<SoftwareItem> SoftwareItems { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            
            // Load config
            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string configPath = Path.Combine(exeDir, "installer-config.json");
            if (File.Exists(configPath))
            {
                _config = SimpleJsonParser.Parse(File.ReadAllText(configPath));
            }

            _installationManager = new InstallationManager();
            _installationManager.ProgressUpdated += OnProgressUpdated;
            _installationManager.StatusUpdated += OnStatusUpdated;
            _installationManager.LogUpdated += OnLogUpdated;
            
            InitializeSoftwareList();
            SoftwareList.ItemsSource = SoftwareItems;
        }

        private void InitializeSoftwareList()
        {
            SoftwareItems = new ObservableCollection<SoftwareItem>();

            // Add disk partitioning if enabled
            if (_config?.DiskPartitioning != null && _config.DiskPartitioning.Enabled)
            {
                SoftwareItems.Add(new SoftwareItem { Name = "Disk Partitioning", Status = "Pending", StatusColor = Brushes.Orange });
            }

            // Add all software packages from config
            if (_config?.SoftwarePackages != null)
            {
                foreach (var pkg in _config.SoftwarePackages)
                {
                    SoftwareItems.Add(new SoftwareItem { Name = pkg.Name, Status = "Pending", StatusColor = Brushes.Orange });
                }
            }

            // Add database configuration
            if (_config?.MySQLConfig != null && !string.IsNullOrEmpty(_config.MySQLConfig.Database))
            {
                SoftwareItems.Add(new SoftwareItem { Name = "Database Configuration", Status = "Pending", StatusColor = Brushes.Orange });
            }

            // Add final package
            if (_config?.FinalInstallPackage != null && !string.IsNullOrEmpty(_config.FinalInstallPackage.RelativePath))
            {
                string finalName = _config.FinalInstallPackage.Name;
                SoftwareItems.Add(new SoftwareItem { Name = !string.IsNullOrEmpty(finalName) ? finalName : "Final Application", Status = "Pending", StatusColor = Brushes.Orange });
            }

            // Add post-install scripts step
            if (_config?.PostInstallScripts != null && _config.PostInstallScripts.Enabled)
            {
                string desc = _config.PostInstallScripts.Description;
                SoftwareItems.Add(new SoftwareItem { Name = !string.IsNullOrEmpty(desc) ? desc : "Post-Install Scripts", Status = "Pending", StatusColor = Brushes.Orange });
            }

            // Add OS configuration step
            if (_config?.OsConfiguration != null && _config.OsConfiguration.Enabled)
            {
                string desc = _config.OsConfiguration.Description;
                SoftwareItems.Add(new SoftwareItem { Name = !string.IsNullOrEmpty(desc) ? desc : "OS Configuration", Status = "Pending", StatusColor = Brushes.Orange });
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Auto-start installation immediately
            await StartInstallationAsync();
        }

        private async Task StartInstallationAsync()
        {
            if (_isInstalling) return;

            _isInstalling = true;
            ExitButton.Visibility = Visibility.Collapsed;
            LogPanel.Visibility = Visibility.Visible;
            CurrentProgressGrid.Visibility = Visibility.Visible;
            StatusLabel.Text = "Installing software packages...";

            _startTime = DateTime.Now;
            _timer.Start();

            try
            {
                await _installationManager.StartInstallationAsync();
                
                if (_config?.OsConfiguration != null && _config.OsConfiguration.Enabled)
                {
                    // Check if OS config was actually reached (no failures = auto-reboot)
                    // If there were failures, the OS config step was skipped and no reboot will happen
                    bool hasFailures = SoftwareItems.Any(s => s.Status == "Failed" || s.Status == "Skipped");
                    if (hasFailures)
                    {
                        StatusLabel.Text = "Installation completed with errors. OS configuration was skipped.";
                    }
                    else
                    {
                        _systemShuttingDown = true;
                        StatusLabel.Text = "Installation complete! The system will restart to finalize OS configuration, then shut down automatically. Power the PC on 30 seconds after power off.";
                        ExitButton.Content = "Restarting...";
                        ExitButton.IsEnabled = false;
                        ExitButton.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    StatusLabel.Text = "Installation completed successfully!";
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Installation failed. Check log for details.";
                LogTextBlock.Text += $"\nERROR: {ex.Message}\n";
                // Also write to log file
                try
                {
                    string logPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "oneclickinstaller_log.txt");
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL ERROR: {ex.Message}{Environment.NewLine}");
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Stack Trace: {ex.StackTrace}{Environment.NewLine}");
                }
                catch { }
            }
            finally
            {
                _timer.Stop();
                _isInstalling = false;
                if (!_systemShuttingDown)
                {
                    ExitButton.Content = "Close";
                    ExitButton.Visibility = Visibility.Visible;
                }
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstalling)
            {
                var result = MessageBox.Show(
                    "Installation is in progress. Are you sure you want to exit?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;
            }

            Application.Current.Shutdown();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            var elapsed = DateTime.Now - _startTime;
            TimeElapsedLabel.Text = $"Time Elapsed: {elapsed:hh\\:mm\\:ss}";
            
            if (OverallProgress.Value > 0)
            {
                var estimatedTotal = TimeSpan.FromSeconds(elapsed.TotalSeconds / (OverallProgress.Value / 100.0));
                var remaining = estimatedTotal - elapsed;
                if (remaining.TotalSeconds > 0)
                {
                    TimeRemainingLabel.Text = $"Estimated Time Remaining: {remaining:hh\\:mm\\:ss}";
                }
            }
        }

        private void OnProgressUpdated(object sender, ProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.OverallProgress >= 0)
                {
                    OverallProgress.Value = e.OverallProgress;
                    OverallProgressText.Text = $"{e.OverallProgress:F0}%";
                }
                
                if (e.CurrentProgress >= 0)
                {
                    CurrentProgress.Value = e.CurrentProgress;
                    CurrentProgressText.Text = $"{e.CurrentProgress:F0}%";
                }
            });
        }

        private void OnStatusUpdated(object sender, StatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = e.Message;
                CurrentItemLabel.Text = e.CurrentItem;
                
                if (e.ItemIndex >= 0 && e.ItemIndex < SoftwareItems.Count)
                {
                    var item = SoftwareItems[e.ItemIndex];
                    item.Status = e.Status;
                    switch (e.Status)
                    {
                        case "Installing":
                            item.StatusColor = Brushes.Yellow;
                            break;
                        case "Completed":
                            item.StatusColor = Brushes.LimeGreen;
                            break;
                        case "Failed":
                            item.StatusColor = Brushes.Red;
                            break;
                        case "Skipped":
                            item.StatusColor = Brushes.Gray;
                            break;
                        default:
                            item.StatusColor = Brushes.Orange;
                            break;
                    }
                }
            });
        }

        private void OnLogUpdated(object sender, LogEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {e.Message}\n";
                if (LogTextBlock.Parent is ScrollViewer scrollViewer)
                {
                    scrollViewer.ScrollToEnd();
                }
            });
        }
    }

    public class SoftwareItem : INotifyPropertyChanged
    {
        private string _status;
        private Brush _statusColor;

        public string Name { get; set; }
        
        public string Status 
        { 
            get => _status; 
            set 
            { 
                _status = value; 
                OnPropertyChanged(nameof(Status)); 
            } 
        }
        
        public Brush StatusColor 
        { 
            get => _statusColor; 
            set 
            { 
                _statusColor = value; 
                OnPropertyChanged(nameof(StatusColor)); 
            } 
        }

        public event PropertyChangedEventHandler PropertyChanged;
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ProgressEventArgs : EventArgs
    {
        public double OverallProgress { get; set; }
        public double CurrentProgress { get; set; }
    }

    public class StatusEventArgs : EventArgs
    {
        public string Message { get; set; }
        public string CurrentItem { get; set; }
        public int ItemIndex { get; set; }
        public string Status { get; set; }
    }

    public class LogEventArgs : EventArgs
    {
        public string Message { get; set; }
    }
}