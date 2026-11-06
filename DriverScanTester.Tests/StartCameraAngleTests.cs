using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.Utils;
using DriverScanTester.ViewModels;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Coverage for the optional per-waypoint start camera angle (horizontal yaw at
    /// +0x1A8): numeric input, the "capture current camera angle" button, persistence,
    /// and the runtime contract that the angle is written while W is released and
    /// before the leg's first W-down.
    /// </summary>
    public sealed class StartCameraAngleTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo InitialHeadingAlignmentField = typeof(MovementSystem).GetField(
            "_initialHeadingAlignmentPending",
            PrivateInstance) ?? throw new InvalidOperationException("_initialHeadingAlignmentPending not found.");

        private static readonly FieldInfo WaypointsField = typeof(MovementSystem).GetField(
            "_waypoints",
            PrivateInstance) ?? throw new InvalidOperationException("_waypoints not found.");

        private static readonly FieldInfo StartCameraLockActiveField = typeof(MovementSystem).GetField(
            "_startCameraLockActive",
            PrivateInstance) ?? throw new InvalidOperationException("_startCameraLockActive not found.");

        [Theory]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData("0x3FC96E22", 1070165538)]
        [InlineData("1070165538", 1070165538)]
        [InlineData("1.5736735", 1070165538)]
        [InlineData("1.5736735rad", 1070165538)]
        [InlineData("0x40C90FDB", BotConstants.CameraAngleBits.North_0Deg)]
        public void CameraAngleTextAcceptsTheExactValueFormats(string text, int? expectedBits)
        {
            Assert.True(CameraAngleValue.TryParse(text, out int? bits));
            Assert.Equal(expectedBits, bits);
        }

        [Theory]
        [InlineData("90deg", (float)(Math.PI / 2))]
        [InlineData("90°", (float)(Math.PI / 2))]
        [InlineData("90", (float)(Math.PI / 2))]
        [InlineData("180", (float)Math.PI)]
        public void CameraAngleTextAcceptsDegrees(string text, float expectedRadians)
        {
            Assert.True(CameraAngleValue.TryParse(text, out int? bits));
            Assert.Equal(BitConverter.SingleToInt32Bits(expectedRadians), bits);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("0xZZ")]
        [InlineData("deg")]
        public void InvalidCameraAngleTextIsRejected(string text)
        {
            Assert.False(CameraAngleValue.TryParse(text, out _));
        }

        [Fact]
        public void CameraAngleTextShowsTheRawAddressValueLikeTheAddressListAndRoundTrips()
        {
            // C_CameraHorizontal sample from the address list: 1070780928 (0x3FD2D200).
            const int bits = 1070780928;

            Assert.Equal("0x3FD2D200", $"0x{bits:X8}");
            Assert.Equal("1070780928", CameraAngleValue.Format(bits));
            Assert.True(CameraAngleValue.TryParse(CameraAngleValue.Format(bits), out int? reparsed));
            Assert.Equal(bits, reparsed);

            string description = CameraAngleValue.Describe(bits);
            Assert.StartsWith("1070780928 (0x3FD2D200)", description);
            Assert.Contains("\u00B0", description);
            Assert.Contains(" rad", description);

            // The editor must never present the value in radians.
            Assert.DoesNotContain("rad", CameraAngleValue.Format(bits));
        }

        [Fact]
        public void PathPointStartAngleDefaultsToNullAndRoundTripsInThePathFile()
        {
            var point = new PathPoint(10, 20);
            Assert.Null(point.StartCameraAngleBits);
            Assert.Equal("", point.StartCameraAngleText);

            point.StartCameraAngleText = "0x3FC96E22";
            Assert.Equal(1070165538, point.StartCameraAngleBits);

            var segment = new PathSegment
            {
                Name = "StartCamRoundTrip",
                Points = new List<PathPoint> { point }
            };

            string json = JsonSerializer.Serialize(segment);
            Assert.DoesNotContain("StartCameraAngleText", json);
            PathSegment? loaded = JsonSerializer.Deserialize<PathSegment>(json);

            Assert.NotNull(loaded);
            Assert.NotNull(loaded!.Points);
            Assert.Single(loaded.Points);
            Assert.Equal(1070165538, loaded.Points[0].StartCameraAngleBits);
        }

        [Fact]
        public void LegacyPathFileWithoutTheFieldLoadsWithNoStartAngle()
        {
            const string legacyJson =
                "{\"Name\":\"Legacy\",\"Points\":[{\"X\":1,\"Y\":2,\"Precision\":12,\"Mode\":0}]}";

            PathSegment? loaded = JsonSerializer.Deserialize<PathSegment>(legacyJson);

            Assert.NotNull(loaded);
            Assert.NotNull(loaded!.Points);
            Assert.Single(loaded.Points);
            Assert.Null(loaded.Points[0].StartCameraAngleBits);
        }

        [Fact]
        public void CaptureCurrentCameraAngleButtonWritesTheLiveYawBitsIntoTheSelectedPoint()
        {
            const ulong moduleBase = 0x100000;
            const ulong cameraBase = 0x200000;
            int liveBits = BitConverter.SingleToInt32Bits(1.5736735f);

            var access = new BotGameAccess(
                isAttached: () => true,
                getPid: () => 1u,
                getPointerSize: () => 8,
                getModuleBase: () => moduleBase,
                readMemory: (uint pid, ulong address, byte[] buffer, uint size) =>
                {
                    byte[]? value = null;
                    if (address == moduleBase + BotConstants.MemoryOffsets.CameraPtr)
                        value = BitConverter.GetBytes(cameraBase);
                    else if (address == cameraBase + BotConstants.MemoryOffsets.CameraAngle)
                        value = BitConverter.GetBytes(liveBits);

                    if (value == null)
                        return false;

                    Array.Copy(value, buffer, Math.Min(value.Length, buffer.Length));
                    return true;
                });

            var vm = new PathEditorViewModel(access);
            var point = new PathPoint(11, 22);
            vm.Points.Add(point);
            vm.SelectedPoint = point;

            Assert.True(vm.CaptureCameraAngleCommand.CanExecute(null));
            vm.CaptureCameraAngleCommand.Execute(null);

            Assert.Equal(liveBits, point.StartCameraAngleBits);
            Assert.Contains("Captured current camera angle", vm.StatusText);
        }

        [Fact]
        public async Task StartCameraAngleIsWrittenBeforeTheFirstWDownAndSkipsTheFirstStepTurn()
        {
            var events = new List<string>();
            var simulation = new StartCameraSimulatedGameMemory(events)
            {
                CameraAngle = BitConverter.Int32BitsToSingle(BotConstants.CameraAngleBits.North_0Deg)
            };

            var path = new List<Waypoint>
            {
                new Waypoint(
                    4000,
                    4000,
                    MovementPrecision.Medium,
                    BotMode.OnlyMove,
                    zoneRestriction: ZoneRestriction.Both,
                    startCameraAngleBits: BotConstants.CameraAngleBits.East_90Deg)
            };

            var movement = new RecordingStartCameraMovementSystem(
                simulation.CreateMemory(_ => { }),
                _ => { },
                path,
                events);

            await movement.Update(CancellationToken.None);

            int cameraIndex = events.FindIndex(e => e.StartsWith("CAM:", StringComparison.Ordinal));
            int wDownIndex = events.FindIndex(e => e == "W:DOWN");

            Assert.True(cameraIndex >= 0, "The configured start camera angle was never written.");
            Assert.True(wDownIndex >= 0, "W was not pressed after the start camera angle was applied.");
            Assert.True(cameraIndex < wDownIndex,
                $"Expected the camera write before W-down. Events: {string.Join(", ", events)}");
            Assert.Equal(BotConstants.CameraAngleBits.East_90Deg, simulation.LastWrittenCameraAngleBits);
            Assert.DoesNotContain("A:DOWN", events.GetRange(0, wDownIndex));
            Assert.DoesNotContain("D:DOWN", events.GetRange(0, wDownIndex));
            Assert.False((bool)InitialHeadingAlignmentField.GetValue(movement)!,
                "The explicit start angle must suppress the first-step A/D alignment.");
        }

        [Fact]
        public async Task StartCameraLockKeepsTheAngleAndSuppressesSteeringUntilThePlayerMoves()
        {
            // Post-town-teleport case: the client still ignores W (action=1) while the map
            // loads. Holding the configured angle for 500 ms of *time* let the stuck
            // recovery rotate the camera to 211° and walk away from the repot point. The
            // lock must stay armed until real displacement happens.
            var events = new List<string>();
            var simulation = new StartCameraSimulatedGameMemory(events)
            {
                CameraAngle = BitConverter.Int32BitsToSingle(BotConstants.CameraAngleBits.North_0Deg),
                X = 262f,
                Y = 188f
            };

            var path = new List<Waypoint>
            {
                new Waypoint(
                    4000,
                    4000,
                    MovementPrecision.Medium,
                    BotMode.OnlyMove,
                    zoneRestriction: ZoneRestriction.Both,
                    startCameraAngleBits: BotConstants.CameraAngleBits.East_90Deg)
            };

            var movement = new RecordingStartCameraMovementSystem(
                simulation.CreateMemory(_ => { }),
                _ => { },
                path,
                events);

            await movement.Update(CancellationToken.None);
            int wDownIndex = events.FindIndex(e => e == "W:DOWN");
            Assert.True(wDownIndex >= 0, $"W down missing. Events: {string.Join(", ", events)}");

            // Loading: no displacement at all, W is physically held but the game ignores it.
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                BotConstants.Camera.StartAngleLockMs + 400);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
                await movement.Update(CancellationToken.None);
            }

            Assert.True((bool)StartCameraLockActiveField.GetValue(movement)!,
                "The start-angle lock released without any player movement.");
            Assert.DoesNotContain(
                events.GetRange(wDownIndex, events.Count - wDownIndex),
                e => e.StartsWith("A:", StringComparison.Ordinal) || e.StartsWith("D:", StringComparison.Ordinal));

            // The game starts processing the held W: real movement releases the lock.
            simulation.Action = 3;
            simulation.X += 4f;
            deadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Camera.StartAngleLockMs + 1500);
            while (DateTime.UtcNow < deadline &&
                   (bool)StartCameraLockActiveField.GetValue(movement)!)
            {
                await Task.Delay(25);
                await movement.Update(CancellationToken.None);
            }

            Assert.False((bool)StartCameraLockActiveField.GetValue(movement)!,
                "The start-angle lock did not release after real movement.");
        }

        [Fact]
        public async Task DirectCameraPressesWAtTheConfiguredStartAngleBeforeWritingItsBearing()
        {
            var events = new List<string>();
            var simulation = new StartCameraSimulatedGameMemory(events)
            {
                CameraAngle = BitConverter.Int32BitsToSingle(BotConstants.CameraAngleBits.North_0Deg)
            };

            var path = new List<Waypoint>
            {
                new Waypoint(
                    4000,
                    4000,
                    MovementPrecision.Medium,
                    BotMode.OnlyMove,
                    zoneRestriction: ZoneRestriction.Both,
                    steeringMode: MovementSteeringMode.DirectCamera,
                    startCameraAngleBits: BotConstants.CameraAngleBits.East_90Deg)
            };

            var movement = new RecordingStartCameraMovementSystem(
                simulation.CreateMemory(_ => { }),
                _ => { },
                path,
                events);

            await movement.Update(CancellationToken.None);

            // The start-angle lock holds the configured yaw until W actually moves the
            // player, then for at least StartAngleLockMs. Simulate real forward movement
            // through the lock window instead of assuming a single tick is enough.
            simulation.X += 2f;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                BotConstants.Camera.StartAngleLockMs + 1500);
            while (DateTime.UtcNow < deadline &&
                   events.Count(e => e.StartsWith("CAM:", StringComparison.Ordinal)) < 2)
            {
                await Task.Delay(25);
                await movement.Update(CancellationToken.None);
            }

            int startAngleIndex = events.IndexOf(
                $"CAM:0x{BotConstants.CameraAngleBits.East_90Deg:X8}");
            int wDownIndex = events.FindIndex(e => e == "W:DOWN");
            List<int> cameraIndices = events
                .Select((e, index) => (e, index))
                .Where(x => x.e.StartsWith("CAM:", StringComparison.Ordinal))
                .Select(x => x.index)
                .ToList();

            Assert.True(startAngleIndex >= 0, $"Start angle write missing. Events: {string.Join(", ", events)}");
            Assert.True(wDownIndex >= 0, $"W down missing. Events: {string.Join(", ", events)}");
            Assert.True(startAngleIndex < wDownIndex,
                $"W must be pressed after the start angle. Events: {string.Join(", ", events)}");
            Assert.True(cameraIndices.Count >= 2,
                $"DirectCamera steering never resumed after W-down. Events: {string.Join(", ", events)}");
            Assert.True(cameraIndices[1] > wDownIndex,
                $"DirectCamera steering may resume only after W-down. Events: {string.Join(", ", events)}");
        }

        [Fact]
        public async Task InitialResyncKeepsTheFirstWaypointSoItsStartCameraIsWrittenBeforeW()
        {
            // Reproduces the 2026-11-04 repot start: the player stands ~7 tiles from the
            // first path point, so the initial route optimisation used to drop that point
            // and head straight for the second one. The configured StartCam was never
            // written and the bot pressed W (then steered with A/D) on the old camera.
            var events = new List<string>();
            var simulation = new StartCameraSimulatedGameMemory(events)
            {
                CameraAngle = BitConverter.Int32BitsToSingle(BotConstants.CameraAngleBits.North_0Deg),
                X = 262f,
                Y = 188f
            };

            var path = new List<Waypoint>
            {
                new Waypoint(
                    269,
                    189,
                    MovementPrecision.Medium,
                    BotMode.OnlyMove,
                    zoneRestriction: ZoneRestriction.Both,
                    steeringMode: MovementSteeringMode.DirectCamera,
                    startCameraAngleBits: BotConstants.CameraAngleBits.East_90Deg),
                new Waypoint(277, 188, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both),
                new Waypoint(300, 188, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both)
            };

            var movement = new RecordingStartCameraMovementSystem(
                simulation.CreateMemory(_ => { }),
                _ => { },
                path,
                events);

            await movement.Update(CancellationToken.None);

            int startAngleIndex = events.IndexOf(
                $"CAM:0x{BotConstants.CameraAngleBits.East_90Deg:X8}");
            int wDownIndex = events.FindIndex(e => e == "W:DOWN");

            Assert.True(startAngleIndex >= 0,
                $"The first waypoint's start camera was never written. Events: {string.Join(", ", events)}");
            Assert.True(wDownIndex >= 0, $"W down missing. Events: {string.Join(", ", events)}");
            Assert.True(startAngleIndex < wDownIndex,
                $"W must be pressed after the start angle. Events: {string.Join(", ", events)}");
            Assert.DoesNotContain("A:DOWN", events.GetRange(0, wDownIndex));
            Assert.DoesNotContain("D:DOWN", events.GetRange(0, wDownIndex));

            // The first camera write must be the configured start angle, not a steering
            // bearing: DirectCamera may rewrite the yaw only after W has gone down.
            string firstCameraEvent = events.First(e => e.StartsWith("CAM:", StringComparison.Ordinal));
            Assert.Equal($"CAM:0x{BotConstants.CameraAngleBits.East_90Deg:X8}", firstCameraEvent);

            var queue = (Queue<Waypoint>)WaypointsField.GetValue(movement)!;
            Assert.NotEmpty(queue);
            Assert.Equal(269f, queue.Peek().X);
            Assert.Equal(189f, queue.Peek().Y);
        }

        [Fact]
        public async Task InitialResyncKeepsTheFirstWaypointEvenWhenThePlayerStandsOnIt()
        {
            // A path usually starts where it was recorded — on the player's position. The
            // initial resync used to treat that first point as "already reached" and skip
            // it, so its StartCam was never written and W went down on the old camera.
            var events = new List<string>();
            var simulation = new StartCameraSimulatedGameMemory(events)
            {
                CameraAngle = BitConverter.Int32BitsToSingle(BotConstants.CameraAngleBits.North_0Deg),
                X = 222f,
                Y = 213f
            };

            var path = new List<Waypoint>
            {
                new Waypoint(
                    222,
                    213,
                    MovementPrecision.Medium,
                    BotMode.OnlyMove,
                    zoneRestriction: ZoneRestriction.Both,
                    startCameraAngleBits: BotConstants.CameraAngleBits.East_90Deg),
                new Waypoint(229, 213, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both),
                new Waypoint(242, 203, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both)
            };

            var movement = new RecordingStartCameraMovementSystem(
                simulation.CreateMemory(_ => { }),
                _ => { },
                path,
                events);

            await movement.Update(CancellationToken.None);

            int startAngleIndex = events.IndexOf(
                $"CAM:0x{BotConstants.CameraAngleBits.East_90Deg:X8}");
            int wDownIndex = events.FindIndex(e => e == "W:DOWN");

            Assert.True(startAngleIndex >= 0,
                $"The first waypoint's start camera was never written. Events: {string.Join(", ", events)}");
            Assert.True(wDownIndex >= 0, $"W down missing. Events: {string.Join(", ", events)}");
            Assert.True(startAngleIndex < wDownIndex,
                $"W must be pressed after the start angle. Events: {string.Join(", ", events)}");
            Assert.Equal(BotConstants.CameraAngleBits.East_90Deg, simulation.LastWrittenCameraAngleBits);
        }

        [Fact]
        public async Task InitialResyncDoesNotWalkBackToAStartCameraPointWhenTheRouteIsResumedFurtherAlong()
        {
            var events = new List<string>();
            var simulation = new StartCameraSimulatedGameMemory(events)
            {
                CameraAngle = BitConverter.Int32BitsToSingle(BotConstants.CameraAngleBits.North_0Deg),
                X = 279f,
                Y = 188f
            };

            var path = new List<Waypoint>
            {
                new Waypoint(262, 188, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both,
                    startCameraAngleBits: BotConstants.CameraAngleBits.East_90Deg),
                new Waypoint(270, 188, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both),
                new Waypoint(278, 188, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both),
                new Waypoint(286, 188, MovementPrecision.Medium, BotMode.OnlyMove, zoneRestriction: ZoneRestriction.Both)
            };

            var movement = new RecordingStartCameraMovementSystem(
                simulation.CreateMemory(_ => { }),
                _ => { },
                path,
                events);

            await movement.Update(CancellationToken.None);

            var queue = (Queue<Waypoint>)WaypointsField.GetValue(movement)!;
            Assert.NotEmpty(queue);
            Assert.Equal(286f, queue.Peek().X);
            Assert.DoesNotContain(events, e => e.StartsWith("CAM:", StringComparison.Ordinal));
        }

        private sealed class RecordingStartCameraMovementSystem : MovementSystem
        {
            private readonly List<string> _events;

            public RecordingStartCameraMovementSystem(
                GameMemoryService memory,
                Action<string> log,
                List<Waypoint> path,
                List<string> events)
                : base(
                    memory,
                    log,
                    path[^1].X,
                    path[^1].Y,
                    customPath: path,
                    loopPath: false,
                    enableWaypointSpecialRecoveries: false)
            {
                InternalRepotEnabled = false;
                AllowSpeedPotionBuff = false;
                _events = events;
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
                => _events.Add(keyUp ? "W:UP" : "W:DOWN");

            protected override void SendSyntheticBackwardKeyEvent(bool keyUp)
                => _events.Add(keyUp ? "S:UP" : "S:DOWN");

            protected override void SendTurnKeyEvent(bool leftA, bool keyUp)
                => _events.Add($"{(leftA ? "A" : "D")}:{(keyUp ? "UP" : "DOWN")}");

            protected override void CaptureStuckScreenshot()
            {
                // Tests must never capture the desktop.
            }
        }

        /// <summary>
        /// Minimal deterministic player/camera memory used by the ordering tests. It
        /// records every camera-yaw write so the tests can prove it happened before W.
        /// </summary>
        private sealed class StartCameraSimulatedGameMemory
        {
            private const ulong ModuleBase = 0x100000;
            private const ulong PlayerBase = 0x200000;
            private const ulong CameraBase = 0x300000;
            private const ulong PlayerRange = 0x2000;
            private const ulong CameraRange = 0x400;

            private readonly List<string> _events;

            public float X = 262f;
            public float Y = 188f;
            public int Map = 1;
            public bool InCity = false;
            public byte Action = 25;
            public float CameraAngle;
            public short CameraDistance = BotConstants.Camera.DefaultDistanceLock;
            public int LastWrittenCameraAngleBits;

            public StartCameraSimulatedGameMemory(List<string> events)
            {
                _events = events;
            }

            public GameMemoryService CreateMemory(Action<string> log)
                => new GameMemoryService(0, Read, Write, ModuleBase, 8, log);

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
                        BotConstants.MemoryOffsets.IsInCity => BitConverter.GetBytes(InCity ? 1 : 0),
                        BotConstants.MemoryOffsets.CurrentMap => BitConverter.GetBytes(Map),
                        BotConstants.MemoryOffsets.CurrentAction => new[] { Action },
                        BotConstants.MemoryOffsets.TargetSelected => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Animation1 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Animation2 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Hp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.Mana => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxHp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxMp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.AttackSpeed1 => BitConverter.GetBytes((short)0),
                        BotConstants.MemoryOffsets.AttackSpeed2 => BitConverter.GetBytes((short)0),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                if (address >= CameraBase && address < CameraBase + CameraRange)
                {
                    ulong offset = address - CameraBase;
                    byte[] value = offset switch
                    {
                        BotConstants.MemoryOffsets.CameraAngle => BitConverter.GetBytes(CameraAngle),
                        BotConstants.MemoryOffsets.CameraDistance => BitConverter.GetBytes(CameraDistance),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                return false;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = (uint)buffer.Length;
                if (address >= CameraBase && address < CameraBase + CameraRange)
                {
                    ulong offset = address - CameraBase;
                    if (offset == BotConstants.MemoryOffsets.CameraAngle && buffer.Length == 4)
                    {
                        LastWrittenCameraAngleBits = BitConverter.ToInt32(buffer, 0);
                        _events.Add($"CAM:0x{LastWrittenCameraAngleBits:X8}");
                        CameraAngle = BitConverter.ToSingle(buffer, 0);
                    }
                    else if (offset == BotConstants.MemoryOffsets.CameraDistance && buffer.Length == 2)
                    {
                        CameraDistance = BitConverter.ToInt16(buffer, 0);
                    }
                    return true;
                }

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
