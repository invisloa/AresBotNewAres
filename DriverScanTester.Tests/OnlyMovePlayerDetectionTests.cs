using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// OnlyMove is pure navigation: no other-player detection scan, marker check or
    /// city/repot response may run for that waypoint mode. Combat-enabled waypoint modes
    /// keep the existing detection and protection behavior, and a mode change takes
    /// effect immediately (including for delayed detection results).
    /// </summary>
    public sealed class OnlyMovePlayerDetectionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly MethodInfo AdvanceMethod = typeof(MovementSystem).GetMethod(
            "AdvanceReachedWaypoints",
            PrivateInstance) ?? throw new InvalidOperationException("AdvanceReachedWaypoints not found.");

        // ───────────────────────────── movement mode policy ─────────────────────────────

        [Theory]
        [InlineData(BotMode.OnlyMove, false)]
        [InlineData(BotMode.MoveAndAttack, true)]
        [InlineData(BotMode.MoveAndAttackAndLoot, true)]
        public void OnlyMoveAloneDisablesOtherPlayerDetection(BotMode mode, bool expected)
        {
            Assert.Equal(expected, MovementSystem.SupportsOtherPlayerDetection(mode));
        }

        [Fact]
        public async Task ActiveWaypointModeSwitchesDetectionImmediately()
        {
            var path = new List<Waypoint>
            {
                new(100, 100, MovementPrecision.Accurate, BotMode.OnlyMove),
                new(200, 200, MovementPrecision.Accurate, BotMode.MoveAndAttackAndLoot),
                new(300, 300, MovementPrecision.Accurate, BotMode.OnlyMove)
            };
            var movement = new SyntheticMovement(CreateMemory(), _ => { }, path);
            movement.InternalRepotEnabled = false;

            Assert.Equal(BotMode.OnlyMove, movement.CurrentMode);
            Assert.False(movement.IsOtherPlayerDetectionEnabled);

            Assert.True(await AdvanceAsync(movement, 100, 100));
            Assert.Equal(BotMode.MoveAndAttackAndLoot, movement.CurrentMode);
            Assert.True(movement.IsOtherPlayerDetectionEnabled);

            Assert.True(await AdvanceAsync(movement, 200, 200));
            Assert.Equal(BotMode.OnlyMove, movement.CurrentMode);
            Assert.False(movement.IsOtherPlayerDetectionEnabled);
        }

        [Theory]
        [InlineData(BotMode.MoveAndAttack)]
        [InlineData(BotMode.MoveAndAttackAndLoot)]
        public void CombatModesPreserveOtherPlayerDetection(BotMode mode)
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(449f, 321f))
            };
            var safety = CombatSafetyFixtures.WithPresence(() => 5000, scanner);

            CombatTargetValidation validation = safety.Validate(
                forcePlayerScan: true,
                playerDetectionEnabled: MovementSystem.SupportsOtherPlayerDetection(mode));

            Assert.Equal(1, scanner.ObserveCount);
            Assert.Equal(TargetEntityKind.OtherPlayer, validation.Kind);
            Assert.False(validation.AttackAllowed);
        }

        // ───────────────────────────── combat safety gate ─────────────────────────────

        [Fact]
        public void OnlyMoveSkipsTheOtherPlayerScanInTargetValidation()
        {
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(449f, 321f))
            };
            var safety = CombatSafetyFixtures.WithPresence(() => 5000, scanner);

            CombatTargetValidation disabled = safety.Validate(
                forcePlayerScan: true,
                playerDetectionEnabled: false);

            Assert.Equal(0, scanner.ObserveCount);
            Assert.Equal(TargetEntityKind.Monster, disabled.Kind);
            Assert.True(disabled.AttackAllowed);

            // The same target still gets the full protection once detection is enabled.
            CombatTargetValidation enabled = safety.Validate(
                forcePlayerScan: true,
                playerDetectionEnabled: true);

            Assert.Equal(1, scanner.ObserveCount);
            Assert.Equal(TargetEntityKind.OtherPlayer, enabled.Kind);
            Assert.False(enabled.AttackAllowed);
            Assert.NotNull(enabled.PlayerMarker);
        }

        [Fact]
        public async Task OnlyMoveUpdateDoesNotRunAnyPlayerDetectionScan()
        {
            MovementMemory memory = new() { TargetId = 5000 };
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(449f, 321f))
            };
            var safety = CombatSafetyFixtures.WithPresence(() => memory.TargetId, scanner);
            var path = new List<Waypoint>
            {
                new(1000, 1000, MovementPrecision.Accurate, BotMode.OnlyMove)
            };
            var movement = new SyntheticMovement(memory.Create(), _ => { }, path, safety)
            {
                InternalRepotEnabled = false
            };

            await movement.Update(CancellationToken.None);

            Assert.False(movement.IsOtherPlayerDetectionEnabled);
            Assert.Equal(0, scanner.ObserveCount);
        }

        // ─────────────────────────── loot/pixel detection gate ───────────────────────────

        [Fact]
        public void ImmediatePlayerReportCannotStopTheBotWhileDetectionIsDisabled()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            bool detectionEnabled = false;
            loot.IsPlayerDetectionEnabled = () => detectionEnabled;
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;

            loot.ReportImmediateOtherPlayerMarker(EmpireMarker(), "test");

            Assert.Equal(0, detected);
            Assert.Contains(log, line => line.Contains("detection is disabled", StringComparison.Ordinal));

            // Switching to a combat waypoint enables the existing response immediately.
            detectionEnabled = true;
            loot.ReportImmediateOtherPlayerMarker(EmpireMarker(), "test");
            Assert.Equal(1, detected);
        }

        [Fact]
        public void OnlyMoveDoesNotRunThePeriodicPixelScan()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            loot.PlayerMarkerScanEnabled = true;
            loot.IsPlayerDetectionEnabled = () => false;
            loot.ShouldIgnoreOtherPlayerMarker = _ => false;
            SetBitmap(loot, CreateEmpireMarkerFrame());

            InvokePrivate(loot, "ScanForOtherPlayerPixels");

            Assert.False((bool)GetPrivateField(loot, "_playerMarkerConfirmationPending")!);
            Assert.Equal(0, (int)GetPrivateField(loot, "_playerMarkerChecksConfirmed")!);
        }

        [Fact]
        public void DelayedMarkerConfirmationCannotInterruptAfterSwitchingToOnlyMove()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            bool detectionEnabled = true;
            loot.IsPlayerDetectionEnabled = () => detectionEnabled;
            loot.PlayerMarkerScanEnabled = true;
            loot.ShouldIgnoreOtherPlayerMarker = _ => false;
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;
            SetBitmap(loot, CreateEmpireMarkerFrame());

            // Candidate detected while the previous combat waypoint was still active.
            InvokePrivate(loot, "ScanForOtherPlayerPixels");
            Assert.True((bool)GetPrivateField(loot, "_playerMarkerConfirmationPending")!);
            Assert.Equal(1, (int)GetPrivateField(loot, "_playerMarkerChecksConfirmed")!);

            // The route switched to OnlyMove before the delayed confirmation was due.
            detectionEnabled = false;
            SetPrivateField(loot, "_playerMarkerCandidateDetectedAt", DateTime.UtcNow.AddMinutes(-1));

            var confirmed = (bool)InvokePrivateResult(loot, "ConfirmPlayerMarkerIfDue")!;

            Assert.False(confirmed);
            Assert.Equal(0, detected);
            Assert.False((bool)GetPrivateField(loot, "_playerMarkerConfirmationPending")!);
            Assert.Equal(0, (int)GetPrivateField(loot, "_playerMarkerChecksConfirmed")!);
        }

        [Fact]
        public void DelayedImmediateReportCannotStopTheBotAfterSwitchingToOnlyMove()
        {
            var log = new List<string>();
            using var loot = CreateLoot(log.Add);
            bool detectionEnabled = true;
            loot.IsPlayerDetectionEnabled = () => detectionEnabled;
            int detected = 0;
            loot.OtherPlayerMarkerDetected = () => detected++;

            // Detection was obtained while MoveAndAttack was active...
            loot.ReportImmediateOtherPlayerMarker(EmpireMarker(), "combat waypoint");
            Assert.Equal(1, detected);

            // ...but the delayed duplicate arrives after the route switched to OnlyMove.
            detectionEnabled = false;
            loot.ReportImmediateOtherPlayerMarker(EmpireMarker(), "delayed combat result");

            Assert.Equal(1, detected);
            Assert.Contains(log, line => line.Contains("delayed combat result", StringComparison.Ordinal) &&
                                          line.Contains("detection is disabled", StringComparison.Ordinal));
        }

        // ───────────────────────────────── helpers ─────────────────────────────────

        private static async Task<bool> AdvanceAsync(MovementSystem movement, float x, float y)
        {
            var task = (Task<bool>?)AdvanceMethod.Invoke(movement, new object[] { x, y, CancellationToken.None });
            Assert.NotNull(task);
            return await task!;
        }

        private static GameMemoryService CreateMemory()
        {
            GameMemoryService.ReadMemoryDelegate read =
                (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    return false;
                };
            GameMemoryService.WriteMemoryDelegate write =
                (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                {
                    bytesWritten = 0;
                    return false;
                };
            return new GameMemoryService(0, read, write, 0, 8, _ => { });
        }

        private static LootSystem CreateLoot(Action<string> log)
            => new(CreateMemory(), log);

        private static PlayerMarkerCandidate EmpireMarker()
            => new("Empire/yellow", new Rectangle(210, 90, 14, 14), 160, 0.8f, 1f, new PointF(217, 97));

        private static void SetBitmap(LootSystem loot, Bitmap frame)
        {
            SetPrivateField(loot, "_bitmap", frame);
            SetPrivateField(loot, "_clientWidth", frame.Width);
            SetPrivateField(loot, "_clientHeight", frame.Height);
        }

        private static Bitmap CreateEmpireMarkerFrame()
        {
            Bitmap frame = new(320, 240, PixelFormat.Format32bppArgb);
            for (int y = 90; y < 104; y++)
            for (int x = 210; x < 224; x++)
                frame.SetPixel(x, y, Color.FromArgb(255, 255, 0));
            return frame;
        }

        private static object? InvokePrivateResult(object target, string method, params object?[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, PrivateInstance)
                ?? throw new InvalidOperationException($"Method {method} was not found on {target.GetType().Name}.");
            return info.Invoke(target, args);
        }

        private static void InvokePrivate(object target, string method, params object?[] args)
            => InvokePrivateResult(target, method, args);

        private static object? GetPrivateField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, PrivateInstance)
                ?? throw new InvalidOperationException($"Field {name} was not found.");
            return field.GetValue(target);
        }

        private static void SetPrivateField(object target, string name, object? value)
        {
            FieldInfo field = target.GetType().GetField(name, PrivateInstance)
                ?? throw new InvalidOperationException($"Field {name} was not found.");
            field.SetValue(target, value);
        }

        /// <summary>Key events are suppressed; the tests never touch the real keyboard.</summary>
        private sealed class SyntheticMovement : MovementSystem
        {
            public SyntheticMovement(
                GameMemoryService memory,
                Action<string> log,
                List<Waypoint> path,
                CombatTargetSafety? targetSafety = null)
                : base(
                    memory,
                    log,
                    path[^1].X,
                    path[^1].Y,
                    customPath: path,
                    initialMode: path[0].Mode,
                    loopPath: false,
                    enableWaypointSpecialRecoveries: false,
                    targetSafety: targetSafety)
            {
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }
            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }
            protected override void SendTurnKeyEvent(bool leftA, bool keyUp) { }
            protected override void SendTargetCycleKey() { }
            protected override void SendSkillThreeKeyEvent(bool keyUp) { }
            protected override void CaptureStuckScreenshot() { }
        }

        /// <summary>
        /// Minimal read-only game simulation: a stable map/position plus a selected target
        /// so the full MovementSystem.Update tick can run without a live game client.
        /// </summary>
        private sealed class MovementMemory
        {
            private const ulong ModuleBase = 0x100000;
            private const ulong PlayerBase = 0x200000;
            private const ulong CameraBase = 0x300000;
            private const ulong PlayerRange = 0x2000;
            private const ulong CameraRange = 0x400;

            public float X = 100f;
            public float Y = 100f;
            public int TargetId = 5000;

            public GameMemoryService Create()
                => new(0, Read, Write, ModuleBase, 8, _ => { });

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                bytesRead = 0;
                if (buffer.Length == 0)
                    return false;

                if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                    return Copy(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                if (address == ModuleBase + BotConstants.MemoryOffsets.CameraPtr)
                    return Copy(buffer, BitConverter.GetBytes(CameraBase), out bytesRead);

                if (address >= PlayerBase && address < PlayerBase + PlayerRange)
                {
                    ulong offset = address - PlayerBase;
                    byte[] value = offset switch
                    {
                        BotConstants.MemoryOffsets.X => BitConverter.GetBytes((short)MathF.Round(X)),
                        BotConstants.MemoryOffsets.Y => BitConverter.GetBytes((short)MathF.Round(Y)),
                        BotConstants.MemoryOffsets.IsInCity => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.CurrentMap => BitConverter.GetBytes(1),
                        BotConstants.MemoryOffsets.CurrentAction => new byte[] { 25 },
                        BotConstants.MemoryOffsets.TargetSelected => BitConverter.GetBytes(TargetId),
                        BotConstants.MemoryOffsets.Animation1 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Animation2 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Hp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.Mana => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxHp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxMp => BitConverter.GetBytes((short)1000),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                if (address >= CameraBase && address < CameraBase + CameraRange)
                {
                    ulong offset = address - CameraBase;
                    byte[] value = offset switch
                    {
                        BotConstants.MemoryOffsets.CameraAngle => BitConverter.GetBytes(0f),
                        BotConstants.MemoryOffsets.CameraDistance =>
                            BitConverter.GetBytes(BotConstants.Camera.DefaultDistanceLock),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                return false;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = (uint)buffer.Length;
                return true;
            }

            private static bool Copy(byte[] buffer, byte[] value, out uint bytesRead)
            {
                Array.Clear(buffer, 0, buffer.Length);
                int count = Math.Min(buffer.Length, value.Length);
                if (count > 0)
                    Array.Copy(value, buffer, count);
                bytesRead = (uint)buffer.Length;
                return true;
            }
        }
    }
}
