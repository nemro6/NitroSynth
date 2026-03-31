using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;
using NitroSynth.App.Controls;
using NitroSynth.App.InstrumentEditor;
using NitroSynth.App.ViewModels;

namespace NitroSynth.App
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private bool _isMixerMainKnobDragging;
        private Point _mixerMainKnobDragStart;
        private int _mixerMainKnobDragStartValue;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainWindowViewModel();
            DataContext = _viewModel;

            var piano = this.FindControl<PianoKeyboard>("Piano");
            if (piano != null)
            {
                piano.NoteOn += OnPianoNoteOn;
                piano.NoteOff += OnPianoNoteOff;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _viewModel.Shutdown();
            }
            finally
            {
                base.OnClosed(e);
            }
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void OnMixerChannelGatePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            bool isRightClick = point.Properties.IsRightButtonPressed
                || point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed;
            if (!isRightClick)
                return;

            if (DataContext is not MainWindowViewModel vm)
                return;

            if (sender is not Control control)
                return;

            if (control.DataContext is not MainWindowViewModel.MixerChannelGate gate)
                return;

            vm.HandleMixerChannelGateRightClick(gate);
            e.Handled = true;
        }

        private void OnMixerStripOutputTogglePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            bool isRightClick = point.Properties.IsRightButtonPressed
                || point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed;
            if (!isRightClick)
                return;

            if (DataContext is not MainWindowViewModel vm)
                return;

            if (sender is not Control control)
                return;

            if (control.DataContext is not MainWindowViewModel.MixerStrip strip)
                return;

            vm.HandleMixerStripOutputToggleRightClick(strip);
            e.Handled = true;
        }

        private void OnMixerMainVolumeKnobPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            int step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 8 : 1;
            int delta = e.Delta.Y > 0 ? step : (e.Delta.Y < 0 ? -step : 0);
            if (delta == 0)
                return;

            vm.SelectedSseqVolume = Math.Clamp(vm.SelectedSseqVolume + delta, 0, 127);
            e.Handled = true;
        }

        private void OnMixerMainVolumeKnobPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (sender is not Control control)
                return;

            var point = e.GetCurrentPoint(control);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            _isMixerMainKnobDragging = true;
            _mixerMainKnobDragStart = point.Position;
            _mixerMainKnobDragStartValue = vm.SelectedSseqVolume;
            e.Pointer.Capture(control);
            e.Handled = true;
        }

        private void OnMixerMainVolumeKnobPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isMixerMainKnobDragging)
                return;

            if (DataContext is not MainWindowViewModel vm)
                return;

            if (sender is not Control control)
                return;

            if (e.Pointer.Captured != control)
                return;

            var position = e.GetPosition(control);
            double deltaY = _mixerMainKnobDragStart.Y - position.Y;
            int delta = (int)Math.Round(deltaY / 2.0, MidpointRounding.AwayFromZero);
            vm.SelectedSseqVolume = Math.Clamp(_mixerMainKnobDragStartValue + delta, 0, 127);
            e.Handled = true;
        }

        private void OnMixerMainVolumeKnobPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isMixerMainKnobDragging)
                return;

            _isMixerMainKnobDragging = false;
            if (sender is Control control && e.Pointer.Captured == control)
                e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void OnMixerMainVolumeKnobPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            _isMixerMainKnobDragging = false;
        }

        private void OnPianoNoteOn(object? sender, int note)
        {
            _viewModel.PlaySelectedInstNote(note);
        }

        private void OnPianoNoteOff(object? sender, int note)
        {
            _viewModel.StopAudio();
        }

        private async void OnOpenSdatClicked(object? sender, RoutedEventArgs e)
        {
            if (StorageProvider is null) return;

            var options = new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Open .sdat file",
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Nitro Sound Archive (*.sdat)") { Patterns = new[] { "*.sdat" } },
                    FilePickerFileTypes.All
                }
            };

            var files = await StorageProvider.OpenFilePickerAsync(options);
            if (files.Count == 0) return;

            await _viewModel.LoadSdatAsync(files[0]);
            Title = $"{files[0].Name} - NitroSynth";
        }

        private void OnOpenSdatInfoClicked(object? sender, RoutedEventArgs e)
        {
            var vm = _viewModel;
            var infoVm = new SdatInfoViewModel(
                vm.LoadedFilePath,
                vm.LastSdatSize,
                vm.LastSdatVersion,
                vm.Blocks
            );

            var win = new SdatInfoWindow { DataContext = infoVm };
            win.Show(this);
        }

        private void OnInstDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var row =
                (sender as DataGrid)?.SelectedItem as MainWindowViewModel.InstRow
                ?? InstGrid?.SelectedItem as MainWindowViewModel.InstRow;
            if (row is null) return;

            switch (row.Type)
            {
                case SBNK.InstrumentType.Pcm:
                    if (vm.TryGetPcmArticulationBytes(row.Id, out var pcm))
                        new PCM(pcm, $"PCM - Inst {row.Id:000}").Show(this);
                    break;

                case SBNK.InstrumentType.Psg:
                    if (vm.TryGetPsgArticulationBytes(row.Id, out var psg))
                        new PSG(psg, $"PSG - Inst {row.Id:000}").Show(this);
                    break;

                case SBNK.InstrumentType.Noise:
                    if (vm.TryGetNoiseArticulationBytes(row.Id, out var noi))
                        new Noise(noi, $"Noise - Inst {row.Id:000}").Show(this);
                    break;

                case SBNK.InstrumentType.DirectPcm:
                    if (vm.TryGetPcmArticulationBytes(row.Id, out var dpcm))
                        new PCM(dpcm, $"Direct PCM - Inst {row.Id:000}").Show(this);
                    break;

                case SBNK.InstrumentType.DrumSet:
                    if (vm.TryGetDrumSetModel(row.Id, out var ds))
                        new DrumSet(ds, $"Drum Set - Inst {row.Id:000}").Show(this);
                    break;

                case SBNK.InstrumentType.KeySplit:
                    if (vm.TryGetKeySplitModel(row.Id, out var ks))
                        new KeySplit(ks, $"Key Split - Inst {row.Id:000}").Show(this);
                    break;
            }

            _viewModel.PlaySelectedInstNote(row.BaseKey);
        }

        private async void OnMidiRefreshClicked(object? sender, RoutedEventArgs e)
        {
            await _viewModel.RefreshMidiInputsAsync();
        }

        private async void OnMidiOpenClicked(object? sender, RoutedEventArgs e)
        {
            await _viewModel.OpenSelectedMidiInputAsync();
        }

        private async void OnSseqPlayClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.StartSelectedSseqPlaybackAsync();
        }

        private async void OnSseqPauseClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.ToggleSelectedSseqPauseAsync();
        }

        private void OnSseqStopClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.StopSelectedSseqPlayback();
        }

        private async void OnSseqExportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                await ExportSelectedSseqWithDialogAsync(vm);
        }

        private void OnSseqImportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySseqImportNotImplemented();
        }

        private void OnSseqSaveClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySseqSaveNotImplemented();
        }

        private async void OnSseqHexClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.TryCreateSelectedSseqHexEditorSession(out var session))
                return;

            await ShowHexEditorDialogAsync(session);
        }

        private async void OnSbnkExportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                await ExportSelectedSbnkWithDialogAsync(vm);
        }

        private void OnSbnkImportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySbnkImportNotImplemented();
        }

        private void OnSbnkSaveClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySbnkSaveNotImplemented();
        }

        private async void OnSbnkBnkClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.TryCreateSelectedSbnkBnkEditorSession(out var session))
                return;

            await ShowMusEditorDialogAsync(session);
        }

        private async void OnSbnkHexClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.TryCreateSelectedSbnkHexEditorSession(out var session))
                return;

            await ShowHexEditorDialogAsync(session);
        }

        private async void OnSwarExportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                await ExportSelectedSwarWithDialogAsync(vm);
        }

        private void OnSwarImportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySwarImportNotImplemented();
        }

        private void OnSwarSaveClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySwarSaveNotImplemented();
        }

        private async void OnSwarHexClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.TryCreateSelectedSwarHexEditorSession(out var session))
                return;

            await ShowHexEditorDialogAsync(session);
        }

        private async void OnSsarExportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                await ExportSelectedSsarWithDialogAsync(vm);
        }

        private async void OnSsarMusClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.TryCreateSelectedSsarMusEditorSession(out var session))
                return;

            await ShowMusEditorDialogAsync(session);
        }

        private void OnSsarImportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySsarImportNotImplemented();
        }

        private void OnSsarSaveClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NotifySsarSaveNotImplemented();
        }

        private async void OnSsarHexClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.TryCreateSelectedSsarHexEditorSession(out var session))
                return;

            await ShowHexEditorDialogAsync(session);
        }

        private void OnMidiCloseClicked(object? sender, RoutedEventArgs e)
        {
            _viewModel.CloseMidiInput();
        }

        private void OnMidiResetClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.ResetMidi();
        }

        private void OnSdatTreeDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (sender is not TreeView tree) return;
            if (tree.SelectedItem is not SdatTreeNode node) return;
            if (node.ItemId is null) return;

            string? targetHeader = null;
            if (node.Kind == SdatTreeNodeKind.SseqEntry)
            {
                if (!vm.SelectSseqById(node.ItemId.Value))
                    return;
                targetHeader = "SSEQ";
            }
            else if (node.Kind == SdatTreeNodeKind.SsarEntry)
            {
                if (!vm.SelectSsarById(node.ItemId.Value))
                    return;
                targetHeader = "SSAR";
            }
            else if (node.Kind == SdatTreeNodeKind.SbnkEntry)
            {
                if (!vm.SelectBankById(node.ItemId.Value))
                    return;
                targetHeader = "SBNK";
            }
            else if (node.Kind == SdatTreeNodeKind.SwarEntry)
            {
                if (!vm.SelectSwarById(node.ItemId.Value))
                    return;
                targetHeader = "SWAR";
            }
            else if (node.Kind == SdatTreeNodeKind.StrmEntry)
            {
                if (!vm.SelectStrmById(node.ItemId.Value))
                    return;
                targetHeader = "STRM";
            }
            else
            {
                return;
            }

            if (MainTabs is null) return;
            foreach (var item in MainTabs.Items)
            {
                if (item is TabItem tab &&
                    string.Equals(tab.Header?.ToString(), targetHeader, StringComparison.Ordinal))
                {
                    MainTabs.SelectedItem = tab;
                    e.Handled = true;
                    return;
                }
            }
        }

        private void OnSdatTreeAddNewClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var node = GetSdatTreeNodeFromMenuSender(sender);
            if (node is null || !node.IsRealFileEntry)
                return;

            vm.NotifySdatTreeActionNotImplemented("Add new", node);
        }

        private void OnSdatTreeRenameClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var node = GetSdatTreeNodeFromMenuSender(sender);
            if (node is null || !node.IsRealFileEntry)
                return;

            vm.NotifySdatTreeActionNotImplemented("Rename", node);
        }

        private void OnSdatTreeReplaceClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var node = GetSdatTreeNodeFromMenuSender(sender);
            if (node is null || !node.IsRealFileEntry)
                return;

            vm.NotifySdatTreeActionNotImplemented("Replace", node);
        }

        private async void OnSdatTreeExportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var node = GetSdatTreeNodeFromMenuSender(sender);
            if (node is null || !node.IsRealFileEntry)
                return;

            if (!TrySelectTreeNodeTarget(vm, node))
                return;

            switch (node.Kind)
            {
                case SdatTreeNodeKind.SseqEntry:
                    await ExportSelectedSseqWithDialogAsync(vm);
                    break;
                case SdatTreeNodeKind.SsarEntry:
                    vm.NotifySdatTreeActionNotImplemented("Export", node);
                    break;
                case SdatTreeNodeKind.SbnkEntry:
                    await ExportSelectedSbnkWithDialogAsync(vm);
                    break;
                case SdatTreeNodeKind.SwarEntry:
                    await ExportSelectedSwarWithDialogAsync(vm);
                    break;
                case SdatTreeNodeKind.StrmEntry:
                    vm.NotifySdatTreeActionNotImplemented("Export", node);
                    break;
            }
        }

        private void OnSdatTreeDeleatClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var node = GetSdatTreeNodeFromMenuSender(sender);
            if (node is null || !node.IsRealFileEntry)
                return;

            vm.NotifySdatTreeActionNotImplemented("Deleat", node);
        }

        private void OnSwavDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var row =
                (sender as DataGrid)?.SelectedItem as MainWindowViewModel.SwavRow
                ?? SwavGrid?.SelectedItem as MainWindowViewModel.SwavRow;
            if (row is null) return;

            if (!vm.TryGetSwav(row.SwarId, row.Id, out var swav))
                return;

            var title = $"SWAV - SWAR {row.SwarId:D3} / SWAV {row.Id:D3}";
            new SWAVEditor(swav, row.SwarId, row.Id, title).Show(this);
        }

        private void OnSeqPlayerDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var row =
                (sender as DataGrid)?.SelectedItem as MainWindowViewModel.SeqPlayerRow
                ?? SeqPlayerGrid?.SelectedItem as MainWindowViewModel.SeqPlayerRow;
            if (row is null) return;

            if (!vm.TryCreateSeqPlayerWindowSession(row, out var session))
                return;

            new SeqPlayerWindow(session).Show(this);
        }

        private void OnSsarSequenceDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var row =
                (sender as DataGrid)?.SelectedItem as MainWindowViewModel.SsarSequenceRow
                ?? SsarSequenceGrid?.SelectedItem as MainWindowViewModel.SsarSequenceRow;
            if (row is null) return;

            if (!vm.TryCreateSsarSequenceWindowSession(row, out var session))
                return;

            new SsarSseqWindow(vm, session).Show(this);
        }

        private void OnSsarSequenceAddNewClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var row = GetSsarSequenceRowFromMenuSender(sender);
            vm.NotifySsarSequenceActionNotImplemented("Add new", row);
        }

        private void OnSsarSequenceRenameClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var row = GetSsarSequenceRowFromMenuSender(sender);
            vm.NotifySsarSequenceActionNotImplemented("Rename", row);
        }

        private void OnSsarSequenceReplaceClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var row = GetSsarSequenceRowFromMenuSender(sender);
            vm.NotifySsarSequenceActionNotImplemented("Replace", row);
        }

        private async void OnSsarSequenceExportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var row = GetSsarSequenceRowFromMenuSender(sender);
            if (row is null)
                return;

            await ExportSsarSequenceWithDialogAsync(vm, row);
        }

        private void OnSsarSequenceDeleatClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var row = GetSsarSequenceRowFromMenuSender(sender);
            vm.NotifySsarSequenceActionNotImplemented("Deleat", row);
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

        private async Task ExportSelectedSseqWithDialogAsync(MainWindowViewModel vm)
        {
            if (StorageProvider is null)
                return;

            string suggestedName = vm.GetSelectedSseqExportBaseName();

            var options = new FilePickerSaveOptions
            {
                Title = "Export SSEQ / SMFT / MIDI",
                SuggestedFileName = $"{suggestedName}.smft",
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
                vm.NotifySseqExportFailed("Could not resolve save path.");
                return;
            }

            var kind = ResolveSseqExportKindFromPath(path);
            if (kind is null)
            {
                vm.NotifySseqExportFailed("Unsupported file type. Use .sseq, .smft, or .mid.");
                return;
            }

            vm.ExportSelectedSseq(path, kind.Value);
        }

        private async Task ExportSelectedSbnkWithDialogAsync(MainWindowViewModel vm)
        {
            if (StorageProvider is null)
                return;

            string suggestedName = vm.GetSelectedSbnkExportBaseName();

            var options = new FilePickerSaveOptions
            {
                Title = "Export SBNK",
                SuggestedFileName = $"{suggestedName}.sbnk",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("SBNK file (*.sbnk)") { Patterns = new[] { "*.sbnk" } },
                    FilePickerFileTypes.All
                }
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file is null)
                return;

            string? path = file.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                vm.NotifySbnkExportFailed("Could not resolve save path.");
                return;
            }

            if (!string.Equals(Path.GetExtension(path), ".sbnk", StringComparison.OrdinalIgnoreCase))
                path = Path.ChangeExtension(path, ".sbnk");

            vm.ExportSelectedSbnk(path);
        }

        private async Task ExportSelectedSwarWithDialogAsync(MainWindowViewModel vm)
        {
            if (StorageProvider is null)
                return;

            string suggestedName = vm.GetSelectedSwarExportBaseName();

            var options = new FilePickerSaveOptions
            {
                Title = "Export SWAR",
                SuggestedFileName = $"{suggestedName}.swar",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("SWAR file (*.swar)") { Patterns = new[] { "*.swar" } },
                    FilePickerFileTypes.All
                }
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file is null)
                return;

            string? path = file.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                vm.NotifySwarExportFailed("Could not resolve save path.");
                return;
            }

            if (!string.Equals(Path.GetExtension(path), ".swar", StringComparison.OrdinalIgnoreCase))
                path = Path.ChangeExtension(path, ".swar");

            vm.ExportSelectedSwar(path);
        }

        private async Task ExportSelectedSsarWithDialogAsync(MainWindowViewModel vm)
        {
            if (StorageProvider is null)
                return;

            string suggestedName = vm.GetSelectedSsarExportBaseName();

            var options = new FilePickerSaveOptions
            {
                Title = "Export SSAR / MUS",
                SuggestedFileName = $"{suggestedName}.ssar",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("SSAR file (*.ssar)") { Patterns = new[] { "*.ssar" } },
                    new("MUS text (*.mus)") { Patterns = new[] { "*.mus" } },
                    FilePickerFileTypes.All
                }
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file is null)
                return;

            string? path = file.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                vm.NotifySsarExportFailed("Could not resolve save path.");
                return;
            }

            string ext = Path.GetExtension(path);
            if (string.Equals(ext, ".mus", StringComparison.OrdinalIgnoreCase))
            {
                vm.ExportSelectedSsarMus(path);
                return;
            }

            if (!string.Equals(ext, ".ssar", StringComparison.OrdinalIgnoreCase))
                path = Path.ChangeExtension(path, ".ssar");

            vm.ExportSelectedSsar(path);
        }

        private async Task ShowHexEditorDialogAsync(MainWindowViewModel.HexEditorSession session)
        {
            var window = new HexEditorWindow(
                session.Title,
                session.HeaderText,
                session.InitialBytes,
                session.SaveAsync);

            await window.ShowDialog(this);
        }

        private async Task ShowMusEditorDialogAsync(MainWindowViewModel.MusEditorSession session)
        {
            var window = new MusEditorWindow(
                session.Title,
                session.HeaderText,
                session.InitialText,
                session.SaveAsync);

            await window.ShowDialog(this);
        }

        private async Task ExportSsarSequenceWithDialogAsync(MainWindowViewModel vm, MainWindowViewModel.SsarSequenceRow row)
        {
            if (StorageProvider is null)
                return;

            if (!vm.TryCreateSsarSequenceWindowSession(row, out var session))
                return;

            var options = new FilePickerSaveOptions
            {
                Title = "Export SSEQ / SMFT / MIDI",
                SuggestedFileName = $"{session.ExportBaseName}.smft",
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
                vm.NotifySseqExportFailed("Could not resolve save path.");
                return;
            }

            var kind = ResolveSseqExportKindFromPath(path);
            if (kind is null)
            {
                vm.NotifySseqExportFailed("Unsupported file type. Use .sseq, .smft, or .mid.");
                return;
            }

            vm.ExportStandaloneSseqWithStatus(path, kind.Value, session.BaseEventData, session.DecompiledText);
        }

        private static SdatTreeNode? GetSdatTreeNodeFromMenuSender(object? sender)
        {
            if (sender is not MenuItem menuItem)
                return null;

            if (menuItem.CommandParameter is SdatTreeNode parameterNode)
                return parameterNode;

            return menuItem.DataContext as SdatTreeNode;
        }

        private MainWindowViewModel.SsarSequenceRow? GetSsarSequenceRowFromMenuSender(object? sender)
        {
            if (sender is MenuItem menuItem)
            {
                if (menuItem.CommandParameter is MainWindowViewModel.SsarSequenceRow rowFromParameter)
                    return rowFromParameter;

                if (menuItem.DataContext is MainWindowViewModel.SsarSequenceRow rowFromDataContext)
                    return rowFromDataContext;
            }

            return SsarSequenceGrid?.SelectedItem as MainWindowViewModel.SsarSequenceRow;
        }

        private static bool TrySelectTreeNodeTarget(MainWindowViewModel vm, SdatTreeNode node)
        {
            if (node.ItemId is not int id)
                return false;

            return node.Kind switch
            {
                SdatTreeNodeKind.SseqEntry => vm.SelectSseqById(id),
                SdatTreeNodeKind.SsarEntry => vm.SelectSsarById(id),
                SdatTreeNodeKind.SbnkEntry => vm.SelectBankById(id),
                SdatTreeNodeKind.SwarEntry => vm.SelectSwarById(id),
                SdatTreeNodeKind.StrmEntry => vm.SelectStrmById(id),
                _ => false
            };
        }
    }
}
