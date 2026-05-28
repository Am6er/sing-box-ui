using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Sing_box_UI
{
    internal sealed class LogViewerForm : Form
    {
        private const int EmGetFirstVisibleLine = 0x00CE;
        private const int EmLineScroll = 0x00B6;
        private const int WmSetRedraw = 0x000B;
        private static readonly Regex SeverityRegex = new Regex(@"\b(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _logPath;
        private readonly RichTextBox _logTextBox;
        private readonly Label _findInLogsLabel;
        private readonly TextBox _findTextBox;
        private readonly Button _findButton;
        private readonly Button _findNextButton;
        private readonly Label _occurrencesLabel;
        private readonly Button _clearAllButton;
        private readonly Button _showFileButton;
        private readonly Button _closeButton;
        private readonly Timer _refreshTimer;

        private long _lastReadPosition;
        private string _activeSearchQuery;
        private List<int> _searchMatchPositions = new List<int>();
        private int _currentSearchMatchIndex = -1;

        public LogViewerForm(string logPath)
        {
            _logPath = logPath;

            Text = "Sing-box logs";
            Icon = FormIconHelper.LoadApplicationIcon();
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 480);
            ClientSize = new Size(860, 560);

            _logTextBox = new RichTextBox
            {
                Left = 12,
                Top = 12,
                Width = 836,
                Height = 456,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                HideSelection = true,
                DetectUrls = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point)
            };

            _findInLogsLabel = new Label
            {
                Left = 12,
                Top = 480,
                Width = 80,
                Height = 24,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                Text = "Find in logs:"
            };

            _findTextBox = new TextBox
            {
                Left = 96,
                Top = 476,
                Width = 280,
                Height = 24,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            _findTextBox.TextChanged += (_, __) => ResetSearchState();
            _findTextBox.KeyDown += FindTextBoxKeyDown;

            _findButton = new Button
            {
                Left = 388,
                Top = 475,
                Width = 80,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Text = "Find"
            };
            _findButton.Click += (_, __) => FindInLogs(false);

            _findNextButton = new Button
            {
                Left = 476,
                Top = 475,
                Width = 80,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Text = "Find next"
            };
            _findNextButton.Click += (_, __) => FindInLogs(true);

            _occurrencesLabel = new Label
            {
                Left = 564,
                Top = 480,
                Width = 284,
                Height = 24,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                TextAlign = ContentAlignment.MiddleRight,
                Text = "Number of occurences: 0/0"
            };

            _clearAllButton = new Button
            {
                Left = 412,
                Top = 516,
                Width = 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Clear all"
            };
            _clearAllButton.Click += (_, __) => ClearAllLogs();

            _showFileButton = new Button
            {
                Left = 636,
                Top = 516,
                Width = 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Show file..."
            };
            _showFileButton.Click += (_, __) => ShowLogFileInExplorer();

            _closeButton = new Button
            {
                Left = 748,
                Top = 516,
                Width = 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Close",
                DialogResult = DialogResult.OK
            };
            _closeButton.Click += (_, __) => Close();

            Controls.Add(_logTextBox);
            Controls.Add(_findInLogsLabel);
            Controls.Add(_findTextBox);
            Controls.Add(_findButton);
            Controls.Add(_findNextButton);
            Controls.Add(_occurrencesLabel);
            Controls.Add(_clearAllButton);
            Controls.Add(_showFileButton);
            Controls.Add(_closeButton);

            CancelButton = _closeButton;

            _refreshTimer = new Timer
            {
                Interval = 1000
            };
            _refreshTimer.Tick += (_, __) => RefreshLogContents();

            Shown += (_, __) =>
            {
                RefreshLogContents();
                ActiveControl = _closeButton;
                _refreshTimer.Start();
            };

            Activated += (_, __) => ClearFullTextSelectionIfNeeded();
            Resize += (_, __) =>
            {
                if (WindowState != FormWindowState.Minimized)
                {
                    BeginInvoke(new Action(ClearFullTextSelectionIfNeeded));
                }
            };

            FormClosed += (_, __) =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                PerformWithoutRedraw(() => _logTextBox.Clear());
                _lastReadPosition = 0;
                _searchMatchPositions.Clear();
                _activeSearchQuery = null;
                _currentSearchMatchIndex = -1;
            };

            UpdateSearchUiState();
        }

        private void RefreshLogContents()
        {
            var appendResult = ReadLogDeltaSafe();
            if (!appendResult.HasChanges)
            {
                return;
            }

            var previousFirstVisibleLine = GetFirstVisibleLine();
            var previousLineCount = Math.Max(1, _logTextBox.Lines.Length);
            var visibleLineCount = GetVisibleLineCount();
            var previousSelectionStart = _logTextBox.SelectionStart;
            var previousSelectionLength = _logTextBox.SelectionLength;
            var shouldScrollToEnd = previousSelectionLength == 0 &&
                previousFirstVisibleLine + visibleLineCount >= previousLineCount - 1;

            PerformWithoutRedraw(() =>
            {
                if (appendResult.ResetExistingContent)
                {
                    _logTextBox.Clear();
                }

                if (!string.IsNullOrEmpty(appendResult.NewContent))
                {
                    AppendStyledLogContent(appendResult.NewContent);
                }

                if (!string.IsNullOrWhiteSpace(_activeSearchQuery))
                {
                    RefreshSearchMatches(true, previousSelectionStart, previousSelectionLength);
                }

                if (shouldScrollToEnd)
                {
                    _logTextBox.SelectionStart = _logTextBox.TextLength;
                    _logTextBox.SelectionLength = 0;
                    _logTextBox.ScrollToCaret();
                    return;
                }

                RestoreFirstVisibleLine(previousFirstVisibleLine);
                RestoreSelection(previousSelectionStart, previousSelectionLength);
            });
        }

        private LogAppendResult ReadLogDeltaSafe()
        {
            try
            {
                if (!File.Exists(_logPath))
                {
                    _lastReadPosition = 0;
                    return new LogAppendResult(false, false, string.Empty);
                }

                using (var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    var shouldResetExistingContent = stream.Length < _lastReadPosition;
                    if (shouldResetExistingContent)
                    {
                        _lastReadPosition = 0;
                    }

                    if (stream.Length == _lastReadPosition)
                    {
                        return new LogAppendResult(false, shouldResetExistingContent, string.Empty);
                    }

                    stream.Seek(_lastReadPosition, SeekOrigin.Begin);
                    var newContent = reader.ReadToEnd();
                    _lastReadPosition = stream.Position;
                    return new LogAppendResult(true, shouldResetExistingContent, newContent);
                }
            }
            catch (Exception ex)
            {
                _lastReadPosition = 0;
                return new LogAppendResult(true, true, "Failed to read log file." + Environment.NewLine + ex.Message);
            }
        }

        private void ShowLogFileInExplorer()
        {
            try
            {
                if (File.Exists(_logPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + _logPath + "\"",
                        UseShellExecute = true
                    });
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetDirectoryName(_logPath),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to open log file location:" + Environment.NewLine + ex.Message,
                    "Sing-box UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ClearAllLogs()
        {
            try
            {
                File.WriteAllText(_logPath, string.Empty);
                _lastReadPosition = 0;
                PerformWithoutRedraw(() => _logTextBox.Clear());
                ResetSearchState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to clear log file:" + Environment.NewLine + ex.Message,
                    "Sing-box UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private int GetFirstVisibleLine()
        {
            return SendMessage(_logTextBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
        }

        private int GetVisibleLineCount()
        {
            var fontHeight = Math.Max(1, _logTextBox.Font.Height);
            return Math.Max(1, _logTextBox.ClientSize.Height / fontHeight);
        }

        private void RestoreFirstVisibleLine(int firstVisibleLine)
        {
            if (firstVisibleLine < 0)
            {
                return;
            }

            var currentFirstVisibleLine = GetFirstVisibleLine();
            var delta = firstVisibleLine - currentFirstVisibleLine;
            if (delta == 0)
            {
                return;
            }

            SendMessage(_logTextBox.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(delta));
        }

        private void ClearFullTextSelectionIfNeeded()
        {
            if (_logTextBox.TextLength == 0)
            {
                return;
            }

            if (_logTextBox.SelectionLength == _logTextBox.TextLength)
            {
                _logTextBox.SelectionLength = 0;
            }
        }

        private void FindTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            FindInLogs((ModifierKeys & Keys.Shift) == Keys.Shift);
            e.SuppressKeyPress = true;
            e.Handled = true;
        }

        private void FindInLogs(bool findNext)
        {
            var query = _findTextBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                ResetSearchState();
                return;
            }

            if (!string.Equals(_activeSearchQuery, query, StringComparison.Ordinal) || _searchMatchPositions.Count == 0)
            {
                _activeSearchQuery = query;
                RefreshSearchMatches(false, -1, 0);
            }

            if (_searchMatchPositions.Count == 0)
            {
                MessageBox.Show(
                    "No matches were found in the log.",
                    "Sing-box UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (findNext)
            {
                _currentSearchMatchIndex = (_currentSearchMatchIndex + 1) % _searchMatchPositions.Count;
            }
            else
            {
                _currentSearchMatchIndex = 0;
            }

            SelectCurrentSearchMatch();
        }

        private void RefreshSearchMatches(bool preserveCurrentSelection, int previousSelectionStart, int previousSelectionLength)
        {
            _searchMatchPositions = BuildSearchMatchPositions(_activeSearchQuery, _logTextBox.Text);

            if (_searchMatchPositions.Count == 0)
            {
                _currentSearchMatchIndex = -1;
                UpdateOccurrencesLabel();
                return;
            }

            if (preserveCurrentSelection && previousSelectionLength > 0)
            {
                _currentSearchMatchIndex = _searchMatchPositions.FindIndex(position => position == previousSelectionStart);
            }

            if (_currentSearchMatchIndex < 0 || _currentSearchMatchIndex >= _searchMatchPositions.Count)
            {
                _currentSearchMatchIndex = 0;
            }

            UpdateOccurrencesLabel();
        }

        private void SelectCurrentSearchMatch()
        {
            if (_currentSearchMatchIndex < 0 || _currentSearchMatchIndex >= _searchMatchPositions.Count)
            {
                UpdateOccurrencesLabel();
                return;
            }

            var queryLength = (_activeSearchQuery ?? string.Empty).Length;
            var matchStart = _searchMatchPositions[_currentSearchMatchIndex];

            _logTextBox.SelectionStart = matchStart;
            _logTextBox.SelectionLength = queryLength;
            _logTextBox.ScrollToCaret();
            _logTextBox.Focus();

            UpdateOccurrencesLabel();
        }

        private void ResetSearchState()
        {
            _activeSearchQuery = null;
            _searchMatchPositions.Clear();
            _currentSearchMatchIndex = -1;
            UpdateSearchUiState();
            UpdateOccurrencesLabel();
        }

        private void UpdateSearchUiState()
        {
            var hasQuery = !string.IsNullOrWhiteSpace(_findTextBox.Text);
            _findButton.Enabled = hasQuery;
            _findNextButton.Enabled = hasQuery;
        }

        private void UpdateOccurrencesLabel()
        {
            var currentOccurrence = _currentSearchMatchIndex >= 0 ? _currentSearchMatchIndex + 1 : 0;
            _occurrencesLabel.Text = "Number of occurences: " + currentOccurrence + "/" + _searchMatchPositions.Count;
        }

        private static List<int> BuildSearchMatchPositions(string query, string text)
        {
            var positions = new List<int>();
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
            {
                return positions;
            }

            var startIndex = 0;
            while (startIndex < text.Length)
            {
                var matchIndex = text.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    break;
                }

                positions.Add(matchIndex);
                startIndex = matchIndex + query.Length;
            }

            return positions;
        }

        private void RestoreSelection(int selectionStart, int selectionLength)
        {
            var clampedStart = Math.Max(0, Math.Min(selectionStart, _logTextBox.TextLength));
            var maxLength = Math.Max(0, _logTextBox.TextLength - clampedStart);
            var clampedLength = Math.Max(0, Math.Min(selectionLength, maxLength));

            _logTextBox.SelectionStart = clampedStart;
            _logTextBox.SelectionLength = clampedLength;
        }

        private void AppendStyledLogContent(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            var normalizedContent = content.Replace("\r\n", "\n").Replace('\r', '\n');
            var lineStart = 0;

            for (var index = 0; index < normalizedContent.Length; index++)
            {
                if (normalizedContent[index] != '\n')
                {
                    continue;
                }

                AppendStyledLogLine(normalizedContent.Substring(lineStart, index - lineStart), true);
                lineStart = index + 1;
            }

            if (lineStart < normalizedContent.Length)
            {
                AppendStyledLogLine(normalizedContent.Substring(lineStart), false);
            }
        }

        private void AppendStyledLogLine(string lineText, bool appendNewLine)
        {
            var startIndex = _logTextBox.TextLength;
            var appendedText = appendNewLine ? lineText + Environment.NewLine : lineText;

            _logTextBox.SelectionStart = startIndex;
            _logTextBox.SelectionLength = 0;
            _logTextBox.SelectionColor = SystemColors.WindowText;
            _logTextBox.SelectionBackColor = SystemColors.Window;
            _logTextBox.AppendText(appendedText);

            if (string.IsNullOrEmpty(lineText))
            {
                return;
            }

            var lineStyle = ResolveSeverityStyle(lineText);
            if (lineStyle == null)
            {
                return;
            }

            _logTextBox.SelectionStart = startIndex;
            _logTextBox.SelectionLength = lineText.Length;
            _logTextBox.SelectionColor = lineStyle.ForegroundColor;
            _logTextBox.SelectionBackColor = lineStyle.BackgroundColor;
        }

        private static SeverityStyle ResolveSeverityStyle(string lineText)
        {
            var severityMatch = SeverityRegex.Match(lineText ?? string.Empty);
            if (!severityMatch.Success)
            {
                return null;
            }

            switch (severityMatch.Value.ToUpperInvariant())
            {
                case "TRACE":
                    return new SeverityStyle(Color.FromArgb(103, 92, 120), Color.FromArgb(244, 241, 248));
                case "DEBUG":
                    return new SeverityStyle(Color.FromArgb(58, 78, 108), Color.FromArgb(232, 239, 250));
                case "INFO":
                    return new SeverityStyle(Color.FromArgb(35, 102, 78), Color.FromArgb(232, 247, 239));
                case "WARN":
                case "WARNING":
                    return new SeverityStyle(Color.FromArgb(122, 90, 31), Color.FromArgb(252, 247, 228));
                case "ERROR":
                    return new SeverityStyle(Color.FromArgb(104, 47, 34), Color.FromArgb(251, 236, 232));
                case "FATAL":
                    return new SeverityStyle(Color.FromArgb(92, 28, 28), Color.FromArgb(247, 224, 224));
                default:
                    return null;
            }
        }

        private void PerformWithoutRedraw(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (!_logTextBox.IsHandleCreated)
            {
                action();
                return;
            }

            SendMessage(_logTextBox.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);

            try
            {
                action();
            }
            finally
            {
                SendMessage(_logTextBox.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                _logTextBox.Invalidate();
                _logTextBox.Update();
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private sealed class LogAppendResult
        {
            public LogAppendResult(bool hasChanges, bool resetExistingContent, string newContent)
            {
                HasChanges = hasChanges;
                ResetExistingContent = resetExistingContent;
                NewContent = newContent ?? string.Empty;
            }

            public bool HasChanges { get; }

            public bool ResetExistingContent { get; }

            public string NewContent { get; }
        }

        private sealed class SeverityStyle
        {
            public SeverityStyle(Color foregroundColor, Color backgroundColor)
            {
                ForegroundColor = foregroundColor;
                BackgroundColor = backgroundColor;
            }

            public Color ForegroundColor { get; }

            public Color BackgroundColor { get; }
        }
    }
}
