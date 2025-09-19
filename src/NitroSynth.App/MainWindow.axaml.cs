using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NitroSynth.App.ViewModels;

namespace NitroSynth.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
    }

    private async void OnOpenSdatClicked(object? sender, RoutedEventArgs e)
    {
        if (StorageProvider is null)
        {
            return;
        }

        var options = new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = ".sdat ファイルを開く",
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Nitro Sound Archive (*.sdat)")
                {
                    Patterns = new[] { "*.sdat" }
                },
                FilePickerFileTypes.All
            }
        };

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0)
        {
            return;
        }

        await _viewModel.LoadSdatAsync(files[0]);
    }
}
