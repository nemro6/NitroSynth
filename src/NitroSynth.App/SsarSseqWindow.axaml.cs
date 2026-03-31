using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NitroSynth.App.ViewModels;

namespace NitroSynth.App
{
    public partial class SsarSseqWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly MainWindowViewModel.SsarSseqWindowSession _session;
        private byte[] _baseEventData = Array.Empty<byte>();
        private int _currentPlaybackStartOffset;
        private int _selectedSbnkId;
        private bool _disposed;

        public SsarSseqWindow()
        {
            InitializeComponent();
            _viewModel = new MainWindowViewModel();
            _session = default;
            _currentPlaybackStartOffset = 0;
            _selectedSbnkId = 0;
            UpdateTransportButtons();
        }

        public SsarSseqWindow(MainWindowViewModel viewModel, MainWindowViewModel.SsarSseqWindowSession session)
            : this()
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _session = session;
            _currentPlaybackStartOffset = session.PlaybackStartOffset;
            _selectedSbnkId = session.SbnkId;

            Title = session.Title;
            VolumeTextBox.Text = session.Volume.ToString();
            ChannelPriorityTextBox.Text = session.ChannelPriority.ToString();
            PlayerPriorityTextBox.Text = session.PlayerPriority.ToString();
            PlayerTextBox.Text = session.PlayerDisplay;
            DecompilerTextBox.Text = session.DecompiledText;
            _baseEventData = (byte[])session.BaseEventData.Clone();
            InitializeSbnkSelector(session.SbnkId, session.SbnkDisplay);

            StatusText.Text = $"Direct type / range select / paste supported. Start=0x{_currentPlaybackStartOffset:X6}";
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateTransportButtons();
        }

        private void InitializeSbnkSelector(int initialSbnkId, string fallbackDisplay)
        {
            var options = _viewModel.BankOptions
                .OrderBy(x => x.Id)
                .ToList();

            string fallbackName = fallbackDisplay ?? string.Empty;
            int sep = fallbackName.IndexOf(':');
            if (sep >= 0 && sep + 1 < fallbackName.Length)
                fallbackName = fallbackName[(sep + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(fallbackName))
                fallbackName = $"SBNK {initialSbnkId:D3}";

            if (!options.Any(x => x.Id == initialSbnkId))
                options.Add(new SBNK.BankOption(initialSbnkId, fallbackName));

            SbnkComboBox.ItemsSource = options
                .OrderBy(x => x.Id)
                .ToList();

            SbnkComboBox.SelectedItem = (SbnkComboBox.ItemsSource as IEnumerable<SBNK.BankOption>)
                ?.FirstOrDefault(x => x.Id == initialSbnkId);

            if (SbnkComboBox.SelectedItem is SBNK.BankOption selected)
                _selectedSbnkId = selected.Id;
        }

        private void OnSbnkSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (SbnkComboBox.SelectedItem is SBNK.BankOption bank)
                _selectedSbnkId = bank.Id;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (!_disposed)
            {
                _disposed = true;
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            base.OnClosed(e);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainWindowViewModel.IsSseqPlaying)
                or nameof(MainWindowViewModel.IsSseqPaused))
            {
                Dispatcher.UIThread.Post(UpdateTransportButtons);
                return;
            }

            if (e.PropertyName == nameof(MainWindowViewModel.StatusMessage))
            {
                Dispatcher.UIThread.Post(() => StatusText.Text = _viewModel.StatusMessage);
            }
        }

        private void UpdateTransportButtons()
        {
            bool isPlaying = _viewModel.IsSseqPlaying;
            bool isPaused = _viewModel.IsSseqPaused;

            PlayButton.IsEnabled = !isPlaying && !isPaused;
            PauseButton.IsEnabled = isPlaying || isPaused;
            StopButton.IsEnabled = isPlaying || isPaused;
            PauseButton.Content = isPaused ? "RESUME" : "PAUSE";
        }

        private bool TryGetCurrentEventData(out byte[] eventData)
        {
            eventData = Array.Empty<byte>();

            string editedText = DecompilerTextBox.Text ?? string.Empty;
            if (!_viewModel.TryCompileSseqTextAgainstBase(_baseEventData, editedText, out eventData, out var error))
            {
                StatusText.Text = error ?? "SSEQ compile failed.";
                return false;
            }

            return true;
        }

        private async void OnPlayClicked(object? sender, RoutedEventArgs e)
        {
            if (!TryGetCurrentEventData(out var eventData))
                return;

            _baseEventData = (byte[])eventData.Clone();
            int playbackStartOffset = eventData.Length == 0
                ? 0
                : Math.Clamp(_currentPlaybackStartOffset, 0, eventData.Length - 1);
            await _viewModel.StartSsarSequencePlaybackAsync(
                eventData,
                playbackStartOffset,
                _selectedSbnkId,
                _session.SequenceDisplay,
                sequencePriorityBase: _session.ChannelPriority,
                playerId: _session.PlayerId);
            StatusText.Text = $"Playback started at 0x{playbackStartOffset:X6} (SBNK {_selectedSbnkId:D3}).";
            UpdateTransportButtons();
        }

        private async void OnPauseClicked(object? sender, RoutedEventArgs e)
        {
            await _viewModel.ToggleSsarSequencePauseAsync();
            UpdateTransportButtons();
        }

        private void OnStopClicked(object? sender, RoutedEventArgs e)
        {
            _viewModel.StopSsarSequencePlayback();
            UpdateTransportButtons();
        }

        private void OnSaveClicked(object? sender, RoutedEventArgs e)
        {
            if (!TryGetCurrentEventData(out var eventData))
                return;

            _baseEventData = eventData;
            StatusText.Text = "Saved to SSAR sequence session memory.";
        }

        private async void OnHexClicked(object? sender, RoutedEventArgs e)
        {
            if (!TryGetCurrentEventData(out var eventData))
                return;

            byte[] sseqBytes = _viewModel.BuildStandaloneSseqBytes(eventData);

            var window = new HexEditorWindow(
                $"HEX Editor - SSAR {_session.SsarId:D3} SEQ {_session.SequenceId:D3}",
                $"{_session.SsarDisplay} / {_session.SequenceDisplay}",
                sseqBytes,
                SaveHexEditedBytesAsync);

            await window.ShowDialog(this);
        }

        private async Task<MainWindowViewModel.HexSaveResult> SaveHexEditedBytesAsync(byte[] updatedBytes)
        {
            if (updatedBytes is null || updatedBytes.Length == 0)
                return new MainWindowViewModel.HexSaveResult(false, "SSEQ data is empty.");

            try
            {
                var parsed = SSEQ.Read(updatedBytes);
                _baseEventData = parsed.EventData.ToArray();

                if (!_viewModel.TryDecompileStandaloneSseq(_baseEventData, _session.ExportBaseName, out var text, out var error))
                    return new MainWindowViewModel.HexSaveResult(false, error ?? "Failed to decompile updated SSEQ.");

                if (_baseEventData.Length == 0)
                    _currentPlaybackStartOffset = 0;
                else
                    _currentPlaybackStartOffset = Math.Clamp(_currentPlaybackStartOffset, 0, _baseEventData.Length - 1);

                DecompilerTextBox.Text = text;
                StatusText.Text = "Saved and reflected to SSAR sequence window.";
                return new MainWindowViewModel.HexSaveResult(true, "Saved and reflected to SSAR sequence window.");
            }
            catch (Exception ex)
            {
                return new MainWindowViewModel.HexSaveResult(false, $"Invalid SSEQ data: {ex.Message}");
            }
            finally
            {
                await Task.CompletedTask;
            }
        }

        private async void OnReplaceClicked(object? sender, RoutedEventArgs e)
        {
            if (StorageProvider is null)
            {
                StatusText.Text = "REPLACE failed: storage provider is unavailable.";
                return;
            }

            var options = new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Replace with SSEQ",
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("SSEQ file (*.sseq)") { Patterns = new[] { "*.sseq" } },
                    FilePickerFileTypes.All
                }
            };

            var files = await StorageProvider.OpenFilePickerAsync(options);
            if (files.Count == 0)
                return;

            var path = files[0].Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "REPLACE failed: could not resolve file path.";
                return;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var parsed = SSEQ.Read(bytes);
                _baseEventData = parsed.EventData.ToArray();

                if (!_viewModel.TryDecompileStandaloneSseq(_baseEventData, _session.ExportBaseName, out var text, out var error))
                {
                    StatusText.Text = error ?? "REPLACE failed: could not decompile SSEQ.";
                    return;
                }

                _currentPlaybackStartOffset = 0;
                DecompilerTextBox.Text = text;
                StatusText.Text = $"Replaced from: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"REPLACE failed: {ex.Message}";
            }
        }

        private async void OnExportClicked(object? sender, RoutedEventArgs e)
        {
            if (!TryGetCurrentEventData(out var eventData))
                return;

            if (StorageProvider is null)
            {
                StatusText.Text = "EXPORT failed: storage provider is unavailable.";
                return;
            }

            var options = new FilePickerSaveOptions
            {
                Title = "Export SSEQ / SMFT / MIDI",
                SuggestedFileName = $"{_session.ExportBaseName}.smft",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("SMFT file (*.smft)") { Patterns = new[] { "*.smft" } },
                    new("SSEQ file (*.sseq)") { Patterns = new[] { "*.sseq" } },
                    new("MIDI file (*.mid)") { Patterns = new[] { "*.mid" } },
                    FilePickerFileTypes.All
                }
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file is null)
                return;

            string? path = file.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "EXPORT failed: could not resolve save path.";
                return;
            }

            var kind = ResolveSseqExportKindFromPath(path);
            if (kind is null)
            {
                StatusText.Text = "EXPORT failed: unsupported file type. Use .sseq, .smft, or .mid.";
                return;
            }

            if (_viewModel.ExportStandaloneSseq(path, kind.Value, eventData, DecompilerTextBox.Text ?? string.Empty, out var message))
            {
                StatusText.Text = message;
                return;
            }

            StatusText.Text = message;
        }

        private static MainWindowViewModel.SseqExportKind? ResolveSseqExportKindFromPath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".sseq" => MainWindowViewModel.SseqExportKind.Sseq,
                ".smft" => MainWindowViewModel.SseqExportKind.Smft,
                ".mid" or ".midi" => MainWindowViewModel.SseqExportKind.Midi,
                _ => null
            };
        }

        private void OnCloseClicked(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
