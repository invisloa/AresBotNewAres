using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Late-bound access to the attached game process.
    ///
    /// Every member is a delegate instead of a captured PID/handle, so a window opened
    /// while the game is NOT attached keeps working: the editor/profile tools can be used
    /// offline, and the moment the user attaches, the very same window starts reading live
    /// player data (no stale PID, no re-open needed).
    /// </summary>
    public sealed class BotGameAccess
    {
        public BotGameAccess(
            Func<bool> isAttached,
            Func<uint> getPid,
            Func<int> getPointerSize,
            Func<ulong> getModuleBase,
            Func<uint, ulong, byte[], uint, bool> readMemory)
        {
            IsAttached = isAttached ?? throw new ArgumentNullException(nameof(isAttached));
            GetPid = getPid ?? throw new ArgumentNullException(nameof(getPid));
            GetPointerSize = getPointerSize ?? throw new ArgumentNullException(nameof(getPointerSize));
            GetModuleBase = getModuleBase ?? throw new ArgumentNullException(nameof(getModuleBase));
            ReadMemory = readMemory ?? throw new ArgumentNullException(nameof(readMemory));
        }

        /// <summary>True while a game process is attached.</summary>
        public Func<bool> IsAttached { get; }

        /// <summary>PID of the attached process (0 while detached).</summary>
        public Func<uint> GetPid { get; }

        /// <summary>Pointer size of the attached process (4/8; host default while detached).</summary>
        public Func<int> GetPointerSize { get; }

        /// <summary>Resolves the Ares.exe module base (0 while detached/unresolved).</summary>
        public Func<ulong> GetModuleBase { get; }

        /// <summary>Driver-backed memory read: (pid, address, buffer, size) =&gt; success.</summary>
        public Func<uint, ulong, byte[], uint, bool> ReadMemory { get; }

        /// <summary>An access object that never touches the game (design-time / offline fallback).</summary>
        public static BotGameAccess Detached { get; } = new(
            isAttached: () => false,
            getPid: () => 0,
            getPointerSize: () => IntPtr.Size,
            getModuleBase: () => 0,
            readMemory: (_, _, _, _) => false);
    }
}
