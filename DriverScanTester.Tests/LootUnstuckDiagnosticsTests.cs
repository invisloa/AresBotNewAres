using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Diagnostic-only coverage for the combined position and collection-evidence
    /// loot unstuck decision. The click operation is injected so the tests never
    /// move the real mouse or click the desktop; production calls the same method
    /// without an override.
    /// </summary>
    public sealed class LootUnstuckDiagnosticsTests
    {
        private const uint Pid = 123;
        private const int PointerSize = 8;
        private const ulong ModuleBase = 0x400000;
        private const ulong PlayerBase = 0x12345000;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Fact]
        public void Unchanged2DPositionAndConfirmedNoPickupTriggerGroundClick()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51);

            var clicks = InvokeUnbug(loot, 225, context);

            var click = Assert.Single(clicks);
            Assert.Equal((550, 697, 100), click);
            Assert.Contains(lines, line => line.Contains("[LootUnstuck] TRIGGERED", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=PositionUnchangedAndNoItemCollected", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("BeforeX=225", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentX=225", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("DeltaX=0", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("BeforePosition=(225,51)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentPosition=(225,51)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=0.00", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("PositionChanged=False", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("ItemCollected=False", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CollectionExitReason=PositionStable", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Condition=(playerPositionUnchanged && confirmedNoItemCollected) => True", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Action=GroundClick", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("ClickConfiguredX=900 ClickConfiguredY=600", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("ClickReferenceClientOrigin=(450,103)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("ClickBitmapLocalAfterClamp=(450,497)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("ClickScreen=(550,697)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("ClickDelayMs=100", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CLICK START source=UnbugWhenCollecting screen=(550,697)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CLICK RETURNED source=UnbugWhenCollecting", StringComparison.Ordinal));
        }

        [Fact]
        public void StationaryPlayerWithCollectedItemSkipsGroundClick()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, itemCollected: true);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Empty(clicks);
            Assert.Contains(lines, line => line.Contains("ItemCollected=True", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=ItemSuccessfullyCollected", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("CLICK START", StringComparison.Ordinal));
        }

        [Fact]
        public void ExistingStackQuantityIncreaseConfirmsCollectionWithoutNewSlot()
        {
            var before = new GameMemoryService.InventorySnapshot(new[] { 9050 }, new[] { 4 });
            var after = new GameMemoryService.InventorySnapshot(new[] { 9050 }, new[] { 5 });
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, itemCollected: true);
            context.InventoryBefore = before;
            context.InventoryAfter = after;
            context.InventoryChanged = true;

            Assert.True(LootSystem.InventoryHasIncrease(before, after));
            Assert.True(LootSystem.DetermineItemCollected(true, before, true, after));
            Assert.Empty(InvokeUnbug(loot, 225, context));
            Assert.Contains(lines, line => line.Contains("Reason=ItemSuccessfullyCollected", StringComparison.Ordinal));
        }

        [Fact]
        public void InvalidInventoryEvidenceIsUnknownAndCannotTriggerUnstuck()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, itemCollected: null);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Empty(clicks);
            Assert.Contains(lines, line => line.Contains("ItemCollected=Unknown", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=InsufficientCollectionEvidence", StringComparison.Ordinal));
        }

        [Fact]
        public void ChangedXSkipsTheGroundClickAndEmitsSkippedLog()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 228, Y = 54 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Empty(clicks);
            Assert.Contains(lines, line => line.Contains("[LootUnstuck] SKIPPED", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("BeforeX=225", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentX=228", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("DeltaX=3", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=PlayerPositionChanged", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("GroundClickExecuted=False", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("CLICK START", StringComparison.Ordinal));
        }

        [Fact]
        public void UnchangedXWithChangedYSkipsBecauseThe2DPositionChanged()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 54 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Empty(clicks);
            Assert.Contains(lines, line => line.Contains("PositionChanged=True", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=PlayerPositionChanged", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=3.00", StringComparison.Ordinal));
        }

        [Fact]
        public void InventorySnapshotReadsItemTypesAndStackCountsOnlyWhenComplete()
        {
            var bytes = new byte[BotConstants.GameMagicValues.TotalInventorySlots * BotConstants.GameMagicValues.InventorySlotSize];
            BitConverter.GetBytes((short)9050).CopyTo(bytes, 0);
            bytes[6] = 4;
            BitConverter.GetBytes((short)9060).CopyTo(bytes, BotConstants.GameMagicValues.InventorySlotSize);
            bytes[BotConstants.GameMagicValues.InventorySlotSize + 6] = 2;
            var memory = new PositionMemory { X = 225, Y = 51, InventoryBytes = bytes };
            var service = memory.CreateService();

            Assert.True(service.TryGetInventorySnapshot(out var snapshot));
            Assert.Equal(9050, snapshot.ItemTypes[0]);
            Assert.Equal(4, snapshot.ItemCounts[0]);
            Assert.Equal(9060, snapshot.ItemTypes[1]);
            Assert.Equal(2, snapshot.ItemCounts[1]);
            Assert.Equal(false, LootSystem.DetermineItemCollected(true, snapshot, true, snapshot));

            memory.InventoryBytes = bytes[..^1];
            Assert.False(service.TryGetInventorySnapshot(out _));
        }

        [Fact]
        public void InstantPickupIsComparedAgainstThePreClickInventorySnapshot()
        {
            var bytes = new byte[BotConstants.GameMagicValues.TotalInventorySlots * BotConstants.GameMagicValues.InventorySlotSize];
            var memory = new PositionMemory { X = 225, Y = 51, InventoryBytes = bytes };
            var service = memory.CreateService();

            Assert.True(service.TryGetInventorySnapshot(out var before));
            BitConverter.GetBytes((short)9050).CopyTo(bytes, 0);
            bytes[6] = 1;
            Assert.True(service.TryGetInventorySnapshot(out var after));

            Assert.True(LootSystem.DetermineItemCollected(true, before, true, after));
        }

        [Fact]
        public void DelayedPickupFoundInFinalInventorySnapshotSkipsGroundClick()
        {
            var before = new GameMemoryService.InventorySnapshot(new[] { 0 }, new[] { 0 });
            var after = new GameMemoryService.InventorySnapshot(new[] { 9050 }, new[] { 1 });
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(
                beforeX: 225,
                beforeY: 51,
                itemCollected: LootSystem.DetermineItemCollected(true, before, true, after));
            context.InventoryBefore = before;
            context.InventoryAfter = after;
            context.InventoryChanged = true;

            Assert.Empty(InvokeUnbug(loot, 225, context));
            Assert.Contains(lines, line => line.Contains("Reason=ItemSuccessfullyCollected", StringComparison.Ordinal));
        }

        [Fact]
        public void CollectionSummaryReportsSnapshotDiagnosticsAndUnknownPickup()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(
                beforeX: 225,
                beforeY: 51,
                inventoryChanged: true,
                checksumBefore: 1234,
                checksumAfter: 1250,
                itemCollected: null);
            context.InventoryBefore = new GameMemoryService.InventorySnapshot(new[] { 123 }, new[] { 2 });
            context.InventoryAfter = new GameMemoryService.InventorySnapshot(new[] { 123 }, new[] { 1 });

            loot.LogLootCollectionSummary(context);

            string summary = Assert.Single(
                lines.Where(line => line.Contains("COLLECTION SUMMARY", StringComparison.Ordinal)));
            Assert.Contains("ChecksumBefore=1234", summary, StringComparison.Ordinal);
            Assert.Contains("ChecksumAfter=1250", summary, StringComparison.Ordinal);
            Assert.Contains("InventoryChanged=True", summary, StringComparison.Ordinal);
            Assert.Contains("ItemCollected=Unknown", summary, StringComparison.Ordinal);
            Assert.Contains("InventoryBefore=Valid:", summary, StringComparison.Ordinal);
            Assert.Contains("InventoryAfter=Valid:", summary, StringComparison.Ordinal);
            Assert.Contains("ChecksumBeforeTiming=before-click-before-collection-attempt", summary, StringComparison.Ordinal);
            Assert.Contains("ChecksumMeaning=legacy-item-type-sum-not-pickup-proof", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void CollectionExitReasonsAreClassifiedAndWrittenToTheSummary()
        {
            Assert.Equal("ScanCancelled", LootSystem.DetectCollectionAbortReason(true, false, false));
            Assert.Equal("LootAborted", LootSystem.DetectCollectionAbortReason(false, true, false));
            Assert.Equal("GameWindowFocusLost", LootSystem.DetectCollectionAbortReason(false, false, true));
            Assert.Null(LootSystem.DetectCollectionAbortReason(false, false, false));

            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);

            loot.LogLootCollectionSummary(CreateContext(exitReason: "PositionStable"));
            loot.LogLootCollectionSummary(CreateContext(exitReason: "MaxEmptyRoundsReached"));

            Assert.Contains(lines, line => line.Contains("CollectionExitReason=PositionStable", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CollectionExitReason=MaxEmptyRoundsReached", StringComparison.Ordinal));
        }

        [Fact]
        public void DiagnosticPositionReadFailureCannotTriggerTheGroundClick()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 54, ThrowOnXRead = true };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Empty(clicks);
            Assert.Contains(lines, line => line.Contains("POSITION READ FAILED stage=UnstuckDecision", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentPosition=<unavailable>", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentX=<unavailable>", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("DeltaX=Unavailable", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=Unavailable", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("PositionChanged=Unknown", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=InsufficientCollectionEvidence", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("[LootUnstuck] SKIPPED", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("CLICK START", StringComparison.Ordinal));
        }

        [Fact]
        public void InvalidBeforePositionCannotTriggerTheGroundClick()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, beforePositionValid: false);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Empty(clicks);
            Assert.Contains(lines, line => line.Contains("BeforePosition=<unavailable>", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=Unavailable", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("PositionChanged=Unknown", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=InsufficientCollectionEvidence", StringComparison.Ordinal));
        }

        [Fact]
        public void LoggingCannotCauseAdditionalClicksOrRepeatedUnstuckAttempts()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51);

            loot.LogLootCollectionSummary(context);
            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Single(clicks);
            Assert.Single(lines.Where(line => line.Contains("CLICK START", StringComparison.Ordinal)));
            Assert.Single(lines.Where(line => line.Contains("[LootUnstuck] TRIGGERED", StringComparison.Ordinal)));
            Assert.Single(lines.Where(line => line.Contains("CLICK RETURNED", StringComparison.Ordinal)));
        }

        [Fact]
        public void ClickOperationFailureIsLoggedAndStillPropagates()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                loot.UnbugWhenCollecting(225, context, (_, _, _) => throw new InvalidOperationException("click failure")));

            Assert.Equal("click failure", exception.Message);
            Assert.Contains(lines, line => line.Contains(
                "CLICK FAILED source=UnbugWhenCollecting error=InvalidOperationException: click failure",
                StringComparison.Ordinal));
        }

        private static LootSystem CreateLoot(Action<string> log, PositionMemory memory)
        {
            var loot = new LootSystem(memory.CreateService(), log);
            SetField(loot, "_referenceClientOriginX", 450);
            SetField(loot, "_referenceClientOriginY", 103);
            SetField(loot, "_clientOriginX", 100);
            SetField(loot, "_clientOriginY", 200);
            SetField(loot, "_clientWidth", 1370);
            SetField(loot, "_clientHeight", 840);
            return loot;
        }

        private static List<(int X, int Y, int Delay)> InvokeUnbug(
            LootSystem loot,
            int beforeX,
            LootSystem.LootCollectionDiagnostics context)
        {
            var clicks = new List<(int X, int Y, int Delay)>();
            loot.UnbugWhenCollecting(beforeX, context, (x, y, delay) => clicks.Add((x, y, delay)));
            return clicks;
        }

        private static LootSystem.LootCollectionDiagnostics CreateContext(
            int beforeX = 225,
            float beforeY = 51,
            bool beforePositionValid = true,
            bool inventoryChanged = false,
            int checksumBefore = 1000,
            int checksumAfter = 1000,
            string exitReason = "PositionStable",
            bool? itemCollected = false)
            => new()
            {
                AttemptId = 7,
                BeforeTriggerX = beforeX,
                BeforePositionValid = beforePositionValid,
                BeforePositionX = beforeX,
                BeforePositionY = beforeY,
                ChecksumBeforeWait = checksumBefore,
                ChecksumAfterWait = checksumAfter,
                InventoryBeforeValid = true,
                InventoryBefore = new GameMemoryService.InventorySnapshot(new[] { 123 }, new[] { 1 }),
                InventoryAfterValid = true,
                InventoryAfter = new GameMemoryService.InventorySnapshot(new[] { 123 }, new[] { 1 }),
                InventoryChanged = inventoryChanged,
                ItemCollected = itemCollected,
                XMovementObserved = true,
                StableChecks = 3,
                StableChecksRequired = 3,
                EmptyRounds = 1,
                MaxEmptyRounds = 10,
                ExitReason = exitReason
            };

        private static void SetField(object target, string name, object value)
        {
            FieldInfo? field = target.GetType().GetField(name, PrivateInstance);
            Assert.NotNull(field);
            field!.SetValue(target, value);
        }

        private sealed class PositionMemory
        {
            public short X { get; set; }
            public short Y { get; set; }
            public bool FailYRead { get; set; }
            public bool ThrowOnYRead { get; set; }
            public bool ThrowOnXRead { get; set; }
            public byte[]? InventoryBytes { get; set; }

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;
                        if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                            return Fill(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                        if (address == PlayerBase + BotConstants.MemoryOffsets.X)
                        {
                            if (ThrowOnXRead)
                                throw new InvalidOperationException("diagnostic X read failed");
                            return Fill(buffer, BitConverter.GetBytes(X), out bytesRead);
                        }

                        if (address == PlayerBase + (ulong)BotConstants.MemoryOffsets.SlotFirstSell && InventoryBytes != null)
                            return Fill(buffer, InventoryBytes, out bytesRead);

                        if (address == PlayerBase + BotConstants.MemoryOffsets.Y)
                        {
                            if (ThrowOnYRead)
                                throw new InvalidOperationException("diagnostic Y read failed");
                            if (FailYRead)
                                return false;
                            return Fill(buffer, BitConverter.GetBytes(Y), out bytesRead);
                        }

                        return false;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        return false;
                    };

                return new GameMemoryService(Pid, read, write, ModuleBase, PointerSize, _ => { });
            }

            private static bool Fill(byte[] buffer, byte[] data, out uint bytesRead)
            {
                Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                bytesRead = (uint)data.Length;
                return true;
            }
        }
    }
}
