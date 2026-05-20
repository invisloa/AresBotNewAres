using System;

namespace DriverScanTester.PointerScan
{
    public interface IMemoryReader : IDisposable
    {
        int PointerSize { get; }

        bool Read(ulong address, Span<byte> buffer, out int bytesRead);
    }
}
