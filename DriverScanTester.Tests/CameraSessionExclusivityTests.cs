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
    }
}
