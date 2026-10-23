using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class CameraSessionExclusivityTests
    {
        [Fact]
        public void OperationTestIsRejectedWhileProfileWorkflowRuns()
        {
            string? reason = CameraSessionExclusivity.FindConflict(
                workflowRunning: true,
                movementRunning: false,
                lootRunning: false,
                movementDiagnosticBlocking: false,
                workflowTaskActive: true,
                movementTaskActive: false,
                lootTaskActive: false);

            Assert.Equal("profile workflow is running", reason);
        }

        [Fact]
        public void OperationTestIsRejectedWhileStandaloneMovementRuns()
        {
            string? reason = CameraSessionExclusivity.FindConflict(
                workflowRunning: false,
                movementRunning: true,
                lootRunning: false,
                movementDiagnosticBlocking: false,
                workflowTaskActive: false,
                movementTaskActive: true,
                lootTaskActive: false);

            Assert.Equal("standalone movement bot is running", reason);
        }

        [Fact]
        public void OperationTestIsRejectedDuringPreviousRuntimeCleanup()
        {
            string? reason = CameraSessionExclusivity.FindConflict(
                workflowRunning: false,
                movementRunning: false,
                lootRunning: false,
                movementDiagnosticBlocking: false,
                workflowTaskActive: false,
                movementTaskActive: true,
                lootTaskActive: false);

            Assert.Equal("previous movement bot is still cleaning up", reason);
        }

        [Fact]
        public void OperationTestIsAllowedWhenTheBotIsIdle()
        {
            string? reason = CameraSessionExclusivity.FindConflict(
                workflowRunning: false,
                movementRunning: false,
                lootRunning: false,
                movementDiagnosticBlocking: false,
                workflowTaskActive: false,
                movementTaskActive: false,
                lootTaskActive: false);

            Assert.Null(reason);
        }

        [Fact]
        public void StandaloneLootIsRejectedWhileAnIndependentCameraSessionIsActive()
        {
            StandaloneLootStartupDecision decision = StandaloneLootStartupPolicy.Evaluate(
                workflowRunning: false,
                workflowTaskActive: false,
                movementRunning: false,
                movementCameraAvailable: false,
                lootTaskActive: false,
                foreignCameraSessionActive: true);

            Assert.False(decision.CanStart);
            Assert.Contains("camera-owning operation", decision.RejectionReason!, StringComparison.Ordinal);
        }

        [Fact]
        public void StandaloneLootIsRejectedWhileThePreviousLootTaskCleansUp()
        {
            StandaloneLootStartupDecision decision = StandaloneLootStartupPolicy.Evaluate(
                workflowRunning: false,
                workflowTaskActive: false,
                movementRunning: false,
                movementCameraAvailable: false,
                lootTaskActive: true,
                foreignCameraSessionActive: false);

            Assert.False(decision.CanStart);
            Assert.Contains("previous standalone loot task", decision.RejectionReason!, StringComparison.Ordinal);
        }

        [Fact]
        public void StandaloneLootMayJoinAValidSharedMovementCamera()
        {
            StandaloneLootStartupDecision decision = StandaloneLootStartupPolicy.Evaluate(
                workflowRunning: false,
                workflowTaskActive: false,
                movementRunning: true,
                movementCameraAvailable: true,
                lootTaskActive: false,
                foreignCameraSessionActive: false);

            Assert.True(decision.CanStart);
            Assert.Null(decision.RejectionReason);
        }

        [Fact]
        public void StandaloneLootMayStartWhenNoCameraSessionExists()
        {
            StandaloneLootStartupDecision decision = StandaloneLootStartupPolicy.Evaluate(
                workflowRunning: false,
                workflowTaskActive: false,
                movementRunning: false,
                movementCameraAvailable: false,
                lootTaskActive: false,
                foreignCameraSessionActive: false);

            Assert.True(decision.CanStart);
        }
    }
}
