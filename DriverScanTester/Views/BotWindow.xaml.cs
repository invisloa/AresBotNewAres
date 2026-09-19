using DriverScanTester.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace DriverScanTester.Views
{
    public partial class BotWindow : Window
    {
        public BotWindow()
        {
            InitializeComponent();
        }

        private void BotLogBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Keep the newest entry visible: stick caret at the end and scroll there.
            // Only autoscroll when the user is already at (or near) the bottom so
            // reading older lines is not yanked away on every new line.
            var tb = (System.Windows.Controls.TextBox)sender;
            const double bottomTolerance = 20.0;
            bool nearBottom = tb.VerticalOffset + tb.ViewportHeight >= tb.ExtentHeight - bottomTolerance;
            if (nearBottom)
            {
                tb.CaretIndex = tb.Text.Length;
                tb.ScrollToEnd();
            }
        }
    }
}
