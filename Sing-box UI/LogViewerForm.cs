using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sing_box_UI
{
    internal sealed class LogViewerForm : Form
    {
        private const int WmSetRedraw = 0x000B;
        private const int EmGetScrollPos = 0x04DD;
        private const int EmSetScrollPos = 0x04DE;
        private const int SbVert = 1;
        private const uint SifAll = 0x17;
        private static readonly Regex SeverityRegex = new Regex(@"\b(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _logPath;
        private readonly Label _severityFilterLabel;
        private readonly ComboBox _severityFilterComboBox;
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
        private bool _isInitialLoadInProgress;
        private bool _followTail;
        private bool _suspendViewTracking;
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

            _severityFilterLabel = new Label
            {
                Left = 12,
                Top = 14,
                Width = 104,
                Height = 24,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Text = "Log severity level:"
            };

            _severityFilterComboBox = new ComboBox
            {
                Left = 122,
                Top = 10,
                Width = 180,
                Height = 24,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _severityFilterComboBox.Items.AddRange(new object[]
            {
                new SeverityFilterOption("Default", null),
                new SeverityFilterOption("Trace", 0),
                new SeverityFilterOption("Debug", 1),
                new SeverityFilterOption("Info", 2),
                new SeverityFilterOption("Warn", 3),
                new SeverityFilterOption("Error", 4),
                new SeverityFilterOption("Fatal", 5)
            });
            _severityFilterComboBox.SelectedIndex = 0;

            _logTextBox = new RichTextBox
            {
                Left = 12,
                Top = 44,
                Width = 836,
                Height = 424,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                HideSelection = true,
                DetectUrls = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point)
            };
            _logTextBox.VScroll += (_, __) => HandleUserViewportInteraction();
            _logTextBox.MouseWheel += (_, __) => HandleUserViewportInteraction();
            _logTextBox.MouseUp += (_, __) => HandleUserViewportInteraction();
            _logTextBox.KeyUp += (_, __) => HandleUserViewportInteraction();
            _severityFilterComboBox.SelectedIndexChanged += async (_, __) => await ReloadVisibleLogFromFileAsync();

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

            Controls.Add(_severityFilterLabel);
            Controls.Add(_severityFilterComboBox);
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
                ActiveControl = _closeButton;
                _logTextBox.Text = "Loading logs...";
                BeginInvoke(new Action(async () => await LoadInitialLogAsync()));
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
                _followTail = false;
                _searchMatchPositions.Clear();
                _activeSearchQuery = null;
                _currentSearchMatchIndex = -1;
            };

            UpdateSearchUiState();
        }

        private void RefreshLogContents()
        {
            if (_isInitialLoadInProgress)
            {
                return;
            }

            var appendResult = ReadLogDeltaSafe();
            if (!appendResult.HasChanges)
            {
                return;
            }

            var filteredNewContent = ApplySeverityFilter(appendResult.NewContent);
            if (!appendResult.ResetExistingContent && string.IsNullOrEmpty(filteredNewContent))
            {
                return;
            }

            var previousScrollPosition = GetScrollPosition();
            var previousSelectionStart = _logTextBox.SelectionStart;
            var previousSelectionLength = _logTextBox.SelectionLength;
            var shouldScrollToEnd = _followTail;

            PerformWithoutRedraw(() =>
            {
                if (appendResult.ResetExistingContent)
                {
                    _logTextBox.Clear();
                }

                if (!string.IsNullOrEmpty(filteredNewContent))
                {
                    AppendStyledLogContent(filteredNewContent);
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

                if (previousSelectionLength > 0)
                {
                    RestoreSelection(previousSelectionStart, previousSelectionLength);
                }

                RestoreScrollPosition(previousScrollPosition);
            });
        }

        private async Task LoadInitialLogAsync()
        {
            await ReloadVisibleLogFromFileAsync();
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

        private Task<LogSnapshot> ReadEntireLogSnapshotSafeAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(_logPath))
                    {
                        return new LogSnapshot(string.Empty, 0);
                    }

                    using (var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream))
                    {
                        var content = reader.ReadToEnd();
                        return new LogSnapshot(content, stream.Length);
                    }
                }
                catch (Exception ex)
                {
                    return new LogSnapshot("Failed to read log file." + Environment.NewLine + ex.Message, 0);
                }
            });
        }

        private async Task ReloadVisibleLogFromFileAsync()
        {
            if (_isInitialLoadInProgress)
            {
                return;
            }

            _isInitialLoadInProgress = true;
            _refreshTimer.Stop();

            try
            {
                var snapshot = await ReadEntireLogSnapshotSafeAsync();
                var renderedSnapshot = await Task.Run(() =>
                {
                    var filteredContent = ApplySeverityFilter(snapshot.Content);
                    return new RenderedLogSnapshot(
                        filteredContent,
                        BuildStyledLogRtf(filteredContent),
                        snapshot.Length);
                });

                if (IsDisposed || Disposing)
                {
                    return;
                }

                _lastReadPosition = renderedSnapshot.SourceLength;

                PerformWithoutRedraw(() =>
                {
                    if (string.IsNullOrEmpty(renderedSnapshot.FilteredContent))
                    {
                        _logTextBox.Clear();
                    }
                    else
                    {
                        _logTextBox.Rtf = renderedSnapshot.RenderedRtf;
                    }

                    _logTextBox.SelectionStart = 0;
                    _logTextBox.SelectionLength = 0;
                });

                _followTail = IsScrolledToBottom();

                if (!string.IsNullOrWhiteSpace(_activeSearchQuery))
                {
                    RefreshSearchMatches(false, -1, 0);
                }
            }
            finally
            {
                _isInitialLoadInProgress = false;

                if (!IsDisposed && !Disposing)
                {
                    _refreshTimer.Start();
                }
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

        private bool IsScrolledToBottom()
        {
            if (!_logTextBox.IsHandleCreated)
            {
                return false;
            }

            var scrollInfo = new ScrollInfo
            {
                cbSize = (uint)Marshal.SizeOf(typeof(ScrollInfo)),
                fMask = SifAll
            };

            if (!GetScrollInfo(_logTextBox.Handle, SbVert, ref scrollInfo))
            {
                return false;
            }

            var bottomPosition = scrollInfo.nPos + (int)Math.Max(scrollInfo.nPage, 1u);
            return bottomPosition >= scrollInfo.nMax;
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

        private void UpdateFollowTailFromCurrentView()
        {
            if (_isInitialLoadInProgress || !_logTextBox.IsHandleCreated || _suspendViewTracking)
            {
                return;
            }

            _followTail = _logTextBox.SelectionLength == 0 && IsScrolledToBottom();
        }

        private void HandleUserViewportInteraction()
        {
            if (_isInitialLoadInProgress || !_logTextBox.IsHandleCreated || _suspendViewTracking)
            {
                return;
            }

            // Any user-driven viewport movement should immediately disable tail-following.
            // If the user actually lands on the very last line, we re-enable it on the next UI turn.
            _followTail = false;
            BeginInvoke(new Action(UpdateFollowTailFromCurrentView));
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
            _followTail = false;

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

        private string ApplySeverityFilter(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            var minimumSeverity = GetSelectedMinimumSeverity();
            if (!minimumSeverity.HasValue)
            {
                return content;
            }

            var normalizedContent = content.Replace("\r\n", "\n").Replace('\r', '\n');
            var builder = new StringBuilder(normalizedContent.Length);
            var lineStart = 0;

            for (var index = 0; index <= normalizedContent.Length; index++)
            {
                var isLineBreak = index == normalizedContent.Length || normalizedContent[index] == '\n';
                if (!isLineBreak)
                {
                    continue;
                }

                var lineText = normalizedContent.Substring(lineStart, index - lineStart);
                if (TryGetSeverityRank(lineText, out var severityRank) && severityRank >= minimumSeverity.Value)
                {
                    builder.Append(lineText);
                    if (index < normalizedContent.Length)
                    {
                        builder.Append(Environment.NewLine);
                    }
                }

                lineStart = index + 1;
            }

            return builder.ToString();
        }

        private int? GetSelectedMinimumSeverity()
        {
            var selectedOption = _severityFilterComboBox.SelectedItem as SeverityFilterOption;
            return selectedOption?.MinimumSeverityRank;
        }

        private static bool TryGetSeverityRank(string lineText, out int severityRank)
        {
            severityRank = -1;

            var severityMatch = SeverityRegex.Match(lineText ?? string.Empty);
            if (!severityMatch.Success)
            {
                return false;
            }

            switch (severityMatch.Value.ToUpperInvariant())
            {
                case "TRACE":
                    severityRank = 0;
                    return true;
                case "DEBUG":
                    severityRank = 1;
                    return true;
                case "INFO":
                    severityRank = 2;
                    return true;
                case "WARN":
                case "WARNING":
                    severityRank = 3;
                    return true;
                case "ERROR":
                    severityRank = 4;
                    return true;
                case "FATAL":
                    severityRank = 5;
                    return true;
                default:
                    return false;
            }
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

        private static string BuildStyledLogRtf(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }

            var colorTable = new[]
            {
                Color.Black,
                SystemColors.WindowText,
                SystemColors.Window,
                Color.FromArgb(103, 92, 120),
                Color.FromArgb(244, 241, 248),
                Color.FromArgb(58, 78, 108),
                Color.FromArgb(232, 239, 250),
                Color.FromArgb(35, 102, 78),
                Color.FromArgb(232, 247, 239),
                Color.FromArgb(122, 90, 31),
                Color.FromArgb(252, 247, 228),
                Color.FromArgb(104, 47, 34),
                Color.FromArgb(251, 236, 232),
                Color.FromArgb(92, 28, 28),
                Color.FromArgb(247, 224, 224)
            };

            var builder = new StringBuilder(content.Length + 1024);
            builder.Append(@"{\rtf1\ansi\ansicpg1251\deff0");
            builder.Append(@"{\fonttbl{\f0\fmodern Consolas;}}");
            builder.Append(@"{\colortbl ;");

            foreach (var color in colorTable)
            {
                builder.Append(@"\red").Append(color.R)
                    .Append(@"\green").Append(color.G)
                    .Append(@"\blue").Append(color.B)
                    .Append(';');
            }

            builder.Append('}');
            builder.Append(@"\fs18");

            var normalizedContent = content.Replace("\r\n", "\n").Replace('\r', '\n');
            var lineStart = 0;

            for (var index = 0; index <= normalizedContent.Length; index++)
            {
                var isLineBreak = index == normalizedContent.Length || normalizedContent[index] == '\n';
                if (!isLineBreak)
                {
                    continue;
                }

                var lineText = normalizedContent.Substring(lineStart, index - lineStart);
                var lineStyle = ResolveSeverityStyle(lineText);
                if (lineStyle == null)
                {
                    builder.Append(@"\cf2\highlight3 ");
                }
                else
                {
                    builder.Append(@"\cf").Append(GetForegroundColorIndex(lineStyle))
                        .Append(@"\highlight").Append(GetBackgroundColorIndex(lineStyle))
                        .Append(' ');
                }

                builder.Append(EscapeRtfText(lineText));
                if (index < normalizedContent.Length)
                {
                    builder.Append(@"\par ");
                }

                lineStart = index + 1;
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static int GetForegroundColorIndex(SeverityStyle style)
        {
            if (style.ForegroundColor == Color.FromArgb(103, 92, 120)) return 4;
            if (style.ForegroundColor == Color.FromArgb(58, 78, 108)) return 6;
            if (style.ForegroundColor == Color.FromArgb(35, 102, 78)) return 8;
            if (style.ForegroundColor == Color.FromArgb(122, 90, 31)) return 10;
            if (style.ForegroundColor == Color.FromArgb(104, 47, 34)) return 12;
            if (style.ForegroundColor == Color.FromArgb(92, 28, 28)) return 14;
            return 2;
        }

        private static int GetBackgroundColorIndex(SeverityStyle style)
        {
            if (style.BackgroundColor == Color.FromArgb(244, 241, 248)) return 5;
            if (style.BackgroundColor == Color.FromArgb(232, 239, 250)) return 7;
            if (style.BackgroundColor == Color.FromArgb(232, 247, 239)) return 9;
            if (style.BackgroundColor == Color.FromArgb(252, 247, 228)) return 11;
            if (style.BackgroundColor == Color.FromArgb(251, 236, 232)) return 13;
            if (style.BackgroundColor == Color.FromArgb(247, 224, 224)) return 15;
            return 3;
        }

        private static string EscapeRtfText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length * 2);
            foreach (var character in text)
            {
                switch (character)
                {
                    case '\\':
                    case '{':
                    case '}':
                        builder.Append('\\').Append(character);
                        break;
                    case '\t':
                        builder.Append(@"\tab ");
                        break;
                    default:
                        if (character <= 0x7f)
                        {
                            builder.Append(character);
                        }
                        else
                        {
                            builder.Append(@"\u").Append((short)character).Append('?');
                        }
                        break;
                }
            }

            return builder.ToString();
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

            _suspendViewTracking = true;
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
                _suspendViewTracking = false;
            }
        }

        private NativePoint GetScrollPosition()
        {
            var position = new NativePoint();
            SendMessage(_logTextBox.Handle, EmGetScrollPos, IntPtr.Zero, ref position);
            return position;
        }

        private void RestoreScrollPosition(NativePoint position)
        {
            SendMessage(_logTextBox.Handle, EmSetScrollPos, IntPtr.Zero, ref position);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetScrollInfo(IntPtr hwnd, int fnBar, ref ScrollInfo lpsi);

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

        private sealed class LogSnapshot
        {
            public LogSnapshot(string content, long length)
            {
                Content = content ?? string.Empty;
                Length = length;
            }

            public string Content { get; }

            public long Length { get; }
        }

        private sealed class RenderedLogSnapshot
        {
            public RenderedLogSnapshot(string filteredContent, string renderedRtf, long sourceLength)
            {
                FilteredContent = filteredContent ?? string.Empty;
                RenderedRtf = renderedRtf;
                SourceLength = sourceLength;
            }

            public string FilteredContent { get; }

            public string RenderedRtf { get; }

            public long SourceLength { get; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;

            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScrollInfo
        {
            public uint cbSize;

            public uint fMask;

            public int nMin;

            public int nMax;

            public uint nPage;

            public int nPos;

            public int nTrackPos;
        }

        private sealed class SeverityFilterOption
        {
            public SeverityFilterOption(string displayText, int? minimumSeverityRank)
            {
                DisplayText = displayText;
                MinimumSeverityRank = minimumSeverityRank;
            }

            public string DisplayText { get; }

            public int? MinimumSeverityRank { get; }

            public override string ToString()
            {
                return DisplayText;
            }
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
