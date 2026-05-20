using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DriverScanTester.Models
{
    public static class PsApi
    {
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_VM_READ = 0x0010;

        [Flags]
        public enum MemState : uint { MEM_COMMIT = 0x1000, MEM_FREE = 0x10000, MEM_RESERVE = 0x2000 }

        [Flags]
        public enum MemProtect : uint
        {
            PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04, PAGE_WRITECOPY = 0x08,
            PAGE_EXECUTE = 0x10, PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_WRITECOPY = 0x80,
            PAGE_GUARD = 0x100, PAGE_NOCACHE = 0x200, PAGE_WRITECOMBINE = 0x400,
            PAGE_EXECUTE_WRITECOMBINE = PAGE_EXECUTE_READWRITE | PAGE_WRITECOMBINE
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION64
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public ulong RegionSize;
            public MemState State;
            public MemProtect Protect;
            public uint Type;
            public uint __alignment2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint desiredAccess, bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(nint h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int VirtualQueryEx(
            nint hProcess,
            nuint lpAddress,
            out MEMORY_BASIC_INFORMATION64 lpBuffer,
            nuint dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process(nint hProcess, out bool wow64Process);
    }
}
