using System;
using System.Buffers;

namespace DriverScanTester.PointerScan
{
    public sealed class DelegateMemoryReader : IMemoryReader
    {
        private readonly Func<ulong, byte[], (bool ok, int read)> _reader;
        private readonly int _pointerSize;
        private bool _disposed;

        public DelegateMemoryReader(int pointerSize, Func<ulong, byte[], (bool ok, int read)> reader)
        {
            _pointerSize = pointerSize;
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public int PointerSize => _pointerSize;

        public bool Read(ulong address, Span<byte> buffer, out int bytesRead)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DelegateMemoryReader));
            }

            bytesRead = 0;
            if (buffer.Length == 0)
            {
                return true;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var (ok, read) = _reader(address, rented);
                bytesRead = Math.Min(buffer.Length, Math.Max(read, 0));
                if (bytesRead > 0)
                {
                    rented.AsSpan(0, bytesRead).CopyTo(buffer);
                }

                return ok && bytesRead > 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
