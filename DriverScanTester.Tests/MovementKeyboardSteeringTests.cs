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
        [InlineData(0f, false)]
        [InlineData(100f, false)]
        [InlineData(-100f, false)]
        [InlineData(100.1f, true)]
        [InlineData(-100.1f, true)]
        [InlineData(180f, true)]
        [InlineData(-180f, true)]
        public void OnlyHeadingErrorsAboveTheThresholdSnapTheCameraDirectly(float signedError, bool expected)
        {
            MethodInfo method = typeof(MovementSystem).GetMethod("ShouldSnapCameraForLargeTurn", PrivateStatic)
                ?? throw new InvalidOperationException("MovementSystem.ShouldSnapCameraForLargeTurn was not found.");

            Assert.Equal(expected, (bool)method.Invoke(null, new object[] { signedError })!);
        }

        [Fact]
        public void KeyboardTurnCameraSnapThresholdMatchesTheGameplayRequirement()
        {
            Assert.Equal(100f, BotConstants.Movement.KeyboardTurnCameraSnapThresholdDegrees);
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
            heldField.SetValue(movement, Enum.Parse(TurnKeyStateType, "RightD"));
            frozenField.SetValue(movement, 91.5f);

            // No key-down is synthesized. StopMoving exercises only its allowed defensive
            // W/A/D KEYUP cleanup and resets the logical owner state.
            movement.StopMoving();

            Assert.Equal("None", heldField.GetValue(movement)!.ToString());
            Assert.Null(frozenField.GetValue(movement));
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
