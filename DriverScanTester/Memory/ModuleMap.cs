using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DriverScanTester.Memory
{
    /// <summary>
    /// Captures the loaded module list for a process so pointer hits can be classified as module-based ("green").
    /// </summary>
    public sealed class ModuleMap
    {
        public sealed class ModuleEntry
        {
            public ModuleEntry(string name, ulong baseAddress, uint size)
            {
                Name = name;
                BaseAddress = baseAddress;
                Size = size;
                EndAddress = baseAddress + size;
            }

            public string Name { get; }
            public ulong BaseAddress { get; }
            public uint Size { get; }
            public ulong EndAddress { get; }
        }

        private readonly List<ModuleEntry> _modules;

        private ModuleMap(List<ModuleEntry> modules)
        {
            _modules = modules;
            _modules.Sort((a, b) => a.BaseAddress.CompareTo(b.BaseAddress));
        }

        public static ModuleMap Create(uint processId)
        {
            var modules = new List<ModuleEntry>();
            nint snapshot = NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.TH32CS_SNAPMODULE | NativeMethods.TH32CS_SNAPMODULE32,
                processId);

            if (snapshot == NativeMethods.INVALID_HANDLE_VALUE)
                return new ModuleMap(modules);

            try
            {
                var entry = new NativeMethods.MODULEENTRY32
                {
                    dwSize = (uint)Marshal.SizeOf<NativeMethods.MODULEENTRY32>()
                };

                bool hasEntry = NativeMethods.Module32First(snapshot, ref entry);
                while (hasEntry)
                {
                    string name = entry.szModule;
                    ulong baseAddr = (ulong)entry.modBaseAddr;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        modules.Add(new ModuleEntry(name, baseAddr, entry.modBaseSize));
                    }

                    hasEntry = NativeMethods.Module32Next(snapshot, ref entry);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(snapshot);
            }

            return new ModuleMap(modules);
        }

        public bool TryFindModule(ulong address, out ModuleEntry? module)
        {
            foreach (var entry in _modules)
            {
                if (address >= entry.BaseAddress && address < entry.EndAddress)
                {
                    module = entry;
                    return true;
                }
            }

            module = null;
            return false;
        }

        public bool TryFindModuleByName(string name, out ModuleEntry? module)
        {
            // 1. Try exact match (case-insensitive)
            foreach (var entry in _modules)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    module = entry;
                    return true;
                }
            }

            // 2. Try match without extension if the input has one
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(name);
                foreach (var entry in _modules)
                {
                    if (string.Equals(entry.Name, nameWithoutExt, StringComparison.OrdinalIgnoreCase))
                    {
                        module = entry;
                        return true;
                    }
                }
            }
            
            // 3. Try match with .exe added if input lacks one
            string nameWithExe = name + ".exe";
            foreach (var entry in _modules)
            {
                if (string.Equals(entry.Name, nameWithExe, StringComparison.OrdinalIgnoreCase))
                {
                    module = entry;
                    return true;
                }
            }

            module = null;
            return false;
        }

        private static class NativeMethods
        {
            public const uint TH32CS_SNAPMODULE = 0x00000008;
            public const uint TH32CS_SNAPMODULE32 = 0x00000010;
            public static readonly nint INVALID_HANDLE_VALUE = new(-1);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
            public struct MODULEENTRY32
            {
                public uint dwSize;
                public uint th32ModuleID;
                public uint th32ProcessID;
                public uint GlblcntUsage;
                public uint ProccntUsage;
                public nint modBaseAddr;
                public uint modBaseSize;
                public nint hModule;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
                public string szModule;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string szExePath;
            }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern bool Module32First(nint hSnapshot, ref MODULEENTRY32 lpme);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern bool Module32Next(nint hSnapshot, ref MODULEENTRY32 lpme);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(nint hObject);
        }
    }
}
