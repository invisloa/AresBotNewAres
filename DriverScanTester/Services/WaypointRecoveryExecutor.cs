using System;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Executes waypoint recovery actions that need operation or saved-path services.
    /// The auxiliary path runner is intentionally separate from the parent route runner
    /// and has waypoint special recoveries disabled, preventing ownership conflicts and
    /// recursive recovery chains.
    /// </summary>
    public sealed class WaypointRecoveryExecutor
    {
        private readonly SavedPathLoader _pathLoader;
        private readonly Action<string> _log;
        private readonly PathRunnerService _auxiliaryPathRunner;
        private readonly OperationRunnerService _operationRunner;

        public WaypointRecoveryExecutor(
            GameMemoryService memoryService,
            SavedPathLoader pathLoader,
            ItemSellerService itemSeller,
            BotProfile profile,
            Action<string> log,
            Action focusGameWindow)
        {
            if (memoryService == null) throw new ArgumentNullException(nameof(memoryService));
            _pathLoader = pathLoader ?? throw new ArgumentNullException(nameof(pathLoader));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            _auxiliaryPathRunner = new PathRunnerService(
                memoryService,
                log,
                enableWaypointSpecialRecoveries: false);

            var context = new OperationContext(
                memoryService,
                _auxiliaryPathRunner,
                itemSeller ?? throw new ArgumentNullException(nameof(itemSeller)),
                profile ?? throw new ArgumentNullException(nameof(profile)),
                log,
                focusGameWindow ?? throw new ArgumentNullException(nameof(focusGameWindow)));
            _operationRunner = new OperationRunnerService(context, log);
        }

        public async Task<bool> RunOperationAsync(string operationName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(operationName))
            {
                _log("[WaypointRecovery] Operation recovery has no operation name.");
                return false;
            }

            if (!_operationRunner.IsKnown(operationName))
            {
                _log($"[WaypointRecovery] Unknown operation '{operationName}'.");
                return false;
            }

            _log($"[WaypointRecovery] Running operation '{operationName}'.");
            bool succeeded = await _operationRunner.RunWithRetryAsync(operationName, token);
            token.ThrowIfCancellationRequested();
            return succeeded;
        }

        public async Task<bool> RunRecoveryPathAsync(string pathName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(pathName))
            {
                _log("[WaypointRecovery] Recovery path is empty.");
                return false;
            }

            _log($"[WaypointRecovery] Running recovery path '{pathName}'.");
            var waypoints = _pathLoader.LoadSegment(pathName);
            if (waypoints == null)
                return false;

            token.ThrowIfCancellationRequested();
            bool completed = await _auxiliaryPathRunner.RunPathAsync(waypoints, loop: false, token);
            token.ThrowIfCancellationRequested();
            return completed;
        }
    }
}
