extern "C" {
#include <ntifs.h>
}

extern "C" {
    NTKERNELAPI
        NTSTATUS
        MmCopyVirtualMemory(
            _In_  PEPROCESS       SourceProcess,
            _In_  PVOID           SourceAddress,
            _In_  PEPROCESS       TargetProcess,
            _Out_ PVOID           TargetAddress,
            _In_  SIZE_T          BufferSize,
            _In_  KPROCESSOR_MODE PreviousMode,
            _Out_ PSIZE_T         ReturnSize
        );
}

namespace driver
{
    constexpr ULONG  MAX_BITMAP_BYTES = 1u << 20;
    constexpr SIZE_T CHUNK_BYTES = 256u * 1024u;
    constexpr ULONG  CACHE_PAGE_LEN = PAGE_SIZE;
    constexpr ULONG  NUM_BUCKETS = 4096;

    static __forceinline SIZE_T  min_sz(SIZE_T a, SIZE_T b) { return a < b ? a : b; }

    typedef struct _PAGE_NODE {
        UINT64       Base;
        PUCHAR       Data;
        UINT32       Len;
        _PAGE_NODE* Next;
    } PAGE_NODE;

    typedef struct _DEVICE_EXTENSION {
        HANDLE       PrevPid;
        PAGE_NODE* Buckets[NUM_BUCKETS];
        FAST_MUTEX   PrevLock;
    } DEVICE_EXTENSION, * PDEVICE_EXTENSION;

    static __forceinline ULONG HashPage(UINT64 base) { return (ULONG)((base >> 12) & (NUM_BUCKETS - 1)); }

    static void PrevClear(PDEVICE_EXTENSION ext)
    {
        ExAcquireFastMutex(&ext->PrevLock);
        for (auto& head : ext->Buckets) {
            PAGE_NODE* p = head; head = nullptr;
            while (p) {
                PAGE_NODE* n = p->Next;
                if (p->Data) ExFreePoolWithTag(p->Data, 'pcpg');
                ExFreePoolWithTag(p, 'pcpg');
                p = n;
            }
        }
        ext->PrevPid = nullptr;
        ExReleaseFastMutex(&ext->PrevLock);
    }

    static NTSTATUS PrevPutPage(PDEVICE_EXTENSION ext, UINT64 base, const UCHAR* src, ULONG len)
    {
        NTSTATUS st = STATUS_SUCCESS;
        ExAcquireFastMutex(&ext->PrevLock);

        ULONG h = HashPage(base);
        PAGE_NODE* node = ext->Buckets[h];
        while (node && node->Base != base) { node = node->Next; }

        if (!node) {
            node = (PAGE_NODE*)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(PAGE_NODE), 'pcpg');
            if (!node) { st = STATUS_INSUFFICIENT_RESOURCES; goto out; }
            node->Base = base;
            node->Data = nullptr;
            node->Len = 0;
            node->Next = ext->Buckets[h];
            ext->Buckets[h] = node;
        }

        if (!node->Data || node->Len != len) {
            if (node->Data) ExFreePoolWithTag(node->Data, 'pcpg');
            node->Data = (PUCHAR)ExAllocatePool2(POOL_FLAG_NON_PAGED, len, 'pcpg');
            if (!node->Data) { node->Len = 0; st = STATUS_INSUFFICIENT_RESOURCES; goto out; }
            node->Len = len;
        }

        RtlCopyMemory(node->Data, src, len);

    out:
        ExReleaseFastMutex(&ext->PrevLock);
        return st;
    }

    namespace codes {
        constexpr ULONG read = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x697, METHOD_BUFFERED, FILE_SPECIAL_ACCESS);
        constexpr ULONG write = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x698, METHOD_BUFFERED, FILE_SPECIAL_ACCESS);
        constexpr ULONG scan = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69A, METHOD_OUT_DIRECT, FILE_SPECIAL_ACCESS);
        constexpr ULONG prev_reset = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69C, METHOD_BUFFERED, FILE_SPECIAL_ACCESS);
        constexpr ULONG prev_push = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69D, METHOD_BUFFERED, FILE_SPECIAL_ACCESS);
        constexpr ULONG scan_cmp_cached = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x69E, METHOD_OUT_DIRECT, FILE_SPECIAL_ACCESS);
    }

    enum : ULONG
    {
        SCAN_ELEM_BYTE = 0x01, // 1 byte
        SCAN_ELEM_SHORT = 0x02, // 2 bytes
        SCAN_ELEM_LONG64 = 0x04, // existing: 8 bytes (your previous "LONG")
        SCAN_ELEM_CLONG = 0x08, // NEW: C long on Windows (4 bytes)

        SCAN_PRED_EQ = 0x10,
        SCAN_PRED_NE = 0x20,
        SCAN_PRED_GT = 0x40,
        SCAN_PRED_LT = 0x80,
    };

#pragma pack(push,1)
    struct Request64
    {
        HANDLE  ProcessId;
        ULONG64 Target;
        ULONG64 Buffer;
        UINT32  Size;
        UINT64  ReturnSize;
    };

    struct ScanArgs
    {
        HANDLE  ProcessId;
        ULONG64 Address;
        UINT32  Length;
        UINT32  Flags;

        // Targets for direct-equality scans (SCAN_PRED_EQ in 'scan')
        UINT8   TargetByte;    // 1-byte
        INT16   TargetShort;   // 2-byte (signed value supplied; compare byte-wise equality)
        INT64   TargetLong64;  // 8-byte (existing)

        INT32   TargetCLong;   // NEW: 4-byte C long (Windows)
    };

    struct PrevInit
    {
        HANDLE  ProcessId;
        UINT32  Flags;
    };

    struct PrevPageHdr
    {
        UINT64  PageBase;
        UINT32  DataLen;
    };
#pragma pack(pop)

    static __forceinline bool HasExactlyOne(ULONG value, ULONG mask)
    {
        ULONG v = value & mask;
        return v && !(v & (v - 1));
    }

    static __forceinline ULONG ElemSizeFromFlags(ULONG flags)
    {
        if (flags & SCAN_ELEM_LONG64) return 8u;
        if (flags & SCAN_ELEM_CLONG)  return 4u; // C long on Windows
        if (flags & SCAN_ELEM_SHORT)  return 2u;
        return 1u;
    }

    static __forceinline NTSTATUS ValidateLengthForBitmap(ULONG length, ULONG elemSize)
    {
        if (length == 0) return STATUS_INVALID_PARAMETER;
        if ((length % elemSize) != 0) return STATUS_INVALID_PARAMETER;
        const ULONG elemCount = length / elemSize;
        const ULONG maxElems = MAX_BITMAP_BYTES * 8u;
        return (elemCount > maxElems) ? STATUS_INVALID_PARAMETER : STATUS_SUCCESS;
    }

#define COMPLETE(_irp,_st,_info) do { \
    (_irp)->IoStatus.Status = (_st);  \
    (_irp)->IoStatus.Information = (_info); \
    IoCompleteRequest((_irp), IO_NO_INCREMENT); \
    return (_st); \
} while(0)

    _Function_class_(DRIVER_DISPATCH)
        static NTSTATUS create(_In_ PDEVICE_OBJECT dev, _Inout_ PIRP irp)
    {
        UNREFERENCED_PARAMETER(dev);
        irp->IoStatus.Status = STATUS_SUCCESS;
        IoCompleteRequest(irp, IO_NO_INCREMENT);
        return STATUS_SUCCESS;
    }

    _Function_class_(DRIVER_DISPATCH)
        static NTSTATUS close(_In_ PDEVICE_OBJECT dev, _Inout_ PIRP irp)
    {
        UNREFERENCED_PARAMETER(dev);
        irp->IoStatus.Status = STATUS_SUCCESS;
        IoCompleteRequest(irp, IO_NO_INCREMENT);
        return STATUS_SUCCESS;
    }

    _Function_class_(DRIVER_DISPATCH)
        static NTSTATUS device_control(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
    {
        PIO_STACK_LOCATION stk = IoGetCurrentIrpStackLocation(Irp);
        ULONG ioctl = stk->Parameters.DeviceIoControl.IoControlCode;
        constexpr ULONG REQ_NEED = sizeof(Request64);
        NTSTATUS status = STATUS_UNSUCCESSFUL;
        SIZE_T   xfer = 0;
        PEPROCESS proc = nullptr;

        auto devExt = static_cast<PDEVICE_EXTENSION>(DeviceObject->DeviceExtension);

        if (ioctl == codes::prev_reset)
        {
            if (stk->Parameters.DeviceIoControl.InputBufferLength < sizeof(PrevInit))
                COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            PrevInit init{};
            RtlCopyMemory(&init, Irp->AssociatedIrp.SystemBuffer, sizeof(init));

            const ULONG elemMask = SCAN_ELEM_BYTE | SCAN_ELEM_SHORT | SCAN_ELEM_CLONG | SCAN_ELEM_LONG64;
            if (!HasExactlyOne(init.Flags, elemMask) ||
                (init.Flags & ~elemMask) != 0)
                COMPLETE(Irp, STATUS_INVALID_PARAMETER, 0);

            PrevClear(devExt);
            devExt->PrevPid = init.ProcessId;

            COMPLETE(Irp, STATUS_SUCCESS, 0);
        }

        if (ioctl == codes::prev_push)
        {
            if (stk->Parameters.DeviceIoControl.InputBufferLength < sizeof(PrevPageHdr))
                COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            PUCHAR baseBuf = (PUCHAR)Irp->AssociatedIrp.SystemBuffer;
            PrevPageHdr hdr{};
            RtlCopyMemory(&hdr, baseBuf, sizeof(hdr));

            if (stk->Parameters.DeviceIoControl.InputBufferLength < sizeof(hdr) + hdr.DataLen)
                COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            if (hdr.DataLen != CACHE_PAGE_LEN)
                COMPLETE(Irp, STATUS_INVALID_PARAMETER, 0);

            NTSTATUS stp = PrevPutPage(devExt, hdr.PageBase, baseBuf + sizeof(hdr), hdr.DataLen);
            COMPLETE(Irp, stp, 0);
        }

        if (ioctl == codes::scan_cmp_cached)
        {
            if (stk->Parameters.DeviceIoControl.InputBufferLength < sizeof(ScanArgs))
                COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            ScanArgs args{};
            RtlCopyMemory(&args, Irp->AssociatedIrp.SystemBuffer, sizeof(args));

            const ULONG elemMask = SCAN_ELEM_BYTE | SCAN_ELEM_SHORT | SCAN_ELEM_CLONG | SCAN_ELEM_LONG64;
            if (!HasExactlyOne(args.Flags, elemMask))
                COMPLETE(Irp, STATUS_INVALID_PARAMETER, 0);

            const ULONG predMask = SCAN_PRED_EQ | SCAN_PRED_NE | SCAN_PRED_GT | SCAN_PRED_LT;
            if (!HasExactlyOne(args.Flags, predMask) ||
                (args.Flags & ~(elemMask | predMask)) != 0)
                COMPLETE(Irp, STATUS_INVALID_PARAMETER, 0);

            const ULONG elemSize = ElemSizeFromFlags(args.Flags);
            NTSTATUS v = ValidateLengthForBitmap(args.Length, elemSize);
            if (!NT_SUCCESS(v)) COMPLETE(Irp, v, 0);

            const ULONG elemCount = args.Length / elemSize;
            const ULONG bitmapLen = (elemCount + 7u) / 8u;

            PMDL mdl = Irp->MdlAddress;
            if (!mdl) COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            ULONG outCap = MmGetMdlByteCount(mdl);
            if (outCap < bitmapLen) COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            PUCHAR outBm = (PUCHAR)MmGetSystemAddressForMdlSafe(mdl, NormalPagePriority);
            if (!outBm) COMPLETE(Irp, STATUS_INSUFFICIENT_RESOURCES, 0);

            if (bitmapLen) RtlZeroMemory(outBm, bitmapLen);

            if (devExt->PrevPid != args.ProcessId)
                COMPLETE(Irp, STATUS_INVALID_DEVICE_STATE, 0);

            status = PsLookupProcessByProcessId(args.ProcessId, &proc);
            if (!NT_SUCCESS(status)) COMPLETE(Irp, status, 0);

            PUCHAR scratch = (PUCHAR)ExAllocatePool2(POOL_FLAG_NON_PAGED, CHUNK_BYTES, 'mbps');
            if (!scratch) {
                ObDereferenceObject(proc);
                COMPLETE(Irp, STATUS_INSUFFICIENT_RESOURCES, 0);
            }

            enum class cmp_t : UCHAR { EQ, NE, GT, LT };
            const cmp_t cmp =
                (args.Flags & SCAN_PRED_EQ) ? cmp_t::EQ :
                (args.Flags & SCAN_PRED_NE) ? cmp_t::NE :
                (args.Flags & SCAN_PRED_GT) ? cmp_t::GT :
                cmp_t::LT;

            SIZE_T remaining = args.Length;
            SIZE_T srcOffset = 0;
            SIZE_T elemIndex = 0;

            while (remaining) {
                SIZE_T toRead = min_sz(remaining, CHUNK_BYTES);
                toRead -= (toRead % elemSize);

                SIZE_T got = 0;
                NTSTATUS st2 = MmCopyVirtualMemory(
                    proc,
                    (PUCHAR)(args.Address) + srcOffset,
                    PsGetCurrentProcess(),
                    scratch,
                    toRead,
                    KernelMode,
                    &got);

                if (!NT_SUCCESS(st2) && got == 0)
                    break;

                SIZE_T localOff = 0;
                while (localOff < got) {
                    const UINT64 curVa = args.Address + srcOffset + localOff;
                    const UINT64 pageBase = curVa & ~((UINT64)0xFFF);
                    const ULONG  pageOff = (ULONG)(curVa - pageBase);

                    PAGE_NODE* node = devExt->Buckets[HashPage(pageBase)];
                    while (node && node->Base != pageBase) node = node->Next;
                    PUCHAR prevPage = node ? node->Data : nullptr;
                    ULONG  prevLen = node ? node->Len : 0;

                    const SIZE_T bytesLeft = (SIZE_T)(got - localOff);

                    if (!prevPage) {
                        SIZE_T toBoundary = min_sz(bytesLeft, (SIZE_T)(0x1000 - pageOff));
                        SIZE_T skipBytes = toBoundary - (toBoundary % elemSize);
                        elemIndex += (skipBytes / elemSize);
                        localOff += toBoundary;
                        continue;
                    }

                    SIZE_T spanBytes = min_sz(bytesLeft, (SIZE_T)(prevLen - pageOff));
                    spanBytes -= (spanBytes % elemSize);
                    if (spanBytes == 0) {
                        SIZE_T advance = min_sz(bytesLeft, (SIZE_T)(0x1000 - pageOff));
                        localOff += advance;
                        continue;
                    }

                    const UCHAR* cur = (const UCHAR*)scratch + localOff;
                    const UCHAR* prv = prevPage + pageOff;

                    if (elemSize == 1) {
                        switch (cmp) {
                        case cmp_t::EQ: for (SIZE_T i = 0; i < spanBytes; ++i, ++elemIndex) if (cur[i] == prv[i]) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::NE: for (SIZE_T i = 0; i < spanBytes; ++i, ++elemIndex) if (cur[i] != prv[i]) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::GT: for (SIZE_T i = 0; i < spanBytes; ++i, ++elemIndex) if (cur[i] > prv[i]) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::LT: for (SIZE_T i = 0; i < spanBytes; ++i, ++elemIndex) if (cur[i] < prv[i]) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        }
                    }
                    else if (elemSize == 2) {
                        switch (cmp) {
                        case cmp_t::EQ: for (SIZE_T i = 0; i < spanBytes; i += 2, ++elemIndex) if (*(UNALIGNED const USHORT*)(cur + i) == *(UNALIGNED const USHORT*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::NE: for (SIZE_T i = 0; i < spanBytes; i += 2, ++elemIndex) if (*(UNALIGNED const USHORT*)(cur + i) != *(UNALIGNED const USHORT*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::GT: for (SIZE_T i = 0; i < spanBytes; i += 2, ++elemIndex) if (*(UNALIGNED const USHORT*)(cur + i) > *(UNALIGNED const USHORT*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::LT: for (SIZE_T i = 0; i < spanBytes; i += 2, ++elemIndex) if (*(UNALIGNED const USHORT*)(cur + i) < *(UNALIGNED const USHORT*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        }
                    }
                    else if (elemSize == 4) { // NEW: C long (Windows)
                        switch (cmp) {
                        case cmp_t::EQ: for (SIZE_T i = 0; i < spanBytes; i += 4, ++elemIndex) if (*(UNALIGNED const ULONG*)(cur + i) == *(UNALIGNED const ULONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::NE: for (SIZE_T i = 0; i < spanBytes; i += 4, ++elemIndex) if (*(UNALIGNED const ULONG*)(cur + i) != *(UNALIGNED const ULONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::GT: for (SIZE_T i = 0; i < spanBytes; i += 4, ++elemIndex) if (*(UNALIGNED const ULONG*)(cur + i) > *(UNALIGNED const ULONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::LT: for (SIZE_T i = 0; i < spanBytes; i += 4, ++elemIndex) if (*(UNALIGNED const ULONG*)(cur + i) < *(UNALIGNED const ULONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        }
                    }
                    else { // elemSize == 8 (existing)
                        switch (cmp) {
                        case cmp_t::EQ: for (SIZE_T i = 0; i < spanBytes; i += 8, ++elemIndex) if (*(UNALIGNED const ULONGLONG*)(cur + i) == *(UNALIGNED const ULONGLONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::NE: for (SIZE_T i = 0; i < spanBytes; i += 8, ++elemIndex) if (*(UNALIGNED const ULONGLONG*)(cur + i) != *(UNALIGNED const ULONGLONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::GT: for (SIZE_T i = 0; i < spanBytes; i += 8, ++elemIndex) if (*(UNALIGNED const ULONGLONG*)(cur + i) > *(UNALIGNED const ULONGLONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        case cmp_t::LT: for (SIZE_T i = 0; i < spanBytes; i += 8, ++elemIndex) if (*(UNALIGNED const ULONGLONG*)(cur + i) < *(UNALIGNED const ULONGLONG*)(prv + i)) outBm[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u)); break;
                        }
                    }

                    localOff += spanBytes;
                }

                srcOffset += got;
                remaining -= (ULONG)got;

                if (!NT_SUCCESS(st2) && got > 0) break;
            }

            ExFreePoolWithTag(scratch, 'mbps');
            ObDereferenceObject(proc);

            if (bitmapLen && (elemCount & 7u))
                outBm[bitmapLen - 1] &= (UCHAR)((1u << (elemCount & 7u)) - 1u);

            COMPLETE(Irp, STATUS_SUCCESS, bitmapLen);
        }

        if (ioctl == codes::scan)
        {
            if (stk->Parameters.DeviceIoControl.InputBufferLength < sizeof(ScanArgs))
                COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            ScanArgs args{};
            RtlCopyMemory(&args, Irp->AssociatedIrp.SystemBuffer, sizeof(ScanArgs));

            const ULONG elemMask = SCAN_ELEM_BYTE | SCAN_ELEM_SHORT | SCAN_ELEM_CLONG | SCAN_ELEM_LONG64;
            if (!HasExactlyOne(args.Flags, elemMask))
                COMPLETE(Irp, STATUS_INVALID_PARAMETER, 0);

            // Direct scan only supports equality predicate (as before)
            if ((args.Flags & ~(elemMask | SCAN_PRED_EQ)) != 0 ||
                (args.Flags & SCAN_PRED_EQ) == 0)
                COMPLETE(Irp, STATUS_INVALID_PARAMETER, 0);

            const ULONG elemSize = ElemSizeFromFlags(args.Flags);
            NTSTATUS v2 = ValidateLengthForBitmap(args.Length, elemSize);
            if (!NT_SUCCESS(v2)) COMPLETE(Irp, v2, 0);

            const ULONG elemCount = args.Length / elemSize;
            const ULONG bitmapLen = (elemCount + 7u) / 8u;

            PMDL mdl = Irp->MdlAddress;
            if (!mdl) COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            ULONG outCap = MmGetMdlByteCount(mdl);
            if (outCap < bitmapLen) COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);

            PUCHAR outBitmap = (PUCHAR)MmGetSystemAddressForMdlSafe(mdl, NormalPagePriority);
            if (!outBitmap) COMPLETE(Irp, STATUS_INSUFFICIENT_RESOURCES, 0);

            if (bitmapLen) RtlZeroMemory(outBitmap, bitmapLen);

            status = PsLookupProcessByProcessId(args.ProcessId, &proc);
            if (!NT_SUCCESS(status)) COMPLETE(Irp, status, 0);

            PVOID scratch = ExAllocatePool2(POOL_FLAG_NON_PAGED, CHUNK_BYTES, 'mbps');
            if (!scratch) {
                ObDereferenceObject(proc);
                COMPLETE(Irp, STATUS_INSUFFICIENT_RESOURCES, 0);
            }

            SIZE_T remaining = args.Length;
            SIZE_T srcOffset = 0;
            SIZE_T elemIndex = 0;

            const UINT8     tgt8 = args.TargetByte;
            const USHORT    tgt16 = (USHORT)args.TargetShort;
            const ULONG     tgt32 = (ULONG)args.TargetCLong;  // NEW: 4-byte
            const ULONGLONG tgt64 = (ULONGLONG)args.TargetLong64;

            while (remaining)
            {
                SIZE_T toRead = min_sz(remaining, CHUNK_BYTES);
                toRead -= (toRead % elemSize);

                SIZE_T got = 0;
                NTSTATUS st2 = MmCopyVirtualMemory(
                    proc,
                    (PUCHAR)(args.Address) + srcOffset,
                    PsGetCurrentProcess(),
                    scratch,
                    toRead,
                    KernelMode,
                    &got);

                if (!NT_SUCCESS(st2) && got == 0)
                    break;

                const PUCHAR p = (const PUCHAR)scratch;
                if (elemSize == 1)
                {
                    for (SIZE_T i = 0; i < got; ++i, ++elemIndex)
                        if (p[i] == tgt8)
                            outBitmap[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u));
                }
                else if (elemSize == 2)
                {
                    const SIZE_T use = got & ~(SIZE_T)1;
                    for (SIZE_T i = 0; i < use; i += 2, ++elemIndex) {
                        USHORT v16 = *(UNALIGNED const USHORT*)(p + i);
                        if (v16 == tgt16)
                            outBitmap[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u));
                    }
                }
                else if (elemSize == 4) // NEW: C long (Windows)
                {
                    const SIZE_T use = got & ~(SIZE_T)3;
                    for (SIZE_T i = 0; i < use; i += 4, ++elemIndex) {
                        ULONG v32 = *(UNALIGNED const ULONG*)(p + i);
                        if (v32 == tgt32)
                            outBitmap[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u));
                    }
                }
                else // elemSize == 8 (existing)
                {
                    const SIZE_T use = got & ~(SIZE_T)7;
                    for (SIZE_T i = 0; i < use; i += 8, ++elemIndex) {
                        ULONGLONG v64 = *(UNALIGNED const ULONGLONG*)(p + i);
                        if (v64 == tgt64)
                            outBitmap[elemIndex >> 3] |= (UCHAR)(1u << (elemIndex & 7u));
                    }
                }

                srcOffset += got;
                remaining -= (ULONG)got;

                if (!NT_SUCCESS(st2) && got > 0)
                    break;
            }

            ExFreePoolWithTag(scratch, 'mbps');
            ObDereferenceObject(proc);

            if (bitmapLen && (elemCount & 7u))
                outBitmap[bitmapLen - 1] &= (UCHAR)((1u << (elemCount & 7u)) - 1u);

            COMPLETE(Irp, STATUS_SUCCESS, bitmapLen);
        }

        if (stk->Parameters.DeviceIoControl.InputBufferLength < REQ_NEED ||
            stk->Parameters.DeviceIoControl.OutputBufferLength < REQ_NEED)
        {
            COMPLETE(Irp, STATUS_BUFFER_TOO_SMALL, 0);
        }

        auto* in = reinterpret_cast<Request64*>(Irp->AssociatedIrp.SystemBuffer);
        HANDLE  pid = in->ProcessId;
        PVOID   target_ptr = reinterpret_cast<PVOID>(in->Target);
        PVOID   buffer_ptr = reinterpret_cast<PVOID>(in->Buffer);
        SIZE_T  size = in->Size;

        switch (ioctl)
        {
        case codes::read: {
            status = PsLookupProcessByProcessId(pid, &proc);
            if (NT_SUCCESS(status)) {
                SIZE_T got = 0;
                NTSTATUS st2 = MmCopyVirtualMemory(
                    proc, target_ptr,
                    PsGetCurrentProcess(), buffer_ptr,
                    size, KernelMode, &got);

                xfer = got;
                ObDereferenceObject(proc);
                status = (!NT_SUCCESS(st2) && got > 0) ? STATUS_SUCCESS : st2;
            }
            break;
        }

        case codes::write: {
            status = PsLookupProcessByProcessId(pid, &proc);
            if (NT_SUCCESS(status)) {
                SIZE_T got = 0;
                NTSTATUS st2 = MmCopyVirtualMemory(
                    PsGetCurrentProcess(), buffer_ptr,
                    proc, target_ptr,
                    size, KernelMode, &got);

                xfer = got;
                ObDereferenceObject(proc);
                status = (!NT_SUCCESS(st2) && got > 0) ? STATUS_SUCCESS : st2;
            }
            break;
        }

        default:
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
        }

        auto* out = reinterpret_cast<Request64*>(Irp->AssociatedIrp.SystemBuffer);
        out->ReturnSize = static_cast<UINT64>(xfer);
        out->Target = reinterpret_cast<ULONG64>(target_ptr);
        Irp->IoStatus.Status = status;
        Irp->IoStatus.Information = REQ_NEED;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return status;
    }

    _Function_class_(DRIVER_UNLOAD)
        static void DriverUnload(_In_ PDRIVER_OBJECT DriverObject)
    {
        if (DriverObject && DriverObject->DeviceObject)
        {
            auto ext = static_cast<PDEVICE_EXTENSION>(DriverObject->DeviceObject->DeviceExtension);
            if (ext) {
                PrevClear(ext);
            }
        }
        UNICODE_STRING symlink = RTL_CONSTANT_STRING(L"\\DosDevices\\SexyDriver");
        IoDeleteSymbolicLink(&symlink);
        IoDeleteDevice(DriverObject->DeviceObject);
    }

    static NTSTATUS driver_main(_In_ PDRIVER_OBJECT drv, _In_ PUNICODE_STRING)
    {
        UNICODE_STRING dev_name = RTL_CONSTANT_STRING(L"\\Device\\SexyDriver");
        PDEVICE_OBJECT dev_obj = nullptr;
        NTSTATUS st = IoCreateDevice(
            drv, sizeof(DEVICE_EXTENSION),
            &dev_name, FILE_DEVICE_UNKNOWN,
            FILE_DEVICE_SECURE_OPEN, FALSE, &dev_obj);
        if (!NT_SUCCESS(st))
            return st;

        auto ext = static_cast<PDEVICE_EXTENSION>(dev_obj->DeviceExtension);
        if (ext) {
            RtlZeroMemory(ext->Buckets, sizeof(ext->Buckets));
            ExInitializeFastMutex(&ext->PrevLock);
            ext->PrevPid = nullptr;
        }

        UNICODE_STRING sym = RTL_CONSTANT_STRING(L"\\DosDevices\\SexyDriver");
        st = IoCreateSymbolicLink(&sym, &dev_name);
        if (!NT_SUCCESS(st))
        {
            IoDeleteDevice(dev_obj);
            return st;
        }

        ClearFlag(dev_obj->Flags, DO_BUFFERED_IO);
        SetFlag(dev_obj->Flags, DO_DIRECT_IO);

        drv->MajorFunction[IRP_MJ_CREATE] = create;
        drv->MajorFunction[IRP_MJ_CLOSE] = close;
        drv->MajorFunction[IRP_MJ_DEVICE_CONTROL] = device_control;
        drv->DriverUnload = DriverUnload;

        ClearFlag(dev_obj->Flags, DO_DEVICE_INITIALIZING);
        return STATUS_SUCCESS;
    }
}

extern "C"
NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING)
{
    return driver::driver_main(DriverObject, nullptr);
}
