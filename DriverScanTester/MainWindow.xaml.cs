using DriverScanTester.ViewModels;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DriverScanTester
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            var vm = new MainViewModel();
            this.DataContext = vm;
        }
        private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // stick caret at the end and scroll there
            var tb = (System.Windows.Controls.TextBox)sender;
            tb.CaretIndex = tb.Text.Length;
            tb.ScrollToEnd();
        }

    }
}