using System;
using System.Collections.Generic;
using System.Windows;
using Application = System.Windows.Application;

namespace DriverScanTester.Utils
{
    public static class MatchesUiHelper
    {
        private const int PAGE_SIZE = 0x1000; // keep local so VM doesn't need to pass it

        // Expand 1-bit-per-element bitmaps into concrete addresses
        public static List<UIntPtr> ExpandCandidateBitmaps(
            IEnumerable<KeyValuePair<ulong, byte[]>> candidates,
            int elemSize /* 1 for byte, 2 for short */)
        {
            var addresses = new List<UIntPtr>();

            foreach (var kv in candidates)
            {
                ulong pageBase = kv.Key;
                byte[] bm = kv.Value;

                int pageElemCount = PAGE_SIZE / elemSize;
                int bitIndex = 0;

                for (int i = 0; i < bm.Length; i++)
                {
                    byte v = bm[i];
                    if (v == 0) { bitIndex += 8; continue; }

                    for (int b = 0; b < 8 && bitIndex < pageElemCount; b++, bitIndex++)
                    {
                        if (((v >> b) & 1) != 0)
                        {
                            ulong addr = pageBase + (ulong)(bitIndex * elemSize);
                            addresses.Add((UIntPtr)addr);
                        }
                    }
                }
            }

            return addresses;
        }

        public static void ShowMatchesWindow(
            IEnumerable<UIntPtr> addresses,
            Func<UIntPtr, (bool ok, string value)> reader,
            bool isByte,
            Action<UIntPtr> pointerScanAction = null)
        {
            var wnd = new DriverScanTester.Views.MatchesWindow(addresses, reader, isByte ? "byte" : "short", pointerScanAction)

            {
                Owner = Application.Current?.MainWindow
            };
            wnd.Show();
        }
    }
}
