using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using NitroSynth.App.Sdat;

namespace NitroSynth.App.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private const string DefaultMessage = "メニューから.sdatファイルを開いてください。";

    private string _statusMessage = DefaultMessage;
    private string? _loadedFilePath;
    private SoundBankSummary? _selectedSoundBank;

    public ObservableCollection<SoundBankSummary> SoundBanks { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string? LoadedFilePath
    {
        get => _loadedFilePath;
        private set => SetField(ref _loadedFilePath, value);
    }

    public bool HasSoundBanks => SoundBanks.Count > 0;

    public SoundBankSummary? SelectedSoundBank
    {
        get => _selectedSoundBank;
        set
        {
            if (SetField(ref _selectedSoundBank, value))
            {
                OnPropertyChanged(nameof(SelectedSoundBankDescription));
            }
        }
    }

    public string SelectedSoundBankDescription => SelectedSoundBank is { } bank
        ? $"FILE ID: {bank.FileId}"
        : "SBNKを選択してください。";

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadSdatAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            var archive = await SdatParser.ParseAsync(stream);

            UpdateSoundBanks(archive.SoundBanks);

            LoadedFilePath = file.Path?.LocalPath ?? file.Name;
            StatusMessage = $"読み込み完了: {file.Name} ({archive.FileSize:N0} バイト) / SBNK {SoundBanks.Count} 件";
        }
        catch (Exception ex)
        {
            UpdateSoundBanks(Array.Empty<SoundBankSummary>());
            var lengthText = stream.CanSeek
                ? $" ({stream.Length:N0} バイト)"
                : string.Empty;

            LoadedFilePath = file.Path?.LocalPath ?? file.Name;
            StatusMessage = $"読み込み完了: {file.Name}{lengthText}";
        }
        catch (Exception ex)
        {
            LoadedFilePath = file.Path?.LocalPath ?? file.Name;
            StatusMessage = $"読み込みに失敗しました: {ex.Message}";
        }
    }

    private void UpdateSoundBanks(IReadOnlyList<SoundBankSummary> banks)
    {
        SoundBanks.Clear();
        foreach (var bank in banks)
        {
            SoundBanks.Add(bank);
        }

        OnPropertyChanged(nameof(HasSoundBanks));
        SelectedSoundBank = SoundBanks.Count > 0 ? SoundBanks[0] : null;
        if (SoundBanks.Count == 0)
        {
            OnPropertyChanged(nameof(SelectedSoundBankDescription));
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
