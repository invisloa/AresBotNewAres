using DriverScanTester.Models;
using DriverScanTester.PointerScan;
using DriverScanTester.Services;

using DriverScanTester.Utils;
using DriverScanTester.Views;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Threading;
using Application = System.Windows.Application;
using System.Threading.Tasks;

namespace DriverScanTester.ViewModels
{
    public sealed class MainViewModel : BaseViewModel
    {
        // ---------- UI state ----------
        private bool _isByte = true;
        private bool _isShort;
        private bool _isCLong;  // NEW: C long (4 bytes on Windows)
        private bool _isLong;   // 64-bit
        private string _pidText = "";
        private string _valueScanText = "";
        private string _rangeCenterText = string.Empty;
        private string _rangeRadiusText = string.Empty;
        private string _pointerTargetText = string.Empty;
        private string _logText = "";
        private string _botTargetXText = "766";
        private string _botTargetYText = "190";
        private string _testAngleText = "15400";
        private string _subScan1Text = "";
        private string _subScan2Text = "";
        private MovementPrecision _selectedPrecision = MovementPrecision.Medium;
        private BotMode _selectedBotMode = BotMode.OnlyMove;

        public MovementPrecision SelectedPrecision
        {
            get => _selectedPrecision;
            set => SetProperty(ref _selectedPrecision, value);
        }

        public BotMode SelectedBotMode
        {
            get => _selectedBotMode;
            set => SetProperty(ref _selectedBotMode, value);
        }

        public bool IsByte
        {
            get => _isByte;
            set
            {
                if (SetProperty(ref _isByte, value) && value)
                {
                    IsShort = false;
                    IsCLong = false;
                    IsLong = false;
                }
            }
        }

        public bool IsShort
        {
            get => _isShort;
            set
            {
                if (SetProperty(ref _isShort, value) && value)
                {
                    IsByte = false;
                    IsCLong = false;
                    IsLong = false;
                }
            }
        }

        public bool IsCLong   // NEW: 32-bit C long
        {
            get => _isCLong;
            set
            {
                if (SetProperty(ref _isCLong, value) && value)
                {
                    IsByte = false;
                    IsShort = false;
                    IsLong = false;
                }
            }
        }

        public bool IsLong    // 64-bit
        {
            get => _isLong;
            set
            {
                if (SetProperty(ref _isLong, value) && value)
                {
                    IsByte = false;
                    IsShort = false;
                    IsCLong = false;
                }
            }
        }

        public string PidText
        {
            get => _pidText;
            set => SetProperty(ref _pidText, value);
        }

        public string ValueScanText
        {
            get => _valueScanText;
            set => SetProperty(ref _valueScanText, value);
        }

        public string RangeCenterText
        {
            get => _rangeCenterText;
            set => SetProperty(ref _rangeCenterText, value);
        }

        public string RangeRadiusText
        {
            get => _rangeRadiusText;
            set => SetProperty(ref _rangeRadiusText, value);
        }

        public string PointerTargetText
        {
            get => _pointerTargetText;
            set => SetProperty(ref _pointerTargetText, value);
        }

        public string LogText
        {
            get => _logText;
            private set => SetProperty(ref _logText, value);
        }

        public string BotTargetXText
        {
            get => _botTargetXText;
            set => SetProperty(ref _botTargetXText, value);
        }

        public string BotTargetYText
        {
            get => _botTargetYText;
            set => SetProperty(ref _botTargetYText, value);
        }

        public string TestAngleText
        {
            get => _testAngleText;
            set => SetProperty(ref _testAngleText, value);
        }

        public string SubScan1Text
        {
            get => _subScan1Text;
            set => SetProperty(ref _subScan1Text, value);
        }

        public string SubScan2Text
        {
            get => _subScan2Text;
            set => SetProperty(ref _subScan2Text, value);
        }

        // ---------- Commands ----------
        public ICommand AttachCommand { get; }
        public ICommand SameAsOriginalCommand { get; }
        public ICommand FirstScanCommand { get; }
        public ICommand FirstScanNotEqualCommand { get; }

        public ICommand IncreasedCommand { get; }
        public ICommand DecreasedCommand { get; }
        public ICommand NotChangedCommand { get; }
        public ICommand ChangedCommand { get; }
        public ICommand NotEqualCommand { get; }
        public ICommand ShowLockWindowCommand { get; }
        public ICommand ShowSearchAddressCommand { get; }
        public ICommand ShowHexWindowCommand { get; }
        public ICommand SubScanEqualsCommand { get; }
        public ICommand SubScanNotEqualsCommand { get; }
        public ICommand SubScan1Command { get; }
        public ICommand SubScan2Command { get; }
        public ICommand ShowMatchesCommand { get; }
        public ICommand PointerScanCommand { get; }
        public ICommand RunBotCommand { get; }
        public ICommand RunLootBotCommand { get; }
        public ICommand TestAngleCommand { get; }
        public ICommand OpenPathEditorCommand { get; }
        public ICommand OpenBotWindowCommand { get; }
        public ICommand ClearLogCommand { get; }

        public delegate bool DriverWriteDelegate(uint pid, ulong remoteAddr, byte[] src, out uint wrote);
        // ---------- Driver plumbing ----------
        private PointerScanner? _pointerScanner;
        private IMemoryReader? _pointerReader;

        private const int PAGE_SIZE = 0x1000;
        private const string DEVICE_PATH = @"\\.\SexyDriver";

        // 1 MiB bitmap => up to 8 Mi elements per call (elem = byte/short/clong/long64).
        private const int MAX_BITMAP_BYTES = 1 << 20; // must mirror driver

        private SafeFileHandle _hDevice;
        private nint _hProcess = nint.Zero;
        private uint _attachedPid;
        private bool _isAttached;
        public bool IsAttached => _isAttached;
        private int _cachedPointerSize; // 0 => unknown (lazy init)

        private readonly struct ScanWindow
        {
            public bool Enabled { get; }
            public ulong Start { get; }
            public ulong EndExclusive { get; }

            public ScanWindow(ulong start, ulong endExclusive)
            {
                Enabled = true;
                Start = start;
                EndExclusive = endExclusive;
            }
        }

        // ---------- Baseline + progressive candidates ----------
        // ORIGINAL baseline (never mutated after creation). pageBase -> 4096 bytes
        private readonly Dictionary<ulong, byte[]> _userSnapshot = new();

        // Progressive candidates: pageBase -> bitmap sized to (PAGE_SIZE / elemSize)/8
        private readonly Dictionary<ulong, byte[]> _candidates = new();

        // Managed fallback baseline (used when the driver lacks 32-bit support)
        private readonly Dictionary<ulong, byte[]> _managedBaseline = new();
        private bool _driverSupportsCLong = true;
        private bool _loggedClongFallback;

        private enum RelOp { Increased, Decreased }

        // ---------- Element-size helpers ----------
        private int ElemSize => IsLong ? 8 : (IsCLong ? 4 : (IsShort ? 2 : 1));
        private uint ElemFlag => IsLong
            ? Flags.SCAN_ELEM_LONG64
            : (IsCLong ? Flags.SCAN_ELEM_CLONG
                       : (IsShort ? Flags.SCAN_ELEM_SHORT : Flags.SCAN_ELEM_BYTE));
        private int PageElemCount => PAGE_SIZE / ElemSize;
        private int PageBitmapBytes => (PageElemCount + 7) / 8;
        private ulong MaxScanChunkBytesForCurrentElem => (ulong)MAX_BITMAP_BYTES * 8UL * (ulong)ElemSize; // fits bitmap output per call
        private string CurrentElementLabel => IsByte ? "byte" : (IsShort ? "short" : (IsCLong ? "int32" : "long64"));
        private bool UseManagedBaseline => IsCLong && !_driverSupportsCLong;

        public MainViewModel()
        {
            AttachCommand = new RelayCommand(_ => Attach(), _ => true);

            // IMPORTANT: this command now does a compare vs ORIGINAL, then returns to "last-scan" mode.
            SameAsOriginalCommand = new RelayCommand(_ => CompareWithOriginal(), _ => _isAttached);

            FirstScanCommand = new RelayCommand(_ => FirstScan(), _ => _isAttached);
            FirstScanNotEqualCommand = new RelayCommand(_ => LogUnsupported("First ≠ (use filters after first scan)"), _ => _isAttached);

            IncreasedCommand = new RelayCommand(_ => FilterRelative(RelOp.Increased), _ => _isAttached);
            DecreasedCommand = new RelayCommand(_ => FilterRelative(RelOp.Decreased), _ => _isAttached);

            NotChangedCommand = new RelayCommand(_ => CachedCompare(eq: true), _ => _isAttached);
            ChangedCommand = new RelayCommand(_ => CachedCompare(eq: false), _ => _isAttached);
            NotEqualCommand = new RelayCommand(_ => CachedCompare(eq: false), _ => _isAttached);

            SubScanEqualsCommand = new RelayCommand(_ => SubScanEqualsConstant(), _ => _isAttached);
            SubScanNotEqualsCommand = new RelayCommand(_ => SubScanNotEqualsConstant(), _ => _isAttached);
            SubScan1Command = new RelayCommand(_ => SubScan1(), _ => _isAttached);
            SubScan2Command = new RelayCommand(_ => SubScan2(), _ => _isAttached);
            ShowMatchesCommand = new RelayCommand(_ => ShowMatches(), _ => _isAttached && _candidates.Count > 0);
            ShowLockWindowCommand = new RelayCommand(_ => ShowLockWindow(), _ => _isAttached);
            ShowSearchAddressCommand = new RelayCommand(_ => ShowSearchAddressWindow(), _ => _isAttached);
            ShowHexWindowCommand = new RelayCommand(_ => ShowHexWindow(), _ => _isAttached);
            PointerScanCommand = new RelayCommand(_ => OpenPointerScanFromMain(), _ => _isAttached && _pointerScanner != null);
            RunBotCommand = new RelayCommand(_ => ToggleMovementBot(), _ => _isAttached);
            RunLootBotCommand = new RelayCommand(_ => ToggleLootBot(), _ => _isAttached);
            TestAngleCommand = new RelayCommand(_ => RunTestAngle(), _ => _isAttached);
            OpenPathEditorCommand = new RelayCommand(_ => OpenPathEditor(), _ => _isAttached);
            OpenBotWindowCommand = new RelayCommand(_ => OpenBotWindow(), _ => _isAttached);
            ClearLogCommand = new RelayCommand(_ => LogText = "", _ => true);

            StartHotkeyListener();
        }

        public void OpenPathEditorInternal() => OpenPathEditor();

        private void OpenBotWindow()
        {
            try
            {
                var botVm = new BotViewModel(this, AppendLog);
                var win = new Views.BotWindow
                {
                    DataContext = botVm,
                    Owner = Application.Current?.MainWindow
                };
                win.Show();
            }
            catch (Exception ex)
            {
                AppendLog("OpenBotWindow error: " + ex.Message);
            }
        }

        private void OpenPathEditor()
        {
            try
            {
                if (!_isAttached)
                {
                    AppendLog("Attach first.");
                    return;
                }

                Func<uint, ulong, byte[], uint, bool> readFunc = (pid, addr, buf, _) => 
                {
                    return DriverRead(pid, addr, buf, out _);
                };

                Func<ulong> modBaseFunc = () => 
                {
                    ulong b = FindModuleInScanner("Ares.exe", false);
                    if (b == 0 && _pointerScanner != null)
                    {
                        _pointerScanner.RefreshModules();
                        b = FindModuleInScanner("Ares.exe", true);
                    }
                    return b;
                };

                var vm = new PathEditorViewModel(readFunc, GetPointerSize, modBaseFunc, _attachedPid);
                vm.OnRunPath += (path, loop) => 
                {
                    // Start bot with this path
                    StartBotWithPath(path, loop);
                };
                vm.OnStopBot += () => StopAllBotsInternal();

                var win = new Views.PathEditorWindow
                {
                    DataContext = vm,
                    Owner = Application.Current?.MainWindow
                };
                win.Show();
            }
            catch (Exception ex)
            {
                AppendLog("OpenPathEditor error: " + ex.Message);
            }
        }

        private void StartBotWithPath(List<DriverScanTester.Services.Waypoint> path, bool loop)
        {
            // 1. Stop existing
            if (_isMovementBotRunning || _isHealManaBotRunning)
            {
                _movementBotCts?.Cancel();
                _healManaBotCts?.Cancel();
                _isMovementBotRunning = false;
                _isHealManaBotRunning = false;
            }

            // 2. Resolve base
            ulong baseAddr = FindModuleInScanner("Ares.exe", false);
            if (baseAddr == 0)
            {
                _pointerScanner?.RefreshModules();
                baseAddr = FindModuleInScanner("Ares.exe", true);
            }

            // 3. Init MovementSystem with path
            if (!float.TryParse(BotTargetXText, out float tx)) tx = 0;
            if (!float.TryParse(BotTargetYText, out float ty)) ty = 0;

            var initialMode = path.Count > 0 ? path[0].Mode : Services.BotMode.OnlyMove;

            var memoryService = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
            _movementSystem = new MovementSystem(memoryService, AppendLog, tx, ty, SelectedPrecision, path, initialMode, loop);
            
            _movementBotCts = new CancellationTokenSource();
            _isMovementBotRunning = true;
            var token = _movementBotCts.Token;

            Task.Run(() => MovementBotLoop(token), token);
            AppendLog($"Bot started with Custom Route ({path.Count} points). Loop: {loop}");

            // 4. Start Heal/Mana
            _healManaSystem = new HealManaSystem(memoryService, AppendLog);
            _healManaBotCts = new CancellationTokenSource();
            _isHealManaBotRunning = true;
            var hmToken = _healManaBotCts.Token;
            Task.Run(() => HealManaBotLoop(hmToken), hmToken);
        }

        public void ShowSearchAddressWindow()
        {
            try
            {
                if (!_isAttached)
                {
                    AppendLog("Attach first.");
                    return;
                }

                var vm = new SearchAddressViewModel(this);
                var win = new Views.SearchAddressWindow
                {
                    DataContext = vm,
                    Owner = Application.Current?.MainWindow
                };
                win.Show();
            }
            catch (Exception ex)
            {
                AppendLog("ShowSearchAddressWindow error: " + ex.Message);
            }
        }

        public void ShowHexWindow()
        {
            try
            {
                if (!_isAttached)
                {
                    AppendLog("Attach first.");
                    return;
                }

                // Create the VM, pass driver read + logger
                var hexVm = new DriverScanTester.ViewModels.HexViewModel(DriverRead, AppendLog)
                {
                    IsAttached = _isAttached,
                    AttachedPid = _attachedPid
                };

                // Seed at 0 by default (user can paste an address); or, if you have a "selected address", use it here.
                hexVm.InitializeAt(0UL, autoRefreshStart: true);

                var win = new DriverScanTester.Views.HexWindow
                {
                    DataContext = hexVm,
                    Title = "Hex Viewer"
                };
                win.Show();
            }
            catch (System.Exception ex)
            {
                AppendLog("ShowHexWindow exception: " + ex.Message);
            }
        }

        private bool DriverWrite(uint pid, ulong remoteAddr, byte[] src, out uint wrote)
        {
            wrote = 0;
            nint data = Marshal.AllocHGlobal(src.Length);
            try
            {
                Marshal.Copy(src, 0, data, src.Length);

                var req = new Request64
                {
                    ProcessId = pid,
                    Target = remoteAddr,
                    Buffer = (ulong)data.ToInt64(),
                    Size = (uint)src.Length,
                    ReturnSize = 0
                };

                int sz = Marshal.SizeOf<Request64>();
                nint inBuf = Marshal.AllocHGlobal(sz);
                nint outBuf = Marshal.AllocHGlobal(sz);
                try
                {
                    Marshal.StructureToPtr(req, inBuf, false);

                    if (!IoctlRaw(CTL.WRITE, inBuf, sz, outBuf, sz, out uint _, out int err))
                    {
                        AppendLog($"WRITE ioctl failed: {err}");
                        return false;
                    }

                    var rsp = Marshal.PtrToStructure<Request64>(outBuf);
                    wrote = (uint)rsp.ReturnSize;
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        public void ShowLockWindow()
        {
            try
            {
                if (!_isAttached)
                {
                    AppendLog("Attach first.");
                    return;
                }

                var lockVm = new LockingValuesViewModel(DriverRead, DriverWrite, AppendLog, GetPointerSize, (name) =>
                {
                    // Strategy 1: Use the robust PointerScanner (Driver-backed)
                    if (_pointerScanner != null)
                    {
                        ulong found = FindModuleInScanner(name, false);
                        if (found != 0) return found;

                        // Not found? Force a refresh of the module list (maybe it loaded late)
                        _pointerScanner.RefreshModules();
                        found = FindModuleInScanner(name, true);
                        if (found != 0) return found;
                    }
                    
                    // Strategy 2: Fallback to standard API (unlikely to work if Scanner failed, but safe fallback)
                    var map = DriverScanTester.Memory.ModuleMap.Create(_attachedPid);
                    if (map.TryFindModuleByName(name, out var mod) && mod != null)
                        return mod.BaseAddress;

                    AppendLog($"Warning: Could not find module base for '{name}'. Check spelling or attachment.");
                    return 0;
                })
                {
                    IsAttached = _isAttached,
                    AttachedPid = _attachedPid
                };

                // Pre-load default entries if the list is empty
                if (lockVm.Entries.Count == 0)
                {
                    lockVm.Entries.Add(new LockAddressEntryViewModel(
                        () => lockVm.IsAttached,
                        () => lockVm.AttachedPid,
                        DriverRead,
                        DriverWrite,
                        GetPointerSize,
                        (name) =>
                        {
                            if (_pointerScanner != null)
                            {
                                ulong found = FindModuleInScanner(name, false);
                                if (found != 0) return found;
                                _pointerScanner.RefreshModules();
                                return FindModuleInScanner(name, true);
                            }
                            var map = DriverScanTester.Memory.ModuleMap.Create(_attachedPid);
                            if (map.TryFindModuleByName(name, out var mod) && mod != null)
                                return mod.BaseAddress;
                            return 0;
                        },
                        AppendLog)
                    {
                        NameText = "Camera Vertical Angle",
                        AddressText = "[Ares.exe + 0x4704B0] + 0x1BE",
                        ValueType = LockValueType.Short,
                        ValueText = "16320"
                    });
                }

                var page = new DriverScanTester.Views.LockWindow { DataContext = lockVm };
                var nav = new System.Windows.Navigation.NavigationWindow
                {
                    Title = "Lock Values",
                    Content = page,
                    Width = 800,
                    Height = 500,
                    ShowsNavigationUI = false
                };

                // Global F1 hotkey – unlocks all entries regardless of window focus.
                // Use MainWindow (not NavigationWindow) for the HWND so that the HwndSource
                // hook reliably intercepts WM_HOTKEY even when the lock window is not focused.
                var globalF1 = new GlobalHotKey(
                    Application.Current?.MainWindow ?? (Window)nav,
                    key: 0x70 /* F1 */, modifiers: 0, id: 9000);
                globalF1.HotKeyPressed += () =>
                {
                    if (lockVm.UnlockAllCommand.CanExecute(null))
                        Application.Current?.Dispatcher.Invoke(() => lockVm.UnlockAllCommand.Execute(null));
                };

                nav.Closed += (_, _) =>
                {
                    lockVm.IsAttached = false;
                    globalF1.Dispose();
                };
                nav.Show();
            }
            catch (Exception ex)
            {
                AppendLog("ShowLockWindow exception: " + ex.Message);
            }
        }

        private ulong FindModuleInScanner(string name, bool isRetry)
        {
            if (_pointerScanner == null) return 0;
            var modules = _pointerScanner.Modules;

            // 1. Exact match
            foreach (var m in modules)
            {
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    return m.BaseAddress;
            }

            // 2. Smart match (ignore extension)
            string nameNoExt = name;
            bool hasExt = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || 
                          name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            
            if (hasExt)
                nameNoExt = System.IO.Path.GetFileNameWithoutExtension(name);

            foreach (var m in modules)
            {
                // If input was "Game.exe" and module is "Game", match.
                if (hasExt && string.Equals(m.Name, nameNoExt, StringComparison.OrdinalIgnoreCase))
                    return m.BaseAddress;

                // If input was "Game" and module is "Game.exe", match.
                if (!hasExt && string.Equals(m.Name, name + ".exe", StringComparison.OrdinalIgnoreCase))
                    return m.BaseAddress;
            }
            
            return 0;
        }

        private void ShowMatches()
        {
            try
            {
                if (!_isAttached || _candidates.Count == 0)
                {
                    AppendLog("No candidates to show.");
                    return;
                }

                var addresses = MatchesUiHelper.ExpandCandidateBitmaps(_candidates, ElemSize);
                if (addresses.Count == 0) { AppendLog("No set bits in candidates; nothing to show."); return; }

                Func<nuint, (bool ok, string value)> reader = p =>
                {
                    if (IsByte)
                    {
                        var buf = new byte[1];
                        bool ok = DriverRead(_attachedPid, p.ToUInt64(), buf, out uint got) && got == 1;
                        return (ok, ok ? buf[0].ToString() : string.Empty);
                    }
                    else if (IsShort)
                    {
                        var buf = new byte[2];
                        bool ok = DriverRead(_attachedPid, p.ToUInt64(), buf, out uint got) && got == 2;
                        short s = ok ? BitConverter.ToInt16(buf, 0) : (short)0;
                        return (ok, ok ? s.ToString() : string.Empty);
                    }
                    else if (IsCLong) // NEW: 32-bit
                    {
                        var buf = new byte[4];
                        bool ok = DriverRead(_attachedPid, p.ToUInt64(), buf, out uint got) && got == 4;
                        int v = ok ? BitConverter.ToInt32(buf, 0) : 0;
                        return (ok, ok ? v.ToString() : string.Empty);
                    }
                    else // IsLong (64-bit)
                    {
                        var buf = new byte[8];
                        bool ok = DriverRead(_attachedPid, p.ToUInt64(), buf, out uint got) && got == 8;
                        long v = ok ? BitConverter.ToInt64(buf, 0) : 0L;
                        return (ok, ok ? v.ToString() : string.Empty);
                    }
                };

                // existing helper likely expects "isByte" for display width/layout; pass IsByte to preserve UI
                MatchesUiHelper.ShowMatchesWindow(addresses, reader, IsByte, LaunchPointerScan);

            }
            catch (Exception ex)
            {
                AppendLog("ShowMatches exception: " + ex.Message);
            }
        }

        private void LaunchPointerScan(UIntPtr address)
        {
            if (address == UIntPtr.Zero)
            {
                return;
            }

            ShowPointerScanDialog(address.ToUInt64());
        }

        private void OpenPointerScanFromMain()
        {
            if (!_isAttached)
            {
                AppendLog("Attach first.");
                return;
            }

            if (_pointerScanner == null)
            {
                AppendLog("Pointer scan not available. Attach first.");
                return;
            }

            if (!TryParseAddress(PointerTargetText, out ulong target))
            {
                AppendLog("Enter a pointer target address (e.g. 0x1234).");
                return;
            }

            if (!TryResolveScanWindow(out var window))
            {
                return;
            }

            ShowPointerScanDialog(target, window);
        }

        private void ShowPointerScanDialog(ulong address, ScanWindow window = default)
        {
            if (!_isAttached || _pointerScanner == null)
            {
                AppendLog("Pointer scan not available. Attach first.");
                return;
            }

            var (suggestStart, suggestEnd) = PointerScanner.SuggestTightRangeAround(address);
            if (window.Enabled)
            {
                suggestStart = window.Start;
                suggestEnd = window.EndExclusive;
            }

            var vm = new PointerScanViewModel(_pointerScanner)
            {
                TargetAddressText = $"0x{address:X}",
                RangeStartText = $"0x{suggestStart:X}",
                RangeEndText = $"0x{suggestEnd:X}"
            };

            var dlg = new PointerScanWindow(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            dlg.Show();

        }

        // ========================= Actions =========================

        private void Attach()
        {
            try
            {
                _hDevice?.Dispose();
                _hDevice = null;
                if (_hProcess != nint.Zero) { PsApi.CloseHandle(_hProcess); _hProcess = nint.Zero; }
                _isAttached = false;
                DisposePointerScanner();
                InvalidateCommands();

                _userSnapshot.Clear();
                _candidates.Clear();
                _managedBaseline.Clear();
                _driverSupportsCLong = true;
                _loggedClongFallback = false;

                if (!TryParsePid(out uint pid)) return;

                _hDevice = CreateFile(DEVICE_PATH,
                    Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    nint.Zero,
                    Native.OPEN_EXISTING,
                    Native.FILE_ATTRIBUTE_NORMAL,
                    nint.Zero);

                if (_hDevice.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    AppendLog($"CreateFile failed: {err}");
                    _hDevice.Dispose();
                    _hDevice = null;
                    return;
                }

                _hProcess = PsApi.OpenProcess(PsApi.PROCESS_QUERY_INFORMATION | PsApi.PROCESS_VM_READ, false, pid);
                if (_hProcess == nint.Zero)
                {
                    _hProcess = PsApi.OpenProcess(PsApi.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                }

                if (_hProcess == nint.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    AppendLog($"OpenProcess failed: {err}");
                    _hDevice?.Dispose();
                    _hDevice = null;
                    return;
                }

                _attachedPid = pid;
                _isAttached = true;
                AppendLog($"Attached to device. PID={_attachedPid}");
                InitializePointerScanner();
                InvalidateCommands();
            }
            catch (Exception ex)
            {
                AppendLog("Attach exception: " + ex.Message);
            }
        }

        private void InitializePointerScanner()
        {
            DisposePointerScanner();

            if (_hProcess == nint.Zero || !_isAttached)
            {
                return;
            }

            try
            {
                int pointerSize;
                if (IsCLong)
                {
                    pointerSize = 4;
                }
                else if (IsLong)
                {
                    pointerSize = 8;
                }
                else
                {
                    pointerSize = PointerScanner.DetectPointerSize(_hProcess, _attachedPid);
                }

                _pointerReader = new DelegateMemoryReader(pointerSize, (address, buffer) =>
                {
                    bool ok = DriverRead(_attachedPid, address, buffer, out uint got);
                    return (ok && got > 0, (int)got);
                });
                _pointerScanner = new PointerScanner(_hProcess, _attachedPid, _pointerReader, AppendLog);
                AppendLog($"Pointer scanner ready ({pointerSize * 8}-bit) using Driver.");
                InvalidateCommands();
            }
            catch (Exception ex)
            {
                AppendLog("Pointer scanner unavailable: " + ex.Message);
                DisposePointerScanner();
            }
        }

        private void DisposePointerScanner()
        {
            _pointerScanner = null;
            if (_pointerReader != null)
            {
                _pointerReader.Dispose();
                _pointerReader = null;
            }
            InvalidateCommands();
        }

        // ---------- FIRST SCAN ----------
        private void FirstScan()
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }

            if (!TryResolveScanWindow(out var window))
            {
                return;
            }

            _candidates.Clear(); // start fresh
            _managedBaseline.Clear();

            if (IsByte)
            {
                if (TryParseByte(ValueScanText, out byte b))
                    FirstScan_ValueEquals_Any(b, 0, 0, 0, haveShort: false, haveCLong: false, haveLong: false, window);
                else
                    FirstScan_UnknownSnapshotAll(window);
            }
            else if (IsShort)
            {
                if (TryParseShort(ValueScanText, out short s))
                    FirstScan_ValueEquals_Any(0, s, 0, 0, haveShort: true, haveCLong: false, haveLong: false, window);
                else
                    FirstScan_UnknownSnapshotAll(window);
            }
            else if (IsCLong) // NEW: 32-bit
            {
                if (TryParseInt32(ValueScanText, out int i))
                    FirstScan_ValueEquals_Any(0, 0, i, 0, haveShort: false, haveCLong: true, haveLong: false, window);
                else
                    FirstScan_UnknownSnapshotAll(window);
            }
            else // IsLong (64-bit)
            {
                if (TryParseLong(ValueScanText, out long l))
                    FirstScan_ValueEquals_Any(0, 0, 0, l, haveShort: false, haveCLong: false, haveLong: true, window);
                else
                    FirstScan_UnknownSnapshotAll(window);
            }

            // After the first scan, set the "last-scan" baseline so all following filters compare vs last.
            RefreshBaselineFromCandidates();
        }

        private void FirstScan_ValueEquals_Any(byte targetByte, short targetShort, int targetCLong, long targetLong, bool haveShort, bool haveCLong, bool haveLong, ScanWindow window)
        {
            if (IsCLong && !_driverSupportsCLong)
            {
                ManualFirstScanEqualsInt32(targetCLong, window);
                return;
            }

            string what =
                IsByte ? $"byte == {targetByte}" :
                IsShort ? $"short == {unchecked((ushort)targetShort)}" :
                IsCLong ? $"clong (32-bit) == {targetCLong}" :
                $"long (64-bit) == {targetLong}";
            AppendLog($"First Scan ({what}) over all filtered readable memory…");

            long totalMatches = 0;
            int elemSize = ElemSize;
            uint elemFlag = ElemFlag;

            ulong maxChunkBytes = (ulong)MAX_BITMAP_BYTES * 8UL * (ulong)elemSize;
            ulong maxPagesPerChunk = Math.Max(1UL, maxChunkBytes / PAGE_SIZE);

            foreach (var (baseAddr, size) in EnumerateReadableRegions(window))
            {
                ulong regionStart = baseAddr;
                ulong regionEnd = baseAddr + size;

                ulong cur = regionStart;
                while (cur < regionEnd)
                {
                    ulong bytesRemaining = regionEnd - cur;
                    ulong pagesRemaining = bytesRemaining / PAGE_SIZE;

                    ulong pagesThisChunk = Math.Min(pagesRemaining, maxPagesPerChunk);
                    if (pagesThisChunk == 0) pagesThisChunk = 1;

                    uint chunkBytes = (uint)(pagesThisChunk * PAGE_SIZE);

                    var args = new ScanArgs
                    {
                        ProcessId = _attachedPid,
                        Address = cur,
                        Length = chunkBytes,
                        Flags = elemFlag | Flags.SCAN_PRED_EQ,
                        TargetByte = targetByte,
                        TargetShort = targetShort,
                        TargetLong = targetLong,
                        TargetCLong = targetCLong
                    };

                    int elemCount = (int)(chunkBytes / elemSize);
                    int bitmapLen = (elemCount + 7) / 8;
                    byte[] bitmap = new byte[bitmapLen];

                    unsafe
                    {
                        if (!IoctlInOut(CTL.SCAN, args, sizeof(ScanArgs), bitmap, bitmapLen, out uint _, out int err))
                        {
                            if (IsCLong && err == ERROR_INVALID_PARAMETER)
                                goto FallbackToManaged;

                            AppendLog($"SCAN failed @ 0x{cur:X}: {err}");
                            return;
                        }
                    }

                    ApplyRunBitmapToPagesIntersectOrSeed(
                        runBase: cur,
                        runBitmap: bitmap,
                        runLenBytes: chunkBytes,
                        elemSize: elemSize,
                        seedIfMissing: true,
                        intersect: false,
                        window,
                        out long addedHits);

                    totalMatches += addedHits;
                    cur += chunkBytes;
                }
            }

            AppendLog($"First Scan complete. Matches: {totalMatches}");
            PruneEmptyCandidatePages();
            return;

        FallbackToManaged:
            ActivateClongFallback();
            _candidates.Clear();
            if (haveCLong)
            {
                ManualFirstScanEqualsInt32(targetCLong, window);
            }
            else
            {
                AppendLog("SCAN failed with ERROR_INVALID_PARAMETER.");
            }
        }

        private void FirstScan_UnknownSnapshotAll(ScanWindow window)
        {
            AppendLog("First Scan (Unknown value): snapshotting all readable pages as ORIGINAL baseline…");

            if (!UseManagedBaseline)
            {
                // Reset driver cache with current element mode (for possible immediate original compare if needed)
                var init = new PrevInit { ProcessId = _attachedPid, Flags = ElemFlag };
                unsafe
                {
                    if (!IoctlInOnly(CTL.PREV_RESET, init, sizeof(PrevInit), out int errRst))
                    {
                        if (IsCLong && errRst == ERROR_INVALID_PARAMETER)
                        {
                            ActivateClongFallback();
                        }
                        else
                        {
                            AppendLog($"prev_reset failed: {errRst}");
                            return;
                        }
                    }
                }
            }
            else
            {
                _managedBaseline.Clear();
            }

            long totalBytesSnapshotted = 0;
            long totalPages = 0;

            byte[] page = new byte[PAGE_SIZE];

            foreach (var (baseAddr, size) in EnumerateReadableRegions(window))
            {
                ulong regionEnd = baseAddr + size;
                ulong pageBase = baseAddr & ~0xFFFUL;

                while (pageBase < regionEnd)
                {
                    if (!DriverRead(_attachedPid, pageBase, page, out uint got) || got < PAGE_SIZE)
                    {
                        pageBase += PAGE_SIZE;
                        continue;
                    }

                    _userSnapshot[pageBase] = (byte[])page.Clone();

                    // Seed candidates to "all ones" for these pages, sized by element
                    var bm = NewAllOnesPageBitmap(PageElemCount);
                    MaskBitmapToWindow(bm, ElemSize, PageElemCount, pageBase, window);
                    _candidates[pageBase] = bm;

                    totalPages++;
                    totalBytesSnapshotted += got;

                    pageBase += PAGE_SIZE;
                }
            }

            AppendLog($"Snapshot complete. Pages: {totalPages}, Bytes: {totalBytesSnapshotted:N0}. All filtered readable addresses are candidates.");
            PruneEmptyCandidatePages();
        }

        private void ManualFirstScanEqualsInt32(int targetCLong, ScanWindow window)
        {
            long totalMatches = 0;
            const int elemSize = 4;
            int pageElemCount = PAGE_SIZE / elemSize;
            byte[] page = new byte[PAGE_SIZE];

            foreach (var (baseAddr, size) in EnumerateReadableRegions(window))
            {
                ulong regionEnd = baseAddr + size;
                ulong pageBase = baseAddr & ~0xFFFUL;

                while (pageBase < regionEnd)
                {
                    if (!DriverRead(_attachedPid, pageBase, page, out uint got) || got < PAGE_SIZE)
                    {
                        pageBase += PAGE_SIZE;
                        continue;
                    }

                    byte[] bitmap = new byte[PageBitmapBytes];

                    for (int elem = 0; elem < pageElemCount; elem++)
                    {
                        int offset = elem * elemSize;
                        int value = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(offset));
                        if (value == targetCLong)
                        {
                            SetBitmapBit(bitmap, elem);
                        }
                    }

                    MaskBitmapToWindow(bitmap, elemSize, pageElemCount, pageBase, window);
                    int hits = CountBits(bitmap, pageElemCount);
                    if (hits > 0)
                    {
                        _candidates[pageBase] = bitmap;
                        totalMatches += hits;
                    }

                    pageBase += PAGE_SIZE;
                }
            }

            AppendLog($"First Scan complete. Matches: {totalMatches}");
            PruneEmptyCandidatePages();
        }

        private long ManagedCachedCompareAndIntersect(uint predicateFlag, ScanWindow window)
        {
            var pages = new List<ulong>(_candidates.Keys);
            if (pages.Count == 0) return 0;

            const int elemSize = 4;
            int pageElemCount = PAGE_SIZE / elemSize;
            byte[] current = new byte[PAGE_SIZE];

            foreach (var pageBase in pages)
            {
                if (!_candidates.TryGetValue(pageBase, out var bm) || bm == null)
                    continue;

                if (!DriverRead(_attachedPid, pageBase, current, out uint got) || got != PAGE_SIZE ||
                    !_managedBaseline.TryGetValue(pageBase, out var baseline) || baseline.Length != PAGE_SIZE)
                {
                    Array.Clear(bm, 0, bm.Length);
                    continue;
                }

                for (int elem = 0; elem < pageElemCount; elem++)
                {
                    int byteIndex = elem >> 3;
                    int bitMask = 1 << (elem & 7);
                    if ((bm[byteIndex] & bitMask) == 0)
                        continue;

                    int currentValue = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(elem * elemSize));
                    int previousValue = BinaryPrimitives.ReadInt32LittleEndian(baseline.AsSpan(elem * elemSize));

                    bool keep = predicateFlag switch
                    {
                        Flags.SCAN_PRED_EQ => currentValue == previousValue,
                        Flags.SCAN_PRED_NE => currentValue != previousValue,
                        Flags.SCAN_PRED_GT => currentValue > previousValue,
                        Flags.SCAN_PRED_LT => currentValue < previousValue,
                        _ => false
                    };

                    if (!keep)
                    {
                        bm[byteIndex] &= (byte)~bitMask;
                    }
                }

                MaskBitmapToWindow(bm, elemSize, pageElemCount, pageBase, window);
            }

            return CountAllCandidateBits();
        }

        private long ManagedScanEqualsAndIntersect(int targetCLong, bool invertForNE, ScanWindow window)
        {
            var pages = new List<ulong>(_candidates.Keys);
            if (pages.Count == 0) return 0;

            const int elemSize = 4;
            int pageElemCount = PAGE_SIZE / elemSize;
            byte[] current = new byte[PAGE_SIZE];

            foreach (var pageBase in pages)
            {
                if (!_candidates.TryGetValue(pageBase, out var bm) || bm == null)
                    continue;

                if (!DriverRead(_attachedPid, pageBase, current, out uint got) || got != PAGE_SIZE)
                {
                    Array.Clear(bm, 0, bm.Length);
                    continue;
                }

                for (int elem = 0; elem < pageElemCount; elem++)
                {
                    int byteIndex = elem >> 3;
                    int bitMask = 1 << (elem & 7);
                    if ((bm[byteIndex] & bitMask) == 0)
                        continue;

                    int currentValue = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(elem * elemSize));
                    bool match = currentValue == targetCLong;
                    if (invertForNE) match = !match;

                    if (!match)
                    {
                        bm[byteIndex] &= (byte)~bitMask;
                    }
                }

                MaskBitmapToWindow(bm, elemSize, pageElemCount, pageBase, window);
            }

            return CountAllCandidateBits();
        }

        // ---------- Progressive refinement (always vs LAST SCAN) ----------
        private void CachedCompare(bool eq)
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_candidates.Count == 0)
            {
                AppendLog("No candidates yet. Run First Scan.");
                return;
            }

            string title = eq ? "Not Changed (== last)" : "Changed (!= last)";
            AppendLog($"{title}: refining current candidates…");

            // DO NOT refresh baseline here. We want to compare NOW vs PREVIOUS.

            uint predFlag = eq ? Flags.SCAN_PRED_EQ : Flags.SCAN_PRED_NE;
            long totalMatches = BatchedCachedCompareAndIntersect(predFlag);

            AppendLog($"{title}: Matches: {totalMatches}");
            PruneEmptyCandidatePages();

            // Advance the baseline AFTER the compare to make next step "vs this scan"
            RefreshBaselineFromCandidates();
        }

        private void FilterRelative(RelOp op)
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_candidates.Count == 0)
            {
                AppendLog("No candidates yet. Run First Scan first.");
                return;
            }

            string title = op == RelOp.Increased ? "Increased (> last)" : "Decreased (< last)";
            AppendLog($"{title}: refining current candidates…");

            // DO NOT refresh baseline here.

            uint predFlag = op == RelOp.Increased ? Flags.SCAN_PRED_GT : Flags.SCAN_PRED_LT;
            long totalMatches = BatchedCachedCompareAndIntersect(predFlag);

            AppendLog($"{title}: Matches: {totalMatches}");
            PruneEmptyCandidatePages();

            // Move baseline forward AFTER filtering
            RefreshBaselineFromCandidates();
        }

        // ---------- Sub-scan equals / not-equals constant (batched, still vs LAST) ----------
        private void SubScanEqualsConstant()
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_candidates.Count == 0) { AppendLog("No candidates yet. Run First Scan first."); return; }

            // DO NOT refresh baseline here.

            if (IsByte)
            {
                if (!TryParseByte(ValueScanText, out byte b)) { AppendLog("Enter a byte value (e.g. 123 or 0x7B)."); return; }
                AppendLog($"Sub Scan (byte == {b}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(b, 0, 0, 0, haveShort: false, haveCLong: false, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan (== {b}) complete. Matches: {totalMatches}");
            }
            else if (IsShort)
            {
                if (!TryParseShort(ValueScanText, out short s)) { AppendLog("Enter a short value (e.g. 12345 or 0x3039)."); return; }
                AppendLog($"Sub Scan (short == {unchecked((ushort)s)}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, s, 0, 0, haveShort: true, haveCLong: false, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan (== {unchecked((ushort)s)}) complete. Matches: {totalMatches}");
            }
            else if (IsCLong)
            {
                if (!TryParseInt32(ValueScanText, out int i)) { AppendLog("Enter a 32-bit value (e.g. 123456 or 0x1E240)."); return; }
                AppendLog($"Sub Scan (clong == {i}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, i, 0, haveShort: false, haveCLong: true, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan (== {i}) complete. Matches: {totalMatches}");
            }
            else // IsLong (64-bit)
            {
                if (!TryParseLong(ValueScanText, out long l)) { AppendLog("Enter a 64-bit value (e.g. 123456789 or 0x75BCD15)."); return; }
                AppendLog($"Sub Scan (long64 == {l}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, 0, l, haveShort: false, haveCLong: false, haveLong: true, invertForNE: false);
                AppendLog($"Sub Scan (== {l}) complete. Matches: {totalMatches}");
            }
            PruneEmptyCandidatePages();

            // Baseline forward
            RefreshBaselineFromCandidates();
        }

        private void SubScanNotEqualsConstant()
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_candidates.Count == 0) { AppendLog("No candidates yet. Run First Scan first."); return; }

            // DO NOT refresh baseline here.

            if (IsByte)
            {
                if (!TryParseByte(ValueScanText, out byte b)) { AppendLog("Enter a byte value (e.g. 123 or 0x7B)."); return; }
                AppendLog($"Sub Scan (byte != {b}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(b, 0, 0, 0, haveShort: false, haveCLong: false, haveLong: false, invertForNE: true);
                AppendLog($"Sub Scan (!= {b}) complete. Matches: {totalMatches}");
            }
            else if (IsShort)
            {
                if (!TryParseShort(ValueScanText, out short s)) { AppendLog("Enter a short value (e.g. 12345 or 0x3039)."); return; }
                AppendLog($"Sub Scan (short != {unchecked((ushort)s)}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, s, 0, 0, haveShort: true, haveCLong: false, haveLong: false, invertForNE: true);
                AppendLog($"Sub Scan (!= {unchecked((ushort)s)}) complete. Matches: {totalMatches}");
            }
            else if (IsCLong)
            {
                if (!TryParseInt32(ValueScanText, out int i)) { AppendLog("Enter a 32-bit value (e.g. 123456 or 0x1E240)."); return; }
                AppendLog($"Sub Scan (clong != {i}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, i, 0, haveShort: false, haveCLong: true, haveLong: false, invertForNE: true);
                AppendLog($"Sub Scan (!= {i}) complete. Matches: {totalMatches}");
            }
            else // IsLong (64-bit)
            {
                if (!TryParseLong(ValueScanText, out long l)) { AppendLog("Enter a 64-bit value (e.g. 123456789 or 0x75BCD15)."); return; }
                AppendLog($"Sub Scan (long64 != {l}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, 0, l, haveShort: false, haveCLong: false, haveLong: true, invertForNE: true);
                AppendLog($"Sub Scan (!= {l}) complete. Matches: {totalMatches}");
            }
            PruneEmptyCandidatePages();

            // Baseline forward
            RefreshBaselineFromCandidates();
        }

        private void SubScan1()
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_candidates.Count == 0) { AppendLog("No candidates yet. Run First Scan first."); return; }

            if (IsByte)
            {
                if (!TryParseByte(SubScan1Text, out byte b)) { AppendLog("Enter a byte value for Sub1 (e.g. 123 or 0x7B)."); return; }
                AppendLog($"Sub Scan1 (byte == {b}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(b, 0, 0, 0, haveShort: false, haveCLong: false, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan1 (== {b}) complete. Matches: {totalMatches}");
            }
            else if (IsShort)
            {
                if (!TryParseShort(SubScan1Text, out short s)) { AppendLog("Enter a short value for Sub1 (e.g. 12345 or 0x3039)."); return; }
                AppendLog($"Sub Scan1 (short == {unchecked((ushort)s)}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, s, 0, 0, haveShort: true, haveCLong: false, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan1 (== {unchecked((ushort)s)}) complete. Matches: {totalMatches}");
            }
            else if (IsCLong)
            {
                if (!TryParseInt32(SubScan1Text, out int i)) { AppendLog("Enter a 32-bit value for Sub1 (e.g. 123456 or 0x1E240)."); return; }
                AppendLog($"Sub Scan1 (clong == {i}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, i, 0, haveShort: false, haveCLong: true, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan1 (== {i}) complete. Matches: {totalMatches}");
            }
            else
            {
                if (!TryParseLong(SubScan1Text, out long l)) { AppendLog("Enter a 64-bit value for Sub1 (e.g. 123456789 or 0x75BCD15)."); return; }
                AppendLog($"Sub Scan1 (long64 == {l}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, 0, l, haveShort: false, haveCLong: false, haveLong: true, invertForNE: false);
                AppendLog($"Sub Scan1 (== {l}) complete. Matches: {totalMatches}");
            }
            PruneEmptyCandidatePages();
            RefreshBaselineFromCandidates();
        }

        private void SubScan2()
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_candidates.Count == 0) { AppendLog("No candidates yet. Run First Scan first."); return; }

            if (IsByte)
            {
                if (!TryParseByte(SubScan2Text, out byte b)) { AppendLog("Enter a byte value for Sub2 (e.g. 123 or 0x7B)."); return; }
                AppendLog($"Sub Scan2 (byte == {b}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(b, 0, 0, 0, haveShort: false, haveCLong: false, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan2 (== {b}) complete. Matches: {totalMatches}");
            }
            else if (IsShort)
            {
                if (!TryParseShort(SubScan2Text, out short s)) { AppendLog("Enter a short value for Sub2 (e.g. 12345 or 0x3039)."); return; }
                AppendLog($"Sub Scan2 (short == {unchecked((ushort)s)}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, s, 0, 0, haveShort: true, haveCLong: false, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan2 (== {unchecked((ushort)s)}) complete. Matches: {totalMatches}");
            }
            else if (IsCLong)
            {
                if (!TryParseInt32(SubScan2Text, out int i)) { AppendLog("Enter a 32-bit value for Sub2 (e.g. 123456 or 0x1E240)."); return; }
                AppendLog($"Sub Scan2 (clong == {i}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, i, 0, haveShort: false, haveCLong: true, haveLong: false, invertForNE: false);
                AppendLog($"Sub Scan2 (== {i}) complete. Matches: {totalMatches}");
            }
            else
            {
                if (!TryParseLong(SubScan2Text, out long l)) { AppendLog("Enter a 64-bit value for Sub2 (e.g. 123456789 or 0x75BCD15)."); return; }
                AppendLog($"Sub Scan2 (long64 == {l}) vs last: refining current candidates…");
                long totalMatches = BatchedScanEqualsAndIntersect_Any(0, 0, 0, l, haveShort: false, haveCLong: false, haveLong: true, invertForNE: false);
                AppendLog($"Sub Scan2 (== {l}) complete. Matches: {totalMatches}");
            }
            PruneEmptyCandidatePages();
            RefreshBaselineFromCandidates();
        }

        // ---------- SPECIAL: Compare WITH ORIGINAL once ----------
        private void CompareWithOriginal()
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_candidates.Count == 0) { AppendLog("No candidates yet. Run First Scan first."); return; }
            if (_userSnapshot.Count == 0)
            {
                AppendLog("No ORIGINAL snapshot available. Run First Scan (Unknown value) first or snapshot a page.");
                return;
            }

            AppendLog("Same as Original (== original): refining current candidates…");

            // Load ORIGINAL into driver's cached baseline
            if (!RestoreBaselineFromOriginal())
            {
                AppendLog("Failed to restore ORIGINAL baseline to driver.");
                return;
            }

            long totalMatches = BatchedCachedCompareAndIntersect(Flags.SCAN_PRED_EQ);
            AppendLog($"Same as Original: Matches: {totalMatches}");
            PruneEmptyCandidatePages();

            // IMPORTANT: switch back to "last scan" mode by refreshing baseline to NOW
            RefreshBaselineFromCandidates();
        }

        // ========================= Helpers =========================

        private void ActivateClongFallback()
        {
            _driverSupportsCLong = false;
            _managedBaseline.Clear();

            if (!_loggedClongFallback)
            {
                AppendLog("Driver rejected 32-bit scan requests. Falling back to managed scanning.");
                _loggedClongFallback = true;
            }
        }

        private IEnumerable<(ulong Base, ulong Size)> EnumerateReadableRegions(ScanWindow window)
        {
            if (_hProcess == nint.Zero) yield break;

            ulong addr = window.Enabled ? window.Start : 0UL;
            ulong limit = window.Enabled ? window.EndExclusive : ulong.MaxValue;
            int mbiSize = Marshal.SizeOf<PsApi.MEMORY_BASIC_INFORMATION64>();

            while (addr < limit)
            {
                if (PsApi.VirtualQueryEx(_hProcess, (nuint)addr, out var mbi, (nuint)mbiSize) == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (addr == (window.Enabled ? window.Start : 0UL))
                    {
                        AppendLog($"VirtualQueryEx failed at start address 0x{addr:X}: {err}");
                    }
                    break;
                }

                ulong regionStart = mbi.BaseAddress;
                ulong regionEnd = regionStart + mbi.RegionSize;

                if (window.Enabled)
                {
                    if (regionEnd <= window.Start)
                    {
                        addr = regionEnd;
                        continue;
                    }

                    if (regionStart >= window.EndExclusive)
                    {
                        break;
                    }

                    ulong trimmedStart = Math.Max(regionStart, window.Start);
                    ulong trimmedEnd = Math.Min(regionEnd, window.EndExclusive);
                    if (trimmedEnd <= trimmedStart)
                    {
                        addr = regionEnd;
                        continue;
                    }

                    regionStart = Math.Max(regionStart, AlignDown(trimmedStart, PAGE_SIZE));
                    regionEnd = Math.Min(regionEnd, AlignUp(trimmedEnd, PAGE_SIZE));

                    if (regionEnd <= regionStart)
                    {
                        addr = regionEnd;
                        continue;
                    }
                }

                if (IsLikelyPlayerDataRegion(mbi))
                    yield return (regionStart, regionEnd - regionStart);

                ulong next = mbi.BaseAddress + mbi.RegionSize;
                if (next <= addr) break;
                addr = window.Enabled ? Math.Max(next, window.Start) : next;
            }
        }

        private bool TryResolveScanWindow(out ScanWindow window)
        {
            window = default;

            string centerText = RangeCenterText?.Trim() ?? string.Empty;
            string radiusText = RangeRadiusText?.Trim() ?? string.Empty;

            bool hasCenter = !string.IsNullOrEmpty(centerText);
            bool hasRadius = !string.IsNullOrEmpty(radiusText);

            if (!hasCenter && !hasRadius)
            {
                return true;
            }

            if (!hasCenter || !hasRadius)
            {
                AppendLog("Specify both Center addr and ± Range to limit scans.");
                return false;
            }

            if (!TryParseAddress(centerText, out ulong center))
            {
                AppendLog("Enter a valid center address (decimal or 0xHEX).");
                return false;
            }

            if (!TryParseUnsigned(radiusText, out ulong radius))
            {
                AppendLog("Enter a valid ± range (decimal or 0xHEX).");
                return false;
            }

            ulong start = center >= radius ? center - radius : 0UL;
            ulong endInclusive;
            if (center > ulong.MaxValue - radius)
            {
                endInclusive = ulong.MaxValue;
            }
            else
            {
                endInclusive = center + radius;
            }

            ulong endExclusive = endInclusive == ulong.MaxValue ? ulong.MaxValue : endInclusive + 1UL;

            if (endExclusive <= start)
            {
                AppendLog("Range window is empty. Adjust center/range values.");
                return false;
            }

            window = new ScanWindow(start, endExclusive);
            return true;
        }

        private static bool IsWritable(PsApi.MemProtect p) =>
            (p & PsApi.MemProtect.PAGE_GUARD) == 0 &&
            (p == PsApi.MemProtect.PAGE_READWRITE ||
             p == PsApi.MemProtect.PAGE_WRITECOPY ||
             p == PsApi.MemProtect.PAGE_EXECUTE_READWRITE ||
             p == PsApi.MemProtect.PAGE_EXECUTE_WRITECOPY);

        private static bool IsExecutable(PsApi.MemProtect p) =>
            p == PsApi.MemProtect.PAGE_EXECUTE ||
            p == PsApi.MemProtect.PAGE_EXECUTE_READ ||
            p == PsApi.MemProtect.PAGE_EXECUTE_READWRITE ||
            p == PsApi.MemProtect.PAGE_EXECUTE_WRITECOMBINE ||
            p == PsApi.MemProtect.PAGE_EXECUTE_WRITECOPY;

        private static bool IsLikelyPlayerDataRegion(in PsApi.MEMORY_BASIC_INFORMATION64 mbi)
        {
            bool committed = mbi.State == PsApi.MemState.MEM_COMMIT;
            bool privateMem = mbi.Type == 0 || mbi.Type == 0x20000 /* MEM_PRIVATE */;
            bool writable = IsWritable(mbi.Protect);
            bool executable = IsExecutable(mbi.Protect);
            bool writeCombine = (mbi.Protect & PsApi.MemProtect.PAGE_WRITECOMBINE) != 0;
            return committed && privateMem && writable && !executable && !writeCombine && mbi.RegionSize != 0;
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            LogText += $"[{stamp}] {line}\r\n";
        }

        private void LogUnsupported(string what) =>
            AppendLog($"{what} not supported by this driver build.");

        private static void InvalidateCommands() => CommandManager.InvalidateRequerySuggested();

        private bool TryParsePid(out uint pid)
        {
            pid = 0;
            try
            {
                var t = (PidText ?? "").Trim();
                if (!string.IsNullOrEmpty(t) && uint.TryParse(t, out pid))
                    return true;

                // Try common variations of the target process name
                var searchNames = new[] { "ares", "Ares", "Ares.exe" };
                Process[]? procs = null;

                foreach (var name in searchNames)
                {
                    procs = Process.GetProcessesByName(name.EndsWith(".exe") ? name[..^4] : name);
                    if (procs != null && procs.Length > 0) break;
                }

                if (procs == null || procs.Length == 0)
                {
                    AppendLog("Process 'ares' not found and no PID entered.");
                    return false;
                }
                pid = (uint)procs[0].Id;
                PidText = pid.ToString();
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("Error finding PID: " + ex.Message);
                return false;
            }
        }

        private bool TryParseAddress(string text, out ulong value) => TryParseUnsigned(text, out value);

        private bool TryParseUnsigned(string text, out ulong value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ulong.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            return ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseByte(string s, out byte b)
        {
            b = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return byte.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out b);
            if (int.TryParse(s, out int iv) && iv >= 0 && iv <= 255) { b = (byte)iv; return true; }
            return false;
        }

        private static bool TryParseShort(string s, out short sh)
        {
            sh = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ushort.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort u))
                {
                    sh = unchecked((short)u);
                    return true;
                }
                return false;
            }
            return short.TryParse(s, out sh);
        }

        private static bool TryParseInt32(string s, out int i) // NEW: for C long (32-bit)
        {
            i = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out uint u))
                {
                    i = unchecked((int)u);
                    return true;
                }
                return false;
            }
            return int.TryParse(s, out i);
        }

        private static bool TryParseLong(string s, out long l)
        {
            l = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var u))
                {
                    l = unchecked((long)u);
                    return true;
                }
                return false;
            }
            return long.TryParse(s, out l);
        }

        // ---------- Candidate bitmap helpers (1 bit per ELEMENT) ----------
        private byte[] GetOrCreateCandidateBitmap(ulong pageBase)
        {
            if (!_candidates.TryGetValue(pageBase, out var bm))
            {
                bm = new byte[PageBitmapBytes]; // zeroed
                _candidates[pageBase] = bm;
            }
            return bm;
        }

        private static void SetBitmapBit(byte[] bitmap, int elementIndex)
        {
            int byteIndex = elementIndex >> 3;
            int bitIndex = elementIndex & 7;
            bitmap[byteIndex] |= (byte)(1 << bitIndex);
        }

        private static byte[] NewAllOnesPageBitmap(int pageElemCount)
        {
            int bytes = (pageElemCount + 7) / 8;
            var bm = new byte[bytes];
            for (int i = 0; i < bytes; i++) bm[i] = 0xFF;

            int extraBits = bytes * 8 - pageElemCount;
            if (extraBits > 0)
            {
                int usedBits = 8 - extraBits;
                bm[bytes - 1] &= (byte)((1 << usedBits) - 1);
            }
            return bm;
        }

        private static bool IsBitmapZero(byte[] bm)
        {
            for (int i = 0; i < bm.Length; i++)
                if (bm[i] != 0) return false;
            return true;
        }

        private void PruneEmptyCandidatePages()
        {
            var toRemove = new List<ulong>();
            foreach (var kv in _candidates)
                if (IsBitmapZero(kv.Value)) toRemove.Add(kv.Key);
            foreach (var k in toRemove) _candidates.Remove(k);
        }

        private static int CountBits(byte[] bm, int elemCount)
        {
            int bytes = (elemCount + 7) / 8;
            int c = 0;
            for (int i = 0; i < bytes; i++)
            {
                byte v = bm[i];
                v = (byte)(v - (v >> 1 & 0x55));
                v = (byte)((v & 0x33) + (v >> 2 & 0x33));
                c += (v + (v >> 4) & 0x0F) * 0x01;
            }
            return c;
        }

        private static void InvertBitmapInPlace(byte[] bm, int elemCount)
        {
            int bytes = (elemCount + 7) / 8;
            for (int i = 0; i < bytes; i++) bm[i] = (byte)~bm[i];
            int extraBits = bytes * 8 - elemCount;
            if (extraBits > 0)
            {
                int usedBits = 8 - extraBits;
                bm[bytes - 1] &= (byte)((1 << usedBits) - 1);
            }
        }

        // ========================= Batched scan helpers =========================

        private List<(ulong Base, uint Length)> BuildRunsFromCandidates()
        {
            var pages = new List<ulong>(_candidates.Keys);
            pages.Sort();

            var runs = new List<(ulong Base, uint Length)>();
            if (pages.Count == 0) return runs;

            ulong runStart = pages[0];
            ulong runEnd = runStart + PAGE_SIZE;

            for (int i = 1; i < pages.Count; i++)
            {
                ulong p = pages[i];
                bool contiguous = p == runEnd;

                // make sure each run won't exceed the bitmap capacity for the current element size
                bool wouldExceed = (runEnd - runStart + PAGE_SIZE) > MaxScanChunkBytesForCurrentElem;

                if (!contiguous || wouldExceed)
                {
                    runs.Add((runStart, (uint)(runEnd - runStart)));
                    runStart = p;
                    runEnd = p + PAGE_SIZE;
                }
                else
                {
                    runEnd += PAGE_SIZE;
                }
            }
            runs.Add((runStart, (uint)(runEnd - runStart)));
            return runs;
        }

        private long BatchedCachedCompareAndIntersect(uint predicateFlag)
        {
            if (!TryResolveScanWindow(out var window))
                return 0;

            if (UseManagedBaseline)
            {
                return ManagedCachedCompareAndIntersect(predicateFlag, window);
            }

            var runs = BuildRunsFromCandidates();
            if (runs.Count == 0) return 0;

            int elemSize = ElemSize;
            uint elemFlag = ElemFlag;

            var args = new ScanArgs
            {
                ProcessId = _attachedPid,
                Flags = elemFlag | predicateFlag,
                TargetByte = 0,
                TargetShort = 0,
                TargetLong = 0,
                TargetCLong = 0
            };

            ulong maxChunkBytes = (ulong)MAX_BITMAP_BYTES * 8UL * (ulong)elemSize;
            ulong maxPagesPerChunk = Math.Max(1UL, maxChunkBytes / PAGE_SIZE);

            foreach (var (runBase, runLenBytes) in runs)
            {
                ulong cur = runBase;
                ulong end = runBase + runLenBytes;

                while (cur < end)
                {
                    ulong bytesRemaining = end - cur;
                    ulong pagesRemaining = bytesRemaining / PAGE_SIZE;

                    ulong pagesThisChunk = Math.Min(pagesRemaining, maxPagesPerChunk);
                    if (pagesThisChunk == 0) pagesThisChunk = 1;

                    uint chunkBytes = (uint)(pagesThisChunk * PAGE_SIZE);

                    args.Address = cur;
                    args.Length = chunkBytes;

                    int elemCount = (int)(chunkBytes / elemSize);
                    int bitmapLen = (elemCount + 7) / 8;
                    byte[] runBitmap = new byte[bitmapLen];

                    unsafe
                    {
                        if (!IoctlInOut(CTL.SCAN_CMP_CACHED, args, sizeof(ScanArgs), runBitmap, bitmapLen, out uint _, out int err))
                        {
                            AppendLog($"SCAN_CMP_CACHED failed @ 0x{cur:X}: {err}");
                        }
                        else
                        {
                            ApplyRunBitmapToPagesIntersectOrSeed(cur, runBitmap, chunkBytes, elemSize, seedIfMissing: false, intersect: true, window, out _);
                        }
                    }

                    cur += chunkBytes;
                }
            }

            return CountAllCandidateBits();
        }

        private long BatchedScanEqualsAndIntersect_Any(byte targetByte, short targetShort, int targetCLong, long targetLong, bool haveShort, bool haveCLong, bool haveLong, bool invertForNE)
        {
            if (!TryResolveScanWindow(out var window))
                return 0;

            if (UseManagedBaseline && haveCLong)
            {
                return ManagedScanEqualsAndIntersect(targetCLong, invertForNE, window);
            }

            var runs = BuildRunsFromCandidates();
            if (runs.Count == 0) return 0;

            int elemSize = ElemSize;
            uint elemFlag = ElemFlag;

            var args = new ScanArgs
            {
                ProcessId = _attachedPid,
                Flags = elemFlag | Flags.SCAN_PRED_EQ,
                TargetByte = targetByte,
                TargetShort = targetShort,
                TargetLong = targetLong,
                TargetCLong = targetCLong
            };

            ulong maxChunkBytes = (ulong)MAX_BITMAP_BYTES * 8UL * (ulong)elemSize;
            ulong maxPagesPerChunk = Math.Max(1UL, maxChunkBytes / PAGE_SIZE);

            foreach (var (runBase, runLenBytes) in runs)
            {
                ulong cur = runBase;
                ulong end = runBase + runLenBytes;

                while (cur < end)
                {
                    ulong bytesRemaining = end - cur;
                    ulong pagesRemaining = bytesRemaining / PAGE_SIZE;

                    ulong pagesThisChunk = Math.Min(pagesRemaining, maxPagesPerChunk);
                    if (pagesThisChunk == 0) pagesThisChunk = 1;

                    uint chunkBytes = (uint)(pagesThisChunk * PAGE_SIZE);

                    args.Address = cur;
                    args.Length = chunkBytes;

                    int elemCount = (int)(chunkBytes / elemSize);
                    int bitmapLen = (elemCount + 7) / 8;
                    byte[] runBitmap = new byte[bitmapLen];

                    unsafe
                    {
                        if (!IoctlInOut(CTL.SCAN, args, sizeof(ScanArgs), runBitmap, bitmapLen, out uint _, out int err))
                        {
                            AppendLog($"SCAN (EQ) failed @ 0x{cur:X}: {err}");
                        }
                        else
                        {
                            if (invertForNE)
                                InvertBitmapInPlace(runBitmap, elemCount);

                            ApplyRunBitmapToPagesIntersectOrSeed(cur, runBitmap, chunkBytes, elemSize, seedIfMissing: false, intersect: true, window, out _);
                        }
                    }

                    cur += chunkBytes;
                }
            }

            return CountAllCandidateBits();
        }

        private long ApplyRunBitmapToPagesIntersectOrSeed(
            ulong runBase,
            byte[] runBitmap,
            ulong runLenBytes,
            int elemSize,
            bool seedIfMissing,
            bool intersect,
            ScanWindow window,
            out long addedWhenSeeding)
        {
            addedWhenSeeding = 0;
            long totalHits = 0;

            int pageElemCount = PAGE_SIZE / elemSize;
            int pageBitmapBytes = (pageElemCount + 7) / 8;

            // How many pages are covered by this run
            ulong pagesInRun = (runLenBytes + PAGE_SIZE - 1) / PAGE_SIZE;

            for (ulong pi = 0; pi < pagesInRun; pi++)
            {
                ulong pageBase = runBase + pi * PAGE_SIZE;

                // Compute element offset from run start to this page start
                long elemsBefore = (long)((pageBase - runBase) / (ulong)elemSize);
                int bitOffset = (int)(elemsBefore & 7);   // 0..7 bit shift inside first byte
                int byteOffset = (int)(elemsBefore >> 3);  // whole bytes to skip

                if (!_candidates.TryGetValue(pageBase, out var bm))
                {
                    if (!seedIfMissing) continue;
                    bm = new byte[pageBitmapBytes]; // zeroed
                    _candidates[pageBase] = bm;
                }

                int hitsBefore = CountBits(bm, pageElemCount);

                // Build an aligned slice of 'runBitmap' for this page (bit-accurate)
                for (int i = 0; i < pageBitmapBytes; i++)
                {
                    byte b0 = (byte)(byteOffset + i < runBitmap.Length ? runBitmap[byteOffset + i] : 0);
                    byte b1 = (byte)(byteOffset + i + 1 < runBitmap.Length ? runBitmap[byteOffset + i + 1] : 0);

                    byte aligned = bitOffset == 0
                        ? b0
                        : (byte)(b0 >> bitOffset | b1 << 8 - bitOffset);

                    if (intersect)
                        bm[i] &= aligned;
                    else
                        bm[i] |= aligned;
                }

                // Mask off unused bits at the end of the page
                int extraBits = pageBitmapBytes * 8 - pageElemCount;
                if (extraBits > 0)
                {
                    int usedBits = 8 - extraBits;
                    bm[pageBitmapBytes - 1] &= (byte)((1 << usedBits) - 1);
                }

                MaskBitmapToWindow(bm, elemSize, pageElemCount, pageBase, window);

                int hitsAfter = CountBits(bm, pageElemCount);
                totalHits += hitsAfter;
                if (!intersect) addedWhenSeeding += hitsAfter - hitsBefore;
            }

            return totalHits;
        }

        private long CountAllCandidateBits()
        {
            long total = 0;
            int elemPerPage = PageElemCount;
            foreach (var kv in _candidates)
            {
                total += CountBits(kv.Value, elemPerPage);
            }
            return total;
        }

        private void MaskBitmapToWindow(byte[] bitmap, int elemSize, int pageElemCount, ulong pageBase, ScanWindow window)
        {
            if (!window.Enabled)
            {
                return;
            }

            int pageBitmapBytes = bitmap.Length;
            ulong pageStart = pageBase;
            ulong pageEnd = pageBase + PAGE_SIZE;

            ulong allowStart = Math.Max(pageStart, window.Start);
            ulong allowEnd = Math.Min(pageEnd, window.EndExclusive);

            if (allowEnd <= allowStart)
            {
                Array.Clear(bitmap, 0, pageBitmapBytes);
                return;
            }

            ulong startOffset = allowStart <= pageStart ? 0UL : allowStart - pageStart;
            ulong endOffset = allowEnd <= pageStart ? 0UL : allowEnd - pageStart;

            int firstElem = (int)Math.Min((ulong)pageElemCount, DivideCeiling(startOffset, (ulong)elemSize));
            int lastElemExclusive = (int)Math.Min((ulong)pageElemCount, DivideCeiling(endOffset, (ulong)elemSize));

            if (lastElemExclusive <= firstElem)
            {
                Array.Clear(bitmap, 0, pageBitmapBytes);
                return;
            }

            ClearBitmapElements(bitmap, 0, firstElem);
            ClearBitmapElements(bitmap, lastElemExclusive, pageElemCount);
        }

        private static void ClearBitmapElements(byte[] bitmap, int startElem, int endElem)
        {
            for (int elem = startElem; elem < endElem; elem++)
            {
                int byteIndex = elem >> 3;
                int bit = elem & 7;
                bitmap[byteIndex] &= (byte)~(1 << bit);
            }
        }

        private static ulong DivideCeiling(ulong value, ulong divisor)
        {
            if (divisor == 0)
            {
                return 0;
            }

            if (value == 0)
            {
                return 0;
            }

            ulong adjusted = value + divisor - 1UL;
            if (adjusted < value)
            {
                return ulong.MaxValue;
            }

            return adjusted / divisor;
        }

        private static ulong AlignDown(ulong value, ulong alignment)
        {
            if (alignment == 0)
            {
                return value;
            }

            return value & ~(alignment - 1UL);
        }

        private static ulong AlignUp(ulong value, ulong alignment)
        {
            if (alignment == 0)
            {
                return value;
            }

            ulong mask = alignment - 1UL;
            if ((value & mask) == 0)
            {
                return value;
            }

            ulong result = (value | mask) + 1UL;
            return result < value ? ulong.MaxValue : result;
        }

        private int GetPointerSize()
        {
            if (_cachedPointerSize != 0)
                return _cachedPointerSize;

            int defaultSize = IntPtr.Size;

            try
            {
                if (_hProcess == nint.Zero)
                {
                    _cachedPointerSize = defaultSize;
                    return _cachedPointerSize;
                }

                if (PsApi.IsWow64Process(_hProcess, out bool isWow64))
                {
                    _cachedPointerSize = isWow64 ? 4 : defaultSize;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 0)
                        AppendLog($"IsWow64Process failed: {err}");
                    _cachedPointerSize = defaultSize;
                }
            }
            catch (EntryPointNotFoundException)
            {
                _cachedPointerSize = defaultSize;
            }
            catch (Exception ex)
            {
                AppendLog("Pointer size detection error: " + ex.Message);
                _cachedPointerSize = defaultSize;
            }

            return _cachedPointerSize;
        }

        // ========================= Baseline management =========================

        private void ManagedRefreshBaselineFromCandidates()
        {
            _managedBaseline.Clear();
            if (_candidates.Count == 0) return;

            byte[] page = new byte[PAGE_SIZE];
            var pages = new List<ulong>(_candidates.Keys);

            foreach (var pageBase in pages)
            {
                if (!DriverRead(_attachedPid, pageBase, page, out uint got) || got != PAGE_SIZE)
                    continue;

                byte[] snapshot = new byte[PAGE_SIZE];
                Buffer.BlockCopy(page, 0, snapshot, 0, PAGE_SIZE);
                _managedBaseline[pageBase] = snapshot;
            }
        }

        private bool RestoreManagedBaselineFromOriginal()
        {
            _managedBaseline.Clear();
            if (_userSnapshot.Count == 0) return false;

            foreach (var kv in _userSnapshot)
            {
                byte[] snapshot = kv.Value;
                if (snapshot == null || snapshot.Length != PAGE_SIZE)
                    continue;

                _managedBaseline[kv.Key] = (byte[])snapshot.Clone();
            }

            return _managedBaseline.Count > 0;
        }

        // Rebuild the driver's cached baseline from CURRENT memory for all candidate pages.
        // Call this ONLY after a refinement step (or once after First Scan) so future filters compare vs the previous step.
        private void RefreshBaselineFromCandidates()
        {
            if (!_isAttached || _candidates.Count == 0) return;

            if (UseManagedBaseline)
            {
                ManagedRefreshBaselineFromCandidates();
                return;
            }

            _managedBaseline.Clear();

            var init = new PrevInit { ProcessId = _attachedPid, Flags = ElemFlag };
            unsafe
            {
                if (!IoctlInOnly(CTL.PREV_RESET, init, sizeof(PrevInit), out int errRst))
                {
                    AppendLog($"prev_reset (refresh) failed: {errRst}");
                    return;
                }
            }

            byte[] page = new byte[PAGE_SIZE];

            // Snapshot only candidate pages to keep things fast
            foreach (var pageBase in _candidates.Keys)
            {
                if (!DriverRead(_attachedPid, pageBase, page, out uint got) || got != PAGE_SIZE)
                    continue;

                var hdr = new PrevPageHdr { PageBase = pageBase, DataLen = PAGE_SIZE };
                unsafe
                {
                    int total = sizeof(PrevPageHdr) + PAGE_SIZE;
                    nint buf = Marshal.AllocHGlobal(total);
                    try
                    {
                        Marshal.StructureToPtr(hdr, buf, false);
                        Marshal.Copy(page, 0, buf + sizeof(PrevPageHdr), PAGE_SIZE);
                        if (!IoctlRaw(CTL.PREV_PUSH, buf, total, nint.Zero, 0, out uint _, out int errPush))
                            AppendLog($"prev_push (refresh) failed @ 0x{pageBase:X}: {errPush}");
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
        }

        // Load ORIGINAL snapshot into the driver's cached baseline
        private bool RestoreBaselineFromOriginal()
        {
            if (UseManagedBaseline)
            {
                return RestoreManagedBaselineFromOriginal();
            }

            if (_userSnapshot.Count == 0) return false;

            _managedBaseline.Clear();

            var init = new PrevInit { ProcessId = _attachedPid, Flags = ElemFlag };
            unsafe
            {
                if (!IoctlInOnly(CTL.PREV_RESET, init, sizeof(PrevInit), out int errRst))
                {
                    AppendLog($"prev_reset (original) failed: {errRst}");
                    return false;
                }
            }

            foreach (var kv in _userSnapshot)
            {
                ulong pageBase = kv.Key;
                byte[] snapshot = kv.Value;
                if (snapshot == null || snapshot.Length != PAGE_SIZE) continue;

                var hdr = new PrevPageHdr { PageBase = pageBase, DataLen = PAGE_SIZE };
                unsafe
                {
                    int total = sizeof(PrevPageHdr) + PAGE_SIZE;
                    nint buf = Marshal.AllocHGlobal(total);
                    try
                    {
                        Marshal.StructureToPtr(hdr, buf, false);
                        Marshal.Copy(snapshot, 0, buf + sizeof(PrevPageHdr), PAGE_SIZE);
                        if (!IoctlRaw(CTL.PREV_PUSH, buf, total, nint.Zero, 0, out uint _, out int errPush))
                        {
                            AppendLog($"prev_push (original) failed @ 0x{pageBase:X}: {errPush}");
                            // keep going; best-effort
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }

            return true;
        }

        // ========================= Driver I/O =========================

        private const int ERROR_PARTIAL_COPY = 299;
        private const int ERROR_INVALID_PARAMETER = 87;

        private bool DriverRead(uint pid, ulong remoteAddr, byte[] dst, out uint got)
        {
            got = 0;
            nint data = Marshal.AllocHGlobal(dst.Length);
            try
            {
                var req = new Request64
                {
                    ProcessId = pid,
                    Target = remoteAddr,
                    Buffer = (ulong)data.ToInt64(),
                    Size = (uint)dst.Length,
                    ReturnSize = 0
                };

                int sz = Marshal.SizeOf<Request64>();
                nint inBuf = Marshal.AllocHGlobal(sz);
                nint outBuf = Marshal.AllocHGlobal(sz);
                try
                {
                    Marshal.StructureToPtr(req, inBuf, false);

                    bool ok = IoctlRaw(CTL.READ, inBuf, sz, outBuf, sz, out uint _, out int err);
                    var rsp = Marshal.PtrToStructure<Request64>(outBuf);
                    got = (uint)rsp.ReturnSize;

                    if (!ok)
                    {
                        if (err != ERROR_PARTIAL_COPY || got == 0)
                        {
                            return false;
                        }
                    }

                    if (got > dst.Length)
                    {
                        got = (uint)dst.Length;
                    }

                    if (got > 0)
                    {
                        Marshal.Copy(data, dst, 0, (int)got);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        private bool IoctlInOnly(uint code, object structValue, int inSize, out int lastError)
        {
            lastError = 0;
            nint inBuf = Marshal.AllocHGlobal(inSize);
            try
            {
                Marshal.StructureToPtr(structValue, inBuf, false);
                bool ok = Native.DeviceIoControl(_hDevice, code, inBuf, (uint)inSize,
                                                 nint.Zero, 0, out uint _, nint.Zero);
                if (!ok) lastError = Marshal.GetLastWin32Error();
                return ok;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
            }
        }

        private bool IoctlInOut(uint code, object inStruct, int inSize, byte[] outBytes, int outSize, out uint bytesReturned, out int lastError)
        {
            lastError = 0;
            bytesReturned = 0;
            nint inBuf = Marshal.AllocHGlobal(inSize);
            nint outBuf = Marshal.AllocHGlobal(outSize);
            try
            {
                Marshal.StructureToPtr(inStruct, inBuf, false);
                bool ok = Native.DeviceIoControl(_hDevice, code, inBuf, (uint)inSize,
                                                 outBuf, (uint)outSize, out bytesReturned, nint.Zero);
                if (!ok)
                {
                    lastError = Marshal.GetLastWin32Error();
                    return false;
                }
                if (outSize > 0)
                    Marshal.Copy(outBuf, outBytes, 0, (int)bytesReturned);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }
        }

        private bool IoctlRaw(uint code, nint inBuf, int inSize, nint outBuf, int outSize, out uint bytesReturned, out int lastError)
        {
            lastError = 0;
            bytesReturned = 0;
            bool ok = Native.DeviceIoControl(_hDevice, code, inBuf, (uint)inSize, outBuf, (uint)outSize, out bytesReturned, nint.Zero);
            if (!ok) lastError = Marshal.GetLastWin32Error();
            return ok;
        }

        private static class CTL
        {
            private const uint FILE_DEVICE_UNKNOWN = 0x00000022;
            private const uint METHOD_BUFFERED = 0;
            private const uint METHOD_OUT_DIRECT = 2;
            private const uint FILE_ANY_ACCESS = 0;

            private static uint CTL_CODE(uint devType, uint function, uint method, uint access)
                => devType << 16 | access << 14 | function << 2 | method;

            public static readonly uint READ = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x697, METHOD_BUFFERED, FILE_ANY_ACCESS);
            public static readonly uint WRITE = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x698, METHOD_BUFFERED, FILE_ANY_ACCESS);
            public static readonly uint SCAN = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69A, METHOD_OUT_DIRECT, FILE_ANY_ACCESS);
            public static readonly uint PREV_RESET = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69C, METHOD_BUFFERED, FILE_ANY_ACCESS);
            public static readonly uint PREV_PUSH = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69D, METHOD_BUFFERED, FILE_ANY_ACCESS);
            public static readonly uint SCAN_CMP_CACHED = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69E, METHOD_OUT_DIRECT, FILE_ANY_ACCESS);
        }

        private static class Flags
        {
            public const uint SCAN_ELEM_BYTE = 0x01;
            public const uint SCAN_ELEM_SHORT = 0x02;
            public const uint SCAN_ELEM_LONG64 = 0x04; // 8-byte
            public const uint SCAN_ELEM_CLONG = 0x08; // NEW: C long (4-byte on Windows)

            public const uint SCAN_PRED_EQ = 0x10;
            public const uint SCAN_PRED_NE = 0x20;
            public const uint SCAN_PRED_GT = 0x40;
            public const uint SCAN_PRED_LT = 0x80;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Request64
        {
            public ulong ProcessId; // x64 HANDLE size
            public ulong Target;
            public ulong Buffer;
            public uint Size;
            public ulong ReturnSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ScanArgs
        {
            public ulong ProcessId; // x64 HANDLE size
            public ulong Address;
            public uint Length;
            public uint Flags;
            public byte TargetByte;
            public short TargetShort;
            public long TargetLong;  // 8-byte target
            public int TargetCLong; // NEW: 4-byte C long target (Windows)
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PrevInit
        {
            public ulong ProcessId; // x64 HANDLE size
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PrevPageHdr
        {
            public ulong PageBase;
            public uint DataLen;
        }

        // ========================= BOT LOGIC =========================



        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);


        private const int VK_PRIOR = 0x21; // Page Up
        private const int VK_HOME = 0x24;  // Home
        private const int VK_END = 0x23;   // End
        private const int SW_RESTORE = 9;
        private const int KEYEVENTF_KEYUP = 0x0002;

        private bool _isMovementBotRunning;
        public bool IsMovementBotRunningInternal => _isMovementBotRunning;
        private bool _isHealManaBotRunning;
        public bool IsHealManaBotRunningInternal => _isHealManaBotRunning;
        private bool _isLootBotRunning;
        public bool IsLootBotRunningInternal => _isLootBotRunning;
        private CancellationTokenSource? _movementBotCts;
        private CancellationTokenSource? _healManaBotCts;
        private CancellationTokenSource? _lootBotCts;
        private MovementSystem? _movementSystem;
        private HealManaSystem? _healManaSystem;
        private LootSystem? _lootSystem;

        // --- 3-Phase Workflow Coordinator ---
        private BotWorkflowCoordinator? _workflowCoordinator;
        private Task? _workflowTask;
        private BotProfileLoader? _profileLoader;

        public bool IsWorkflowRunning => _workflowCoordinator?.IsRunning ?? false;
        public string WorkflowPhaseText => _workflowCoordinator?.CurrentPhase.ToString() ?? "Idle";
        public BotProfile? ActiveProfile => _workflowCoordinator?.ActiveProfile;

        /// <summary>
        /// Starts the 3-phase workflow. If a profile is provided, uses profile-based constructor
        /// with CityToRepotRouteSelector. Otherwise uses legacy hardcoded fallback paths.
        /// </summary>
        public void StartWorkflow(BotProfile? profile = null)
        {
            if (!_isAttached) { AppendLog("Attach first."); return; }
            if (_workflowCoordinator?.IsRunning == true) { AppendLog("Workflow already running."); return; }

            FocusGameWindow();

            ulong baseAddr = FindModuleInScanner("Ares.exe", false);
            if (baseAddr == 0)
            {
                _pointerScanner?.RefreshModules();
                baseAddr = FindModuleInScanner("Ares.exe", true);
            }
            if (baseAddr == 0)
            {
                AppendLog("Failed to resolve module base for GameMemoryService.");
                return;
            }

            var memoryService = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
            var repotSystem = new RepotSystem(memoryService, AppendLog);
            var repotDetector = new RepotDetectorService(AppendLog);
            var pathLoader = new SavedPathLoader(AppendLog);
            var pathRunner = new PathRunnerService(memoryService, AppendLog);

            if (profile != null)
            {
                var routeSelector = new CityToRepotRouteSelector(AppendLog);
                _workflowCoordinator = new BotWorkflowCoordinator(
                    memoryService, repotSystem, repotDetector, pathLoader, pathRunner,
                    profile, routeSelector, AppendLog, FocusGameWindow);
                AppendLog($"Starting workflow with profile '{profile.Name}'.");
            }
            else
            {
                _workflowCoordinator = new BotWorkflowCoordinator(
                    memoryService, repotSystem, repotDetector, pathLoader, pathRunner,
                    AppendLog, FocusGameWindow);
                AppendLog("Starting workflow without profile (hardcoded fallback paths).");
            }

            _workflowCoordinator.OnPhaseChanged = phaseName =>
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    OnPropertyChanged(nameof(IsWorkflowRunning));
                    OnPropertyChanged(nameof(WorkflowPhaseText));
                });
            };
            _workflowCoordinator.OnStopped = () =>
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    OnPropertyChanged(nameof(IsWorkflowRunning));
                    OnPropertyChanged(nameof(WorkflowPhaseText));
                });
            };

            _workflowTask = Task.Run(() => _workflowCoordinator.StartAsync());

            OnPropertyChanged(nameof(IsWorkflowRunning));
            OnPropertyChanged(nameof(WorkflowPhaseText));
        }

        public void StopWorkflow()
        {
            if (_workflowCoordinator == null)
            {
                AppendLog("No workflow to stop.");
                return;
            }
            _workflowCoordinator.Stop();
            _workflowTask = null;
            OnPropertyChanged(nameof(IsWorkflowRunning));
            OnPropertyChanged(nameof(WorkflowPhaseText));
            AppendLog("3-Phase Workflow stopped by user.");
        }

        public List<string> ListProfiles()
        {
            _profileLoader ??= new BotProfileLoader(AppendLog);
            return _profileLoader.ListProfiles();
        }

        public BotProfile? LoadProfile(string name)
        {
            _profileLoader ??= new BotProfileLoader(AppendLog);
            return _profileLoader.LoadProfile(name);
        }

        public List<string> ValidateProfile(BotProfile profile)
        {
            _profileLoader ??= new BotProfileLoader(AppendLog);
            return _profileLoader.ValidateProfile(profile);
        }

        public short HealManaThreshold1
        {
            get => HealManaSystem.Threshold1;
            set => HealManaSystem.Threshold1 = value;
        }

        public short HealManaThreshold2
        {
            get => HealManaSystem.Threshold2;
            set => HealManaSystem.Threshold2 = value;
        }
        
                private void StartHotkeyListener()
                {
                    Task.Run(async () =>
                    {
                        while (true)
                        {
                            // Check Home (VK_HOME) - Toggles Movement Bot
                            if ((GetAsyncKeyState(VK_HOME) & 0x8000) != 0)
                            {
                                Application.Current?.Dispatcher?.Invoke(() => ToggleMovementBot());
                                while ((GetAsyncKeyState(VK_HOME) & 0x8000) != 0) await Task.Delay(50);
                            }

                            // Check Page Up (VK_PRIOR) - Toggles Heal/Mana Bot
                            if ((GetAsyncKeyState(VK_PRIOR) & 0x8000) != 0)
                            {
                                Application.Current?.Dispatcher?.Invoke(() => ToggleHealManaBot());
                                while ((GetAsyncKeyState(VK_PRIOR) & 0x8000) != 0) await Task.Delay(50);
                            }

                            // Check End (VK_END) - Toggles Loot Bot
                            if ((GetAsyncKeyState(VK_END) & 0x8000) != 0)
                            {
                                Application.Current?.Dispatcher?.Invoke(() => ToggleLootBot());
                                while ((GetAsyncKeyState(VK_END) & 0x8000) != 0) await Task.Delay(50);
                            }
                            await Task.Delay(50);
                        }
                    });
                }

                private void FocusGameWindow()
                {
                    try
                    {
                        var proc = Process.GetProcessById((int)_attachedPid);
                        if (proc != null)
                        {
                            nint hwnd = proc.MainWindowHandle;
                            if (hwnd != nint.Zero)
                            {
                                ShowWindow(hwnd, SW_RESTORE);
                                SetForegroundWindow(hwnd);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Warning: Could not focus window. {ex.Message}");
                    }
                }
        
                private void ToggleBot()
                {
                    // For backward compatibility or "Global" control, we can toggle both
                    if (_isMovementBotRunning || _isHealManaBotRunning)
                    {
                        StopAllBots();
                    }
                    else
                    {
                        ToggleMovementBot(true);
                        ToggleHealManaBot(true);
                    }
                }

                public void RunBot()
                {
                    ToggleMovementBot();
                }

                public void TestAngle()
                {
                    RunTestAngle();
                }

                public void StopAllBotsInternal()
                {
                    ToggleMovementBot(false);
                    ToggleHealManaBot(false);
                    ToggleLootBot(false);
                    AppendLog("All bots stopped.");
                }

                private void StopAllBots()
                {
                    StopAllBotsInternal();
                }

                private void ToggleMovementBot(bool? forceState = null)
                {
                    bool shouldRun = forceState ?? !_isMovementBotRunning;

                    if (!shouldRun)
                    {
                        _movementBotCts?.Cancel();
                        _isMovementBotRunning = false;
                        AppendLog("Movement Bot stopped.");
                    }
                    else
                    {
                        if (!_isAttached) { AppendLog("Attach first."); return; }
                        FocusGameWindow();

                        if (!float.TryParse(BotTargetXText, out float tx)) tx = 5000;
                        if (!float.TryParse(BotTargetYText, out float ty)) ty = 5000;
        
                        ulong baseAddr = FindModuleInScanner("Ares.exe", false);
                        if (baseAddr == 0)
                        {
                            _pointerScanner?.RefreshModules();
                            baseAddr = FindModuleInScanner("Ares.exe", true);
                        }
                        var memoryService = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
                        _movementSystem = new MovementSystem(memoryService, AppendLog, tx, ty, SelectedPrecision, null, SelectedBotMode);
        
                        _movementBotCts = new CancellationTokenSource();
                        _isMovementBotRunning = true;
                        var movementToken = _movementBotCts.Token;
                        Task.Run(() => MovementBotLoop(movementToken), movementToken);
                        AppendLog("Movement Bot started.");
                    }
                }

                public void ToggleHealManaBotInternal() => ToggleHealManaBot();

                private void ToggleHealManaBot(bool? forceState = null)
                {
                    bool shouldRun = forceState ?? !_isHealManaBotRunning;

                    if (!shouldRun)
                    {
                        _healManaBotCts?.Cancel();
                        _isHealManaBotRunning = false;
                        AppendLog("Heal/Mana Bot stopped.");
                    }
                    else
                    {
                        if (!_isAttached) { AppendLog("Attach first."); return; }
                        FocusGameWindow();

                        ulong baseAddr = FindModuleInScanner("Ares.exe", false);
                        if (baseAddr == 0)
                        {
                            _pointerScanner?.RefreshModules();
                            baseAddr = FindModuleInScanner("Ares.exe", true);
                        }

                        var memoryService = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
                        _healManaSystem = new HealManaSystem(memoryService, AppendLog);
                        _healManaBotCts = new CancellationTokenSource();
                        _isHealManaBotRunning = true;
                        var healManaToken = _healManaBotCts.Token;
                        Task.Run(() => HealManaBotLoop(healManaToken), healManaToken);
                        AppendLog("Heal/Mana Bot started.");
                    }
                }

                public void ToggleLootBotInternal() => ToggleLootBot();

                private void ToggleLootBot(bool? forceState = null)
                {
                    bool shouldRun = forceState ?? !_isLootBotRunning;

                    if (!shouldRun)
                    {
                        _lootBotCts?.Cancel();
                        _isLootBotRunning = false;
                        AppendLog("Loot Bot stopped.");
                    }
                    else
                    {
                        if (!_isAttached) { AppendLog("Attach first."); return; }
                        FocusGameWindow();

                        ulong baseAddr = FindModuleInScanner("Ares.exe", false);
                        if (baseAddr == 0)
                        {
                            _pointerScanner?.RefreshModules();
                            baseAddr = FindModuleInScanner("Ares.exe", true);
                        }

                        var memoryService = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
                        _lootSystem = new LootSystem(memoryService, AppendLog);
                        _lootBotCts = new CancellationTokenSource();
                        _isLootBotRunning = true;
                        var lootToken = _lootBotCts.Token;
                        Task.Run(() => LootBotLoop(lootToken), lootToken);
                        AppendLog("Loot Bot started.");
                    }
                }
        
                private async Task MovementBotLoop(CancellationToken token)
                {
                    try
                    {
                        // Initial 5-second delay before bot starts moving
                        AppendLog("Movement will start in 5 seconds...");
                        await Task.Delay(5000, token);

                        while (!token.IsCancellationRequested && _isAttached && _movementSystem != null)
                        {
                            await _movementSystem.Update(token);
                            await Task.Delay(50, token); // Update rate
                        }
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        Application.Current?.Dispatcher?.Invoke(() => AppendLog($"Movement Bot error: {ex.Message}"));
                    }
                    finally
                    {
                        Application.Current?.Dispatcher?.Invoke(() => 
                        {
                            _isMovementBotRunning = false;
                            AppendLog("Movement Bot loop ended.");
                        });
                        
                        // Ensure movement stops even on cancellation
                        _movementSystem?.StopMoving();
                    }
                }
        
                private async Task HealManaBotLoop(CancellationToken token)
                {
                    try
                    {
                        while (!token.IsCancellationRequested && _isAttached && _healManaSystem != null)
                        {
                            await _healManaSystem.Update(token);
                            await Task.Delay(100, token); // Update rate for heal/mana
                        }
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        Application.Current?.Dispatcher?.Invoke(() => AppendLog($"Heal/Mana Bot error: {ex.Message}"));
                    }
                    finally
                    {
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            _isHealManaBotRunning = false;
                            AppendLog("Heal/Mana Bot loop ended.");
                        });
                    }
                }

                private async Task LootBotLoop(CancellationToken token)
                {
                    try
                    {
                        while (!token.IsCancellationRequested && _isAttached && _lootSystem != null)
                        {
                            await _lootSystem.Update(token);
                            await Task.Delay(10, token); // Update rate for loot
                        }
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        Application.Current?.Dispatcher?.Invoke(() => AppendLog($"Loot Bot error: {ex.Message}"));
                    }
                    finally
                    {
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            _isLootBotRunning = false;
                            AppendLog("Loot Bot loop ended.");
                        });
                    }
                }
        
                public (int hp, int mana, bool success) GetHpMana()
                {
                    if (!_isAttached || _movementSystem == null)
                        return (0, 0, false);

                    ulong baseAddr = FindModuleInScanner("Ares.exe", false);
                    if (baseAddr == 0)
                    {
                        _pointerScanner?.RefreshModules();
                        baseAddr = FindModuleInScanner("Ares.exe", true);
                    }
                    if (baseAddr == 0) return (0, 0, false);

                    var mem = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
                    return mem.GetHpMana();
                }

                public int GetHpPotionCount()
                {
                    if (!_isAttached) return 0;
                    ulong baseAddr = FindModuleInScanner("Ares.exe", false);
                    if (baseAddr == 0)
                    {
                        _pointerScanner?.RefreshModules();
                        baseAddr = FindModuleInScanner("Ares.exe", true);
                    }
                    if (baseAddr == 0) return 0;
                    var mem = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
                    return mem.GetHpPotionCount();
                }

                public int GetManaPotionCount()
                {
                    if (!_isAttached) return 0;
                    ulong baseAddr = FindModuleInScanner("Ares.exe", false);
                    if (baseAddr == 0)
                    {
                        _pointerScanner?.RefreshModules();
                        baseAddr = FindModuleInScanner("Ares.exe", true);
                    }
                    if (baseAddr == 0) return 0;
                    var mem = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
                    return mem.GetManaPotionCount();
                }

                private void RunTestAngle()
                {
                    if (!_isAttached) return;
        
                    if (short.TryParse(TestAngleText, out short angle))
                    {
                        // Stop all other bots if they are running
                        if (_isMovementBotRunning || _isHealManaBotRunning)
                        {
                            _movementBotCts?.Cancel();
                            _healManaBotCts?.Cancel();
                            _isMovementBotRunning = false;
                            _isHealManaBotRunning = false;
                        }
        
                        ulong baseAddr = FindModuleInScanner("Ares.exe", false);
                        if (baseAddr == 0)
                        {
                            _pointerScanner?.RefreshModules();
                            baseAddr = FindModuleInScanner("Ares.exe", true);
                        }
        
                        var memoryService = new GameMemoryService(_attachedPid, DriverRead, DriverWrite, baseAddr, GetPointerSize(), AppendLog);
                        _movementSystem = new MovementSystem(memoryService, AppendLog, 0, 0);
                        
                        _movementBotCts = new CancellationTokenSource();
                        _isMovementBotRunning = true; // Use movement bot flag for test angle
                        var token = _movementBotCts.Token;
                        
                        Task.Run(async () =>
                        {
                            try 
                            {
                                Application.Current?.Dispatcher?.Invoke(() => AppendLog($"Testing Angle: {angle}. Press PageUp or Run Bot to stop."));
                                while (!token.IsCancellationRequested)
                                {
                                    _movementSystem.TestMove(angle);
                                    await Task.Delay(50, token);
                                }
                            }
                            catch (TaskCanceledException) {}
                            finally 
                            {
                                Application.Current?.Dispatcher?.Invoke(() => 
                                {
                                    _isMovementBotRunning = false;
                                    AppendLog("Test Angle stopped.");
                                });
                            }
                        }, token);
                    }
                }
        private static class Native
        {
            public const uint GENERIC_READ = 0x80000000;
            public const uint GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 0x00000001;
            public const uint FILE_SHARE_WRITE = 0x00000002;
            public const uint OPEN_EXISTING = 3;
            public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern SafeFileHandle CreateFile(
                string lpFileName,
                uint dwDesiredAccess,
                uint dwShareMode,
                nint lpSecurityAttributes,
                uint dwCreationDisposition,
                uint dwFlagsAndAttributes,
                nint hTemplateFile);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                uint dwIoControlCode,
                nint lpInBuffer,
                uint nInBufferSize,
                nint lpOutBuffer,
                uint nOutBufferSize,
                out uint lpBytesReturned,
                nint lpOverlapped);
        }

        private static SafeFileHandle CreateFile(string path, uint access, uint share, nint sec, uint disp, uint flags, nint templ) =>
            Native.CreateFile(path, access, share, sec, disp, flags, templ);

    }
}
