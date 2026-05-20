using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DriverScanTester.Views
{
    /// <summary>
    /// Interaction logic for HexWindow.xaml
    /// </summary>
    public partial class HexWindow : Window
    {
        public HexWindow()
        {
            InitializeComponent();
        }

        private void HexGrid_CurrentCellChanged(object sender, EventArgs e)
        {
            if (DataContext is not ViewModels.HexViewModel vm) return;
            if (HexGrid.CurrentCell.Column == null || HexGrid.CurrentCell.Item is not ViewModels.HexRow row) return;

            int offset = ParseByteColumnOffset(HexGrid.CurrentCell.Column);
            vm.SetSelectedCell(row, offset);
        }

        private static int ParseByteColumnOffset(System.Windows.Controls.DataGridColumn column)
        {
            if (column is System.Windows.Controls.DataGridTemplateColumn template)
            {
                string header = template.Header as string ?? "";
                if (header.Length == 2 && byte.TryParse(header, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                    return b;
            }
            return -1;
        }
    }
}
