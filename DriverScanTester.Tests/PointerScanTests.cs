using DriverScanTester.PointerScan;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class PointerScanTests
    {
        private static readonly PointerScanner.ModuleInfo TestModule = new("Ares.exe", 0x10000000, 0x10000);

        // ====================================================================
        // Test 1 — Depth 1
        // Chain: [Ares.exe + 0x1000] + 0x90 == 0x20000090
        // Memory: [rootAddr] = 0x20000000
        // Offsets expected: 0x1000, 0x90
        // ====================================================================
        [Fact]
        public void PointerChainCreate_Depth1_CorrectOffsets()
        {
            ulong moduleBase = TestModule.BaseAddress;
            ulong rootAddr = moduleBase + 0x1000;
            ulong value = 0x20000000;
            ulong finalTarget = value + 0x90;

            var depth1 = new PointerScanner.PointerScanResult(
                rootAddr, value, TestModule, 0x90, 1, null, 0x1000);

            var chain = PointerScanner.PointerChain.Create(depth1, finalTarget);

            Assert.Equal(2, chain.Offsets.Count);
            Assert.Equal(0x1000, chain.Offsets[0]);
            Assert.Equal(0x90, chain.Offsets[1]);
            Assert.Equal(TestModule, chain.Module);
            Assert.Equal(finalTarget, chain.FinalTarget);
        }

        // ====================================================================
        // Test 2 — Depth 2
        // Chain: [[Ares.exe + 0x2000] + 0x90] + 0xC0 == 0x400000C0
        // Memory:
        //   [rootAddr] = 0x30000000  (midBase)
        //   [midAddr]  = 0x40000000
        // Offsets expected: 0x2000, 0x90, 0xC0
        // ====================================================================
        [Fact]
        public void PointerChainCreate_Depth2_CorrectOffsets()
        {
            ulong moduleBase = TestModule.BaseAddress;
            ulong rootAddr = moduleBase + 0x2000;
            ulong midBase = 0x30000000;
            ulong midAddr = midBase + 0x90;
            ulong midValue = 0x40000000;
            ulong finalTarget = midValue + 0xC0;

            var depth1 = new PointerScanner.PointerScanResult(
                midAddr, midValue, null, 0xC0, 1, null, 0);
            var depth2 = new PointerScanner.PointerScanResult(
                rootAddr, midBase, TestModule, 0x90, 2, depth1, 0x2000);

            var chain = PointerScanner.PointerChain.Create(depth2, finalTarget);

            Assert.Equal(3, chain.Offsets.Count);
            Assert.Equal(0x2000, chain.Offsets[0]);
            Assert.Equal(0x90, chain.Offsets[1]);
            Assert.Equal(0xC0, chain.Offsets[2]);
            Assert.Equal(TestModule, chain.Module);
            Assert.Equal(finalTarget, chain.FinalTarget);
        }

        // ====================================================================
        // Test 3 — Depth 3
        // Chain: [[[Ares.exe + 0x3000] + 0x40] + 0x80] + 0xC0 == 0x600000C0
        // Memory:
        //   [rootAddr] = 0x30000000  (p1Base)
        //   [p1Addr]   = 0x50000000  (p2Base)
        //   [p2Addr]   = 0x60000000
        // Offsets expected: 0x3000, 0x40, 0x80, 0xC0
        // ====================================================================
        [Fact]
        public void PointerChainCreate_Depth3_CorrectOffsets()
        {
            ulong moduleBase = TestModule.BaseAddress;
            ulong rootAddr = moduleBase + 0x3000;
            ulong p1Base = 0x30000000;
            ulong p1Addr = p1Base + 0x40;
            ulong p2Base = 0x50000000;
            ulong p2Addr = p2Base + 0x80;
            ulong p2Value = 0x60000000;
            ulong finalTarget = p2Value + 0xC0;

            var depth1 = new PointerScanner.PointerScanResult(
                p2Addr, p2Value, null, 0xC0, 1, null, 0);
            var depth2 = new PointerScanner.PointerScanResult(
                p1Addr, p2Base, null, 0x80, 2, depth1, 0);
            var depth3 = new PointerScanner.PointerScanResult(
                rootAddr, p1Base, TestModule, 0x40, 3, depth2, 0x3000);

            var chain = PointerScanner.PointerChain.Create(depth3, finalTarget);

            Assert.Equal(4, chain.Offsets.Count);
            Assert.Equal(0x3000, chain.Offsets[0]);
            Assert.Equal(0x40, chain.Offsets[1]);
            Assert.Equal(0x80, chain.Offsets[2]);
            Assert.Equal(0xC0, chain.Offsets[3]);
        }

        // ====================================================================
        // Test 4 — Odrzuć fałszywy absolute-distance chain
        // rootAddr = moduleBase + 0x4000
        // badValue = 0x30000100
        // parentAddr = 0x30000090
        // parentAddr < badValue → NIE MOŻE być zaakceptowane
        // Nie wolno liczyć offsetu jako abs(0x30000090 - 0x30000100) = 0x70
        // ====================================================================
        [Fact]
        public void PointerChainCreate_Depth2_RejectsAbsoluteDistance()
        {
            ulong moduleBase = TestModule.BaseAddress;
            ulong rootAddr = moduleBase + 0x4000;
            ulong badValue = 0x30000100;
            ulong parentAddr = 0x30000090;
            ulong parentValue = 0x40000000;
            ulong finalTarget = parentValue + 0x50;

            var depth1 = new PointerScanner.PointerScanResult(
                parentAddr, parentValue, null, 0x50, 1, null, 0);
            var depth2 = new PointerScanner.PointerScanResult(
                rootAddr, badValue, TestModule, 0, 2, depth1, 0x4000);

            var chain = PointerScanner.PointerChain.Create(depth2, finalTarget);

            long expectedOffset = unchecked((long)(parentAddr - badValue));
            Assert.True(expectedOffset < 0, "Offset must be negative when value > parentAddr");
            Assert.Equal(expectedOffset, chain.Offsets[1]);
        }

        // ====================================================================
        // Test 5 — Sprawdź resolve chaina dla Depth 1
        // [Ares.exe + 0x1000] + 0x90 = finalTarget
        // ====================================================================
        [Fact]
        public void PointerChainCreate_Depth1_ChainResolves()
        {
            ulong moduleBase = TestModule.BaseAddress;
            ulong value = 0x20000000;
            ulong finalTarget = value + 0x90;

            var depth1 = new PointerScanner.PointerScanResult(
                moduleBase + 0x1000, value, TestModule, 0x90, 1, null, 0x1000);

            var chain = PointerScanner.PointerChain.Create(depth1, finalTarget);

            ulong step1 = moduleBase + unchecked((ulong)chain.Offsets[0]);
            Assert.Equal(moduleBase + 0x1000, step1);

            ulong step2 = value + unchecked((ulong)chain.Offsets[1]);
            Assert.Equal(finalTarget, step2);
        }

        // ====================================================================
        // Test 6 — Sprawdź resolve chaina dla Depth 2
        // [[Ares.exe + 0x2000] + 0x90] + 0xC0 = finalTarget
        // ====================================================================
        [Fact]
        public void PointerChainCreate_Depth2_ChainResolves()
        {
            ulong moduleBase = TestModule.BaseAddress;
            ulong rootAddr = moduleBase + 0x2000;
            ulong midBase = 0x30000000;
            ulong midAddr = midBase + 0x90;
            ulong midValue = 0x40000000;
            ulong finalTarget = midValue + 0xC0;

            var depth1 = new PointerScanner.PointerScanResult(
                midAddr, midValue, null, 0xC0, 1, null, 0);
            var depth2 = new PointerScanner.PointerScanResult(
                rootAddr, midBase, TestModule, 0x90, 2, depth1, 0x2000);

            var chain = PointerScanner.PointerChain.Create(depth2, finalTarget);

            ulong step1 = moduleBase + unchecked((ulong)chain.Offsets[0]);
            Assert.Equal(rootAddr, step1);

            ulong step2_val = midBase;
            ulong step2 = step2_val + unchecked((ulong)chain.Offsets[1]);
            Assert.Equal(midAddr, step2);

            ulong step3 = midValue + unchecked((ulong)chain.Offsets[2]);
            Assert.Equal(finalTarget, step3);
        }

        // ====================================================================
        // Test 7 — Depth 2 z ujemnym offsetem (parentAddr < value)
        // rootAddr = moduleBase + 0x2000, value = 0x30000100
        // parentAddr = 0x30000000, parentValue = 0x40000000
        // offset = (long)(0x30000000 - 0x30000100) = -0x100
        // ====================================================================
        [Fact]
        public void PointerChainCreate_Depth2_NegativeOffset_ChainResolves()
        {
            ulong moduleBase = TestModule.BaseAddress;
            ulong rootAddr = moduleBase + 0x2000;
            ulong value = 0x30000100;
            ulong parentAddr = 0x30000000;
            ulong parentValue = 0x40000000;
            ulong finalTarget = parentValue + 0x50;

            var depth1 = new PointerScanner.PointerScanResult(
                parentAddr, parentValue, null, 0x50, 1, null, 0);
            var depth2 = new PointerScanner.PointerScanResult(
                rootAddr, value, TestModule, 0, 2, depth1, 0x2000);

            var chain = PointerScanner.PointerChain.Create(depth2, finalTarget);

            long expectedMidOffset = unchecked((long)(parentAddr - value));
            Assert.Equal(-0x100, expectedMidOffset);
            Assert.Equal(expectedMidOffset, chain.Offsets[1]);

            ulong step1_val = value;
            ulong step1 = unchecked((ulong)((long)step1_val + chain.Offsets[1]));
            Assert.Equal(parentAddr, step1);
        }

        // ====================================================================
        // Test 8 — EnumeratePath zwraca [root, ..., leaf]
        // ====================================================================
        [Fact]
        public void EnumeratePath_Depth2_ReturnsCorrectOrder()
        {
            var depth1 = new PointerScanner.PointerScanResult(
                0x100, 0x200, null, 0, 1, null, 0);
            var depth2 = new PointerScanner.PointerScanResult(
                0x300, 0x100, TestModule, 0, 2, depth1, 0x300);

            var path = depth2.EnumeratePath().ToList();

            Assert.Equal(2, path.Count);
            Assert.Equal(depth2, path[0]);
            Assert.Equal(depth1, path[1]);
        }

        // ====================================================================
        // Test 9 — Round-trip przez ToPersisted
        // ====================================================================
        [Fact]
        public void PointerChainCreate_ToPersisted_RoundTrip()
        {
            ulong moduleBase = TestModule.BaseAddress;
            var depth1 = new PointerScanner.PointerScanResult(
                moduleBase + 0x1000, 0x20000000, TestModule, 0x90, 1, null, 0x1000);

            var chain = PointerScanner.PointerChain.Create(depth1, 0x20000090);
            var persisted = chain.ToPersisted();

            Assert.Equal("Ares.exe", persisted.ModuleName);
            Assert.Equal(moduleBase, persisted.ModuleBaseHint);
            Assert.Equal(2, persisted.Offsets.Length);
            Assert.Equal(0x1000, persisted.Offsets[0]);
            Assert.Equal(0x90, persisted.Offsets[1]);
            Assert.Equal("0x20000090", persisted.FinalTarget);
        }
    }
}
