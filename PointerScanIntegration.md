# Pointer Scan Integration Guide

This build adds a pointer-scanner that mirrors the "Memory Hacking Spiro" pointer workflow while keeping the absolute-address scanner untouched.

## Key types

| Type | Purpose |
| --- | --- |
| `DriverScanTester.PointerScan.PointerScanner` | Direct pointer scans, pointer-chain discovery, persistence helpers. |
| `DriverScanTester.PointerScan.IMemoryReader` | Abstraction for process reads. Plug your RPM/driver reader here. |
| `DriverScanTester.PointerScan.RpmMemoryReader` | Default ReadProcessMemory-backed reader for testing. |
| `DriverScanTester.PointerScan.DelegateMemoryReader` | Delegate-based reader used to route calls through the existing driver. |
| `DriverScanTester.PointerScan.PointerScanner.PointerScanOptions` | Scan configuration (alignment, max results, depth, modules-only, max offset). |
| `DriverScanTester.PointerScan.PointerScanner.PointerChainOptions` | Chain depth / cap configuration. |
| `DriverScanTester.PointerScan.PointerScanner.PointerChain` | Resolved module-relative pointer chain representation. |

## Quick usage from code

```csharp
// Build a reader (RPM shown – swap with your driver implementation)
int pointerSize = PointerScanner.DetectPointerSize(processHandle);
using var reader = new RpmMemoryReader(processHandle, pointerSize);
var scanner = new PointerScanner(processHandle, processId, reader);

var scanOptions = new PointerScanner.PointerScanOptions
{
    Alignment = pointerSize,
    ModulesOnly = false,
    MaxResults = 256,
    MaxDepth = 2, // walk up to two pointer levels during scanning
    MaxOffset = 0x1000 // allow up to ±4KB between stored value and target address
};

var tokenSource = new CancellationTokenSource();
var (start, end) = PointerScanner.SuggestTightRangeAround(targetAddress);
var results = await scanner.ScanAsync(targetAddress, start, end, scanOptions, tokenSource.Token);

// Resolve the closest module-backed chain (depth up to 4 by default)
var chains = await scanner.FindPointerChainsAsync(targetAddress, start, end, scanOptions,
    new PointerScanner.PointerChainOptions { MaxDepth = 4 }, tokenSource.Token);
var bestChain = chains.FirstOrDefault();
if (bestChain != null)
{
    // Persist for later – JSON contains module name, base hint and offsets
    var persisted = bestChain.ToPersisted();
    File.WriteAllText("pointer_chain.json", JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true }));
}
```

*Always read through `IMemoryReader` – do not call `ReadProcessMemory` from the scanner itself. Swap in your driver-based reader as needed.*

## WPF dialog

Open the dialog from anywhere in the app with:

```csharp
var window = new PointerScanWindow(new PointerScanViewModel(scanner))
{
    Owner = Application.Current?.MainWindow
};
window.Show();
```

The view-model exposes bindable fields for target/range/alignment, a `Suggest ±50MB` helper, module-only toggle, result list (greens sorted first with a depth column) and a pointer-chain JSON panel. Cancelling a long scan uses `CancellationToken`.

The Matches window now exposes a **Pointer Scan…** context-menu item that pre-fills the dialog with the selected address and a ±50MB window.

## Persistence format

`PointerScanner.PointerChain.ToPersisted()` produces:

```json
{
  "moduleName": "ares.exe",
  "moduleBaseHint": "0x140000000",
  "offsets": ["0x1234", "0x48", "0x0"],
  "finalTarget": "0x7FF6AABBCCDD"
}
```

Offsets are hex strings. The first offset is relative to `moduleBase`, the middle entries are the additions performed before each intermediate dereference, and the final element is the add applied after the last pointer read to land on `finalTarget` (often 0 for pure pointer chains). Re-resolve by reading the current module base and walking the offsets.

## Practical tips & safety

* On 64-bit games, scanning the entire address space is expensive; prefer tight ranges (the dialog warns when the window exceeds 512 MB).
* Use `PointerScanner.SuggestTightRangeAround` to seed a ±50 MB window.
* Anti-cheat/anti-debug drivers may refuse large RPM requests – the scanner treats short reads as misses. When possible, reuse the driver reader through `DelegateMemoryReader` to benefit from your existing IOCTL throttling/back-off logic.
* The scanner considers pointers whose stored value is within `MaxOffset` bytes of the desired address. Increase this window when targeting structures with large field offsets, or set it to `0` to require exact matches.
* Module enumeration uses Toolhelp snapshots – handles are closed immediately.
* Bitness detection falls back from `IsWow64Process2` to `IsWow64Process` and finally the host pointer size.
* Cancellation is honoured between chunks; extremely fragmented regions may still take time because reads are chunked with carry-over (`ptrSize - 1` bytes) to avoid boundary misses.

This feature set mirrors the old absolute-address workflow – all previous scan/lock logic stays intact. Use the new dialog from the Matches context menu or call the `PointerScanner` API directly when you need module-relative chains.
