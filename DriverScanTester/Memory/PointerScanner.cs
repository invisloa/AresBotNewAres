using DriverScanTester.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Memory
{
    /// <summary>
    /// Describes a pointer scan request. Provide the absolute target address along with the inclusive start and exclusive end
    /// of the address range that should be searched.
    /// </summary>
    [Obsolete("Not used. Use DriverScanTester.PointerScan.PointerScanner instead.")]
    public sealed class PointerScanRequest
    {
        public ulong TargetAddress { get; init; }
        public ulong RangeStart { get; init; }
        public ulong RangeEnd { get; init; }
        public int Alignment { get; init; }
        public int MaxResults { get; init; } = 5000;
        public bool GreenOnly { get; init; }

        /// <summary>
        /// Maximum pointer chain depth.
        /// 1 = direct pointer.
        /// 2 = [root] + offset -> pointer near target.
        /// 3 = [[root] + offset] + offset -> pointer near target.
        /// </summary>
        public int MaxDepth { get; init; } = 2;

        /// <summary>
        /// Maximum positive offset allowed between a stored pointer value and the next address in the chain.
        /// Example:
        /// read(root) = 0x20000000
        /// next pointer address = 0x20000090
        /// offset = 0x90
        /// </summary>
        public ulong MaxOffset { get; init; } = 0x1000;
    }

    /// <summary>
    /// Represents a single pointer hit. Consumers can persist <see cref="PointerAddress"/> plus the module metadata to
    /// rebuild pointer chains later (module base + offsets).
    /// </summary>
[Obsolete("Not used. Use DriverScanTester.PointerScan.PointerScanner instead.")]
    public sealed class PointerScanResult
    {
        public PointerScanResult(ulong pointerAddress, ulong pointsTo, bool isGreen, string? moduleName, ulong distance)
            : this(pointerAddress, pointsTo, isGreen, moduleName, distance, 1, null)
        {
        }

        public PointerScanResult(
            ulong pointerAddress,
            ulong pointsTo,
            bool isGreen,
            string? moduleName,
            ulong distance,
            int depth,
            PointerScanResult? parent)
        {
            PointerAddress = pointerAddress;
            PointsTo = pointsTo;
            IsGreen = isGreen;
            ModuleName = moduleName;
            Distance = distance;
            Depth = depth;
            Parent = parent;
        }

        public ulong PointerAddress { get; }
        public ulong PointsTo { get; }
        public bool IsGreen { get; }
        public string? ModuleName { get; }

        /// <summary>
        /// For Depth 1:
        ///     Distance = TargetAddress - PointsTo
        ///
        /// For Depth > 1:
        ///     Distance = Parent.PointerAddress - PointsTo
        ///
        /// This is the actual pointer-chain offset, not absolute physical distance.
        /// </summary>
        public ulong Distance { get; }

        public int Depth { get; }

        public PointerScanResult? Parent { get; }

        public IReadOnlyList<PointerScanResult> GetPath()
        {
            var path = new List<PointerScanResult>();

            for (var current = this; current != null; current = current.Parent)
            {
                path.Add(current);
            }

            return path;
        }
    }

    [Obsolete("Not used. Use DriverScanTester.PointerScan.PointerScanner instead.")]
    public sealed class PointerScanResults
    {
        public PointerScanResults(IReadOnlyList<PointerScanResult> results, bool truncated)
        {
            Results = results;
            WasTruncated = truncated;
        }

        public IReadOnlyList<PointerScanResult> Results { get; }
        public bool WasTruncated { get; }
    }

    /// <summary>
    /// Scans a process for pointers that match a given absolute address. The scanner relies on an <see cref="IMemoryReader"/>
    /// so callers can plug in the custom driver or fall back to ReadProcessMemory.
    /// </summary>
    [Obsolete("Not used. Use DriverScanTester.PointerScan.PointerScanner instead.")]
    public sealed class PointerScanner
    {
        private const int DefaultChunkSize = 1 << 20; // 1 MiB

        private readonly nint _processHandle;
        private readonly uint _processId;
        private readonly IMemoryReader _reader;
        private readonly int _pointerSize;
        private readonly int _chunkSize;
        private readonly Action<string>? _logger;

        public PointerScanner(nint processHandle, uint processId, IMemoryReader reader, int pointerSize, Action<string>? logger = null, int chunkSize = DefaultChunkSize)
        {
            if (pointerSize != 4 && pointerSize != 8)
                throw new ArgumentOutOfRangeException(nameof(pointerSize), "Pointer size must be 4 or 8 bytes.");

            _processHandle = processHandle;
            _processId = processId;
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _pointerSize = pointerSize;
            _chunkSize = Math.Max(pointerSize, chunkSize);
            _logger = logger;
        }

        public Task<PointerScanResults> ScanAsync(PointerScanRequest request, CancellationToken cancellationToken)
        {
            return Task.Run(() => ScanInternal(request, cancellationToken), cancellationToken);
        }

        private PointerScanResults ScanInternal(PointerScanRequest request, CancellationToken cancellationToken)
        {
            if (_processHandle == nint.Zero)
                throw new InvalidOperationException("Process handle is not available.");

            if (request.RangeEnd <= request.RangeStart)
                throw new ArgumentException("Range end must be greater than range start.");

            int alignment = request.Alignment > 0 ? request.Alignment : _pointerSize;
            ulong alignmentU = (ulong)alignment;
            int maxResults = request.MaxResults > 0 ? request.MaxResults : 5000;
            int maxDepth = request.MaxDepth > 0 ? request.MaxDepth : 1;
            ulong maxOffset = request.MaxOffset;

            var results = new List<PointerScanResult>();
            var frontier = new Dictionary<ulong, List<PointerScanResult>>();
            var moduleMap = ModuleMap.Create(_processId);

            bool truncated = false;

            _logger?.Invoke($"Pointer scan started. Target=0x{request.TargetAddress:X}, Range=0x{request.RangeStart:X}-0x{request.RangeEnd:X}, Depth={maxDepth}, MaxOffset=0x{maxOffset:X}");

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (depth > 1 && frontier.Count == 0)
                {
                    _logger?.Invoke($"Depth {depth}: stopped because previous frontier is empty.");
                    break;
                }

                bool scanFullMemoryForThisDepth = maxDepth > 1 && depth < maxDepth;

                ulong scanStart = scanFullMemoryForThisDepth ? 0UL : request.RangeStart;
                ulong scanEnd = scanFullMemoryForThisDepth ? GetProcessScanEndExclusive() : request.RangeEnd;

                _logger?.Invoke(
                    scanFullMemoryForThisDepth
                        ? $"Depth {depth}: scanning full readable process memory for intermediate pointers."
                        : $"Depth {depth}: scanning requested root range 0x{request.RangeStart:X}-0x{request.RangeEnd:X}.");

                var nextFrontier = new Dictionary<ulong, List<PointerScanResult>>();

                KeyValuePair<ulong, List<PointerScanResult>>[]? sortedFrontier = null;
                ulong[]? sortedFrontierKeys = null;

                if (depth > 1 && maxOffset > 0)
                {
                    sortedFrontier = new KeyValuePair<ulong, List<PointerScanResult>>[frontier.Count];
                    ((ICollection<KeyValuePair<ulong, List<PointerScanResult>>>)frontier).CopyTo(sortedFrontier, 0);

                    Array.Sort(sortedFrontier, static (a, b) => a.Key.CompareTo(b.Key));

                    sortedFrontierKeys = new ulong[sortedFrontier.Length];
                    for (int i = 0; i < sortedFrontier.Length; i++)
                    {
                        sortedFrontierKeys[i] = sortedFrontier[i].Key;
                    }
                }

                ScanAddressRange(
                    scanStart,
                    scanEnd,
                    request,
                    depth,
                    maxDepth,
                    maxOffset,
                    alignmentU,
                    moduleMap,
                    frontier,
                    sortedFrontier,
                    sortedFrontierKeys,
                    nextFrontier,
                    results,
                    cancellationToken);

                _logger?.Invoke($"Depth {depth}: visibleResults={results.Count}, nextFrontier={nextFrontier.Count}");

                frontier = nextFrontier;
            }

            results.Sort(CompareResults);

            if (results.Count > maxResults)
            {
                results = results.GetRange(0, maxResults);
                truncated = true;
            }

            _logger?.Invoke($"Pointer scan complete. Results={results.Count}, Truncated={truncated}");

            return new PointerScanResults(results, truncated);
        }

        private void ScanAddressRange(
            ulong rangeStart,
            ulong rangeEnd,
            PointerScanRequest request,
            int depth,
            int maxDepth,
            ulong maxOffset,
            ulong alignment,
            ModuleMap moduleMap,
            Dictionary<ulong, List<PointerScanResult>> frontier,
            KeyValuePair<ulong, List<PointerScanResult>>[]? sortedFrontier,
            ulong[]? sortedFrontierKeys,
            Dictionary<ulong, List<PointerScanResult>> nextFrontier,
            List<PointerScanResult> results,
            CancellationToken cancellationToken)
        {
            if (rangeEnd <= rangeStart)
                return;

            int overlap = Math.Max(0, _pointerSize - 1);
            var workingBuffer = new byte[_chunkSize + overlap];
            var chunkBuffer = new byte[_chunkSize];

            ulong address = rangeStart;
            int mbiSize = Marshal.SizeOf<PsApi.MEMORY_BASIC_INFORMATION64>();

            while (address < rangeEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (PsApi.VirtualQueryEx(_processHandle, (nuint)address, out var mbi, (nuint)mbiSize) == 0)
                {
                    break;
                }

                ulong regionStart = mbi.BaseAddress;
                ulong regionEnd = AddSaturating(regionStart, mbi.RegionSize);

                if (regionEnd <= regionStart)
                    break;

                if (regionEnd <= address)
                    break;

                if (IsReadableRegion(in mbi))
                {
                    ulong scanStart = Math.Max(regionStart, rangeStart);
                    ulong scanEnd = Math.Min(regionEnd, rangeEnd);

                    if (scanEnd > scanStart)
                    {
                        ProcessRegion(
                            scanStart,
                            scanEnd,
                            request,
                            depth,
                            maxDepth,
                            maxOffset,
                            alignment,
                            moduleMap,
                            frontier,
                            sortedFrontier,
                            sortedFrontierKeys,
                            nextFrontier,
                            results,
                            workingBuffer,
                            chunkBuffer,
                            cancellationToken);
                    }
                }

                address = regionEnd;
            }
        }

        private void ProcessRegion(
            ulong regionStart,
            ulong regionEnd,
            PointerScanRequest request,
            int depth,
            int maxDepth,
            ulong maxOffset,
            ulong alignment,
            ModuleMap moduleMap,
            Dictionary<ulong, List<PointerScanResult>> frontier,
            KeyValuePair<ulong, List<PointerScanResult>>[]? sortedFrontier,
            ulong[]? sortedFrontierKeys,
            Dictionary<ulong, List<PointerScanResult>> nextFrontier,
            List<PointerScanResult> results,
            byte[] workingBuffer,
            byte[] chunkBuffer,
            CancellationToken cancellationToken)
        {
            ulong current = regionStart;
            int overlap = Math.Max(0, _pointerSize - 1);
            int validBytes = 0;

            while (current < regionEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min((ulong)_chunkSize, regionEnd - current);
                if (bytesToRead <= 0)
                    break;

                int tailBytes = Math.Min(overlap, validBytes);
                if (tailBytes > 0)
                    Buffer.BlockCopy(workingBuffer, validBytes - tailBytes, workingBuffer, 0, tailBytes);

                if (!_reader.Read(current, chunkBuffer, bytesToRead, out int bytesRead) || bytesRead <= 0)
                {
                    _logger?.Invoke($"Pointer scan read failed @ 0x{current:X} (bytes={bytesRead}).");
                    break;
                }

                Buffer.BlockCopy(chunkBuffer, 0, workingBuffer, tailBytes, bytesRead);
                validBytes = tailBytes + bytesRead;

                ulong bufferBase = current - (ulong)tailBytes;

                ScanBuffer(
                    bufferBase,
                    workingBuffer,
                    validBytes,
                    regionStart,
                    regionEnd,
                    request,
                    depth,
                    maxDepth,
                    maxOffset,
                    alignment,
                    moduleMap,
                    frontier,
                    sortedFrontier,
                    sortedFrontierKeys,
                    nextFrontier,
                    results,
                    cancellationToken);

                current += (ulong)bytesRead;
            }
        }

        private void ScanBuffer(
            ulong bufferBase,
            byte[] buffer,
            int validBytes,
            ulong rangeStart,
            ulong rangeEnd,
            PointerScanRequest request,
            int depth,
            int maxDepth,
            ulong maxOffset,
            ulong alignment,
            ModuleMap moduleMap,
            Dictionary<ulong, List<PointerScanResult>> frontier,
            KeyValuePair<ulong, List<PointerScanResult>>[]? sortedFrontier,
            ulong[]? sortedFrontierKeys,
            Dictionary<ulong, List<PointerScanResult>> nextFrontier,
            List<PointerScanResult> results,
            CancellationToken cancellationToken)
        {
            if (validBytes < _pointerSize)
                return;

            if (rangeEnd <= rangeStart || rangeEnd <= (ulong)_pointerSize)
                return;

            ulong bufferEndAddress = bufferBase + (ulong)(validBytes - _pointerSize);
            ulong allowedMaxAddress = bufferEndAddress;

            ulong rangeMaxAddress = rangeEnd - (ulong)_pointerSize;
            if (allowedMaxAddress > rangeMaxAddress)
                allowedMaxAddress = rangeMaxAddress;

            ulong startAddress = bufferBase < rangeStart ? rangeStart : bufferBase;
            if (startAddress > allowedMaxAddress)
                return;

            ulong remainder = startAddress % alignment;
            if (remainder != 0)
            {
                ulong aligned = startAddress + (alignment - remainder);
                if (aligned < startAddress)
                    aligned = startAddress;

                startAddress = aligned;
            }

            if (startAddress > allowedMaxAddress)
                return;

            ulong current = startAddress;

            while (current <= allowedMaxAddress)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int offset = (int)(current - bufferBase);

                ulong value = _pointerSize == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4))
                    : BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));

                if (depth == 1)
                {
                    if (TryComputeOffsetToTarget(value, request.TargetAddress, maxOffset, out ulong distanceToTarget))
                    {
                        AddResult(
                            current,
                            value,
                            distanceToTarget,
                            depth,
                            maxDepth,
                            parent: null,
                            request,
                            moduleMap,
                            nextFrontier,
                            results);
                    }
                }
                else
                {
                    if (maxOffset == 0)
                    {
                        if (frontier.TryGetValue(value, out var parents))
                        {
                            foreach (var parent in parents)
                            {
                                AddResult(
                                    current,
                                    value,
                                    distance: 0,
                                    depth,
                                    maxDepth,
                                    parent,
                                    request,
                                    moduleMap,
                                    nextFrontier,
                                    results);
                            }
                        }
                    }
                    else
                    {
                        if (sortedFrontier == null || sortedFrontierKeys == null)
                            return;

                        ulong minKey = value;
                        ulong maxKey = AddSaturating(value, maxOffset);

                        int idx = Array.BinarySearch(sortedFrontierKeys, minKey);
                        if (idx < 0)
                            idx = ~idx;

                        while (idx < sortedFrontierKeys.Length && sortedFrontierKeys[idx] <= maxKey)
                        {
                            ulong parentPointerAddress = sortedFrontierKeys[idx];

                            // Correct chain direction:
                            // parent.PointerAddress = value + offset
                            // offset = parent.PointerAddress - value
                            ulong distanceToParent = parentPointerAddress - value;

                            foreach (var parent in sortedFrontier[idx].Value)
                            {
                                AddResult(
                                    current,
                                    value,
                                    distanceToParent,
                                    depth,
                                    maxDepth,
                                    parent,
                                    request,
                                    moduleMap,
                                    nextFrontier,
                                    results);
                            }

                            idx++;
                        }
                    }
                }

                if (current > ulong.MaxValue - alignment)
                    break;

                current += alignment;
            }
        }

        private void AddResult(
            ulong pointerAddress,
            ulong pointsTo,
            ulong distance,
            int depth,
            int maxDepth,
            PointerScanResult? parent,
            PointerScanRequest request,
            ModuleMap moduleMap,
            Dictionary<ulong, List<PointerScanResult>> nextFrontier,
            List<PointerScanResult> results)
        {
            bool isGreen = moduleMap.TryFindModule(pointerAddress, out var module);
            string? moduleName = isGreen ? module!.Name : null;

            var result = new PointerScanResult(
                pointerAddress,
                pointsTo,
                isGreen,
                moduleName,
                distance,
                depth,
                parent);

            bool isInsideRequestedRootRange = pointerAddress >= request.RangeStart && pointerAddress < request.RangeEnd;
            bool shouldShow = isInsideRequestedRootRange;

            if (request.GreenOnly && !isGreen)
                shouldShow = false;

            if (shouldShow)
                results.Add(result);

            // Even when not shown, this result is required as an intermediate pointer for deeper chains.
            if (depth < maxDepth)
            {
                if (!nextFrontier.TryGetValue(pointerAddress, out var list))
                {
                    list = new List<PointerScanResult>();
                    nextFrontier[pointerAddress] = list;
                }

                list.Add(result);
            }
        }

        private static bool TryComputeOffsetToTarget(
            ulong value,
            ulong targetAddress,
            ulong maxOffset,
            out ulong distance)
        {
            distance = 0;

            if (value > targetAddress)
                return false;

            distance = targetAddress - value;
            return distance <= maxOffset;
        }

        private static ulong AddSaturating(ulong value, ulong add)
        {
            ulong result = value + add;
            return result < value ? ulong.MaxValue : result;
        }

        private ulong GetProcessScanEndExclusive()
        {
            return _pointerSize == 4
                ? 0x100000000UL
                : 0x0000800000000000UL;
        }

        private static int CompareResults(PointerScanResult x, PointerScanResult y)
        {
            int depthCompare = x.Depth.CompareTo(y.Depth);
            if (depthCompare != 0)
                return depthCompare;

            int green = y.IsGreen.CompareTo(x.IsGreen); // true first
            if (green != 0)
                return green;

            int distanceCompare = x.Distance.CompareTo(y.Distance);
            if (distanceCompare != 0)
                return distanceCompare;

            return x.PointerAddress.CompareTo(y.PointerAddress);
        }

        private static bool IsReadableRegion(in PsApi.MEMORY_BASIC_INFORMATION64 mbi)
        {
            if (mbi.RegionSize == 0)
                return false;

            if (mbi.State != PsApi.MemState.MEM_COMMIT)
                return false;

            if ((mbi.Protect & PsApi.MemProtect.PAGE_GUARD) != 0)
                return false;

            if (mbi.Protect == PsApi.MemProtect.PAGE_NOACCESS || mbi.Protect == 0)
                return false;

            PsApi.MemProtect baseProt = mbi.Protect & ~(PsApi.MemProtect.PAGE_GUARD | PsApi.MemProtect.PAGE_NOCACHE | PsApi.MemProtect.PAGE_WRITECOMBINE);

            return baseProt == PsApi.MemProtect.PAGE_READONLY ||
                   baseProt == PsApi.MemProtect.PAGE_READWRITE ||
                   baseProt == PsApi.MemProtect.PAGE_WRITECOPY ||
                   baseProt == PsApi.MemProtect.PAGE_EXECUTE_READ ||
                   baseProt == PsApi.MemProtect.PAGE_EXECUTE_READWRITE ||
                   baseProt == PsApi.MemProtect.PAGE_EXECUTE_WRITECOPY;
        }
    }
}