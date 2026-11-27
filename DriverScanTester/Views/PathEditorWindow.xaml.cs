using System.Windows;
using DriverScanTester.ViewModels;

namespace DriverScanTester.Views
{
    public partial class PathEditorWindow : Window
    {
        public PathEditorWindow()
        {
            InitializeComponent();
            Activated += (_, _) => (DataContext as PathEditorViewModel)?.ProfileEditor.RefreshAutoBuffStatuses();
        }
    }
}
