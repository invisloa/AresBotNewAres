using DriverScanTester.ViewModels;
using System.Windows;

namespace DriverScanTester.Views
{
    public partial class CameraCalibrationWindow : Window
    {
        public CameraCalibrationWindow()
        {
            InitializeComponent();
            Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        }
    }
}
