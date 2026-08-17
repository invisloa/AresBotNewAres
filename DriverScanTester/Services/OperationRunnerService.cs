using System;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Runs named custom operations registered in <see cref="BotOperations"/> with a
    /// hard timeout and a retry loop. Unknown names are reported and fail the caller so
    /// the workflow never silently skips a required step.
    /// </summary>
    public sealed class OperationRunnerService
    {
        /// <summary>Hard cap on a single operation run before it is treated as failed.</summary>
        public const int OperationTimeoutMs = 60_000;

        /// <summary>Number of attempts before an operation is considered failed.</summary>
        public const int MaxAttempts = 3;

        /// <summary>Delay between failed operation attempts.</summary>
        public const int RetryDelayMs = 3_000;

        private readonly OperationContext _ctx;
        private readonly Action<string> _log;

        public OperationRunnerService(OperationContext ctx, Action<string> log)
        {
            _ctx = ctx;
            _log = log;
        }

        /// <summary>True when an operation with this name is registered.</summary>
        public bool IsKnown(string name) => BotOperations.IsKnown(name);

        /// <summary>All registered operation names (for the profile editor / validation).</summary>
        public System.Collections.Generic.IReadOnlyList<string> KnownNames => BotOperations.KnownNames;

        /// <summary>
        /// Runs the named operation, retrying up to <see cref="MaxAttempts"/> times.
        /// Returns true when the operation ultimately succeeded. Throws when cancelled.
        /// </summary>
        public async Task<bool> RunWithRetryAsync(string name, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _log("[Operation] Empty operation name — skipping.");
                return true;
            }

            if (!BotOperations.IsKnown(name))
            {
                _log($"[Operation] Unknown operation '{name}'. Check the BotOperations.Operations registry.");
                return false;
            }

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (await RunOnceAsync(name, token))
                    return true;

                if (token.IsCancellationRequested)
                    return false;

                if (attempt < MaxAttempts)
                {
                    _log($"[Operation] '{name}' attempt {attempt}/{MaxAttempts} failed. Retrying in {RetryDelayMs} ms...");
                    await Task.Delay(RetryDelayMs, token);
                }
                else
                {
                    _log($"[Operation] '{name}' failed after {MaxAttempts} attempts.");
                }
            }

            return false;
        }

        /// <summary>
        /// Runs the named operation once with a hard timeout. Returns true on success.
        /// </summary>
        public async Task<bool> RunOnceAsync(string name, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _log("[Operation] Empty operation name — skipping.");
                return true;
            }

            if (!BotOperations.IsKnown(name))
            {
                _log($"[Operation] Unknown operation '{name}'. Check the BotOperations.Operations registry.");
                return false;
            }

            _log($"[Operation] Running '{name}'...");
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(OperationTimeoutMs);
                bool ok = await BotOperations.Operations[name](_ctx, timeoutCts.Token);
                _log(ok
                    ? $"[Operation] '{name}' completed."
                    : $"[Operation] '{name}' failed.");
                return ok;
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                    _log($"[Operation] '{name}' cancelled by workflow stop.");
                else
                    _log($"[Operation] '{name}' timed out after {OperationTimeoutMs} ms.");
                return false;
            }
            catch (Exception ex)
            {
                _log($"[Operation] '{name}' threw: {ex.Message}");
                return false;
            }
        }
    }
}