using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NitroSynth.App.ViewModels;

namespace NitroSynth.App
{
    public partial class MusEditorWindow : Window
    {
        private Func<string, Task<MainWindowViewModel.MusSaveResult>> _saveAsync;
        private bool _isSaving;

        public MusEditorWindow()
        {
            InitializeComponent();
            _saveAsync = _ => Task.FromResult(new MainWindowViewModel.MusSaveResult(false, "Save callback is not set."));
            StatusText.Text = string.Empty;
        }

        public MusEditorWindow(
            string title,
            string headerText,
            string initialText,
            Func<string, Task<MainWindowViewModel.MusSaveResult>> saveAsync)
            : this()
        {
            Title = string.IsNullOrWhiteSpace(title) ? "MUS Editor" : title;
            HeaderText.Text = headerText ?? string.Empty;
            MusTextEditor.Text = initialText ?? string.Empty;
            _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
            {
                e.Handled = true;
                await SaveAsync();
                return;
            }

            base.OnKeyDown(e);
        }

        private async void OnSaveClicked(object? sender, RoutedEventArgs e)
        {
            await SaveAsync();
        }

        private async Task SaveAsync()
        {
            if (_isSaving)
                return;

            _isSaving = true;
            SaveButton.IsEnabled = false;
            StatusText.Text = "Saving...";

            try
            {
                string text = MusTextEditor.Text ?? string.Empty;
                var result = await _saveAsync(text);
                StatusText.Text = result.Message;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Save failed: {ex.Message}";
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
    }
}
