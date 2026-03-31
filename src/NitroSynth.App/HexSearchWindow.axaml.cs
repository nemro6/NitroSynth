using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NitroSynth.App
{
    public partial class HexSearchWindow : Window
    {
        private readonly Func<HexSearchRequest, bool, (bool success, string message)> _searchHandler;
        private readonly Func<HexSearchRequest, (bool success, string message)> _listHandler;

        public HexSearchWindow()
        {
            InitializeComponent();

            _searchHandler = (_, _) => (false, "Search handler is not set.");
            _listHandler = _ => (false, "List handler is not set.");

            BigEndianRadio.IsChecked = true;
            DataHexRadio.IsChecked = true;
            RangeFromCursorRadio.IsChecked = true;
            StatusText.Text = "Enter search conditions.";
        }

        public HexSearchWindow(
            Func<HexSearchRequest, bool, (bool success, string message)> searchHandler,
            Func<HexSearchRequest, (bool success, string message)> listHandler)
            : this()
        {
            _searchHandler = searchHandler ?? throw new ArgumentNullException(nameof(searchHandler));
            _listHandler = listHandler ?? throw new ArgumentNullException(nameof(listHandler));
        }

        public void FocusSearchInput()
        {
            SearchDataBox.Focus();
        }

        private void OnSearchDataKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            ExecuteSearch(forward: true);
        }

        private void OnSearchPreviousClicked(object? sender, RoutedEventArgs e)
        {
            ExecuteSearch(forward: false);
        }

        private void OnSearchNextClicked(object? sender, RoutedEventArgs e)
        {
            ExecuteSearch(forward: true);
        }

        private void OnListMatchesClicked(object? sender, RoutedEventArgs e)
        {
            if (!TryBuildRequest(out var request, out var validationMessage))
            {
                StatusText.Text = validationMessage;
                return;
            }

            var result = _listHandler(request);
            StatusText.Text = result.message;
        }

        private void OnCloseClicked(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ExecuteSearch(bool forward)
        {
            if (!TryBuildRequest(out var request, out var validationMessage))
            {
                StatusText.Text = validationMessage;
                return;
            }

            var result = _searchHandler(request, forward);
            StatusText.Text = result.message;
        }

        private bool TryBuildRequest(out HexSearchRequest request, out string message)
        {
            request = default;
            message = string.Empty;

            string query = SearchDataBox.Text?.Trim() ?? string.Empty;
            if (query.Length == 0)
            {
                message = "Enter search data.";
                return false;
            }

            var method = LittleEndianRadio.IsChecked == true
                ? HexSearchMethod.LittleEndian
                : HexSearchMethod.BigEndian;

            var dataKind = DataTextRadio.IsChecked == true
                ? HexSearchDataKind.Text
                : HexSearchDataKind.HexData;

            HexSearchRangeKind rangeKind = HexSearchRangeKind.FromCursor;
            if (RangeAllRadio.IsChecked == true)
                rangeKind = HexSearchRangeKind.WholeData;
            else if (RangeSelectionRadio.IsChecked == true)
                rangeKind = HexSearchRangeKind.Selection;

            request = new HexSearchRequest(query, method, dataKind, rangeKind);
            return true;
        }
    }
}
