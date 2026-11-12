using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    internal enum GameCrashRecoveryOutcome
    {
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Ordered recovery after the game client crashes or loses its server connection:
    ///
    ///   1. Make sure the old Ares client process is gone (kill it when it is still alive).
    ///   2. Check the COMPUTER's internet connection; retry every five minutes until it
    ///      is available (the game itself is already stopped at this point).
    ///   3. Launch the client and run the normal login → realm → character-selection
    ///      sequence through <see cref="GameClientStartupService"/>.
    ///   4. Wait five seconds after the character selection, attach to the new client
    ///      and wait until the world map/position are readable.
    ///   5. Press the profile's town-teleport key and wait for the town map to load.
    ///   6. Start the main COT profile.
    ///
    /// All game/browser I/O is injected so the ordering and retry behaviour can be tested
    /// without a real game, without killing processes and without sleeping for minutes.
    /// </summary>
    internal sealed class GameCrashRecoveryService
    {
        private readonly Action<string> _log;
        private readonly Func<CancellationToken, Task<bool>> _hasInternetConnectionAsync;
        private readonly Func<bool, CancellationToken, Task<GameClientStartupResult>> _startGameClientAsync;
        private readonly Func<int, CancellationToken, Task<bool>> _attachToGameAsync;
        private readonly Func<int, CancellationToken, Task<bool>> _waitForMapLoadedAsync;
        private readonly Func<BotProfile, CancellationToken, Task<bool>> _teleportToCityAsync;
        private readonly Func<BotProfile, CancellationToken, Task<bool>> _startMainProfileAsync;

        /// <summary>Returns the PIDs of all running Ares.exe client processes.</summary>
        public Func<IReadOnlyCollection<int>> GetGameProcessIds { get; set; }

        /// <summary>True when the process is still running (or cannot be proven exited).</summary>
        public Func<int, bool> IsProcessRunning { get; set; }

        /// <summary>Requests a process kill; returns false when the kill request failed.</summary>
        public Func<int, bool> KillProcess { get; set; }

        public int InternetRetryDelayMs { get; set; } =
            BotConstants.Connection.RecoveryInternetRetryDelayMs;

        public int ProcessStopTimeoutMs { get; set; } =
            BotConstants.Connection.RecoveryProcessStopTimeoutMs;

        public int PostCharacterSelectionDelayMs { get; set; } =
            BotConstants.Connection.RecoveryPostCharacterSelectionDelayMs;

        public int MapLoadTimeoutMs { get; set; } =
            BotConstants.Connection.RecoveryMapLoadTimeoutMs;

        public int PostTeleportMapLoadTimeoutMs { get; set; } =
            BotConstants.Connection.RecoveryPostTeleportMapLoadTimeoutMs;

        public GameCrashRecoveryService(
            Action<string> log,
            Func<CancellationToken, Task<bool>> hasInternetConnectionAsync,
            Func<bool, CancellationToken, Task<GameClientStartupResult>> startGameClientAsync,
            Func<int, CancellationToken, Task<bool>> attachToGameAsync,
            Func<int, CancellationToken, Task<bool>> waitForMapLoadedAsync,
            Func<BotProfile, CancellationToken, Task<bool>> teleportToCityAsync,
            Func<BotProfile, CancellationToken, Task<bool>> startMainProfileAsync)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _hasInternetConnectionAsync = hasInternetConnectionAsync ?? throw new ArgumentNullException(nameof(hasInternetConnectionAsync));
            _startGameClientAsync = startGameClientAsync ?? throw new ArgumentNullException(nameof(startGameClientAsync));
            _attachToGameAsync = attachToGameAsync ?? throw new ArgumentNullException(nameof(attachToGameAsync));
            _waitForMapLoadedAsync = waitForMapLoadedAsync ?? throw new ArgumentNullException(nameof(waitForMapLoadedAsync));
            _teleportToCityAsync = teleportToCityAsync ?? throw new ArgumentNullException(nameof(teleportToCityAsync));
            _startMainProfileAsync = startMainProfileAsync ?? throw new ArgumentNullException(nameof(startMainProfileAsync));

            GetGameProcessIds = GetAresProcessIds;
            IsProcessRunning = IsAresProcessRunning;
            KillProcess = TryKillAresProcess;
        }

        /// <summary>
        /// Runs the complete recovery. Returns <see cref="GameCrashRecoveryOutcome.Failed"/>
        /// for every expected failure (a clear log line is written for each step); the
        /// caller can then require a manual restart.
        /// </summary>
        public async Task<GameCrashRecoveryOutcome> RecoverAsync(
            uint crashedGameProcessId,
            BotProfile profile,
            bool useMageLogin,
            CancellationToken token)
        {
            if (profile == null)
            {
                _log("[GameRecovery] Recovery failed: no profile is available to start after re-login.");
                return GameCrashRecoveryOutcome.Failed;
            }

            try
            {
                _log($"[GameRecovery] Automatic game recovery started (profile '{profile.Name}').");

                if (!await EnsureGameClientStoppedAsync(crashedGameProcessId, token))
                    return GameCrashRecoveryOutcome.Failed;

                await WaitForInternetAsync(token);

                _log("[GameRecovery] Starting the game client and logging in...");
                GameClientStartupResult startup = await _startGameClientAsync(useMageLogin, token);
                if (startup.Status == GameClientStartupStatus.Cancelled)
                {
                    _log("[GameRecovery] Client startup cancelled; recovery stopped.");
                    return GameCrashRecoveryOutcome.Cancelled;
                }
                if (!startup.Started)
                {
                    _log($"[GameRecovery] Client startup failed: {startup.Message} Manual restart is required.");
                    return GameCrashRecoveryOutcome.Failed;
                }

                int gameProcessId = startup.GameProcessId > 0
                    ? startup.GameProcessId
                    : GetGameProcessIds().FirstOrDefault();
                if (gameProcessId == 0)
                {
                    _log("[GameRecovery] The client was started but no Ares process could be resolved. Manual restart is required.");
                    return GameCrashRecoveryOutcome.Failed;
                }

                _log(
                    $"[GameRecovery] Character selection sequence completed (PID={gameProcessId}). " +
                    $"Waiting {PostCharacterSelectionDelayMs / 1000.0:F0} seconds for the world to load...");
                await Task.Delay(PostCharacterSelectionDelayMs, token);

                _log($"[GameRecovery] Attaching to the new game process (PID={gameProcessId})...");
                if (!await _attachToGameAsync(gameProcessId, token))
                {
                    _log("[GameRecovery] Could not attach to the new game process. Manual restart is required.");
                    return GameCrashRecoveryOutcome.Failed;
                }

                _log("[GameRecovery] Waiting until the logged-in character's map and position are readable...");
                if (!await _waitForMapLoadedAsync(MapLoadTimeoutMs, token))
                {
                    _log("[GameRecovery] The game world did not become readable in time. Manual restart is required.");
                    return GameCrashRecoveryOutcome.Failed;
                }

                _log("[GameRecovery] The map is loaded. Teleporting to the city before starting the main profile...");
                bool teleported = await _teleportToCityAsync(profile, token);
                if (!teleported)
                {
                    _log("[GameRecovery] The town teleport could not be verified; continuing only after checking that the map is stable again.");
                }

                _log("[GameRecovery] Waiting for the town map to load...");
                if (!await _waitForMapLoadedAsync(PostTeleportMapLoadTimeoutMs, token))
                {
                    _log("[GameRecovery] The town map did not become readable after the teleport. Manual restart is required.");
                    return GameCrashRecoveryOutcome.Failed;
                }

                _log($"[GameRecovery] Starting the main profile '{profile.Name}'...");
                if (!await _startMainProfileAsync(profile, token))
                {
                    _log("[GameRecovery] The main profile could not be started. Manual restart is required.");
                    return GameCrashRecoveryOutcome.Failed;
                }

                _log("[GameRecovery] Recovery complete — the main profile is running again.");
                return GameCrashRecoveryOutcome.Completed;
            }
            catch (OperationCanceledException)
            {
                _log("[GameRecovery] Recovery cancelled.");
                return GameCrashRecoveryOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                _log($"[GameRecovery] Unexpected recovery error: {ex.Message}. Manual restart is required.");
                return GameCrashRecoveryOutcome.Failed;
            }
        }

        /// <summary>
        /// Step 1: the old client must be gone before a new one may be launched. When the
        /// process is still active it is killed; when it already exited the step is a no-op.
        /// </summary>
        private async Task<bool> EnsureGameClientStoppedAsync(
            uint crashedGameProcessId,
            CancellationToken token)
        {
            var candidates = new HashSet<int>();
            foreach (int pid in GetGameProcessIds())
                candidates.Add(pid);
            if (crashedGameProcessId > 0 && crashedGameProcessId <= int.MaxValue)
                candidates.Add((int)crashedGameProcessId);

            List<int> running = candidates.Where(IsProcessRunning).ToList();
            if (running.Count == 0)
            {
                _log("[GameRecovery] The game client process is not active — nothing to kill.");
                return true;
            }

            foreach (int pid in running)
            {
                token.ThrowIfCancellationRequested();
                _log($"[GameRecovery] The game client process is still active (PID={pid}); killing it.");
                if (!KillProcess(pid))
                    _log($"[GameRecovery] Kill request for PID={pid} failed; waiting for the process to exit anyway.");
            }

            var stopTimer = Stopwatch.StartNew();
            while (stopTimer.ElapsedMilliseconds < ProcessStopTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                List<int> stillRunning = GetGameProcessIds().Where(IsProcessRunning).ToList();
                if (crashedGameProcessId > 0 && crashedGameProcessId <= int.MaxValue &&
                    IsProcessRunning((int)crashedGameProcessId) &&
                    !stillRunning.Contains((int)crashedGameProcessId))
                {
                    stillRunning.Add((int)crashedGameProcessId);
                }

                if (stillRunning.Count == 0)
                {
                    _log("[GameRecovery] The game client process has exited.");
                    return true;
                }

                await Task.Delay(500, token);
            }

            List<int> remaining = GetGameProcessIds().Where(IsProcessRunning).ToList();
            if (crashedGameProcessId > 0 && crashedGameProcessId <= int.MaxValue &&
                IsProcessRunning((int)crashedGameProcessId) &&
                !remaining.Contains((int)crashedGameProcessId))
            {
                remaining.Add((int)crashedGameProcessId);
            }

            if (remaining.Count == 0)
            {
                _log("[GameRecovery] The game client process has exited.");
                return true;
            }

            _log(
                $"[GameRecovery] The game client process is still alive after {ProcessStopTimeoutMs / 1000.0:F0} seconds " +
                $"(PID(s) {string.Join(", ", remaining)}). A new client cannot be launched safely.");
            return false;
        }

        /// <summary>Step 2: wait until the computer has internet; retry forever (until cancelled).</summary>
        private async Task WaitForInternetAsync(CancellationToken token)
        {
            int attempt = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                attempt++;

                if (await _hasInternetConnectionAsync(token))
                {
                    _log(attempt == 1
                        ? "[GameRecovery] Internet connection is available."
                        : $"[GameRecovery] Internet connection is available again (check {attempt}).");
                    return;
                }

                _log(
                    $"[GameRecovery] No internet connection (check {attempt}). " +
                    $"Waiting {InternetRetryDelayMs / 60000.0:F1} minutes before checking again...");
                await Task.Delay(InternetRetryDelayMs, token);
            }
        }

        private static IReadOnlyCollection<int> GetAresProcessIds()
        {
            var result = new List<int>();
            try
            {
                foreach (Process process in Process.GetProcessesByName("Ares"))
                {
                    using (process)
                        result.Add(process.Id);
                }
            }
            catch
            {
                // An enumeration failure is surfaced by the "no process" path.
            }

            return result;
        }

        private static bool IsAresProcessRunning(int pid)
        {
            if (pid <= 0)
                return false;

            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // The process no longer exists.
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch
            {
                // Access denied / transient failures must not be mistaken for "exited":
                // launching a second client is worse than waiting for the kill timeout.
                return true;
            }
        }

        private static bool TryKillAresProcess(int pid)
        {
            if (pid <= 0)
                return true;

            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;

                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
