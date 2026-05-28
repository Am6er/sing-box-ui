using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sing_box_UI
{
    internal enum UpdateDialogAction
    {
        Close,
        OpenInBrowser,
        Install
    }

    internal sealed class UpdateAvailableDialog : Form
    {
        private readonly Button _openInBrowserButton;
        private readonly Button _installButton;
        private readonly Button _closeButton;

        private UpdateAvailableDialog(string currentVersion, string latestVersion, string releaseNotes)
        {
            Text = "New version sing-box available";
            Icon = FormIconHelper.LoadApplicationIcon();
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(720, 520);
            ClientSize = new Size(720, 520);

            var summaryLabel = new Label
            {
                AutoSize = false,
                Left = 12,
                Top = 12,
                Width = 696,
                Height = 64,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "Current sing-box version: " + currentVersion + Environment.NewLine +
                       "Latest version: " + latestVersion + Environment.NewLine +
                       "Installing the update will restart sing-box."
            };

            var notesLabel = new Label
            {
                AutoSize = true,
                Left = 12,
                Top = 82,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Text = "Release Notes"
            };

            var notesTextBox = new TextBox
            {
                Left = 12,
                Top = 104,
                Width = 696,
                Height = 344,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Text = releaseNotes
            };

            _openInBrowserButton = new Button
            {
                Left = 12,
                Top = 464,
                Width = 140,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Open in browser"
            };
            _openInBrowserButton.Click += (_, __) =>
            {
                Action = UpdateDialogAction.OpenInBrowser;
                DialogResult = DialogResult.OK;
                Close();
            };

            _installButton = new Button
            {
                Left = 160,
                Top = 464,
                Width = 160,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Install and restart"
            };
            _installButton.Click += (_, __) =>
            {
                Action = UpdateDialogAction.Install;
                DialogResult = DialogResult.OK;
                Close();
            };

            _closeButton = new Button
            {
                Left = 608,
                Top = 464,
                Width = 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Close",
                DialogResult = DialogResult.Cancel
            };
            _closeButton.Click += (_, __) =>
            {
                Action = UpdateDialogAction.Close;
                Close();
            };

            Controls.Add(summaryLabel);
            Controls.Add(notesLabel);
            Controls.Add(notesTextBox);
            Controls.Add(_openInBrowserButton);
            Controls.Add(_installButton);
            Controls.Add(_closeButton);

            AcceptButton = _installButton;
            CancelButton = _closeButton;
        }

        public UpdateDialogAction Action { get; private set; }

        public static UpdateDialogAction ShowDialog(string currentVersion, string latestVersion, string releaseNotes)
        {
            using (var dialog = new UpdateAvailableDialog(currentVersion, latestVersion, releaseNotes))
            {
                dialog.ShowDialog();
                return dialog.Action;
            }
        }
    }

    internal sealed class DownloadProgressDialog : Form
    {
        private readonly Label _statusLabel;
        private readonly Label _speedLabel;
        private readonly Label _timeLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _retryButton;
        private readonly Button _closeButton;
        private TaskCompletionSource<bool> _retryCompletionSource;

        public DownloadProgressDialog()
        {
            Text = "Installing sing-box update";
            Icon = FormIconHelper.LoadApplicationIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 180);

            _statusLabel = new Label
            {
                AutoSize = false,
                Left = 12,
                Top = 12,
                Width = 536,
                Height = 40,
                Text = "Preparing download..."
            };

            _progressBar = new ProgressBar
            {
                Left = 12,
                Top = 60,
                Width = 536,
                Height = 24
            };

            _speedLabel = new Label
            {
                AutoSize = false,
                Left = 12,
                Top = 94,
                Width = 536,
                Height = 20,
                Text = "Speed: 0 B/s"
            };

            _timeLabel = new Label
            {
                AutoSize = false,
                Left = 12,
                Top = 116,
                Width = 536,
                Height = 20,
                Text = "Elapsed: 00:00 / Receive timeout: 00:30"
            };

            _retryButton = new Button
            {
                Left = 348,
                Top = 144,
                Width = 100,
                Text = "Retry",
                Enabled = false
            };
            _retryButton.Click += (_, __) =>
            {
                _retryButton.Enabled = false;
                _closeButton.Enabled = false;
                var retryCompletionSource = _retryCompletionSource;
                _retryCompletionSource = null;
                if (retryCompletionSource != null)
                {
                    retryCompletionSource.TrySetResult(true);
                }
            };

            _closeButton = new Button
            {
                Left = 456,
                Top = 144,
                Width = 100,
                Text = "Close",
                Enabled = false
            };
            _closeButton.Click += (_, __) =>
            {
                var retryCompletionSource = _retryCompletionSource;
                _retryCompletionSource = null;
                if (retryCompletionSource != null)
                {
                    retryCompletionSource.TrySetResult(false);
                }

                Close();
            };

            Controls.Add(_statusLabel);
            Controls.Add(_progressBar);
            Controls.Add(_speedLabel);
            Controls.Add(_timeLabel);
            Controls.Add(_retryButton);
            Controls.Add(_closeButton);
        }

        public void UpdateProgress(string fileName, long downloadedBytes, long totalBytes, double bytesPerSecond, TimeSpan elapsed, TimeSpan remaining)
        {
            var totalText = totalBytes > 0 ? FormatBytes(totalBytes) : "Unknown";
            var downloadedText = FormatBytes(downloadedBytes);
            _statusLabel.Text = "Downloading " + fileName + Environment.NewLine + downloadedText + " / " + totalText;
            _speedLabel.Text = "Speed: " + FormatBytes((long)Math.Max(bytesPerSecond, 0)) + "/s";
            _timeLabel.Text = "Elapsed: " + elapsed.ToString(@"mm\:ss") + " / Receive timeout: " + remaining.ToString(@"mm\:ss");

            if (totalBytes > 0)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                var progress = (int)Math.Min(100, Math.Max(0, downloadedBytes * 100L / totalBytes));
                _progressBar.Value = progress;
            }
            else
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
            }
        }

        public void UpdateStatus(string status)
        {
            _statusLabel.Text = status;
        }

        public void UpdateTiming(TimeSpan elapsed, TimeSpan remaining)
        {
            _timeLabel.Text = "Elapsed: " + elapsed.ToString(@"mm\:ss") + " / Receive timeout: " + remaining.ToString(@"mm\:ss");
        }

        public void Complete(string status)
        {
            CompletedSuccessfully = true;
            _statusLabel.Text = status;
            _speedLabel.Text = "Completed";
            _timeLabel.Text = "Elapsed: completed";
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 100;
            _retryButton.Enabled = false;
            _closeButton.Enabled = true;
        }

        public void Fail(string message, bool canRetry)
        {
            CompletedSuccessfully = false;
            _statusLabel.Text = message;
            _speedLabel.Text = "Failed";
            _timeLabel.Text = "Elapsed: failed";
            _retryButton.Enabled = canRetry;
            _closeButton.Enabled = true;
            _retryCompletionSource = canRetry ? new TaskCompletionSource<bool>() : null;
        }

        public bool CompletedSuccessfully { get; private set; }

        public Task<bool> WaitForRetryAsync()
        {
            return _retryCompletionSource == null
                ? Task.FromResult(false)
                : _retryCompletionSource.Task;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = Math.Max(bytes, 0);
            var unitIndex = 0;

            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return value.ToString(value >= 100 || unitIndex == 0 ? "0" : "0.0") + " " + units[unitIndex];
        }
    }

    internal sealed class TimedOperationDialog : Form
    {
        private readonly Label _statusLabel;
        private readonly Label _timeLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _closeButton;

        public TimedOperationDialog(string caption)
        {
            Text = caption;
            Icon = FormIconHelper.LoadApplicationIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 120);

            _statusLabel = new Label
            {
                AutoSize = false,
                Left = 12,
                Top = 12,
                Width = 396,
                Height = 20,
                Text = caption
            };

            _progressBar = new ProgressBar
            {
                Left = 12,
                Top = 40,
                Width = 396,
                Height = 18,
                Style = ProgressBarStyle.Marquee
            };

            _timeLabel = new Label
            {
                AutoSize = false,
                Left = 12,
                Top = 66,
                Width = 396,
                Height = 20,
                Text = "Remaining: 00:30"
            };

            _closeButton = new Button
            {
                Left = 308,
                Top = 88,
                Width = 100,
                Text = "Close",
                Enabled = false
            };
            _closeButton.Click += (_, __) => Close();

            Controls.Add(_statusLabel);
            Controls.Add(_progressBar);
            Controls.Add(_timeLabel);
            Controls.Add(_closeButton);
        }

        public void UpdateStatus(string status)
        {
            _statusLabel.Text = status;
        }

        public void UpdateRemaining(TimeSpan remaining)
        {
            _timeLabel.Text = "Remaining: " + remaining.ToString(@"mm\:ss");
        }

        public void Complete()
        {
            Close();
        }

        public void Fail(string status)
        {
            _statusLabel.Text = status;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            _closeButton.Enabled = true;
        }
    }
}
