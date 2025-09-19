using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace NitroSynth.App.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private const string DefaultMessage = "メニューから.sdatファイルを開いてください。";

    private string _statusMessage = DefaultMessage;
    private string? _loadedFilePath;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadSdatAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
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

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
