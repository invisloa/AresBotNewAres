using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace DriverScanTester.Memory
{
    /// <summary>
    /// Abstraction that supplies raw process memory reads to the pointer scanner. Implementations can wrap the custom driver or
    /// fall back to ReadProcessMemory.
    /// </summary>
    [Obsolete("Not used. Use DriverScanTester.PointerScan.IMemoryReader instead.")]
    public interface IMemoryReader
    {
        bool Read(ulong address, byte[] buffer, int length, out int bytesRead);
    }

    [Obsolete("Not used.")]
    public delegate bool DriverReadDelegate(uint processId, ulong address, byte[] buffer, out uint bytesRead);

    [Obsolete("Not used. Use DriverScanTester.PointerScan.DelegateMemoryReader instead.")]
    public sealed class DriverMemoryReader : IMemoryReader
    {
        private readonly uint _processId;
        private readonly DriverReadDelegate _reader;

        public DriverMemoryReader(uint processId, DriverReadDelegate reader)
        {
            _processId = processId;
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public bool Read(ulong address, byte[] buffer, int length, out int bytesRead)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (length < 0 || length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));

            byte[] destination = buffer;
            bool rented = false;
            if (length != buffer.Length)
            {
                destination = ArrayPool<byte>.Shared.Rent(length);
                rented = true;
            }

            try
            {
                if (!_reader(_processId, address, destination, out uint got))
                {
                    bytesRead = (int)Math.Min(got, (uint)length);
                    if (bytesRead > 0 && rented)
                        Buffer.BlockCopy(destination, 0, buffer, 0, bytesRead);
                    return false;
                }

                bytesRead = (int)Math.Min(got, (uint)length);
                if (rented && bytesRead > 0)
                    Buffer.BlockCopy(destination, 0, buffer, 0, bytesRead);
                return true;
            }
            finally
            {
                if (rented)
                    ArrayPool<byte>.Shared.Return(destination);
            }
        }
    }

    [Obsolete("Not used. Use DriverScanTester.PointerScan.RpmMemoryReader instead.")]
    public sealed class ProcessMemoryReader : IMemoryReader
    {
        private readonly nint _processHandle;

        public ProcessMemoryReader(nint processHandle)
        {
            _processHandle = processHandle;
        }

        public bool Read(ulong address, byte[] buffer, int length, out int bytesRead)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (length < 0 || length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));

            byte[] destination = buffer;
            bool rented = false;
            if (length != buffer.Length)
            {
                destination = ArrayPool<byte>.Shared.Rent(length);
                rented = true;
            }

            try
            {
                if (!NativeMethods.ReadProcessMemory(_processHandle, (nuint)address, destination, (nuint)length, out nuint read))
                {
                    bytesRead = (int)Math.Min(read, (nuint)length);
                    if (bytesRead > 0 && rented)
                        Buffer.BlockCopy(destination, 0, buffer, 0, bytesRead);
                    return false;
                }

                bytesRead = (int)Math.Min(read, (nuint)length);
                if (rented && bytesRead > 0)
                    Buffer.BlockCopy(destination, 0, buffer, 0, bytesRead);
                return true;
            }
            finally
            {
                if (rented)
                    ArrayPool<byte>.Shared.Return(destination);
            }
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool ReadProcessMemory(
                nint hProcess,
                nuint lpBaseAddress,
                [Out] byte[] lpBuffer,
                nuint nSize,
                out nuint lpNumberOfBytesRead);
        }
    }

    [Obsolete("Not used.")]
    public sealed class CompositeMemoryReader : IMemoryReader
    {
        private readonly IMemoryReader _primary;
        private readonly IMemoryReader? _fallback;

        public CompositeMemoryReader(IMemoryReader primary, IMemoryReader? fallback)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _fallback = fallback;
        }

        public bool Read(ulong address, byte[] buffer, int length, out int bytesRead)
        {
            if (_primary.Read(address, buffer, length, out bytesRead))
                return true;

            if (_fallback == null)
                return false;

            return _fallback.Read(address, buffer, length, out bytesRead);
        }
    }
}
