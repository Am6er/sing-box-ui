using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sing_box_UI
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private static readonly Regex AnsiEscapeSequenceRegex = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

        private readonly string _workingDirectory;
        private readonly string _settingsPath;
        private readonly string _singBoxPath;
        private readonly string _logPath;
        private readonly object _logSync = new object();
        private readonly ContextMenuStrip _contextMenu;
        private readonly ToolStripLabel _statusLabel;
        private readonly ToolStripMenuItem _startMenuItem;
        private readonly ToolStripMenuItem _stopMenuItem;
        private readonly ToolStripMenuItem _restartMenuItem;
        private readonly ToolStripMenuItem _exitMenuItem;
        private readonly ToolStripMenuItem _settingsMenuItem;
        private readonly ToolStripMenuItem _showLogsMenuItem;
        private readonly ToolStripMenuItem _selectConfigFileMenuItem;
        private readonly ToolStripMenuItem _checkUpdatesMenuItem;
        private readonly ToolStripMenuItem _currentVersionMenuItem;
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private readonly Icon _defaultIcon;
        private readonly AppSettings _settings;

        private int? _managedProcessId;
        private DateTime? _managedProcessStartedAtUtc;
        private Process _managedProcessHandle;
        private int _activeLogSessionId;
        private string _lastStatusMessage;
        private bool _isExiting;

        public TrayApplicationContext()
        {
            _workingDirectory = Application.StartupPath;
            _settingsPath = Path.Combine(_workingDirectory, "settings.ini");
            _singBoxPath = Path.Combine(_workingDirectory, "sing-box.exe");
            _logPath = Path.Combine(_workingDirectory, "sing-box.log");
            _settings = new AppSettings(_settingsPath);
            _settings.Load();
            _settings.Save();
            _defaultIcon = LoadApplicationIcon();
            _lastStatusMessage = "Initializing";

            _statusLabel = new ToolStripLabel("Status: Initializing")
            {
                AutoSize = false,
                IsLink = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _startMenuItem = new ToolStripMenuItem("Start", null, (_, __) => StartFromMenu());
            _stopMenuItem = new ToolStripMenuItem("Stop", null, (_, __) => StopFromMenu());
            _restartMenuItem = new ToolStripMenuItem("Restart", null, (_, __) => RestartFromMenu());
            _exitMenuItem = new ToolStripMenuItem("Exit", null, (_, __) => ExitApplication());
            _showLogsMenuItem = new ToolStripMenuItem("Show logs", null, (_, __) => ShowLogsFromMenu());
            _selectConfigFileMenuItem = new ToolStripMenuItem("Select config file", null, (_, __) => SelectConfigFileFromMenu());
            _checkUpdatesMenuItem = new ToolStripMenuItem("Check sing-box updates", null, async (_, __) => await CheckSingBoxUpdatesAsync());
            _currentVersionMenuItem = new ToolStripMenuItem("Current sing-box version: loading...")
            {
                Enabled = false
            };
            _settingsMenuItem = new ToolStripMenuItem("Settings...");
            _settingsMenuItem.DropDownItems.AddRange(new ToolStripItem[]
            {
                _selectConfigFileMenuItem,
                _checkUpdatesMenuItem,
                new ToolStripSeparator(),
                _currentVersionMenuItem
            });

            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.AddRange(new ToolStripItem[]
            {
                _statusLabel,
                new ToolStripSeparator(),
                _startMenuItem,
                _stopMenuItem,
                _restartMenuItem,
                new ToolStripSeparator(),
                _settingsMenuItem,
                _showLogsMenuItem,
                new ToolStripSeparator(),
                _exitMenuItem
            });
            _contextMenu.Opening += (_, __) => UpdateMenuState();

            _notifyIcon = new NotifyIcon
            {
                Text = BuildTrayText(_lastStatusMessage),
                Visible = true,
                ContextMenuStrip = _contextMenu,
                Icon = _defaultIcon
            };
            _notifyIcon.MouseUp += NotifyIconMouseUp;

            _statusTimer = new System.Windows.Forms.Timer
            {
                Interval = 2000
            };
            _statusTimer.Tick += (_, __) => RefreshTrackedProcessState();
            _statusTimer.Start();

            if (EnsureSingBoxAvailableOnStartup() && !RestartManagedProcess())
            {
                ShowErrorBalloon("Failed to start sing-box.");
            }

            UpdateMenuState();
        }

        protected override void ExitThreadCore()
        {
            if (_isExiting)
            {
                return;
            }

            _isExiting = true;
            _statusTimer.Stop();
            StopManagedProcess();
            DisposeManagedProcessHandle();

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _statusTimer.Dispose();
            _defaultIcon.Dispose();

            base.ExitThreadCore();
        }

        private string ConfigPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_settings.ConfigFileName))
                {
                    return null;
                }

                return Path.Combine(_workingDirectory, _settings.ConfigFileName);
            }
        }

        private void StartFromMenu()
        {
            if (!RestartManagedProcess())
            {
                ShowErrorBalloon("Failed to start sing-box.");
            }

            UpdateMenuState();
        }

        private void StopFromMenu()
        {
            if (!StopManagedProcess())
            {
                ShowErrorBalloon("Failed to stop sing-box.");
            }

            UpdateMenuState();
        }

        private void RestartFromMenu()
        {
            if (!RestartManagedProcess())
            {
                ShowErrorBalloon("Failed to restart sing-box.");
            }

            UpdateMenuState();
        }

        private void ExitApplication()
        {
            ExitThread();
        }

        private void ShowLogsFromMenu()
        {
            using (var dialog = new LogViewerForm(_logPath))
            {
                dialog.ShowDialog();
            }
        }

        private void SelectConfigFileFromMenu()
        {
            if (!SelectAndStoreConfigFile())
            {
                UpdateMenuState();
                return;
            }

            PromptRestartSingBox();
            UpdateMenuState();
        }

        private void NotifyIconMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            UpdateMenuState();
            ShowTrayContextMenu();
        }

        private void ShowTrayContextMenu()
        {
            try
            {
                var showContextMenuMethod = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (showContextMenuMethod != null)
                {
                    showContextMenuMethod.Invoke(_notifyIcon, null);
                    return;
                }
            }
            catch
            {
            }

            _contextMenu.Show(Cursor.Position);
        }

        private async Task CheckSingBoxUpdatesAsync()
        {
            if (!File.Exists(_singBoxPath))
            {
                MessageBox.Show(
                    "sing-box.exe not found.",
                    "Sing-box UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            _checkUpdatesMenuItem.Enabled = false;

            try
            {
                var currentVersion = SingBoxUpdateService.GetInstalledVersion(_singBoxPath, _workingDirectory);
                var latestRelease = await RunTimedOperationAsync(
                    "Checking updates ...",
                    _settings.CheckUpdatesTimeoutSeconds,
                    cancellationToken => SingBoxUpdateService.GetLatestReleaseAsync(cancellationToken));
                var latestVersion = SingBoxUpdateService.NormalizeVersion(latestRelease.TagName);

                if (SingBoxUpdateService.IsLatestVersionNewer(currentVersion, latestVersion))
                {
                    var releaseNotes = string.IsNullOrWhiteSpace(latestRelease.Body)
                        ? "Release Notes are empty."
                        : latestRelease.Body;

                    var action = UpdateAvailableDialog.ShowDialog(currentVersion, latestVersion, releaseNotes);
                    if (action == UpdateDialogAction.OpenInBrowser)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = latestRelease.HtmlUrl,
                            UseShellExecute = true
                        });
                    }
                    else if (action == UpdateDialogAction.Install)
                    {
                        var asset = SingBoxUpdateService.GetPreferredWindowsAsset(latestRelease);
                        if (asset == null)
                        {
                            MessageBox.Show(
                                "No suitable Windows ZIP asset was found in the latest release.",
                                "Sing-box UI",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                            return;
                        }

                        await InstallLatestReleaseAsync(latestRelease, asset, true);
                    }
                }
                else
                {
                    MessageBox.Show(
                        "sing-box is up to date." + Environment.NewLine + "Current sing-box version: " + currentVersion,
                        "Sing-box UI",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to check sing-box updates:" + Environment.NewLine + ex.Message,
                    "Sing-box UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                _checkUpdatesMenuItem.Enabled = true;
            }
        }

        private void UpdateMenuState()
        {
            RefreshTrackedProcessState();

            _statusLabel.Text = "Status: " + _lastStatusMessage;
            var isRunning = TryGetTrackedProcess(out var trackedProcess);
            trackedProcess?.Dispose();
            _statusLabel.ForeColor = GetStatusColor(_lastStatusMessage);
            _notifyIcon.Text = BuildTrayText(_lastStatusMessage);
            _currentVersionMenuItem.Text = "Current sing-box version: " + GetCurrentVersionLabel();
            UpdateMenuLayout();

            _startMenuItem.Enabled = !isRunning && File.Exists(_singBoxPath);
            _stopMenuItem.Enabled = isRunning;
            _restartMenuItem.Enabled = File.Exists(_singBoxPath);
        }

        private Task InstallLatestReleaseAsync(
            SingBoxUpdateService.GitHubReleaseInfo latestRelease,
            SingBoxUpdateService.GitHubReleaseAsset asset,
            bool restartSingBoxAfterInstall)
        {
            var installedSuccessfully = false;
            var tempZipPath = Path.Combine(Path.GetTempPath(), "sing-box-update-" + SingBoxUpdateService.NormalizeVersion(latestRelease.TagName) + ".zip");
            var receiveTimeout = TimeSpan.FromSeconds(_settings.DownloadTimeoutSeconds);

            using (var dialog = new DownloadProgressDialog())
            {
                dialog.Shown += async (shownSender, shownArgs) =>
                {
                    while (true)
                    {
                        var startedAtUtc = DateTime.UtcNow;
                        var lastDataReceivedAtUtc = startedAtUtc;

                        using (var cancellationTokenSource = new CancellationTokenSource())
                        {
                            var countdownTimer = new System.Windows.Forms.Timer
                            {
                                Interval = 500
                            };

                            countdownTimer.Tick += (timerSender, timerArgs) =>
                            {
                                var elapsed = DateTime.UtcNow - startedAtUtc;
                                var remaining = receiveTimeout - (DateTime.UtcNow - lastDataReceivedAtUtc);
                                if (remaining < TimeSpan.Zero)
                                {
                                    remaining = TimeSpan.Zero;
                                }

                                dialog.UpdateTiming(elapsed, remaining);
                            };

                            countdownTimer.Start();

                            try
                            {
                                dialog.UpdateStatus("Downloading " + asset.Name + "...");
                                await SingBoxUpdateService.DownloadAndInstallAsync(
                                    asset,
                                    _workingDirectory,
                                    tempZipPath,
                                    receiveTimeout,
                                    () =>
                                    {
                                        dialog.BeginInvoke(new Action(() =>
                                            dialog.UpdateStatus("Restarting sing-box for install...")));

                                        if (!StopExistingManagedProcesses())
                                        {
                                            throw new InvalidOperationException("Failed to stop current sing-box.");
                                        }
                                    },
                                    (downloadedBytes, totalBytes, bytesPerSecond) =>
                                    {
                                        lastDataReceivedAtUtc = DateTime.UtcNow;
                                        var elapsed = DateTime.UtcNow - startedAtUtc;
                                        var remaining = receiveTimeout - (DateTime.UtcNow - lastDataReceivedAtUtc);
                                        if (remaining < TimeSpan.Zero)
                                        {
                                            remaining = TimeSpan.Zero;
                                        }

                                        dialog.BeginInvoke(new Action(() =>
                                            dialog.UpdateProgress(asset.Name, downloadedBytes, totalBytes, bytesPerSecond, elapsed, remaining)));
                                    },
                                    cancellationTokenSource.Token);

                                countdownTimer.Stop();
                                countdownTimer.Dispose();

                                if (File.Exists(tempZipPath))
                                {
                                    File.Delete(tempZipPath);
                                }

                                dialog.Complete("Installed " + latestRelease.TagName + " successfully.");
                                installedSuccessfully = true;
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                countdownTimer.Stop();
                                countdownTimer.Dispose();

                                dialog.Fail("Data receive timeout exceeded. You can retry to resume the same file.", true);
                                if (!await dialog.WaitForRetryAsync())
                                {
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                countdownTimer.Stop();
                                countdownTimer.Dispose();

                                dialog.Fail("Install failed:" + Environment.NewLine + ex.Message, true);
                                if (!await dialog.WaitForRetryAsync())
                                {
                                    break;
                                }
                            }
                        }
                    }
                };

                dialog.ShowDialog();
            }

            if (installedSuccessfully && restartSingBoxAfterInstall && !RestartManagedProcess())
            {
                ShowErrorBalloon("Failed to restart sing-box after installing the update.");
            }

            UpdateMenuState();
            return Task.CompletedTask;
        }

        private void RefreshTrackedProcessState()
        {
            if (TryGetTrackedProcess(out var trackedProcess))
            {
                _lastStatusMessage = "Started (PID " + trackedProcess.Id + ")";
                trackedProcess.Dispose();
                return;
            }

            if (TryAdoptExistingProcess())
            {
                _lastStatusMessage = "Started (PID " + _managedProcessId.Value + ")";
                return;
            }

            if (!File.Exists(_singBoxPath))
            {
                _lastStatusMessage = "sing-box.exe not found";
                return;
            }

            var configPath = ConfigPath;
            if (string.IsNullOrWhiteSpace(_settings.ConfigFileName))
            {
                _lastStatusMessage = "Config file is not selected";
                return;
            }

            if (!File.Exists(configPath))
            {
                _lastStatusMessage = "Config file not found";
                return;
            }

            _lastStatusMessage = "Stopped";
        }

        private bool RestartManagedProcess()
        {
            if (!EnsureConfigAvailable(true))
            {
                return false;
            }

            if (!StopExistingManagedProcesses())
            {
                return false;
            }

            return StartManagedProcess(false);
        }

        private bool StartManagedProcess(bool adoptExistingProcess)
        {
            if (TryGetTrackedProcess(out var runningProcess))
            {
                _lastStatusMessage = "Started (PID " + runningProcess.Id + ")";
                runningProcess.Dispose();
                return true;
            }

            if (adoptExistingProcess && TryAdoptExistingProcess())
            {
                _lastStatusMessage = "Started (PID " + _managedProcessId.Value + ")";
                return true;
            }

            if (!File.Exists(_singBoxPath))
            {
                _managedProcessId = null;
                _managedProcessStartedAtUtc = null;
                _lastStatusMessage = "sing-box.exe not found";
                return false;
            }

            if (!EnsureConfigAvailable(true))
            {
                _managedProcessId = null;
                _managedProcessStartedAtUtc = null;
                return false;
            }

            var configPath = ConfigPath;

            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = _singBoxPath,
                    Arguments = "run -c \"" + Path.GetFileName(configPath) + "\"",
                    WorkingDirectory = _workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                if (process == null)
                {
                    _managedProcessId = null;
                    _managedProcessStartedAtUtc = null;
                    _lastStatusMessage = "Process was not created";
                    return false;
                }

                var logSessionId = Interlocked.Increment(ref _activeLogSessionId);

                process.EnableRaisingEvents = true;
                process.OutputDataReceived += (_, eventArgs) => AppendProcessOutputLine(logSessionId, "stdout", eventArgs.Data);
                process.ErrorDataReceived += (_, eventArgs) => AppendProcessOutputLine(logSessionId, "stderr", eventArgs.Data);
                process.Exited += (_, __) => HandleManagedProcessExited(process, logSessionId);

                ResetLogFile();

                _managedProcessId = process.Id;
                _managedProcessStartedAtUtc = process.StartTime.ToUniversalTime();
                _managedProcessHandle = process;
                _lastStatusMessage = "Started (PID " + process.Id + ")";

                AppendLogLine("Sing-box process started with PID " + process.Id);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                return true;
            }
            catch (Exception ex)
            {
                DisposeManagedProcessHandle();
                _managedProcessId = null;
                _managedProcessStartedAtUtc = null;
                _lastStatusMessage = "Start error: " + ex.Message;
                return false;
            }
        }

        private bool EnsureConfigAvailable(bool promptIfMissing)
        {
            var configPath = ConfigPath;

            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
            {
                return true;
            }

            if (!promptIfMissing)
            {
                _lastStatusMessage = string.IsNullOrWhiteSpace(_settings.ConfigFileName)
                    ? "Config file is not selected"
                    : "Config file not found";
                return false;
            }

            if (!SelectAndStoreConfigFile())
            {
                _lastStatusMessage = "Config file is not selected";
                return false;
            }

            return File.Exists(ConfigPath);
        }

        private bool SelectAndStoreConfigFile()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select sing-box config file";
                dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                dialog.Multiselect = false;
                dialog.CheckFileExists = true;
                dialog.RestoreDirectory = true;

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return false;
                }

                var selectedPath = dialog.FileName;
                var targetFileName = Path.GetFileName(selectedPath);
                var targetPath = Path.Combine(_workingDirectory, targetFileName);

                if (!string.Equals(Path.GetFullPath(selectedPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(selectedPath, targetPath, true);
                }

                _settings.ConfigFileName = targetFileName;
                _settings.Save();
                _lastStatusMessage = "Config selected: " + targetFileName;
                return true;
            }
        }

        private bool EnsureSingBoxAvailableOnStartup()
        {
            if (File.Exists(_singBoxPath))
            {
                return true;
            }

            var result = MessageBox.Show(
                "sing-box.exe not found. Download the latest version now?",
                "Sing-box UI",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                _lastStatusMessage = "sing-box.exe not found";
                return false;
            }

            try
            {
                var latestRelease = RunTimedOperationAsync(
                    "Checking updates ...",
                    _settings.CheckUpdatesTimeoutSeconds,
                    cancellationToken => SingBoxUpdateService.GetLatestReleaseAsync(cancellationToken)).GetAwaiter().GetResult();

                var asset = SingBoxUpdateService.GetPreferredWindowsAsset(latestRelease);
                if (asset == null)
                {
                    MessageBox.Show(
                        "No suitable Windows ZIP asset was found in the latest release.",
                        "Sing-box UI",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return false;
                }

                InstallLatestReleaseAsync(latestRelease, asset, false).GetAwaiter().GetResult();
                return File.Exists(_singBoxPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to download the latest sing-box:" + Environment.NewLine + ex.Message,
                    "Sing-box UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        private void PromptRestartSingBox()
        {
            var result = MessageBox.Show(
                "Restart sing-box?",
                "Sing-box UI",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                RestartManagedProcess();
            }
        }

        private async Task<T> RunTimedOperationAsync<T>(
            string dialogCaption,
            int timeoutSeconds,
            Func<CancellationToken, Task<T>> operation)
        {
            using (var dialog = new TimedOperationDialog(dialogCaption))
            using (var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                var completionSource = new TaskCompletionSource<T>();
                var startedAtUtc = DateTime.UtcNow;
                var countdownTimer = new System.Windows.Forms.Timer
                {
                    Interval = 500
                };

                countdownTimer.Tick += (_, __) =>
                {
                    var remaining = TimeSpan.FromSeconds(timeoutSeconds) - (DateTime.UtcNow - startedAtUtc);
                    if (remaining < TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }

                    dialog.UpdateRemaining(remaining);
                };

                dialog.Shown += async (_, __) =>
                {
                    countdownTimer.Start();

                    try
                    {
                        var result = await operation(cancellationTokenSource.Token);
                        completionSource.TrySetResult(result);
                        dialog.Complete();
                    }
                    catch (Exception ex)
                    {
                        completionSource.TrySetException(ex);
                        dialog.Fail(ex is OperationCanceledException
                            ? "Operation timed out."
                            : "Operation failed:" + Environment.NewLine + ex.Message);
                    }
                    finally
                    {
                        countdownTimer.Stop();
                    }
                };

                dialog.ShowDialog();
                return await completionSource.Task;
            }
        }

        private bool StopExistingManagedProcesses()
        {
            var hadFailure = false;
            var killedAnyProcess = false;
            var processName = Path.GetFileNameWithoutExtension(_singBoxPath);

            foreach (var process in Process.GetProcessesByName(processName))
            {
                var processId = 0;

                try
                {
                    using (process)
                    {
                        processId = process.Id;

                        if (process.HasExited)
                        {
                            continue;
                        }

                        if (!ForceKillProcessTree(process.Id))
                        {
                            hadFailure = true;
                            _lastStatusMessage = "Stop error: failed to terminate PID " + process.Id;
                            continue;
                        }

                        killedAnyProcess = true;
                    }
                }
                catch (Exception ex)
                {
                    hadFailure = true;
                    _lastStatusMessage = "Stop error for PID " + processId + ": " + ex.Message;
                }
            }

            _managedProcessId = null;
            _managedProcessStartedAtUtc = null;

            if (!hadFailure && killedAnyProcess)
            {
                _lastStatusMessage = "Stopped";
            }

            return !hadFailure;
        }

        private bool StopManagedProcess()
        {
            if (!TryGetTrackedProcess(out var trackedProcess) && !TryAdoptExistingProcess())
            {
                _managedProcessId = null;
                _managedProcessStartedAtUtc = null;
                _lastStatusMessage = "Stopped";
                return true;
            }

            if (trackedProcess == null && !TryGetTrackedProcess(out trackedProcess))
            {
                _managedProcessId = null;
                _managedProcessStartedAtUtc = null;
                _lastStatusMessage = "Stopped";
                return true;
            }

            using (trackedProcess)
            {
                try
                {
                    trackedProcess.Kill();
                    trackedProcess.WaitForExit(5000);
                    _managedProcessId = null;
                    _managedProcessStartedAtUtc = null;
                    _lastStatusMessage = "Stopped";
                    return true;
                }
                catch (Exception ex)
                {
                    _lastStatusMessage = "Stop error: " + ex.Message;
                    return false;
                }
            }
        }

        private bool TryAdoptExistingProcess()
        {
            foreach (var candidate in FindManagedProcessCandidates())
            {
                try
                {
                    using (var process = Process.GetProcessById(candidate.ProcessId))
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }

                        _managedProcessId = process.Id;
                        _managedProcessStartedAtUtc = candidate.StartedAtUtc;
                        return true;
                    }
                }
                catch
                {
                }
            }

            _managedProcessId = null;
            _managedProcessStartedAtUtc = null;
            return false;
        }

        private bool TryGetTrackedProcess(out Process process)
        {
            process = null;

            if (!_managedProcessId.HasValue || !_managedProcessStartedAtUtc.HasValue)
            {
                return false;
            }

            try
            {
                process = Process.GetProcessById(_managedProcessId.Value);
                if (process.HasExited)
                {
                    process.Dispose();
                    process = null;
                    _managedProcessId = null;
                    _managedProcessStartedAtUtc = null;
                    return false;
                }

                if (process.StartTime.ToUniversalTime() != _managedProcessStartedAtUtc.Value)
                {
                    process.Dispose();
                    process = null;
                    _managedProcessId = null;
                    _managedProcessStartedAtUtc = null;
                    return false;
                }

                return true;
            }
            catch
            {
                process?.Dispose();
                process = null;
                _managedProcessId = null;
                _managedProcessStartedAtUtc = null;
                return false;
            }
        }

        private void ShowErrorBalloon(string message)
        {
            try
            {
                _notifyIcon.ShowBalloonTip(3000, "Sing-box UI", _lastStatusMessage + Environment.NewLine + message, ToolTipIcon.Error);
            }
            catch
            {
            }
        }

        private void UpdateMenuLayout()
        {
            var items = new ToolStripItem[]
            {
                _statusLabel,
                _startMenuItem,
                _stopMenuItem,
                _restartMenuItem,
                _settingsMenuItem,
                _showLogsMenuItem,
                _exitMenuItem,
                _currentVersionMenuItem
            };

            var maxTextWidth = items
                .Select(item => TextRenderer.MeasureText(item.Text, item.Font).Width)
                .DefaultIfEmpty(0)
                .Max();

            var targetWidth = maxTextWidth + 40;
            _statusLabel.Width = targetWidth - 8;
            _contextMenu.MinimumSize = new Size(targetWidth, 0);
        }

        private Icon LoadApplicationIcon()
        {
            try
            {
                return (Icon)Icon.ExtractAssociatedIcon(Application.ExecutablePath).Clone();
            }
            catch
            {
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        private static Color GetStatusColor(string statusText)
        {
            if (statusText.StartsWith("Started", StringComparison.OrdinalIgnoreCase))
            {
                return Color.ForestGreen;
            }

            if (statusText.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase))
            {
                return Color.Firebrick;
            }

            return SystemColors.ControlText;
        }

        private static string BuildTrayText(string statusText)
        {
            if (string.IsNullOrWhiteSpace(statusText))
            {
                return "Sing-box UI";
            }

            const int maxLength = 63;
            return statusText.Length <= maxLength
                ? statusText
                : statusText.Substring(0, maxLength);
        }

        private string GetCurrentVersionLabel()
        {
            if (!File.Exists(_singBoxPath))
            {
                return "not found";
            }

            try
            {
                return SingBoxUpdateService.GetInstalledVersion(_singBoxPath, _workingDirectory);
            }
            catch
            {
                return "unknown";
            }
        }

        private void ResetLogFile()
        {
            lock (_logSync)
            {
                File.WriteAllText(_logPath, string.Empty);
            }
        }

        private void AppendProcessOutputLine(int logSessionId, string streamName, string line)
        {
            if (string.IsNullOrWhiteSpace(line) || logSessionId != _activeLogSessionId)
            {
                return;
            }

            var sanitizedLine = SanitizeProcessOutput(line);
            if (string.IsNullOrWhiteSpace(sanitizedLine))
            {
                return;
            }

            AppendLogLine("[" + streamName + "] " + sanitizedLine);
        }

        private void HandleManagedProcessExited(Process process, int logSessionId)
        {
            try
            {
                if (logSessionId == _activeLogSessionId)
                {
                    AppendLogLine("Sing-box process PID " + process.Id + " was stopped");
                }
            }
            catch
            {
            }
            finally
            {
                if (ReferenceEquals(_managedProcessHandle, process))
                {
                    _managedProcessHandle = null;
                }

                process.Dispose();
            }
        }

        private void DisposeManagedProcessHandle()
        {
            var process = _managedProcessHandle;
            _managedProcessHandle = null;

            if (process == null)
            {
                return;
            }

            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }

        private void AppendLogLine(string message)
        {
            var entry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;

            lock (_logSync)
            {
                File.AppendAllText(_logPath, entry);
            }
        }

        private static string SanitizeProcessOutput(string line)
        {
            var cleanedLine = line ?? string.Empty;
            cleanedLine = cleanedLine.Replace("\uFEFF", string.Empty);
            cleanedLine = AnsiEscapeSequenceRegex.Replace(cleanedLine, string.Empty);
            return cleanedLine.TrimEnd();
        }

        private ManagedProcessCandidate[] FindManagedProcessCandidates()
        {
            var processName = Path.GetFileName(_singBoxPath);
            var normalizedPath = NormalizePath(_singBoxPath);
            var query = "SELECT ProcessId, CreationDate, ExecutablePath FROM Win32_Process WHERE Name='" + processName.Replace("'", "''") + "'";

            using (var searcher = new ManagementObjectSearcher(query))
            using (var processes = searcher.Get())
            {
                return processes
                    .Cast<ManagementObject>()
                    .Select(CreateManagedProcessCandidate)
                    .Where(candidate => candidate != null)
                    .Where(candidate => string.Equals(NormalizePath(candidate.ExecutablePath), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(candidate => candidate.StartedAtUtc)
                    .ToArray();
            }
        }

        private static ManagedProcessCandidate CreateManagedProcessCandidate(ManagementObject process)
        {
            try
            {
                var executablePath = process["ExecutablePath"] as string;
                var processIdValue = process["ProcessId"];
                var creationDate = process["CreationDate"] as string;

                if (string.IsNullOrWhiteSpace(executablePath) || processIdValue == null || string.IsNullOrWhiteSpace(creationDate))
                {
                    return null;
                }

                return new ManagedProcessCandidate(
                    Convert.ToInt32(processIdValue),
                    executablePath,
                    ManagementDateTimeConverter.ToDateTime(creationDate).ToUniversalTime());
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool ForceKillProcessTree(int processId)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + processId + " /F /T",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    process.WaitForExit(5000);
                    return process.ExitCode == 0 || process.ExitCode == 128;
                }
            }
            catch
            {
                return false;
            }
        }

        private sealed class ManagedProcessCandidate
        {
            public ManagedProcessCandidate(int processId, string executablePath, DateTime startedAtUtc)
            {
                ProcessId = processId;
                ExecutablePath = executablePath;
                StartedAtUtc = startedAtUtc;
            }

            public int ProcessId { get; }

            public string ExecutablePath { get; }

            public DateTime StartedAtUtc { get; }
        }
    }
}
