// Views/MatchesWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Clipboard = System.Windows.Clipboard;

namespace DriverScanTester.Views
{
    public partial class MatchesWindow : Window
    {
        private class AddrEntry
        {
            public string Address { get; set; }
            public string Value { get; set; }
            public bool Ok { get; set; }
            public UIntPtr RawAddress { get; set; }
        }

        private readonly List<AddrEntry> _list;
        private readonly Action<UIntPtr> _pointerScanAction;

        public MatchesWindow(IEnumerable<UIntPtr> addresses,
                             Func<UIntPtr, (bool ok, string value)> reader,
                             string elemLabel,
                             Action<UIntPtr> pointerScanAction)

        {
            InitializeComponent();
            Title = $"Scan Results ({elemLabel})";

            _pointerScanAction = pointerScanAction;
            bool hasPointerScan = _pointerScanAction != null;
            var pointerVisibility = hasPointerScan ? Visibility.Visible : Visibility.Collapsed;
            PointerScanButton.Visibility = pointerVisibility;

            var pointerScanMenuItem = FindContextMenuElement<MenuItem>("PointerScan");
            if (pointerScanMenuItem != null)
            {
                pointerScanMenuItem.Visibility = pointerVisibility;
            }

            var pointerScanSelectedMenuItem = FindContextMenuElement<MenuItem>("PointerScanSelected");
            if (pointerScanSelectedMenuItem != null)
            {
                pointerScanSelectedMenuItem.Visibility = pointerVisibility;
            }

            var pointerScanSeparator = FindContextMenuElement<Separator>("PointerScanSeparator");
            if (pointerScanSeparator != null)
            {
                pointerScanSeparator.Visibility = pointerVisibility;
            }

            _list = new List<AddrEntry>();
            foreach (var p in addresses)
            {
                var (ok, val) = reader(p);
                _list.Add(new AddrEntry
                {
                    Address = $"0x{p.ToUInt64():X}",
                    Value = val,
                    Ok = ok,
                    RawAddress = p
                });

            }
            AddressListView.ItemsSource = _list;

        }

        private T? FindContextMenuElement<T>(string tag) where T : FrameworkElement
        {
            return AddressListView.ContextMenu?
                .Items
                .OfType<T>()
                .FirstOrDefault(item => Equals(item.Tag, tag));
        }

        private void CopySelected_Click(object sender, RoutedEventArgs e)
        {
            var sel = AddressListView.SelectedItems.OfType<AddrEntry>().ToList();
            if (sel.Count == 0 && AddressListView.SelectedItem is AddrEntry one) sel.Add(one);
            if (sel.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => x.Address)));
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
            => Clipboard.SetText(string.Join(Environment.NewLine, _list.Select(x => x.Address)));

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void PointerScan_Click(object sender, RoutedEventArgs e)
        {
            if (_pointerScanAction == null)
            {
                return;
            }

            if (TryGetFirstSelected(out var entry))
            {
                _pointerScanAction(entry.RawAddress);
            }

        }

        private bool TryGetFirstSelected(out AddrEntry entry)
        {
            entry = AddressListView.SelectedItems.OfType<AddrEntry>().FirstOrDefault();
            if (entry == null && AddressListView.SelectedItem is AddrEntry one)
            {
                entry = one;
            }

            return entry != null;
        }
    }
}
