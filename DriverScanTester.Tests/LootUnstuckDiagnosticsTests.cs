using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Diagnostic-only coverage for the X-only loot unstuck decision. The click
    /// operation is injected so the tests never move the real mouse or click the
    /// desktop; production calls the same method without an override.
    /// </summary>
    public sealed class LootUnstuckDiagnosticsTests
    {
        private const uint Pid = 123;
        private const int PointerSize = 8;
        private const ulong ModuleBase = 0x400000;
        private const ulong PlayerBase = 0x12345000;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Fact]
        public void EqualXTriggersTheExistingGroundClickAndEmitsDetailedTriggeredLog()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 54 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, inventoryChanged: true);

            var clicks = InvokeUnbug(loot, 225, context);

            var click = Assert.Single(clicks);
            Assert.Equal((550, 697, 100), click);
            Assert.Contains(lines, line => line.Contains("[LootUnstuck] TRIGGERED", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=BeforeAndAfterXAreEqual", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("BeforeX=225", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentX=225", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("DeltaX=0", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("BeforePosition=(225,51)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentPosition=(225,54)", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=3.00", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("InventoryChanged=True", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CollectionExitReason=PositionStable", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Condition=(beforeClickPosX == currentX) => True", StringComparison.Ordinal));
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
            Assert.Contains(lines, line => line.Contains("Reason=XChangedDuringCollection", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("GroundClickExecuted=False", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("CLICK START", StringComparison.Ordinal));
        }

        [Fact]
        public void UnchangedXWithChangedYWarnsAboutTheXOnlyFalsePositiveButStillClicks()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 54 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, inventoryChanged: true);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Single(clicks);
            Assert.Contains(lines, line => line.Contains(
                "WARNING: X is unchanged, but 2D position indicates player movement. Existing X-only condition still triggers the ground click.",
                StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=3.00", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reason=BeforeAndAfterXAreEqual", StringComparison.Ordinal));
        }

        [Fact]
        public void InventoryChecksumChangeIsReportedAsEvidenceNotAsGuaranteedPickup()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 51 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(
                beforeX: 225,
                beforeY: 51,
                inventoryChanged: true,
                checksumBefore: 1234,
                checksumAfter: 1250);

            loot.LogLootCollectionSummary(context);

            string summary = Assert.Single(
                lines.Where(line => line.Contains("COLLECTION SUMMARY", StringComparison.Ordinal)));
            Assert.Contains("ChecksumBefore=1234", summary, StringComparison.Ordinal);
            Assert.Contains("ChecksumAfter=1250", summary, StringComparison.Ordinal);
            Assert.Contains("InventoryChanged=True", summary, StringComparison.Ordinal);
            Assert.Contains("InventoryChangedMeaning=checksum-change-only-not-pickup-proof", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("ItemCollected=True", summary, StringComparison.Ordinal);
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
        public void DiagnosticPositionReadFailureCannotChangeTheUnstuckDecision()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 54, ThrowOnYRead = true };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Single(clicks);
            Assert.Contains(lines, line => line.Contains("POSITION READ FAILED stage=UnstuckDecision", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CurrentPosition=<unavailable>", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=Unavailable", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("[LootUnstuck] TRIGGERED", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("CLICK START source=UnbugWhenCollecting", StringComparison.Ordinal));
        }

        [Fact]
        public void InvalidBeforePositionIsReportedWithoutFabricatingDisplacement()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 54 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, beforePositionValid: false);

            var clicks = InvokeUnbug(loot, 225, context);

            Assert.Single(clicks);
            Assert.Contains(lines, line => line.Contains("BeforePosition=<unavailable>", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Displacement2D=Unavailable", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("WARNING: X is unchanged", StringComparison.Ordinal));
        }

        [Fact]
        public void LoggingCannotCauseAdditionalClicksOrRepeatedUnstuckAttempts()
        {
            var lines = new List<string>();
            var memory = new PositionMemory { X = 225, Y = 54 };
            using var loot = CreateLoot(lines.Add, memory);
            var context = CreateContext(beforeX: 225, beforeY: 51, inventoryChanged: true);

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
            var memory = new PositionMemory { X = 225, Y = 54 };
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
            string exitReason = "PositionStable")
            => new()
            {
                AttemptId = 7,
                BeforeTriggerX = beforeX,
                BeforePositionValid = beforePositionValid,
                BeforePositionX = beforeX,
                BeforePositionY = beforeY,
                ChecksumBeforeWait = checksumBefore,
                ChecksumAfterWait = checksumAfter,
                InventoryChanged = inventoryChanged,
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

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;
                        if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                            return Fill(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                        if (address == PlayerBase + BotConstants.MemoryOffsets.X)
                            return Fill(buffer, BitConverter.GetBytes(X), out bytesRead);

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
