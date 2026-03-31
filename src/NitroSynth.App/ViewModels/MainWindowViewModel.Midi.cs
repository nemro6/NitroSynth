using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.Midi;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    private sealed class AppSettings
    {
        public string? PreferredMidiInputId { get; set; }
        public string? PreferredMidiInputName { get; set; }
        public bool AutoOpenPreferredMidiInput { get; set; }
        public string? PreferredAudioOutputId { get; set; }
        public string? PreferredAudioOutputName { get; set; }
        public bool MonoOutput { get; set; }
        public int PreferredAudioBufferLatencyMs { get; set; } = 48;
        public int PreferredAudioSampleRate { get; set; } = 48000;
        public double PreferredMasterMeterDecayMs { get; set; } = 100;
        public double PreferredMixerMeterDecayMs { get; set; } = 100;
        public int PreferredMeterUpdateFps { get; set; } = 120;
    }

    private MidiIn? _midiIn;
    public sealed class MidiInputOption
    {
        public string Id { get; }
        public string Name { get; }
        public MidiInputOption(string id, string name)
        {
            Id = id; Name = name;
        }
        public override string ToString() => Name;
    }

    public ObservableCollection<MidiInputOption> MidiInOptions { get; } = new();

    private bool _appSettingsLoaded;
    private string? _preferredMidiInputId;
    private string? _preferredMidiInputName;
    private bool _autoOpenPreferredMidiInput;
    private string? _preferredAudioOutputId;
    private string? _preferredAudioOutputName;
    private bool _preferredMonoOutput;
    private int _preferredAudioBufferLatencyMs = 48;
    private int _preferredAudioSampleRate = 48000;
    private double _preferredMasterMeterDecayMs = 100;
    private double _preferredMixerMeterDecayMs = 100;
    private int _preferredMeterUpdateFps = 120;
    private bool _suppressMidiSelectionAutoConnect;

    private MidiInputOption? _selectedMidiIn;
    public MidiInputOption? SelectedMidiIn
    {
        get => _selectedMidiIn;
        set
        {
            if (!SetField(ref _selectedMidiIn, value)) return;
            if (value is not null)
            {
                _preferredMidiInputId = value.Id;
                _preferredMidiInputName = value.Name;
                SaveAppSettings();
            }
            OnPropertyChanged(nameof(CanOpenMidiIn));

            if (!_suppressMidiSelectionAutoConnect && value is not null)
                _ = OpenSelectedMidiInputAsync();
        }
    }

    private bool _isMidiInOpen;
    public bool IsMidiInOpen
    {
        get => _isMidiInOpen;
        private set { if (SetField(ref _isMidiInOpen, value)) { OnPropertyChanged(nameof(CanOpenMidiIn)); } }
    }

    public bool CanOpenMidiIn => !_isMidiInOpen && _selectedMidiIn is not null;

    private string _midiInOpenName = string.Empty;
    public string MidiInStatus => IsMidiInOpen ? $"MIDI IN: {_midiInOpenName}" : "MIDI IN: (none)";

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NitroSynth",
            "settings.json");

    private void EnsureAppSettingsLoaded()
    {
        if (_appSettingsLoaded)
            return;

        LoadAppSettings();
        _appSettingsLoaded = true;
    }

    private void LoadAppSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is null)
                return;

            _preferredMidiInputId = settings.PreferredMidiInputId;
            _preferredMidiInputName = settings.PreferredMidiInputName;
            _autoOpenPreferredMidiInput = settings.AutoOpenPreferredMidiInput;
            _preferredAudioOutputId = settings.PreferredAudioOutputId;
            _preferredAudioOutputName = settings.PreferredAudioOutputName;
            _preferredMonoOutput = settings.MonoOutput;
            if (settings.PreferredAudioBufferLatencyMs > 0)
                _preferredAudioBufferLatencyMs = settings.PreferredAudioBufferLatencyMs;
            if (settings.PreferredAudioSampleRate > 0)
                _preferredAudioSampleRate = Math.Clamp(settings.PreferredAudioSampleRate, 8000, 192000);
            _preferredMasterMeterDecayMs = Math.Clamp(settings.PreferredMasterMeterDecayMs, 0.0, 200.0);
            _preferredMixerMeterDecayMs = Math.Clamp(settings.PreferredMixerMeterDecayMs, 0.0, 200.0);
            _preferredMeterUpdateFps = Math.Clamp(settings.PreferredMeterUpdateFps, 0, 610);
        }
        catch
        {
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var settings = new AppSettings
            {
                PreferredMidiInputId = _preferredMidiInputId,
                PreferredMidiInputName = _preferredMidiInputName,
                AutoOpenPreferredMidiInput = _autoOpenPreferredMidiInput,
                PreferredAudioOutputId = _preferredAudioOutputId,
                PreferredAudioOutputName = _preferredAudioOutputName,
                MonoOutput = _preferredMonoOutput,
                PreferredAudioBufferLatencyMs = _preferredAudioBufferLatencyMs,
                PreferredAudioSampleRate = _preferredAudioSampleRate,
                PreferredMasterMeterDecayMs = _preferredMasterMeterDecayMs,
                PreferredMixerMeterDecayMs = _preferredMixerMeterDecayMs,
                PreferredMeterUpdateFps = _preferredMeterUpdateFps
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
        }
    }

    private MidiInputOption? FindPreferredMidiInput()
    {
        if (MidiInOptions.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(_preferredMidiInputName))
        {
            var byName = MidiInOptions.FirstOrDefault(o => o.Name == _preferredMidiInputName);
            if (byName is not null)
                return byName;
        }

        if (!string.IsNullOrWhiteSpace(_preferredMidiInputId))
        {
            var byId = MidiInOptions.FirstOrDefault(o => o.Id == _preferredMidiInputId);
            if (byId is not null)
                return byId;
        }

        return null;
    }

    public void InitializeMidiInputPreference()
    {
        EnsureAppSettingsLoaded();
    }

    public Task RefreshMidiInputsAsync()
    {
        EnsureAppSettingsLoaded();
        MidiInOptions.Clear();

        for (int device = 0; device < MidiIn.NumberOfDevices; device++)
        {
            var caps = MidiIn.DeviceInfo(device);
            MidiInOptions.Add(new MidiInputOption(device.ToString(), caps.ProductName));
        }

        if (MidiInOptions.Count == 0)
        {
            SelectedMidiIn = null;
            return Task.CompletedTask;
        }

        var selected = FindPreferredMidiInput();
        if (selected is null && SelectedMidiIn is { } current)
        {
            var currentMatch = MidiInOptions.FirstOrDefault(o => o.Name == current.Name) ??
                               MidiInOptions.FirstOrDefault(o => o.Id == current.Id);
            if (currentMatch is not null)
            {
                selected = currentMatch;
            }
        }

        _suppressMidiSelectionAutoConnect = true;
        try
        {
            SelectedMidiIn = selected ?? MidiInOptions[0];
        }
        finally
        {
            _suppressMidiSelectionAutoConnect = false;
        }

        if (_autoOpenPreferredMidiInput && !IsMidiInOpen && SelectedMidiIn is not null)
            return OpenSelectedMidiInputAsync();

        return Task.CompletedTask;
    }

    
    public async Task OpenSelectedMidiInputAsync()
    {
        if (SelectedMidiIn is null) return;
        if (_midiIn != null) CloseMidiInput();

        try
        {
            int devId = int.Parse(SelectedMidiIn.Id);
            _midiIn = new MidiIn(devId);
            _midiIn.MessageReceived += OnMidiMessageReceived;
            _midiIn.Start();

            _midiInOpenName = SelectedMidiIn.Name;
            IsMidiInOpen = true;
            _autoOpenPreferredMidiInput = true;
            SaveAppSettings();
            OnPropertyChanged(nameof(MidiInStatus));
            StatusMessage = $"MIDI input opened: {_midiInOpenName}";
        }
        catch (Exception ex)
        {
            if (_midiIn is not null)
            {
                _midiIn.Dispose();
                _midiIn = null;
            }

            IsMidiInOpen = false;
            _midiInOpenName = string.Empty;
            _autoOpenPreferredMidiInput = false;
            ClearPianoMidiNotes();
            SaveAppSettings();
            OnPropertyChanged(nameof(MidiInStatus));
            StatusMessage = $"Failed to open MIDI input: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    
    public void CloseMidiInput()
    {
        if (_midiIn == null)
        {
            ClearPianoMidiNotes();
            return;
        }
        _midiIn.Stop();
        _midiIn.Dispose();
        _midiIn = null;

        IsMidiInOpen = false;
        _midiInOpenName = string.Empty;
        _autoOpenPreferredMidiInput = false;
        ClearPianoMidiNotes();
        SaveAppSettings();
        OnPropertyChanged(nameof(MidiInStatus));
        StatusMessage = "MIDI input closed.";
    }

    
    private void OnMidiMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        var msg = e.MidiEvent;

        if (msg is NoteOnEvent on)
        {
            int ch = MidiCh0(on.Channel);
            if (on.Velocity > 0)
            {
                SetPianoMidiNoteOn(ch, on.NoteNumber);
                if (ch < MixerStrips.Count)
                {
                    bool anySolo = MixerStrips.Any(s => s.Solo);
                    if ((anySolo && !MixerStrips[ch].Solo) || MixerStrips[ch].Mute) return;

                    PlayMidiNoteOn(ch, on.NoteNumber, on.Velocity);
                }
            }
            else
            {
                SetPianoMidiNoteOff(ch, on.NoteNumber);
                
                if (ch < MixerStrips.Count)
                    StopMidiNote(ch, on.NoteNumber);
            }
        }
        else if (msg is NoteEvent off && off.CommandCode == MidiCommandCode.NoteOff)
        {
            int ch = MidiCh0(off.Channel);
            SetPianoMidiNoteOff(ch, off.NoteNumber);
            if (ch < MixerStrips.Count)
                StopMidiNote(ch, off.NoteNumber);
        }
        else if (msg is NAudio.Midi.ControlChangeEvent cc)
        {
            int ch = MidiCh0(cc.Channel);
            if (ch < MixerStrips.Count)
            {
                switch ((MidiController)cc.Controller)
                {
                    case MidiController.MainVolume:
                        MixerStrips[ch].Volume = cc.ControllerValue; 
                        break;

                    case MidiController.Expression:
                        MixerStrips[ch].Volume2 = cc.ControllerValue; 
                        break;

                    case MidiController.Pan:
                        MixerStrips[ch].Pan = cc.ControllerValue - 64; 
                        break;

                    case MidiController.Modulation:
                        MixerStrips[ch].Modulation = cc.ControllerValue; 
                        break;

                    case (MidiController)20:
                        MixerStrips[ch].BendRange = cc.ControllerValue;
                        break;

                    case (MidiController)21:
                        MixerStrips[ch].ModSpeed = cc.ControllerValue;
                        break;

                    case (MidiController)22:
                        MixerStrips[ch].ModType = cc.ControllerValue;
                        break;

                    case (MidiController)23:
                        MixerStrips[ch].ModRange = cc.ControllerValue;
                        break;

                    case (MidiController)26:
                        MixerStrips[ch].ModDelay = cc.ControllerValue;
                        break;

                    case (MidiController)27:
                        MixerStrips[ch].ModDelay = cc.ControllerValue * 10;
                        break;

                    case (MidiController)05:
                        MixerStrips[ch].Portamento = cc.ControllerValue;
                        break;

                    case (MidiController)65:
                        MixerStrips[ch].PortaEnabled = cc.ControllerValue >= 64;
                        break;

                    case (MidiController)84:
                        MixerStrips[ch].PortaEnabled = true;
                        break;
                }
            }

            
            var engineCc = NitroSynth.App.Audio.ControlChangeEvent.FromMidi(
                tick: 0,
                channel: (byte)ch,
                controllerNumber: (byte)cc.Controller,
                value: (byte)cc.ControllerValue
            );

            NitroSynth.App.Audio.AudioEngine.Instance.ApplyControlChange(engineCc);

        }
        else if (msg is PitchWheelChangeEvent pw)
        {
            int ch = MidiCh0(pw.Channel);
            if (ch < MixerStrips.Count)
            {
                
                int signed = pw.Pitch - 8192;

                
                int bendDs = (int)Math.Round(signed / 8192.0 * 127.0);
                bendDs = Math.Clamp(bendDs, -127, 127);

                MixerStrips[ch].PitchBend = bendDs;

                
                NitroSynth.App.Audio.AudioEngine.Instance.SetPitchBend(ch, bendDs);
            }
        }

        else if (msg is PatchChangeEvent pc) 
        {
            int ch = MidiCh0(pc.Channel);
            if (ch < MixerStrips.Count)
            {
                MixerStrips[ch].ProgramId = pc.Patch; 
                UpdateStripInstrumentType(ch);
            }
        }
    }
}
