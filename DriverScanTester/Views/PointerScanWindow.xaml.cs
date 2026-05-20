using DriverScanTester.PointerScan;
using DriverScanTester.ViewModels;
using System;
using System.Windows;

namespace DriverScanTester.Views
{
    public partial class PointerScanWindow : Window
    {
        public PointerScanWindow(PointerScanner scanner)
        {
            InitializeComponent();
            DataContext = new PointerScanViewModel(scanner);
        }

        public PointerScanWindow(PointerScanViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        private PointerScanViewModel ViewModel => (PointerScanViewModel)DataContext;

        private void ResultsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ViewModel == null) return;
            var grid = (System.Windows.Controls.DataGrid)sender;
            ViewModel.SelectedResults.Clear();
            foreach (var item in grid.SelectedItems)
            {
                if (item is PointerScanViewModel.PointerScanResultViewModel vm)
                {
                    ViewModel.SelectedResults.Add(vm);
                }
            }
            ViewModel.OnSelectionChanged();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ViewModel?.Dispose();
        }
    }
}
