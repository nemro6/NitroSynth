using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NitroSynth.App.ViewModels;

namespace NitroSynth.App
{
    public partial class HexEditorWindow : Window
    {
        private const int BytesPerLine = 16;

        private Func<byte[], Task<MainWindowViewModel.HexSaveResult>> _saveAsync;
        private bool _isSaving;
        private bool _updatingText;
        private string _statusMessage = string.Empty;

        private byte[] _bytes = Array.Empty<byte>();
        private int[] _nibblePositions = Array.Empty<int>();
        private HexSearchWindow? _searchWindow;

        public HexEditorWindow()
        {
            InitializeComponent();
            _saveAsync = _ => Task.FromResult(new MainWindowViewModel.HexSaveResult(false, "Save callback is not set."));
            HexTextEditor.PropertyChanged += OnHexEditorPropertyChanged;
            RefreshStatusText();
        }

        public HexEditorWindow(
            string title,
            string headerText,
            byte[] initialBytes,
            Func<byte[], Task<MainWindowViewModel.HexSaveResult>> saveAsync)
            : this()
        {
            Title = string.IsNullOrWhiteSpace(title) ? "HEX Editor" : title;
            _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
            _ = headerText;
            SetBytes(initialBytes ?? Array.Empty<byte>());
            SetStatusMessage(string.Empty);
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _searchWindow?.Close();
            }
            catch
            {
            }
            finally
            {
                HexTextEditor.PropertyChanged -= OnHexEditorPropertyChanged;
                _searchWindow = null;
                base.OnClosed(e);
            }
        }

        private async void OnSaveClicked(object? sender, RoutedEventArgs e)
        {
            if (_isSaving)
                return;

            _isSaving = true;
            SaveButton.IsEnabled = false;
            SetStatusMessage("Saving...");

            try
            {
                var result = await _saveAsync((byte[])_bytes.Clone());
                SetStatusMessage(result.Message);
                if (result.Success)
                    UpdateByteCount(_bytes.Length);
            }
            catch (Exception ex)
            {
                SetStatusMessage($"Save failed: {ex.Message}");
            }
            finally
            {
                SaveButton.IsEnabled = true;
                _isSaving = false;
            }
        }

        private void OnCloseClicked(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnJumpClicked(object? sender, RoutedEventArgs e)
        {
            JumpToAddressFromInput();
        }

        private void OnJumpAddressKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            JumpToAddressFromInput();
        }

        private void OnOpenFindClicked(object? sender, RoutedEventArgs e)
        {
            OpenSearchWindow();
        }

        private void OpenSearchWindow()
        {
            if (_searchWindow is not null)
            {
                _searchWindow.Activate();
                _searchWindow.FocusSearchInput();
                return;
            }

            _searchWindow = new HexSearchWindow(ExecuteSearchRequest, BuildSearchMatchListResult);
            _searchWindow.Closed += (_, _) => _searchWindow = null;
            _searchWindow.Show(this);
            _searchWindow.FocusSearchInput();
        }

        private async void OnHexEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (_updatingText || _bytes.Length == 0)
                return;

            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _ = shift; // reserved for future behavior customization

            if (ctrl && e.Key == Key.C)
            {
                e.Handled = true;
                await CopySelectionAsHexAsync();
                return;
            }

            if (ctrl && e.Key == Key.X)
            {
                e.Handled = true;
                await CopySelectionAsHexAsync();
                return;
            }

            if (ctrl && e.Key == Key.V)
            {
                e.Handled = true;
                await PasteHexAsync();
                return;
            }

            if (ctrl && e.Key == Key.F)
            {
                e.Handled = true;
                OpenSearchWindow();
                return;
            }

            if (TryMapHexKey(e.Key, out int nibble))
            {
                e.Handled = true;
                ApplySingleNibble(nibble);
                return;
            }

            if (e.Key == Key.Back)
            {
                e.Handled = true;
                MoveCaretByNibble(-1);
                return;
            }

            if (e.Key == Key.Delete)
            {
                e.Handled = true;
                MoveCaretByNibble(+1);
                return;
            }
        }

        private void SetBytes(byte[] bytes)
        {
            _bytes = (byte[])bytes.Clone();
            RebuildEditorText();
            UpdateByteCount(_bytes.Length);
            RefreshStatusText();
        }

        private void RebuildEditorText()
        {
            _updatingText = true;
            try
            {
                if (_bytes.Length == 0)
                {
                    _nibblePositions = Array.Empty<int>();
                    HexTextEditor.Text = string.Empty;
                    OffsetTextEditor.Text = string.Empty;
                    DecodedTextEditor.Text = string.Empty;
                    HexTextEditor.CaretIndex = 0;
                    HexTextEditor.SelectionStart = 0;
                    HexTextEditor.SelectionEnd = 0;
                    return;
                }

                int lineCount = (_bytes.Length + BytesPerLine - 1) / BytesPerLine;
                var hexSb = new StringBuilder(lineCount * 48);
                var offsetSb = new StringBuilder(lineCount * 10);
                var decodedSb = new StringBuilder(lineCount * 20);
                _nibblePositions = new int[_bytes.Length * 2];

                int nibbleCursor = 0;
                for (int line = 0; line < lineCount; line++)
                {
                    int lineOffset = line * BytesPerLine;
                    int lineByteCount = Math.Min(BytesPerLine, _bytes.Length - lineOffset);

                    offsetSb.Append(lineOffset.ToString("X8", CultureInfo.InvariantCulture));

                    for (int column = 0; column < lineByteCount; column++)
                    {
                        if (column > 0)
                            hexSb.Append(' ');

                        int byteIndex = lineOffset + column;

                        int firstNibblePos = hexSb.Length;
                        string hex = _bytes[byteIndex].ToString("X2", CultureInfo.InvariantCulture);
                        hexSb.Append(hex);

                        _nibblePositions[nibbleCursor++] = firstNibblePos;
                        _nibblePositions[nibbleCursor++] = firstNibblePos + 1;

                        decodedSb.Append(ToDecodedChar(_bytes[byteIndex]));
                    }

                    for (int column = lineByteCount; column < BytesPerLine; column++)
                    {
                        decodedSb.Append(' ');
                    }

                    if (line + 1 < lineCount)
                    {
                        offsetSb.AppendLine();
                        hexSb.AppendLine();
                        decodedSb.AppendLine();
                    }
                }

                OffsetTextEditor.Text = offsetSb.ToString();
                HexTextEditor.Text = hexSb.ToString();
                DecodedTextEditor.Text = decodedSb.ToString();
            }
            finally
            {
                _updatingText = false;
            }
        }

        private void ApplySingleNibble(int nibble)
        {
            if (!TryGetSelectionNibbleRange(out int startNibble, out _))
                return;

            int targetNibble = Math.Clamp(startNibble, 0, _nibblePositions.Length - 1);
            int targetByte = targetNibble / 2;
            bool highNibble = (targetNibble & 1) == 0;

            byte old = _bytes[targetByte];
            byte updated = highNibble
                ? (byte)((old & 0x0F) | (nibble << 4))
                : (byte)((old & 0xF0) | nibble);

            _bytes[targetByte] = updated;

            int nextNibble = Math.Min(targetNibble + 1, _nibblePositions.Length - 1);
            RebuildEditorText();
            SetCaretToNibble(nextNibble);
        }

        private void MoveCaretByNibble(int delta)
        {
            if (!TryGetSelectionNibbleRange(out int startNibble, out int endNibble))
                return;

            int current = delta >= 0 ? endNibble : startNibble;
            int next = Math.Clamp(current + delta, 0, _nibblePositions.Length - 1);
            SetCaretToNibble(next);
        }

        private async Task PasteHexAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var clipboard = topLevel?.Clipboard;
            if (clipboard is null)
                return;

            string? text = await clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!TryParseHexBytes(text, out var parsed) || parsed.Length == 0)
            {
                SetStatusMessage("Paste failed: no hex bytes found.");
                return;
            }

            if (!TryGetSelectionNibbleRange(out int startNibble, out _))
                return;

            int startByte = Math.Clamp(startNibble / 2, 0, _bytes.Length - 1);
            int writeCount = Math.Min(parsed.Length, _bytes.Length - startByte);
            if (writeCount <= 0)
                return;

            Buffer.BlockCopy(parsed, 0, _bytes, startByte, writeCount);
            RebuildEditorText();

            int nextNibble = Math.Min((startByte + writeCount) * 2, _nibblePositions.Length - 1);
            SetCaretToNibble(nextNibble);
            SetStatusMessage($"Pasted {writeCount} bytes.");
        }

        private async Task CopySelectionAsHexAsync()
        {
            if (!TryGetSelectionByteRange(out int startByte, out int byteCount))
                return;

            if (byteCount <= 0)
                return;

            var sb = new StringBuilder(byteCount * 3);
            for (int i = 0; i < byteCount; i++)
            {
                if (i > 0)
                    sb.Append(' ');

                sb.Append(_bytes[startByte + i].ToString("X2", CultureInfo.InvariantCulture));
            }

            var topLevel = TopLevel.GetTopLevel(this);
            var clipboard = topLevel?.Clipboard;
            if (clipboard is null)
                return;

            await clipboard.SetTextAsync(sb.ToString());
            SetStatusMessage($"Copied {byteCount} bytes.");
        }

        private bool TryGetSelectionByteRange(out int startByte, out int byteCount)
        {
            startByte = 0;
            byteCount = 0;

            if (!TryGetSelectionNibbleRange(out int startNibble, out int endNibble))
                return false;

            int firstByte = Math.Clamp(startNibble / 2, 0, _bytes.Length - 1);
            int lastByte = Math.Clamp(endNibble / 2, 0, _bytes.Length - 1);
            if (lastByte < firstByte)
                return false;

            startByte = firstByte;
            byteCount = lastByte - firstByte + 1;
            return true;
        }

        private bool TryGetSelectionNibbleRange(out int startNibble, out int endNibble)
        {
            startNibble = 0;
            endNibble = 0;

            if (_nibblePositions.Length == 0)
                return false;

            int selectionStart = HexTextEditor.SelectionStart;
            int selectionEnd = HexTextEditor.SelectionEnd;
            if (selectionEnd < selectionStart)
            {
                (selectionStart, selectionEnd) = (selectionEnd, selectionStart);
            }

            if (selectionStart == selectionEnd)
            {
                if (!TryFindNibbleAtOrAfter(selectionStart, out startNibble))
                    return false;

                endNibble = startNibble;
                return true;
            }

            bool hasStart = TryFindNibbleAtOrAfter(selectionStart, out startNibble);
            bool hasEnd = TryFindNibbleAtOrBefore(selectionEnd - 1, out endNibble);
            return hasStart && hasEnd && endNibble >= startNibble;
        }

        private void SetCaretToNibble(int nibbleIndex)
        {
            if (_nibblePositions.Length == 0)
                return;

            int clamped = Math.Clamp(nibbleIndex, 0, _nibblePositions.Length - 1);
            int pos = _nibblePositions[clamped];

            HexTextEditor.CaretIndex = pos;
            HexTextEditor.SelectionStart = pos;
            HexTextEditor.SelectionEnd = pos;
        }

        private void SetCaretToByte(int byteIndex)
        {
            if (_bytes.Length == 0)
                return;

            int clampedByte = Math.Clamp(byteIndex, 0, _bytes.Length - 1);
            SetCaretToNibble(clampedByte * 2);
            HexTextEditor.Focus();
        }

        private bool TryGetCurrentByteIndex(out int byteIndex)
        {
            byteIndex = 0;

            if (_bytes.Length == 0)
                return false;

            if (!TryGetSelectionNibbleRange(out int startNibble, out _))
                return false;

            byteIndex = Math.Clamp(startNibble / 2, 0, _bytes.Length - 1);
            return true;
        }

        private void JumpToAddressFromInput()
        {
            if (_bytes.Length == 0)
                return;

            if (!TryParseAddress(JumpAddressBox.Text, out int address))
            {
                SetStatusMessage("Invalid address.");
                return;
            }

            if (address < 0 || address >= _bytes.Length)
            {
                SetStatusMessage($"Address out of range (0x00000000 - 0x{_bytes.Length - 1:X8}).");
                return;
            }

            SetCaretToByte(address);
            SetStatusMessage($"Jumped to 0x{address:X8}.");
        }

        private (bool success, string message) ExecuteSearchRequest(HexSearchRequest request, bool forward)
        {
            var result = TrySearch(request, forward);
            SetStatusMessage(result.message);
            return result;
        }

        private (bool success, string message) BuildSearchMatchListResult(HexSearchRequest request)
        {
            if (_bytes.Length == 0)
                return (false, "No data.");

            if (!TryBuildSearchPattern(request, out var pattern, out var patternError))
                return (false, patternError);

            if (!TryResolveSearchRange(request.RangeKind, out int rangeStart, out int rangeEndExclusive, out var rangeError))
                return (false, rangeError);

            const int displayLimit = 4096;
            int foundCount = 0;
            var matches = new List<int>(Math.Min(displayLimit, 256));
            int i = rangeStart;
            int lastStart = rangeEndExclusive - pattern.Length;
            while (i <= lastStart)
            {
                int found = IndexOfPattern(_bytes, pattern, i, rangeEndExclusive - i);
                if (found < 0)
                    break;

                foundCount++;
                if (matches.Count < displayLimit)
                    matches.Add(found);

                i = found + 1;
            }

            if (foundCount == 0)
                return (false, "No matches found.");

            bool truncated = foundCount > matches.Count;
            ShowMatchListWindow(matches, foundCount, truncated);
            return truncated
                ? (true, $"Matches: {foundCount} (showing first {matches.Count}).")
                : (true, $"Matches: {foundCount}.");
        }

        private (bool success, string message) TrySearch(HexSearchRequest request, bool forward)
        {
            if (_bytes.Length == 0)
                return (false, "No data.");

            if (!TryBuildSearchPattern(request, out var pattern, out var patternError))
                return (false, patternError);

            if (!TryResolveSearchRange(request.RangeKind, out int rangeStart, out int rangeEndExclusive, out var rangeError))
                return (false, rangeError);

            if (!TryGetCurrentByteIndex(out int currentByte))
                currentByte = rangeStart - 1;

            if (request.RangeKind == HexSearchRangeKind.Selection)
            {
                if (currentByte < rangeStart || currentByte >= rangeEndExclusive)
                    currentByte = forward ? rangeStart - 1 : rangeEndExclusive;
            }

            int found;
            bool wrapped = false;

            if (request.RangeKind == HexSearchRangeKind.FromCursor)
            {
                if (forward)
                {
                    int searchStart = Math.Clamp(currentByte + 1, 0, _bytes.Length);
                    found = IndexOfPattern(_bytes, pattern, searchStart, _bytes.Length - searchStart);
                }
                else
                {
                    int searchEndExclusive = Math.Clamp(currentByte, 0, _bytes.Length);
                    found = LastIndexOfPattern(_bytes, pattern, 0, searchEndExclusive);
                }
            }
            else if (forward)
            {
                int firstStart = Math.Clamp(currentByte + 1, rangeStart, rangeEndExclusive);
                found = IndexOfPattern(_bytes, pattern, firstStart, rangeEndExclusive - firstStart);

                if (found < 0 && firstStart > rangeStart)
                {
                    found = IndexOfPattern(_bytes, pattern, rangeStart, firstStart - rangeStart);
                    wrapped = found >= 0;
                }
            }
            else
            {
                int firstEndExclusive = Math.Clamp(currentByte, rangeStart, rangeEndExclusive);
                found = LastIndexOfPattern(_bytes, pattern, rangeStart, firstEndExclusive - rangeStart);

                if (found < 0 && firstEndExclusive < rangeEndExclusive)
                {
                    found = LastIndexOfPattern(_bytes, pattern, firstEndExclusive, rangeEndExclusive - firstEndExclusive);
                    wrapped = found >= 0;
                }
            }

            if (found < 0)
                return (false, "Search value not found.");

            SetCaretToByte(found);
            return wrapped
                ? (true, $"Found at 0x{found:X8} (wrapped).")
                : (true, $"Found at 0x{found:X8}.");
        }

        private bool TryBuildSearchPattern(HexSearchRequest request, out byte[] pattern, out string message)
        {
            pattern = Array.Empty<byte>();
            message = string.Empty;

            string query = request.Query?.Trim() ?? string.Empty;
            if (query.Length == 0)
            {
                message = "Enter search data.";
                return false;
            }

            if (request.DataKind == HexSearchDataKind.HexData)
            {
                if (!TryParseHexBytes(query, out pattern) || pattern.Length == 0)
                {
                    message = "Invalid hex data. Example: 00 FF 7F";
                    return false;
                }

                if (request.Method == HexSearchMethod.LittleEndian && pattern.Length > 1)
                    Array.Reverse(pattern);
            }
            else
            {
                pattern = Encoding.UTF8.GetBytes(query);
                if (pattern.Length == 0)
                {
                    message = "Text data is invalid.";
                    return false;
                }
            }

            return true;
        }

        private bool TryResolveSearchRange(
            HexSearchRangeKind rangeKind,
            out int rangeStart,
            out int rangeEndExclusive,
            out string message)
        {
            rangeStart = 0;
            rangeEndExclusive = 0;
            message = string.Empty;

            if (_bytes.Length == 0)
            {
                message = "No data.";
                return false;
            }

            if (rangeKind == HexSearchRangeKind.Selection)
            {
                if (!TryGetExplicitSelectionByteRange(out int selStart, out int selCount))
                {
                    message = "Select at least one byte in the hex area.";
                    return false;
                }

                rangeStart = selStart;
                rangeEndExclusive = selStart + selCount;
                return true;
            }

            rangeStart = 0;
            rangeEndExclusive = _bytes.Length;
            return true;
        }

        private bool TryGetExplicitSelectionByteRange(out int startByte, out int byteCount)
        {
            startByte = 0;
            byteCount = 0;

            if (HexTextEditor.SelectionStart == HexTextEditor.SelectionEnd)
                return false;

            return TryGetSelectionByteRange(out startByte, out byteCount) && byteCount > 0;
        }

        private void ShowMatchListWindow(IReadOnlyList<int> matches, int totalCount, bool truncated)
        {
            var listWindow = new Window
            {
                Width = 320,
                Height = 420,
                MinWidth = 280,
                MinHeight = 280,
                Title = truncated
                    ? $"Match List ({totalCount}, showing {matches.Count})"
                    : $"Match List ({totalCount})"
            };

            var rootBorder = new Border
            {
                Padding = new Thickness(8)
            };
            rootBorder.Classes.Add("editorRoot");

            var rootGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 8
            };

            var infoText = new TextBlock
            {
                Text = truncated
                    ? $"Showing first {matches.Count} matches out of {totalCount}."
                    : $"Matches: {totalCount}",
                FontFamily = "Consolas",
                FontSize = 11
            };

            var items = new string[matches.Count];
            for (int index = 0; index < matches.Count; index++)
                items[index] = $"0x{matches[index]:X8}";

            var listBox = new ListBox
            {
                ItemsSource = items,
                FontFamily = "Consolas",
                FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(listBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

            var buttonRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
            };

            var jumpButton = new Button
            {
                Content = "JUMP",
                MinWidth = 88
            };

            var closeButton = new Button
            {
                Content = "CLOSE",
                MinWidth = 88
            };

            void JumpToSelected()
            {
                int selected = listBox.SelectedIndex;
                if (selected < 0 || selected >= matches.Count)
                    return;

                int address = matches[selected];
                SetCaretToByte(address);
                SetStatusMessage($"Jumped to 0x{address:X8} from match list.");
                listWindow.Close();
            }

            jumpButton.Click += (_, _) => JumpToSelected();
            closeButton.Click += (_, _) => listWindow.Close();

            listBox.SelectionChanged += (_, _) => JumpToSelected();
            listBox.DoubleTapped += (_, _) => JumpToSelected();
            listBox.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter)
                    return;

                args.Handled = true;
                JumpToSelected();
            };

            buttonRow.Children.Add(jumpButton);
            buttonRow.Children.Add(closeButton);

            rootGrid.Children.Add(infoText);
            Grid.SetRow(infoText, 0);
            rootGrid.Children.Add(listBox);
            Grid.SetRow(listBox, 1);
            rootGrid.Children.Add(buttonRow);
            Grid.SetRow(buttonRow, 2);

            rootBorder.Child = rootGrid;

            listWindow.Content = rootBorder;
            listWindow.Show(this);
        }

        private bool TryFindNibbleAtOrAfter(int textPosition, out int nibbleIndex)
        {
            nibbleIndex = 0;
            if (_nibblePositions.Length == 0)
                return false;

            int pos = Array.BinarySearch(_nibblePositions, textPosition);
            if (pos < 0)
                pos = ~pos;

            if (pos >= _nibblePositions.Length)
                pos = _nibblePositions.Length - 1;

            nibbleIndex = pos;
            return true;
        }

        private bool TryFindNibbleAtOrBefore(int textPosition, out int nibbleIndex)
        {
            nibbleIndex = 0;
            if (_nibblePositions.Length == 0)
                return false;

            int pos = Array.BinarySearch(_nibblePositions, textPosition);
            if (pos < 0)
                pos = ~pos - 1;

            if (pos < 0)
                pos = 0;

            nibbleIndex = pos;
            return true;
        }

        private static bool TryMapHexKey(Key key, out int nibble)
        {
            nibble = -1;

            if (key >= Key.D0 && key <= Key.D9)
            {
                nibble = key - Key.D0;
                return true;
            }

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                nibble = key - Key.NumPad0;
                return true;
            }

            if (key >= Key.A && key <= Key.F)
            {
                nibble = 10 + (key - Key.A);
                return true;
            }

            return false;
        }

        private static bool TryParseHexBytes(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();

            if (string.IsNullOrEmpty(text))
                return false;

            var nibbles = new System.Collections.Generic.List<int>(text.Length);
            foreach (char c in text)
            {
                int v = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'A' and <= 'F' => 10 + (c - 'A'),
                    >= 'a' and <= 'f' => 10 + (c - 'a'),
                    _ => -1
                };

                if (v >= 0)
                    nibbles.Add(v);
            }

            if (nibbles.Count < 2)
                return false;

            int byteCount = nibbles.Count / 2;
            var result = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
                result[i] = (byte)((nibbles[2 * i] << 4) | nibbles[2 * i + 1]);

            bytes = result;
            return true;
        }

        private static bool TryParseAddress(string? text, out int address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string token = text.Trim().Replace("_", string.Empty);
            bool forceDecimal = false;

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                token = token[2..];
            }
            else if (token.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            {
                token = token[..^1];
            }
            else if (token.StartsWith("d:", StringComparison.OrdinalIgnoreCase))
            {
                token = token[2..];
                forceDecimal = true;
            }

            if (token.Length == 0)
                return false;

            if (forceDecimal)
                return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);

            // Default interpretation is hexadecimal.
            return int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        private static int IndexOfPattern(byte[] source, byte[] pattern, int startIndex, int count)
        {
            if (pattern.Length == 0 || source.Length == 0 || count <= 0)
                return -1;

            int endExclusive = startIndex + count;
            int lastStart = endExclusive - pattern.Length;
            if (lastStart < startIndex)
                return -1;

            for (int i = startIndex; i <= lastStart; i++)
            {
                bool matched = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                    return i;
            }

            return -1;
        }

        private static int LastIndexOfPattern(byte[] source, byte[] pattern, int startIndex, int count)
        {
            if (pattern.Length == 0 || source.Length == 0 || count <= 0)
                return -1;

            int endExclusive = startIndex + count;
            int lastStart = endExclusive - pattern.Length;
            if (lastStart < startIndex)
                return -1;

            for (int i = lastStart; i >= startIndex; i--)
            {
                bool matched = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                    return i;
            }

            return -1;
        }

        private static char ToDecodedChar(byte value)
        {
            if (value >= 0x20 && value <= 0x7E)
                return (char)value;

            return '.';
        }

        private void UpdateByteCount(int count)
        {
            ByteCountText.Text = $"{count:N0} bytes";
        }

        private void OnHexEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.CaretIndexProperty
                || e.Property == TextBox.SelectionStartProperty
                || e.Property == TextBox.SelectionEndProperty)
            {
                RefreshStatusText();
            }
        }

        private void SetStatusMessage(string? message)
        {
            _statusMessage = message ?? string.Empty;
            RefreshStatusText();
        }

        private void RefreshStatusText()
        {
            string positionText = BuildPositionStatusText();
            if (string.IsNullOrWhiteSpace(_statusMessage))
            {
                StatusText.Text = positionText;
                return;
            }

            StatusText.Text = $"{positionText} | {_statusMessage}";
        }

        private string BuildPositionStatusText()
        {
            if (_bytes.Length == 0)
                return "Offset: N/A";

            bool hasSelection = HexTextEditor.SelectionStart != HexTextEditor.SelectionEnd;
            if (hasSelection && TryGetSelectionByteRange(out int startByte, out int byteCount) && byteCount > 1)
            {
                int endByte = startByte + byteCount - 1;
                return $"Selection: 0x{startByte:X8} - 0x{endByte:X8} ({byteCount} bytes)";
            }

            if (TryGetCurrentByteIndex(out int currentByte))
                return $"Offset: 0x{currentByte:X8}";

            return "Offset: N/A";
        }
    }
}
