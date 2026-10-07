using System;
using System.Collections.Generic;
using System.Reflection;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class MovementKeyboardSteeringTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        private static GameMemoryService CreateStubMemory(Action<string> log)
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
            return new GameMemoryService(0, read, write, 0, 8, log);
        }

        private static Type TurnKeyStateType =>
            typeof(MovementSystem).GetNestedType("TurnKeyState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MovementSystem.TurnKeyState was not found.");

        private static string Decide(float current, float target, out float error)
        {
            MethodInfo method = typeof(MovementSystem).GetMethod("GetDesiredTurnKey", PrivateStatic)
                ?? throw new InvalidOperationException("MovementSystem.GetDesiredTurnKey was not found.");
            object?[] arguments = { current, target, 0f };
            object? result = method.Invoke(null, arguments);
            error = (float)arguments[2]!;
            return result!.ToString()!;
        }

        [Theory]
        [InlineData(0f, 5f, "None")]
        [InlineData(0f, 10f, "None")]
        [InlineData(0f, 10.1f, "RightD")]
        [InlineData(350f, 10f, "RightD")]
        [InlineData(10f, 350f, "LeftA")]
        [InlineData(355f, 2f, "None")]
        [InlineData(0f, 180f, "RightD")]
        [InlineData(75f, 90f, "RightD")]
        [InlineData(96f, 90f, "None")]
        [InlineData(105f, 90f, "LeftA")]
        public void DesiredTurnUsesShortestBearingAndInclusiveTolerance(
            float current,
            float target,
            string expected)
        {
            Assert.Equal(expected, Decide(current, target, out _));
        }

        [Theory]
        [InlineData(350f, 10f, "RightD")]
        [InlineData(10f, 350f, "LeftA")]
        [InlineData(2f, 355f, "LeftA")]
        [InlineData(355f, 2f, "RightD")]
        [InlineData(0f, 180f, "RightD")]
        public void ShortestBearingSignMapsToTheExpectedTurnSide(
            float current,
            float target,
            string expected)
        {
            float signedError = GeometryUtils.GetShortestBearingDiffDeg(current, target);
            MethodInfo method = typeof(MovementSystem).GetMethod("GetTurnKeyForSignedError", PrivateStatic)
                ?? throw new InvalidOperationException("MovementSystem.GetTurnKeyForSignedError was not found.");

            Assert.Equal(expected, method.Invoke(null, new object[] { signedError })!.ToString());
        }

        [Fact]
        public void SevenDegreeWrapErrorsStillReleaseWithinTheInclusiveTolerance()
        {
            // These raw directions map correctly across North, but both are inside
            // the inclusive ±10° controller tolerance and therefore press no key.
            Assert.Equal("None", Decide(2f, 355f, out float leftError));
            Assert.Equal(-7f, leftError, 3);
            Assert.Equal("None", Decide(355f, 2f, out float rightError));
            Assert.Equal(7f, rightError, 3);
        }

        [Theory]
        [InlineData(0f, (int)KeyboardTurnDirective.None)]
        [InlineData(5f, (int)KeyboardTurnDirective.None)]
        [InlineData(10f, (int)KeyboardTurnDirective.None)]
        [InlineData(10.1f, (int)KeyboardTurnDirective.NormalRight)]
        [InlineData(-10f, (int)KeyboardTurnDirective.None)]
        [InlineData(-10.1f, (int)KeyboardTurnDirective.NormalLeft)]
        [InlineData(59.9f, (int)KeyboardTurnDirective.NormalRight)]
        [InlineData(60f, (int)KeyboardTurnDirective.NormalRight)]
        [InlineData(60.1f, (int)KeyboardTurnDirective.FastRight)]
        [InlineData(-59.9f, (int)KeyboardTurnDirective.NormalLeft)]
        [InlineData(-60f, (int)KeyboardTurnDirective.NormalLeft)]
        [InlineData(-60.1f, (int)KeyboardTurnDirective.FastLeft)]
        [InlineData(100f, (int)KeyboardTurnDirective.FastRight)]
        [InlineData(-100f, (int)KeyboardTurnDirective.FastLeft)]
        [InlineData(150f, (int)KeyboardTurnDirective.FastRight)]
        [InlineData(-150f, (int)KeyboardTurnDirective.FastLeft)]
        [InlineData(180f, (int)KeyboardTurnDirective.FastRight)]
        [InlineData(-180f, (int)KeyboardTurnDirective.FastLeft)]
        public void DecideKeyboardTurnUsesTheToleranceAndFastTurnThresholdBoundaries(
            float signedError,
            int expected)
        {
            Assert.Equal((KeyboardTurnDirective)expected, MovementSystem.DecideKeyboardTurn(signedError));
        }

        [Theory]
        [InlineData(350f, 10f, (int)KeyboardTurnDirective.NormalRight)]
        [InlineData(10f, 350f, (int)KeyboardTurnDirective.NormalLeft)]
        [InlineData(300f, 10f, (int)KeyboardTurnDirective.FastRight)]
        [InlineData(60f, 350f, (int)KeyboardTurnDirective.FastLeft)]
        public void DecideKeyboardTurnUsesTheShortestSignedErrorAcrossNorth(
            float current,
            float target,
            int expected)
        {
            float signedError = GeometryUtils.GetShortestBearingDiffDeg(current, target);

            Assert.Equal((KeyboardTurnDirective)expected, MovementSystem.DecideKeyboardTurn(signedError));
        }

        [Fact]
        public void KeyboardFastTurnConstantsMatchTheGameplayRequirement()
        {
            Assert.Equal(10f, BotConstants.Movement.KeyboardTurnToleranceDegrees);
            Assert.Equal(60f, BotConstants.Movement.KeyboardFastTurnThresholdDegrees);
            // The game only accelerates at the actual client boundary, so the cursor must
            // be 1–2 px inside it — a larger inset silently stays at x1.
            Assert.InRange(BotConstants.Movement.KeyboardFastTurnMouseEdgeMarginPx, 1, 2);
            // A cursor counts as being at the edge only within a 2 px tolerance.
            Assert.Equal(2, BotConstants.Movement.KeyboardFastTurnMouseReassertTolerancePx);
            // Deliberate human movement is only concluded past a sensible distance...
            Assert.InRange(BotConstants.Movement.KeyboardFastTurnHumanOverrideDistancePx, 15, 30);
            // ...and the suppression cooldown keeps the bot off the mouse for 1–1.5s.
            Assert.InRange(BotConstants.Movement.KeyboardFastTurnHumanOverrideCooldownMs, 1000, 1500);
        }

        [Theory]
        [InlineData(true, true, true, true, false, true)]
        [InlineData(false, true, true, true, false, false)] // heading no longer needs fast turn
        [InlineData(true, false, true, true, false, false)] // game not foreground
        [InlineData(true, true, false, true, false, false)] // cursor outside client
        [InlineData(true, true, true, false, false, false)] // human-override cooldown
        [InlineData(true, true, true, true, true, false)] // loot/pink owns the mouse
        public void MayUseKeyboardFastTurnMouseRequiresEveryOwnershipCondition(
            bool headingRequiresFastTurn,
            bool gameForeground,
            bool cursorInsideGameClient,
            bool suppressionExpired,
            bool otherMouseOwnerActive,
            bool expected)
        {
            Assert.Equal(expected, MovementSystem.MayUseKeyboardFastTurnMouse(
                headingRequiresFastTurn,
                gameForeground,
                cursorInsideGameClient,
                suppressionExpired,
                otherMouseOwnerActive));
        }

        [Theory]
        [InlineData(500, 500, true)]
        [InlineData(447, 500, true)]
        [InlineData(1726, 500, true)]
        [InlineData(446, 500, false)] // just left of the client
        [InlineData(1727, 500, false)] // just right of the client
        [InlineData(500, 99, false)] // above the client
        [InlineData(500, 901, false)] // below the client
        public void CursorInsideClientUsesTheInclusiveGameClientRectangle(
            int cursorX,
            int cursorY,
            bool expected)
        {
            bool actual = MovementSystem.IsCursorInsideKeyboardFastTurnClient(
                clientLeft: 447,
                clientTop: 100,
                clientRight: 1726,
                clientBottom: 900,
                cursorX: cursorX,
                cursorY: cursorY);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(false, 1278, 1277, false)] // 1 px inward — jitter
        [InlineData(false, 1278, 1270, false)] // 8 px inward — below override distance
        [InlineData(false, 1278, 1254, false)] // exactly at the override distance
        [InlineData(false, 1278, 1253, true)] // past the override distance — human
        [InlineData(false, 1278, 1200, true)] // clearly human
        [InlineData(true, 1, 2, false)]
        [InlineData(true, 1, 25, false)]
        [InlineData(true, 1, 26, true)]
        [InlineData(true, 1, 40, true)]
        public void HumanDisplacementIsDetectedOnlyPastTheOverrideDistance(
            bool leftSide,
            int targetEdgeX,
            int cursorX,
            bool expected)
        {
            Assert.Equal(expected, MovementSystem.IsKeyboardFastTurnHumanDisplacement(
                leftSide, targetEdgeX, cursorX, BotConstants.Movement.KeyboardFastTurnHumanOverrideDistancePx));
        }

        [Theory]
        [InlineData(false, 1278, 1200, 78)]
        [InlineData(true, 1, 40, 39)]
        [InlineData(false, 1278, 1280, -2)] // outward toward the border is not human
        [InlineData(true, 1, 0, -1)]
        public void InwardDisplacementIsPositiveTowardTheGameInterior(
            bool leftSide,
            int targetEdgeX,
            int cursorX,
            int expected)
        {
            Assert.Equal(expected, MovementSystem.ComputeKeyboardFastTurnInwardDisplacement(
                leftSide, targetEdgeX, cursorX));
        }

        [Theory]
        [InlineData(500, 1280, true, 1, 501)]
        [InlineData(500, 1280, false, 1, 1778)]
        [InlineData(500, 1280, true, 2, 502)]
        [InlineData(500, 1280, false, 2, 1777)]
        [InlineData(447, 1280, true, 1, 448)]
        [InlineData(447, 1280, false, 1, 1725)]
        public void FastTurnEdgeCoordinatesSitJustInsideTheClient(
            int clientLeft,
            int clientWidth,
            bool leftSide,
            int edgeMargin,
            int expected)
        {
            int actual = MovementSystem.ComputeKeyboardFastTurnEdgeX(clientLeft, clientWidth, leftSide, edgeMargin);

            Assert.Equal(expected, actual);
            // The target must remain INSIDE the inclusive client range.
            Assert.InRange(actual, clientLeft, clientLeft + clientWidth - 1);
        }

        [Theory]
        [InlineData(500, 1280, 1140)]
        [InlineData(447, 1280, 1087)]
        public void FastTurnNeutralCoordinateIsTheClientHorizontalCentre(
            int clientLeft,
            int clientWidth,
            int expected)
        {
            int actual = MovementSystem.ComputeKeyboardFastTurnNeutralX(clientLeft, clientWidth);

            Assert.Equal(expected, actual);
            Assert.InRange(actual, clientLeft, clientLeft + clientWidth - 1);
        }

        [Fact]
        public void KeyboardTurnDirectiveMapsToExactlyOneKeyAndItsMatchingMouseSide()
        {
            Assert.Equal(("None", "None"), MapDirective(KeyboardTurnDirective.None));
            Assert.Equal(("RightD", "None"), MapDirective(KeyboardTurnDirective.NormalRight));
            Assert.Equal(("RightD", "RightD"), MapDirective(KeyboardTurnDirective.FastRight));
            Assert.Equal(("LeftA", "None"), MapDirective(KeyboardTurnDirective.NormalLeft));
            Assert.Equal(("LeftA", "LeftA"), MapDirective(KeyboardTurnDirective.FastLeft));
        }

        [Fact]
        public void FastTurnSideSwitchMapsToOppositeTurnKeyAndMouseEdge()
        {
            KeyboardTurnDirective right = MovementSystem.DecideKeyboardTurn(120f);
            KeyboardTurnDirective left = MovementSystem.DecideKeyboardTurn(-120f);

            Assert.Equal(KeyboardTurnDirective.FastRight, right);
            Assert.Equal(KeyboardTurnDirective.FastLeft, left);
            Assert.Equal(("RightD", "RightD"), MapDirective(right));
            Assert.Equal(("LeftA", "LeftA"), MapDirective(left));
        }

        [Fact]
        public void SameSideNormalAndFastTransitionsDoNotTouchTheTurnKey()
        {
            // NormalRight and FastRight both own D; NormalLeft and FastLeft both own A.
            Assert.Equal(("None", "None"), TransitionKeys(KeyboardTurnDirective.NormalRight, KeyboardTurnDirective.FastRight));
            Assert.Equal(("None", "None"), TransitionKeys(KeyboardTurnDirective.FastRight, KeyboardTurnDirective.NormalRight));
            Assert.Equal(("None", "None"), TransitionKeys(KeyboardTurnDirective.NormalLeft, KeyboardTurnDirective.FastLeft));
            Assert.Equal(("None", "None"), TransitionKeys(KeyboardTurnDirective.FastLeft, KeyboardTurnDirective.NormalLeft));

            // Opposite-side fast transition swaps exactly one key for the other.
            Assert.Equal(("RightD", "LeftA"), TransitionKeys(KeyboardTurnDirective.FastRight, KeyboardTurnDirective.FastLeft));
            Assert.Equal(("LeftA", "RightD"), TransitionKeys(KeyboardTurnDirective.FastLeft, KeyboardTurnDirective.FastRight));
        }

        [Fact]
        public void KeyboardTurnTransitionsNeverOverlapAAndD()
        {
            // 0° -> 45° -> 75° -> 45° -> 75° left -> 0°, i.e. the requested
            // state sequence: None -> D normal -> D fast -> D normal -> A fast -> None.
            var sequence = new (float Error, KeyboardTurnDirective Expected)[]
            {
                (0f, KeyboardTurnDirective.None),
                (45f, KeyboardTurnDirective.NormalRight),
                (75f, KeyboardTurnDirective.FastRight),
                (45f, KeyboardTurnDirective.NormalRight),
                (-75f, KeyboardTurnDirective.FastLeft),
                (0f, KeyboardTurnDirective.None)
            };

            foreach ((float error, KeyboardTurnDirective expected) in sequence)
            {
                KeyboardTurnDirective directive = MovementSystem.DecideKeyboardTurn(error);
                Assert.Equal(expected, directive);

                (string key, string mouseSide) = MapDirective(directive);
                if (key == "None")
                {
                    Assert.Equal("None", mouseSide);
                }
                else
                {
                    Assert.True(key == "LeftA" || key == "RightD", $"Unexpected key {key}.");
                    Assert.True(mouseSide == "None" || mouseSide == key, $"Mouse side {mouseSide} does not match key {key}.");
                }
            }
        }

        [Fact]
        public void TurnKeyOwnershipTransitionsDoNotRepeatOrOverlapKeys()
        {
            (string release, string press) = Transition("None", "RightD");
            Assert.Equal("None", release);
            Assert.Equal("RightD", press);

            (release, press) = Transition("RightD", "RightD");
            Assert.Equal("None", release);
            Assert.Equal("None", press);

            (release, press) = Transition("RightD", "LeftA");
            Assert.Equal("RightD", release);
            Assert.Equal("LeftA", press);

            (release, press) = Transition("LeftA", "None");
            Assert.Equal("LeftA", release);
            Assert.Equal("None", press);
        }

        [Fact]
        public void StopMovingResetsSimulatedTurnOwnershipAndFrozenTarget()
        {
            var movement = CreateMovement(_ => { });
            FieldInfo heldField = typeof(MovementSystem).GetField("_heldTurnKey", PrivateInstance)!;
            FieldInfo frozenField = typeof(MovementSystem).GetField("_keyboardFrozenBearingDeg", PrivateInstance)!;
            FieldInfo fastMouseField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseActive", PrivateInstance)!;
            FieldInfo fastMouseSideField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseDirection", PrivateInstance)!;
            FieldInfo pendingField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseNeutralizePending", PrivateInstance)!;
            FieldInfo pendingSideField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseNeutralizePendingDirection", PrivateInstance)!;
            heldField.SetValue(movement, Enum.Parse(TurnKeyStateType, "RightD"));
            frozenField.SetValue(movement, 91.5f);
            fastMouseField.SetValue(movement, true);
            fastMouseSideField.SetValue(movement, Enum.Parse(TurnKeyStateType, "RightD"));
            pendingField.SetValue(movement, true);
            pendingSideField.SetValue(movement, Enum.Parse(TurnKeyStateType, "RightD"));

            // No key-down is synthesized. StopMoving exercises only its allowed defensive
            // W/A/D KEYUP cleanup and resets the logical owner state (including the
            // fast-turn mouse ownership). Stop must NOT leave a queued cursor reposition.
            movement.StopMoving();

            Assert.Equal("None", heldField.GetValue(movement)!.ToString());
            Assert.Null(frozenField.GetValue(movement));
            Assert.False((bool)fastMouseField.GetValue(movement)!);
            Assert.Equal("None", fastMouseSideField.GetValue(movement)!.ToString());
            Assert.False((bool)pendingField.GetValue(movement)!);
            Assert.Equal("None", pendingSideField.GetValue(movement)!.ToString());
        }

        [Fact]
        public void HumanOverrideReleaseClearsOwnershipWithoutSchedulingNeutralization()
        {
            var movement = CreateMovement(_ => { });
            FieldInfo activeField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseActive", PrivateInstance)!;
            FieldInfo directionField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseDirection", PrivateInstance)!;
            FieldInfo pendingField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseNeutralizePending", PrivateInstance)!;
            FieldInfo pendingSideField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseNeutralizePendingDirection", PrivateInstance)!;
            FieldInfo suppressedField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseSuppressedUntilUtc", PrivateInstance)!;
            activeField.SetValue(movement, true);
            directionField.SetValue(movement, Enum.Parse(TurnKeyStateType, "RightD"));
            pendingField.SetValue(movement, true);
            pendingSideField.SetValue(movement, Enum.Parse(TurnKeyStateType, "RightD"));

            MethodInfo release = typeof(MovementSystem).GetMethod("ReleaseKeyboardFastTurnMouseForHumanOverride", PrivateInstance)
                ?? throw new InvalidOperationException("MovementSystem.ReleaseKeyboardFastTurnMouseForHumanOverride was not found.");
            DateTime before = DateTime.UtcNow;
            release.Invoke(movement, new object[] { "test human override" });

            Assert.False((bool)activeField.GetValue(movement)!);
            Assert.Equal("None", directionField.GetValue(movement)!.ToString());
            // A human override must never schedule a later cursor reposition.
            Assert.False((bool)pendingField.GetValue(movement)!);
            Assert.Equal("None", pendingSideField.GetValue(movement)!.ToString());
            DateTime suppressedUntil = (DateTime)suppressedField.GetValue(movement)!;
            Assert.True(suppressedUntil > before, "Human override must set the suppression deadline.");
        }

        [Fact]
        public void DirectCameraTakeoverReleasesKeyboardFastTurnMouseOwnership()
        {
            var movement = CreateMovement(_ => { });
            FieldInfo fastMouseField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseActive", PrivateInstance)!;
            FieldInfo fastMouseSideField = typeof(MovementSystem).GetField("_keyboardFastTurnMouseDirection", PrivateInstance)!;
            fastMouseField.SetValue(movement, true);
            fastMouseSideField.SetValue(movement, Enum.Parse(TurnKeyStateType, "LeftA"));

            // DirectCamera routes release the keyboard turn key through ReleaseTurnKey;
            // the fast-turn mouse ownership must be cleaned up there too. No A/D is held,
            // so no key-up is synthesized.
            MethodInfo release = typeof(MovementSystem).GetMethod("ReleaseTurnKey", PrivateInstance)
                ?? throw new InvalidOperationException("MovementSystem.ReleaseTurnKey was not found.");
            release.Invoke(movement, new object[] { "direct camera steering" });

            Assert.False((bool)fastMouseField.GetValue(movement)!);
            Assert.Equal("None", fastMouseSideField.GetValue(movement)!.ToString());
        }

        [Fact]
        public void PathRunnerDefaultsToKeyboardTurnAndCanBeExplicitlyDirect()
        {
            var memory = CreateStubMemory(_ => { });
            var normal = new PathRunnerService(memory, _ => { });
            var recovery = new PathRunnerService(
                memory,
                _ => { },
                enableWaypointSpecialRecoveries: false,
                steeringMode: MovementSteeringMode.DirectCamera);
            FieldInfo modeField = typeof(PathRunnerService).GetField("_steeringMode", PrivateInstance)!;

            Assert.Equal(MovementSteeringMode.KeyboardTurn, modeField.GetValue(normal));
            Assert.Equal(MovementSteeringMode.DirectCamera, modeField.GetValue(recovery));
        }

        [Fact]
        public void WaypointRecoveryExecutorAuxiliaryPathRunnerIsDirectCamera()
        {
            var memory = CreateStubMemory(_ => { });
            var executor = new WaypointRecoveryExecutor(
                memory,
                new SavedPathLoader(_ => { }),
                new ItemSellerService(memory, _ => { }),
                new BotProfile(),
                _ => { },
                () => { });

            FieldInfo runnerField = typeof(WaypointRecoveryExecutor).GetField("_auxiliaryPathRunner", PrivateInstance)!;
            var runner = Assert.IsType<PathRunnerService>(runnerField.GetValue(executor));
            FieldInfo modeField = typeof(PathRunnerService).GetField("_steeringMode", PrivateInstance)!;

            Assert.Equal(MovementSteeringMode.DirectCamera, modeField.GetValue(runner));
            FieldInfo specialRecoveriesField = typeof(PathRunnerService).GetField("_enableWaypointSpecialRecoveries", PrivateInstance)!;
            Assert.False((bool)specialRecoveriesField.GetValue(runner)!);
        }

        [Fact]
        public void WaypointSteeringDefaultsToNullSoRunnerModeApplies()
        {
            var waypoint = new Waypoint(1, 2, MovementPrecision.Medium, BotMode.OnlyMove);

            Assert.Null(waypoint.SteeringMode);
        }

        [Theory]
        [InlineData(null, MovementSteeringMode.KeyboardTurn, MovementSteeringMode.KeyboardTurn)]
        [InlineData(null, MovementSteeringMode.DirectCamera, MovementSteeringMode.DirectCamera)]
        [InlineData(MovementSteeringMode.DirectCamera, MovementSteeringMode.KeyboardTurn, MovementSteeringMode.DirectCamera)]
        [InlineData(MovementSteeringMode.KeyboardTurn, MovementSteeringMode.DirectCamera, MovementSteeringMode.KeyboardTurn)]
        public void ResolveSteeringModePrefersTheWaypointOverrideOverTheRunnerDefault(
            MovementSteeringMode? waypointMode,
            MovementSteeringMode runnerDefault,
            MovementSteeringMode expected)
        {
            Assert.Equal(expected, MovementSystem.ResolveSteeringMode(waypointMode, runnerDefault));
        }

        [Fact]
        public void MethodStepsAndCoordinatesCarryTheirOwnSteeringMode()
        {
            var methodStep = new PathPoint(0, 0, steeringMode: MovementSteeringMode.DirectCamera)
            {
                IsOperationStep = true,
                OnArrivalOperation = "Wait"
            };
            var wadPoint = new PathPoint(10, 20, steeringMode: MovementSteeringMode.KeyboardTurn);

            var methodWaypoint = new Waypoint(
                methodStep.X,
                methodStep.Y,
                methodStep.Precision,
                methodStep.Mode,
                onArrivalOperation: methodStep.OnArrivalOperation,
                isOperationStep: methodStep.IsOperationStep,
                steeringMode: methodStep.SteeringMode);
            var wadWaypoint = new Waypoint(
                wadPoint.X,
                wadPoint.Y,
                wadPoint.Precision,
                wadPoint.Mode,
                steeringMode: wadPoint.SteeringMode);

            Assert.Equal(MovementSteeringMode.DirectCamera, methodWaypoint.SteeringMode);
            Assert.Equal(MovementSteeringMode.KeyboardTurn, wadWaypoint.SteeringMode);
        }

        private static (string Release, string Press) Transition(string current, string desired)
        {
            MethodInfo method = typeof(MovementSystem).GetMethod("GetTurnKeyTransition", PrivateStatic)
                ?? throw new InvalidOperationException("MovementSystem.GetTurnKeyTransition was not found.");
            object currentState = Enum.Parse(TurnKeyStateType, current);
            object desiredState = Enum.Parse(TurnKeyStateType, desired);
            object result = method.Invoke(null, new[] { currentState, desiredState })!;
            Type resultType = result.GetType();
            string release = resultType.GetField("Item1")!.GetValue(result)!.ToString()!;
            string press = resultType.GetField("Item2")!.GetValue(result)!.ToString()!;
            return (release, press);
        }

        private static (string Release, string Press) TransitionKeys(
            KeyboardTurnDirective current,
            KeyboardTurnDirective desired)
            => Transition(MapDirective(current).Key, MapDirective(desired).Key);

        private static (string Key, string MouseSide) MapDirective(KeyboardTurnDirective directive)
        {
            MethodInfo keyMethod = typeof(MovementSystem).GetMethod("GetTurnKeyForDirective", PrivateStatic)
                ?? throw new InvalidOperationException("MovementSystem.GetTurnKeyForDirective was not found.");
            MethodInfo sideMethod = typeof(MovementSystem).GetMethod("GetFastTurnMouseSide", PrivateStatic)
                ?? throw new InvalidOperationException("MovementSystem.GetFastTurnMouseSide was not found.");

            string key = keyMethod.Invoke(null, new object[] { directive })!.ToString()!;
            string mouseSide = sideMethod.Invoke(null, new object[] { directive })!.ToString()!;
            return (key, mouseSide);
        }

        private static MovementSystem CreateMovement(Action<string> log)
        {
            var path = new List<Waypoint>
            {
                new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.OnlyMove)
            };
            return new MovementSystem(
                CreateStubMemory(log),
                log,
                5000,
                5000,
                customPath: path,
                loopPath: true,
                steeringMode: MovementSteeringMode.KeyboardTurn);
        }
    }
}
