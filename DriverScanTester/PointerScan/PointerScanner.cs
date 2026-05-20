using DriverScanTester.Models;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.PointerScan
{
    public sealed class PointerScanner
    {
        private const int DefaultChunkSize = 1 << 20; // 1 MiB
        private const bool LogRejectedCandidates = false;

        private readonly nint _processHandle;
        private readonly uint _processId;
        private IMemoryReader _reader;
        private readonly List<ModuleInfo> _modules = new();
        private readonly object _moduleLock = new();

        private readonly Action<string>? _logger;

        public PointerScanner(nint processHandle, uint processId, IMemoryReader reader, Action<string>? logger = null)
        {
            _processHandle = processHandle;
            _processId = processId;
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _logger = logger;
        }

        public int PointerSize => _reader.PointerSize;

        public IReadOnlyList<ModuleInfo> Modules
        {
            get
            {
                lock (_moduleLock)
                {
                    return _modules.ToList();
                }
            }
        }

        /// <summary>
        /// Suggests a narrow search range around a target address, defaulting to a 50 MiB window
        /// (25 MiB on either side). The caller can widen or shrink the <paramref name="windowBytes"/>
        /// argument depending on how noisy the surrounding memory is expected to be.
        /// </summary>
        /// <param name="target">The address to center the search window around.</param>
        /// <param name="windowBytes">The total width of the window, in bytes. Defaults to 50 MiB.</param>
        public static (ulong Start, ulong End) SuggestTightRangeAround(ulong target, ulong windowBytes = 50UL * 1024 * 1024)
        {
            ulong half = windowBytes / 2UL;
            ulong start = target >= half ? target - half : 0UL;
            ulong end = target + half;
            if (end < target) end = ulong.MaxValue; // overflow clamp
            return (start, end);
        }

        public void ReplaceReader(IMemoryReader reader)
        {
            _reader.Dispose();
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public void RefreshModules()
        {
            lock (_moduleLock)
            {
                _logger?.Invoke("Enumerating modules...");
                _modules.Clear();
                foreach (var m in EnumerateModules())
                {
                    _modules.Add(m);
                }
                _logger?.Invoke($"Found {_modules.Count} modules.");
                if (_modules.Count > 0)
                {
                    _logger?.Invoke($"First module: {_modules[0].Name} @ 0x{_modules[0].BaseAddress:X}");
                }
            }
        }

        public Task<IReadOnlyList<PointerScanResult>> ScanAsync(
            ulong targetMin,
            ulong targetMax,
            ulong rangeStart,
            ulong rangeEnd,
            PointerScanOptions options,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                _logger?.Invoke($"Starting pointer scan (Target: 0x{targetMin:X}-0x{targetMax:X})...");
                if (rangeEnd <= rangeStart) return (IReadOnlyList<PointerScanResult>)Array.Empty<PointerScanResult>();

                RefreshModules();

                int alignment = options.Alignment > 0 ? options.Alignment : 1;
                int pointerSize = PointerSize;
                int overlap = pointerSize - 1;

                const int ChunkSize = 4096;
                byte[] buffer = new byte[ChunkSize + overlap];

                int maxDepth = Math.Max(1, options.MaxDepth);
                long configuredOffset = options.MaxOffset < 0 ? 0 : options.MaxOffset;
                ulong offsetRange = (ulong)configuredOffset;

                Dictionary<ulong, List<PointerScanResult>> frontier = new();
                var results = new List<PointerScanResult>();

                for (int depth = 1; depth <= maxDepth; depth++)
                {
                    if (depth > 1 && frontier.Count == 0) break;

                    bool scanFullForThisDepth = maxDepth > 1 && depth < maxDepth;

                    ulong depthScanStart = scanFullForThisDepth ? 0UL : rangeStart;
                    ulong depthScanEnd = scanFullForThisDepth ? GetProcessScanEndExclusive() : rangeEnd;

                    _logger?.Invoke(
                        scanFullForThisDepth
                            ? $"Depth {depth}: scanning full readable process memory for intermediate pointers."
                            : $"Depth {depth}: scanning requested root range only: 0x{rangeStart:X}-0x{rangeEnd:X}.");

                    var nextFrontier = new Dictionary<ulong, List<PointerScanResult>>();
                    ulong[] sortedFrontierKeys = null;
                    KeyValuePair<ulong, List<PointerScanResult>>[] sortedFrontier = null;

                    if (depth > 1 && offsetRange > 0)
                    {
                        sortedFrontier = frontier.OrderBy(k => k.Key).ToArray();
                        sortedFrontierKeys = sortedFrontier.Select(k => k.Key).ToArray();
                    }

                    foreach (var region in EnumerateReadableRegions(depthScanStart, depthScanEnd))
                    {
                        if (!options.ScanCodeSegments)
                        {
                            const PsApi.MemProtect execMask =
                                PsApi.MemProtect.PAGE_EXECUTE |
                                PsApi.MemProtect.PAGE_EXECUTE_READ |
                                PsApi.MemProtect.PAGE_EXECUTE_READWRITE |
                                PsApi.MemProtect.PAGE_EXECUTE_WRITECOPY;

                            if ((region.Protect & execMask) != 0)
                            {
                                continue;
                            }
                        }

                        ulong currentAddr = region.Start;
                        int validInHead = 0;

                        while (currentAddr < region.End)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            ulong remainingInRegion = region.End - currentAddr;
                            int toRead = (int)Math.Min((ulong)ChunkSize, remainingInRegion);

                            if (!_reader.Read(currentAddr, buffer.AsSpan(validInHead, toRead), out int bytesRead) || bytesRead == 0)
                            {
                                currentAddr = (currentAddr & ~4095UL) + 4096;
                                validInHead = 0;
                                continue;
                            }

                            int totalBytes = validInHead + bytesRead;
                            int limit = totalBytes - pointerSize;

                            ulong bufferBaseAddr = currentAddr - (ulong)validInHead;

                            int startOffset = 0;
                            if (alignment > 1)
                            {
                                ulong remainder = bufferBaseAddr % (ulong)alignment;
                                if (remainder != 0) startOffset = (int)((ulong)alignment - remainder);
                            }

                            for (int offset = startOffset; offset <= limit; offset += alignment)
                            {
                                ulong pointerAddr = bufferBaseAddr + (ulong)offset;
                                ulong value = pointerSize == 8
                                    ? BitConverter.ToUInt64(buffer, offset)
                                    : BitConverter.ToUInt32(buffer, offset);

                                var matches = new List<(PointerScanResult? Parent, long Distance)>();

                                if (depth == 1)
                                {
                                    if (TryComputeOffsetToTargetRange(value, targetMin, targetMax, offsetRange, out long leafDistance))
                                    {
                                        matches.Add((null, leafDistance));
                                    }
                                }
                                else
                                {
                                    if (offsetRange == 0)
                                    {
                                        if (frontier.TryGetValue(value, out var pList))
                                        {
                                            foreach (var p in pList)
                                            {
                                                matches.Add((p, 0));
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (sortedFrontierKeys == null || sortedFrontier == null)
                                        {
                                            continue;
                                        }

                                        ulong minKey = SubtractSaturating(value, offsetRange);
                                        ulong maxKey = AddSaturating(value, offsetRange);

                                        int idx = Array.BinarySearch(sortedFrontierKeys, minKey);
                                        if (idx < 0) idx = ~idx;

                                        while (idx < sortedFrontierKeys.Length && sortedFrontierKeys[idx] <= maxKey)
                                        {
                                            ulong parentAddr = sortedFrontierKeys[idx];
                                            long distanceToParent = ComputeSignedOffset(value, parentAddr);

                                            foreach (var p in sortedFrontier[idx].Value)
                                            {
                                                matches.Add((p, distanceToParent));
                                            }

                                            idx++;
                                        }
                                    }
                                }

                                if (matches.Count == 0)
                                {
                                    continue;
                                }

                                ModuleInfo? module = FindModule(pointerAddr);
                                bool isStatic = module != null;
                                ulong modOffset = isStatic ? (pointerAddr - module.BaseAddress) : 0;

                                foreach (var match in matches)
                                {
                                    var res = new PointerScanResult(
                                        pointerAddr,
                                        value,
                                        module,
                                        match.Distance,
                                        depth,
                                        match.Parent,
                                        modOffset);

                                    if (!TryResolveScanResult(res, out ulong resolvedAddress, out string diagnostic))
                                    {
                                        LogRejectedCandidate($"Rejected depth {depth} candidate; resolve failed:", diagnostic);
                                        continue;
                                    }

                                    if (resolvedAddress < targetMin || resolvedAddress > targetMax)
                                    {
                                        LogRejectedCandidate(
                                            $"Rejected depth {depth} candidate; resolved 0x{resolvedAddress:X}, expected 0x{targetMin:X}-0x{targetMax:X}",
                                            diagnostic);
                                        continue;
                                    }

                                    bool isInsideRequestedRange = IsInsideRange(pointerAddr, rangeStart, rangeEnd);
                                    bool shouldShow = isInsideRequestedRange;

                                    if (options.ModulesOnly && !isStatic)
                                    {
                                        shouldShow = false;
                                    }

                                    if (shouldShow)
                                    {
                                        lock (results)
                                        {
                                            results.Add(res);
                                        }
                                    }

                                    if (depth < maxDepth)
                                    {
                                        lock (nextFrontier)
                                        {
                                            if (!nextFrontier.TryGetValue(pointerAddr, out var list))
                                            {
                                                list = new List<PointerScanResult>();
                                                nextFrontier[pointerAddr] = list;
                                            }

                                            list.Add(res);
                                        }
                                    }
                                }
                            }

                            if (totalBytes >= overlap)
                            {
                                Buffer.BlockCopy(buffer, totalBytes - overlap, buffer, 0, overlap);
                                validInHead = overlap;
                            }
                            else
                            {
                                validInHead = 0;
                            }

                            currentAddr += (ulong)bytesRead;
                        }
                    }

                    _logger?.Invoke($"Depth {depth}: results={results.Count}, nextFrontier={nextFrontier.Count}");
                    frontier = nextFrontier;
                }

                _logger?.Invoke($"Scan complete. Found {results.Count} results.");

                return results
                    .OrderBy(r => r.Depth)
                    .ThenByDescending(r => r.IsGreen)
                    .ThenBy(r => AbsDistance(r.Distance))
                    .ThenBy(r => r.PointerAddress)
                    .Take(options.MaxResults)
                    .ToList();
            }, cancellationToken);
        }

        public async Task<IReadOnlyList<PointerChain>> FindPointerChainsAsync(
            ulong finalTargetMin,
            ulong finalTargetMax,
            ulong rangeStart,
            ulong rangeEnd,
            PointerScanOptions scanOptions,
            PointerChainOptions chainOptions,
            CancellationToken cancellationToken)
        {
            var effectiveScanOptions = new PointerScanOptions
            {
                Alignment = scanOptions.Alignment,
                ModulesOnly = scanOptions.ModulesOnly,
                MaxResults = scanOptions.MaxResults,
                MaxDepth = chainOptions.MaxDepth,
                MaxOffset = scanOptions.MaxOffset,
                ScanCodeSegments = scanOptions.ScanCodeSegments
            };

            var hits = await ScanAsync(finalTargetMin, finalTargetMax, rangeStart, rangeEnd, effectiveScanOptions, cancellationToken)
                .ConfigureAwait(false);

            var chains = new List<PointerChain>();
            foreach (var hit in hits.Where(h => h.IsGreen)
                                     .OrderBy(h => h.Depth)
                                     .ThenBy(h => AbsDistance(h.Distance))
                                     .ThenBy(h => h.PointerAddress))
            {
                var path = hit.EnumeratePath().ToList();
                var leaf = path.Last();

                if (!TryAddSignedOffset(leaf.Value, leaf.Distance, out ulong actualTarget))
                {
                    _logger?.Invoke($"Rejecting chain: invalid signed leaf offset {FormatSignedHex(leaf.Distance)} from value 0x{leaf.Value:X}");
                    continue;
                }

                if (actualTarget < finalTargetMin || actualTarget > finalTargetMax)
                {
                    _logger?.Invoke($"Rejecting chain: actualTarget 0x{actualTarget:X} outside range 0x{finalTargetMin:X}-0x{finalTargetMax:X}");
                    continue;
                }

                var chain = PointerChain.Create(hit, actualTarget);

                if (!TryResolveOffsets(chain.Module, chain.Offsets, out ulong resolved, out string diagnostic))
                {
                    _logger?.Invoke("Rejected chain; resolve failed:");
                    _logger?.Invoke(diagnostic);
                    continue;
                }

                if (resolved < finalTargetMin || resolved > finalTargetMax)
                {
                    _logger?.Invoke($"Rejected chain; resolved 0x{resolved:X}, expected 0x{finalTargetMin:X}-0x{finalTargetMax:X}");
                    _logger?.Invoke(diagnostic);
                    continue;
                }

                chains.Add(chain);
                if (chains.Count >= chainOptions.MaxChains)
                {
                    break;
                }
            }

            return chains;
        }

        public static int DetectPointerSize(nint processHandle, uint processId = 0)
        {
            if (processHandle == nint.Zero)
            {
                throw new ArgumentNullException(nameof(processHandle));
            }

            if (!Environment.Is64BitOperatingSystem)
            {
                return 4;
            }

            if (TryIsWow64Process2(processHandle, out bool isWow64))
            {
                return isWow64 ? 4 : 8;
            }

            if (TryIsWow64Process(processHandle, out bool wow64, out bool accessDenied))
            {
                return wow64 ? 4 : 8;
            }

            if (accessDenied && processId != 0)
            {
                nint queryHandle = PsApi.OpenProcess(PsApi.PROCESS_QUERY_INFORMATION, false, processId);
                if (queryHandle == nint.Zero)
                {
                    queryHandle = PsApi.OpenProcess(PsApi.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                }

                if (queryHandle != nint.Zero)
                {
                    try
                    {
                        if (TryIsWow64Process(queryHandle, out bool reopenedWow64, out _))
                        {
                            return reopenedWow64 ? 4 : 8;
                        }
                    }
                    finally
                    {
                        PsApi.CloseHandle(queryHandle);
                    }
                }
            }

            // Fallback: assume native bitness matches OS
            return IntPtr.Size;
        }

        public static IReadOnlyList<PersistedPointerChain> SaveChainsToJson(IEnumerable<PointerChain> chains, string filePath)
        {
            var list = chains.Select(c => c.ToPersisted()).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            return list;
        }

        private ModuleInfo? FindModule(ulong address)
        {
            lock (_moduleLock)
            {
                foreach (var module in _modules)
                {
                    if (module.Contains(address))
                    {
                        return module;
                    }
                }
            }

            return null;
        }

        private static ulong AddSaturating(ulong value, ulong add)
        {
            ulong result = value + add;
            return result < value ? ulong.MaxValue : result;
        }

        private static ulong SubtractSaturating(ulong value, ulong subtract)
        {
            return value < subtract ? 0UL : value - subtract;
        }

        private static long ComputeSignedOffset(ulong from, ulong to)
        {
            if (to >= from)
            {
                ulong delta = to - from;
                return delta > (ulong)long.MaxValue ? long.MaxValue : unchecked((long)delta);
            }
            else
            {
                ulong delta = from - to;
                return delta > (ulong)long.MaxValue ? long.MinValue : -unchecked((long)delta);
            }
        }

        private static ulong AbsDistance(long value)
        {
            if (value >= 0)
            {
                return unchecked((ulong)value);
            }

            return value == long.MinValue
                ? 1UL << 63
                : unchecked((ulong)(-value));
        }

        private static string FormatSignedHex(long value)
        {
            if (value < 0)
            {
                ulong abs = value == long.MinValue
                    ? 1UL << 63
                    : unchecked((ulong)(-value));

                return $"-0x{abs:X}";
            }

            return $"0x{value:X}";
        }

        private static bool TryAddSignedOffset(ulong value, long offset, out ulong result)
        {
            if (offset >= 0)
            {
                ulong add = unchecked((ulong)offset);

                if (value > ulong.MaxValue - add)
                {
                    result = ulong.MaxValue;
                    return false;
                }

                result = value + add;
                return true;
            }
            else
            {
                ulong subtract = offset == long.MinValue
                    ? 1UL << 63
                    : unchecked((ulong)(-offset));

                if (value < subtract)
                {
                    result = 0;
                    return false;
                }

                result = value - subtract;
                return true;
            }
        }

        private void LogRejectedCandidate(string message, string diagnostic)
        {
            if (!LogRejectedCandidates)
            {
                return;
            }

            _logger?.Invoke(message);
            _logger?.Invoke(diagnostic);
        }

        private ulong GetProcessScanEndExclusive()
        {
            return PointerSize == 4
                ? 0x100000000UL
                : 0x0000800000000000UL;
        }

        private static bool IsInsideRange(ulong address, ulong start, ulong end)
        {
            return address >= start && address < end;
        }

        private static bool TryComputeOffsetToTargetRange(
            ulong value,
            ulong targetMin,
            ulong targetMax,
            ulong maxOffset,
            out long distance)
        {
            distance = 0;

            if (targetMax < targetMin)
            {
                return false;
            }

            ulong minReachable = SubtractSaturating(value, maxOffset);
            ulong maxReachable = AddSaturating(value, maxOffset);

            if (targetMax < minReachable || targetMin > maxReachable)
            {
                return false;
            }

            ulong targetAddress;

            if (value < targetMin)
            {
                targetAddress = targetMin;
            }
            else if (value > targetMax)
            {
                targetAddress = targetMax;
            }
            else
            {
                targetAddress = value;
            }

            distance = ComputeSignedOffset(value, targetAddress);
            return true;
        }

        private bool TryResolveScanResult(
            PointerScanResult result,
            out ulong resolvedAddress,
            out string diagnostic)
        {
            resolvedAddress = 0;
            var sb = new StringBuilder();

            var steps = result.EnumeratePath().ToList();
            if (steps.Count == 0)
            {
                diagnostic = "Empty scan result path.";
                return false;
            }

            ulong currentAddress = steps[0].PointerAddress;
            byte[] resolveBuffer = new byte[PointerSize];

            sb.AppendLine($"startAddress=0x{currentAddress:X}");

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];

                if (!_reader.Read(currentAddress, resolveBuffer, out int read) || read < PointerSize)
                {
                    diagnostic = sb.AppendLine($"read failed at step {i}, address=0x{currentAddress:X}").ToString();
                    return false;
                }

                ulong value = PointerSize == 8
                    ? BitConverter.ToUInt64(resolveBuffer, 0)
                    : BitConverter.ToUInt32(resolveBuffer, 0);

                if (!TryAddSignedOffset(value, step.Distance, out ulong nextAddress))
                {
                    diagnostic = sb.AppendLine($"signed offset overflow/underflow at step {i}, value=0x{value:X}, offset={FormatSignedHex(step.Distance)}").ToString();
                    return false;
                }

                sb.AppendLine($"step={i}, readAt=0x{currentAddress:X}, value=0x{value:X}, offset={FormatSignedHex(step.Distance)}, next=0x{nextAddress:X}");
                currentAddress = nextAddress;
            }

            resolvedAddress = currentAddress;
            diagnostic = sb.ToString();
            return true;
        }

        private IEnumerable<MemoryRegion> EnumerateReadableRegions(ulong rangeStart, ulong rangeEnd)
        {
            if (_processHandle == nint.Zero)
            {
                yield break;
            }

            int mbiSize = Marshal.SizeOf<PsApi.MEMORY_BASIC_INFORMATION64>();
            ulong current = rangeStart;
            while (current < rangeEnd)
            {
                if (PsApi.VirtualQueryEx(_processHandle, (nuint)current, out var mbi, (nuint)mbiSize) == 0)
                {
                    yield break;
                }

                ulong regionStart = Math.Max(mbi.BaseAddress, rangeStart);
                ulong regionEnd = Math.Min(mbi.BaseAddress + mbi.RegionSize, rangeEnd);
                if (regionEnd > regionStart && IsReadable(in mbi))
                {
                    yield return new MemoryRegion(regionStart, regionEnd, mbi.Protect);
                }

                ulong next = mbi.BaseAddress + mbi.RegionSize;
                if (next <= current)
                {
                    break;
                }

                current = next;
            }
        }

        private static bool IsReadable(in PsApi.MEMORY_BASIC_INFORMATION64 mbi)
        {
            if (mbi.State != PsApi.MemState.MEM_COMMIT) return false;
            if ((mbi.Protect & PsApi.MemProtect.PAGE_GUARD) != 0) return false;
            if ((mbi.Protect & PsApi.MemProtect.PAGE_NOACCESS) != 0) return false;

            const PsApi.MemProtect readableFlags =
                PsApi.MemProtect.PAGE_READONLY |
                PsApi.MemProtect.PAGE_READWRITE |
                PsApi.MemProtect.PAGE_WRITECOPY |
                PsApi.MemProtect.PAGE_EXECUTE_READ |
                PsApi.MemProtect.PAGE_EXECUTE_READWRITE |
                PsApi.MemProtect.PAGE_EXECUTE_WRITECOPY;

            return (mbi.Protect & readableFlags) != 0;
        }

        private IEnumerable<ModuleInfo> EnumerateModules()
        {
            var uniqueModules = new Dictionary<ulong, ModuleInfo>();

            // Strategy 1: Toolhelp32Snapshot
            nint snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPMODULE | NativeMethods.TH32CS_SNAPMODULE32, _processId);
            if (snapshot != NativeMethods.INVALID_HANDLE_VALUE)
            {
                try
                {
                    // ... existing logic ...
                    NativeMethods.MODULEENTRY32 module = new NativeMethods.MODULEENTRY32
                    {
                        dwSize = (uint)Marshal.SizeOf<NativeMethods.MODULEENTRY32>()
                    };

                    bool success = NativeMethods.Module32First(snapshot, ref module);
                    while (success)
                    {
                        ulong baseAddr = unchecked((ulong)module.modBaseAddr.ToInt64());
                        if (!uniqueModules.ContainsKey(baseAddr))
                        {
                            uniqueModules[baseAddr] = new ModuleInfo(module.szModule, baseAddr, module.modBaseSize);
                        }
                        success = NativeMethods.Module32Next(snapshot, ref module);
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(snapshot);
                }
            }
            else
            {
                _logger?.Invoke($"Toolhelp32Snapshot failed: {Marshal.GetLastWin32Error()}");
            }

            // Strategy 3: PEB Walk (Driver-based)
            // If standard APIs fail (Access Denied / Anti-Cheat), we can try to walk the PEB manually
            // using the driver's read primitive.
            if (_processHandle != nint.Zero)
            {
                try
                {
                    bool isWow64;
                    if (!NativeMethods.IsWow64Process(_processHandle, out isWow64)) isWow64 = false;

                    foreach (var m in EnumerateModulesViaPeb(isWow64))
                    {
                        if (!uniqueModules.ContainsKey(m.BaseAddress))
                        {
                            uniqueModules[m.BaseAddress] = m;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"PEB Walk failed: {ex.Message}");
                }
            }

            return uniqueModules.Values;
        }

        private IEnumerable<ModuleInfo> EnumerateModulesViaPeb(bool isWow64)
        {
            if (isWow64)
            {
                // 32-bit PEB logic
                if (NativeMethods.NtQueryInformationProcess(_processHandle, NativeMethods.ProcessWow64Information, out nint peb32, (uint)IntPtr.Size, out _) != 0)
                    yield break;

                if (peb32 == nint.Zero) yield break;

                ulong pebAddr = unchecked((ulong)peb32);

                // PEB32 + 0x0C = Ldr
                byte[] ldrPtrBuf = new byte[4];
                if (!_reader.Read(pebAddr + 0x0C, ldrPtrBuf, out int read) || read < 4) yield break;
                uint ldrAddr = BitConverter.ToUInt32(ldrPtrBuf, 0);
                if (ldrAddr == 0) yield break;

                // PEB_LDR_DATA32 + 0x0C = InLoadOrderModuleList (LIST_ENTRY32)
                byte[] listHeadBuf = new byte[4];
                if (!_reader.Read(ldrAddr + 0x0C, listHeadBuf, out read) || read < 4) yield break;
                uint listHead = BitConverter.ToUInt32(listHeadBuf, 0);
                if (listHead == 0) yield break;

                uint current = listHead;
                // Guard against infinite loops
                for (int i = 0; i < 500; i++)
                {
                    if (current == 0 || current == ldrAddr + 0x0C) break;

                    // LDR_DATA_TABLE_ENTRY32
                    byte[] baseAddrBuf = new byte[4];
                    if (!_reader.Read(current + 0x18, baseAddrBuf, out read) || read < 4) break;
                    uint dllBase = BitConverter.ToUInt32(baseAddrBuf, 0);

                    byte[] sizeBuf = new byte[4];
                    if (!_reader.Read(current + 0x20, sizeBuf, out read) || read < 4) break;
                    uint size = BitConverter.ToUInt32(sizeBuf, 0);

                    byte[] nameStrBuf = new byte[8];
                    if (!_reader.Read(current + 0x2C, nameStrBuf, out read) || read < 8) break;
                    // UNICODE_STRING32 { Length (2), MaximumLength (2), Buffer (4) }
                    ushort nameLen = BitConverter.ToUInt16(nameStrBuf, 0);
                    uint nameBufferPtr = BitConverter.ToUInt32(nameStrBuf, 4);

                    string name = "Unknown";
                    if (nameLen > 0 && nameLen < 512 && nameBufferPtr != 0)
                    {
                        byte[] nameBytes = new byte[nameLen];
                        if (_reader.Read(nameBufferPtr, nameBytes, out read) && read == nameLen)
                        {
                            name = Encoding.Unicode.GetString(nameBytes);
                        }
                    }

                    if (dllBase != 0)
                        yield return new ModuleInfo(name, dllBase, size);

                    // Read Flink (offset 0) to go to next
                    byte[] nextBuf = new byte[4];
                    if (!_reader.Read(current, nextBuf, out read) || read < 4) break;
                    current = BitConverter.ToUInt32(nextBuf, 0);
                }
            }
            else
            {
                // 64-bit PEB logic
                var pbi = new NativeMethods.PROCESS_BASIC_INFORMATION();
                if (NativeMethods.NtQueryInformationProcess(_processHandle, NativeMethods.ProcessBasicInformation, ref pbi, (uint)Marshal.SizeOf(pbi), out _) != 0)
                    yield break;

                if (pbi.PebBaseAddress == nint.Zero) yield break;
                ulong pebAddr = unchecked((ulong)pbi.PebBaseAddress);

                // PEB64 + 0x18 = Ldr
                byte[] ldrPtrBuf = new byte[8];
                if (!_reader.Read(pebAddr + 0x18, ldrPtrBuf, out int read) || read < 8) yield break;
                ulong ldrAddr = BitConverter.ToUInt64(ldrPtrBuf, 0);
                if (ldrAddr == 0) yield break;

                // PEB_LDR_DATA64 + 0x10 = InLoadOrderModuleList (LIST_ENTRY64)
                byte[] listHeadBuf = new byte[8];
                if (!_reader.Read(ldrAddr + 0x10, listHeadBuf, out read) || read < 8) yield break;
                ulong listHead = BitConverter.ToUInt64(listHeadBuf, 0);
                if (listHead == 0) yield break;

                ulong current = listHead;
                for (int i = 0; i < 500; i++)
                {
                    if (current == 0 || current == ldrAddr + 0x10) break;

                    // LDR_DATA_TABLE_ENTRY64
                    byte[] baseAddrBuf = new byte[8];
                    if (!_reader.Read(current + 0x30, baseAddrBuf, out read) || read < 8) break;
                    ulong dllBase = BitConverter.ToUInt64(baseAddrBuf, 0);

                    byte[] sizeBuf = new byte[4];
                    if (!_reader.Read(current + 0x40, sizeBuf, out read) || read < 4) break;
                    uint size = BitConverter.ToUInt32(sizeBuf, 0);

                    byte[] nameStrBuf = new byte[16];
                    if (!_reader.Read(current + 0x58, nameStrBuf, out read) || read < 16) break;
                    // UNICODE_STRING64 { Length (2), MaxLength (2), Pad (4), Buffer (8) }
                    ushort nameLen = BitConverter.ToUInt16(nameStrBuf, 0);
                    ulong nameBufferPtr = BitConverter.ToUInt64(nameStrBuf, 8);

                    string name = "Unknown";
                    if (nameLen > 0 && nameLen < 512 && nameBufferPtr != 0)
                    {
                        byte[] nameBytes = new byte[nameLen];
                        if (_reader.Read(nameBufferPtr, nameBytes, out read) && read == nameLen)
                        {
                            name = Encoding.Unicode.GetString(nameBytes);
                        }
                    }

                    if (dllBase != 0)
                        yield return new ModuleInfo(name, dllBase, size);

                    // Flink
                    byte[] nextBuf = new byte[8];
                    if (!_reader.Read(current, nextBuf, out read) || read < 8) break;
                    current = BitConverter.ToUInt64(nextBuf, 0);
                }
            }
        }

        private bool TryResolveOffsets(
            ModuleInfo module,
            IReadOnlyList<long> offsets,
            out ulong resolvedAddress,
            out string diagnostic)
        {
            resolvedAddress = 0;
            var sb = new StringBuilder();

            if (offsets == null || offsets.Count == 0)
            {
                diagnostic = "No offsets.";
                return false;
            }

            if (offsets[0] < 0)
            {
                diagnostic = "Root module offset is negative.";
                return false;
            }

            ulong currentAddress = module.BaseAddress + unchecked((ulong)offsets[0]);
            sb.AppendLine($"moduleBase=0x{module.BaseAddress:X}");
            sb.AppendLine($"rootOffset=0x{offsets[0]:X}");
            sb.AppendLine($"rootAddress=0x{currentAddress:X}");

            byte[] resolveBuffer = new byte[PointerSize];

            for (int i = 1; i < offsets.Count; i++)
            {
                if (!_reader.Read(currentAddress, resolveBuffer, out int read) || read < PointerSize)
                {
                    diagnostic = sb.AppendLine($"read failed at 0x{currentAddress:X}").ToString();
                    return false;
                }

                ulong value = PointerSize == 8
                    ? BitConverter.ToUInt64(resolveBuffer, 0)
                    : BitConverter.ToUInt32(resolveBuffer, 0);

                long offset = offsets[i];

                if (!TryAddSignedOffset(value, offset, out ulong nextAddress))
                {
                    diagnostic = sb.AppendLine($"signed offset overflow/underflow at step {i}, value=0x{value:X}, offset={FormatSignedHex(offset)}").ToString();
                    return false;
                }

                sb.AppendLine($"step={i}, readAt=0x{currentAddress:X}, value=0x{value:X}, offset={FormatSignedHex(offset)}, next=0x{nextAddress:X}");
                currentAddress = nextAddress;
            }

            resolvedAddress = currentAddress;
            diagnostic = sb.ToString();
            return true;
        }

        private bool _readerReadWrapper(ulong address, int length, out byte[] buffer)
        {
            buffer = new byte[length];
            return _reader.Read(address, buffer, out int read) && read == length;
        }

        private static bool TryIsWow64Process2(nint handle, out bool isWow64)
        {
            isWow64 = false;
            try
            {
                if (!NativeMethods.IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
                {
                    return false;
                }

                isWow64 = processMachine != 0 && processMachine != nativeMachine;
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static bool TryIsWow64Process(nint handle, out bool isWow64, out bool accessDenied)
        {
            if (!NativeMethods.IsWow64Process(handle, out bool wow64))
            {
                isWow64 = false;
                accessDenied = Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED;
                return false;
            }

            accessDenied = false;
            isWow64 = wow64;
            return true;
        }

        private readonly record struct MemoryRegion(ulong Start, ulong End, PsApi.MemProtect Protect);

        public sealed class ModuleInfo
        {
            public ModuleInfo(string name, ulong baseAddress, uint size)
            {
                Name = name;
                BaseAddress = baseAddress;
                Size = size;
            }

            public string Name { get; }
            public ulong BaseAddress { get; }
            public uint Size { get; }
            public ulong End => BaseAddress + Size;

            public bool Contains(ulong address) => address >= BaseAddress && address < End;
        }

        public sealed record PointerScanResult(
            ulong PointerAddress,
            ulong Value,
            ModuleInfo? Module,
            long Distance,
            int Depth,
            PointerScanResult? Parent,
            ulong ModuleOffset = 0)
        {
            public bool IsGreen => Module != null;
            public string? ModuleName => Module != null ? $"{Module.Name}+0x{ModuleOffset:X}" : null;

            public IEnumerable<PointerScanResult> EnumeratePath()
            {
                var list = new List<PointerScanResult>();
                for (var current = this; current != null; current = current.Parent)
                {
                    list.Add(current);
                }

                return list;
            }
        }

        public sealed class PointerChain
        {
            private PointerChain(ModuleInfo module, ulong finalTarget, IReadOnlyList<PointerScanResult> steps, IReadOnlyList<long> offsets)
            {
                Module = module;
                FinalTarget = finalTarget;
                Steps = steps;
                Offsets = offsets;
            }

            public ModuleInfo Module { get; }
            public ulong FinalTarget { get; }
            public IReadOnlyList<PointerScanResult> Steps { get; }
            public IReadOnlyList<long> Offsets { get; }

            public PersistedPointerChain ToPersisted()
            {
                return new PersistedPointerChain
                {
                    ModuleName = Module.Name,
                    ModuleBaseHint = Module.BaseAddress,
                    Offsets = Offsets.ToArray(),
                    FinalTarget = $"0x{FinalTarget:X}"
                };
            }

            internal static PointerChain Create(PointerScanResult terminalResult, ulong finalTarget)
            {
                if (terminalResult.Module == null)
                {
                    throw new ArgumentException("Pointer chain must terminate inside a module.", nameof(terminalResult));
                }

                var steps = terminalResult.EnumeratePath().ToList();
                if (steps.Count == 0)
                {
                    throw new ArgumentException("Pointer chain path cannot be empty.", nameof(terminalResult));
                }

                var module = terminalResult.Module;
                var offsets = new List<long>
                {
                    ComputeSignedOffset(module.BaseAddress, steps[0].PointerAddress)
                };

                for (int i = 1; i < steps.Count; i++)
                {
                    var previous = steps[i - 1];
                    var current = steps[i];
                    offsets.Add(ComputeSignedOffset(previous.Value, current.PointerAddress));
                }

                var last = steps[^1];
                offsets.Add(ComputeSignedOffset(last.Value, finalTarget));

                return new PointerChain(module, finalTarget, steps, offsets);
            }
        }

        public sealed class PersistedPointerChain
        {
            public string ModuleName { get; set; } = string.Empty;
            public ulong ModuleBaseHint { get; set; }
            public long[] Offsets { get; set; } = Array.Empty<long>();
            public string FinalTarget { get; set; } = string.Empty;
        }

        public sealed class PointerScanOptions
        {
            /// <summary>
            /// Controls the byte alignment that candidate pointer addresses must satisfy. Use <c>1</c>
            /// to scan every address, or set it to the natural pointer size (4 for 32-bit, 8 for 64-bit)
            /// when you only care about aligned pointers. Increase it further if the target structure
            /// is known to be placed on a coarser boundary.
            /// </summary>
            public int Alignment { get; set; } = 4;
            public bool ModulesOnly { get; set; } = true;
            public int MaxResults { get; set; } = 1024;
            public int MaxDepth { get; set; } = 2;
            /// <summary>
            /// Maximum absolute offset (in bytes) allowed between a pointer's stored value and the
            /// address it is considered to reference. Set to <c>0</c> to require exact matches.
            /// </summary>
            public long MaxOffset { get; set; } = 0x1000;
            public bool ScanCodeSegments { get; set; }
        }

        public sealed class PointerChainOptions
        {
            public int MaxDepth { get; set; } = 2;
            public int MaxChains { get; set; } = 32;
        }

        private static class NativeMethods
        {
            public const int TH32CS_SNAPMODULE = 0x00000008;
            public const int TH32CS_SNAPMODULE32 = 0x00000010;
            public static readonly nint INVALID_HANDLE_VALUE = new nint(-1);
            public const int ERROR_ACCESS_DENIED = 5;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool Module32First(nint hSnapshot, ref MODULEENTRY32 lpme);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool Module32Next(nint hSnapshot, ref MODULEENTRY32 lpme);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(nint handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool IsWow64Process(nint process, out bool wow64Process);

            [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "IsWow64Process2")]
            public static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

            // PSAPI Fallbacks
            [DllImport("psapi.dll", SetLastError = true)]
            public static extern bool EnumProcessModulesEx(nint hProcess, nint lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

            [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern uint GetModuleBaseName(nint hProcess, nint hModule, StringBuilder lpBaseName, uint nSize);

            [DllImport("psapi.dll", SetLastError = true)]
            public static extern bool GetModuleInformation(nint hProcess, nint hModule, out MODULEINFO lpmodinfo, uint cb);

            [StructLayout(LayoutKind.Sequential)]
            public struct MODULEINFO
            {
                public nint lpBaseOfDll;
                public uint SizeOfImage;
                public nint EntryPoint;
            }

            public const uint LIST_MODULES_ALL = 0x03;

            // NTDLL for PEB Walk
            [DllImport("ntdll.dll")]
            public static extern int NtQueryInformationProcess(nint ProcessHandle, int ProcessInformationClass, out nint ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

            [DllImport("ntdll.dll")]
            public static extern int NtQueryInformationProcess(nint ProcessHandle, int ProcessInformationClass, ref PROCESS_BASIC_INFORMATION ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

            public const int ProcessBasicInformation = 0;
            public const int ProcessWow64Information = 26;

            [StructLayout(LayoutKind.Sequential)]
            public struct PROCESS_BASIC_INFORMATION
            {
                public nint Reserved1;
                public nint PebBaseAddress;
                public nint Reserved2_0;
                public nint Reserved2_1;
                public nint UniqueProcessId;
                public nint Reserved3;
            }
        }
    }
}