using System;
using System.Runtime.InteropServices;

namespace DriverScanTester.PointerScan
{
    public sealed class RpmMemoryReader : IMemoryReader
    {
        private readonly nint _processHandle;
        private bool _disposed;

        public RpmMemoryReader(nint processHandle, int pointerSize)
        {
            _processHandle = processHandle;
            PointerSize = pointerSize;
        }

        public int PointerSize { get; }

        public bool Read(ulong address, Span<byte> buffer, out int bytesRead)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RpmMemoryReader));
            }

            bytesRead = 0;
            if (buffer.Length == 0)
            {
                return true;
            }

            unsafe
            {
                fixed (byte* p = buffer)
                {
                    if (!NativeMethods.ReadProcessMemory(_processHandle, (nuint)address, p, (nuint)buffer.Length, out nuint read))
                    {
                        bytesRead = (int)read;
                        return false;
                    }

                    bytesRead = (int)read;
                    return bytesRead > 0;
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern unsafe bool ReadProcessMemory(
                nint hProcess,
                nuint lpBaseAddress,
                void* lpBuffer,
                nuint nSize,
                out nuint lpNumberOfBytesRead);
        }
    }
}
