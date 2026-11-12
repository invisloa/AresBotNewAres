using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    /// <summary>Outcome of one automatic game-client startup attempt.</summary>
    internal enum GameClientStartupStatus
    {
        /// <summary>The client window was found and the login/character selection input
        /// sequence was dispatched. The game may still be loading the world.</summary>
        Started,

        /// <summary>The client could not be launched/detected, or the input sequence failed.</summary>
        Failed,

        /// <summary>An Ares client was already running; no duplicate instance was launched.</summary>
        AlreadyRunning,

        /// <summary>The caller cancelled the startup (Stop All / recovery cancelled).</summary>
        Cancelled
    }

    internal sealed record GameClientStartupResult(
        GameClientStartupStatus Status,
        int GameProcessId,
        string Message)
    {
        public bool Started => Status == GameClientStartupStatus.Started;
    }

    /// <summary>
    /// Owns the complete "launch the launcher, click PLAY, log in and select the character"
    /// automation. It is shared by the Bot Control Panel button and by the automatic
    /// crash recovery, so both paths execute exactly the same input sequence.
    ///
    /// One instance accepts one startup at a time. The caller supplies the cancellation
    /// token, and <see cref="RunningChanged"/> lets the UI refresh its command states while
    /// the sequence is running.
    /// </summary>
    internal sealed class GameClientStartupService
    {
        private const int LauncherDetectionTimeoutMs = 15_000;
        private const int LauncherInitializationDelayMs = 10_000;
        private const int GameStartupCheckIntervalMs = 500;
        private const int GameWindowStartupGraceMs = 30_000;
        private const int GameStartupWaitAfterClickMs = 20_000;
        private const int PlayClickRetryDelayMs = 2_000;
        private const int MaxPlayClickAttempts = 3;
        private const int OverallGameStartupTimeoutMs = 150_000;
        private const int GameLoginDelayMs = 15_000;
        private const int PhoenixSelectionDelayMs = 5_000;
        private const int CharacterSelectionDelayMs = 10_000;
        private const int StartupClickCount = 5;
        private const int StartupClickIntervalMs = 250;
        private const int CharacterEnterDelayMs = 500;
        private const int LoginReferenceClientWidth = 1024;
        private const int LoginReferenceClientHeight = 768;
        private const int PhoenixSelectionOffsetX = 509;
        private const int PhoenixSelectionOffsetY = 193;
        // Calibrated from the character-selection point in the 1024x768 game client.
        private const int CharacterSelectionOffsetX = 179;
        private const int CharacterSelectionOffsetY = 437;
        private const string KnightLoginUsername = "superknaj87";
        private const string MageLoginUsername = "supermage";
        private const string GameLoginPassword = "Jankoza8712";

        private readonly Action<string> _log;
        private readonly AresLauncherWindowService _aresLauncherWindowService = new();

        private int _startupGuard;
        private int _startedGameProcessId;
        private AresLauncherWindowInfo? _lastDetectedLauncherWindow;
        private bool _launcherFailureScreenshotAttempted;

        public GameClientStartupService(Action<string> log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Raised when a startup begins and after its cleanup finishes.</summary>
        public event Action<bool>? RunningChanged;

        /// <summary>True while an automatic startup/Ares login sequence is running.</summary>
        public bool IsRunning => Volatile.Read(ref _startupGuard) != 0;

        /// <summary>Process ID of the Ares client detected by the last successful startup
        /// (0 while no client was found).</summary>
        public int StartedGameProcessId => Volatile.Read(ref _startedGameProcessId);

        /// <summary>
        /// Launches/reuses the launcher, clicks PLAY, waits for the client window, logs in,
        /// selects the realm and double-clicks the character. Returns a result instead of
        /// throwing for expected failures; cancellation is reported as
        /// <see cref="GameClientStartupStatus.Cancelled"/>.
        /// </summary>
        public async Task<GameClientStartupResult> StartAsync(bool useMageLogin, CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _startupGuard, 1, 0) != 0)
            {
                return new GameClientStartupResult(
                    GameClientStartupStatus.Failed,
                    0,
                    "Automatic game startup is already running.");
            }

            Interlocked.Exchange(ref _startedGameProcessId, 0);
            _lastDetectedLauncherWindow = null;
            _launcherFailureScreenshotAttempted = false;

            try
            {
                RunningChanged?.Invoke(true);
                return await StartCoreAsync(useMageLogin, token);
            }
            catch (OperationCanceledException)
            {
                _log("[Launcher] Startup automation cancelled by Stop All.");
                return new GameClientStartupResult(GameClientStartupStatus.Cancelled, 0, "Startup automation cancelled.");
            }
            catch (TimeoutException ex)
            {
                return Fail(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail($"Startup automation failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _startupGuard, 0);
                try { RunningChanged?.Invoke(false); } catch { }
            }
        }

        private async Task<GameClientStartupResult> StartCoreAsync(bool useMageLogin, CancellationToken token)
        {
            var overallTimer = Stopwatch.StartNew();

            if (!_aresLauncherWindowService.TryCaptureGameProcessIds(
                    out HashSet<int> gameProcessIdsBeforeLaunch,
                    out string gameProcessError))
            {
                return Fail($"Could not check for an existing Ares process; launch aborted to avoid a duplicate instance. {gameProcessError}");
            }
            if (gameProcessIdsBeforeLaunch.Count > 0)
            {
                _log(
                    $"[Launcher] Ares is already running (PID(s) {string.Join(", ", gameProcessIdsBeforeLaunch)}). " +
                    "No duplicate client launch or PLAY click was made.");
                return new GameClientStartupResult(
                    GameClientStartupStatus.AlreadyRunning,
                    gameProcessIdsBeforeLaunch.First(),
                    "An Ares client was already running.");
            }

            HashSet<int> launcherProcessIdsBeforeLaunch = _aresLauncherWindowService.CaptureLauncherProcessIds();
            int shortcutProcessId = 0;
            string shortcutProcessName = "unknown";
            if (launcherProcessIdsBeforeLaunch.Count > 0)
            {
                if (launcherProcessIdsBeforeLaunch.Count == 1)
                    shortcutProcessId = launcherProcessIdsBeforeLaunch.First();
                shortcutProcessName = "Launcher";
                _log(
                    $"[Startup] Reusing existing Launcher.exe process(es) (PID(s) {string.Join(", ", launcherProcessIdsBeforeLaunch)}); " +
                    "no second launcher was started.");
            }
            else
            {
                string? gameClientShortcutPath = GameClientShortcutResolver.Find();
                if (gameClientShortcutPath == null)
                    return Fail(GameClientShortcutResolver.GetNotFoundMessage());

                var launchInfo = new ProcessStartInfo
                {
                    FileName = gameClientShortcutPath,
                    UseShellExecute = true
                };
                if (string.Equals(Path.GetExtension(gameClientShortcutPath), ".exe", StringComparison.OrdinalIgnoreCase))
                    launchInfo.WorkingDirectory = Path.GetDirectoryName(gameClientShortcutPath) ?? string.Empty;

                using (Process? shortcutProcess = Process.Start(launchInfo))
                {
                    if (shortcutProcess != null)
                    {
                        try
                        {
                            shortcutProcessId = shortcutProcess.Id;
                            shortcutProcessName = shortcutProcess.ProcessName;
                        }
                        catch
                        {
                            // Shell execution may not expose the associated process.
                        }
                    }
                }

                _log(
                    $"[Startup] Launched the game launcher entry point '{gameClientShortcutPath}'" +
                    (shortcutProcessId > 0 ? $" (PID={shortcutProcessId}, process={shortcutProcessName})." : "."));
            }

            _log(
                $"[Launcher] Waiting {LauncherInitializationDelayMs / 1000} seconds after starting/reusing the launcher " +
                "before looking for its PLAY window; the update progress state is not exposed through a reliable signal.");
            await DelayWithinGameStartupAsync(LauncherInitializationDelayMs, overallTimer, token);

            _log(
                $"[Launcher] Checking for a visible Ares launcher window for up to {LauncherDetectionTimeoutMs / 1000} more seconds.");

            AresLauncherWindowInfo launcherWindow = default;
            int launcherProcessId = 0;
            string lastDetectionReason = "No Launcher.exe process was found.";
            var detectionTimer = Stopwatch.StartNew();
            long nextDetectionLogMs = 5_000;
            while (detectionTimer.ElapsedMilliseconds < LauncherDetectionTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                EnsureGameStartupHasTime(overallTimer);

                HashSet<int> currentLauncherProcessIds = _aresLauncherWindowService.CaptureLauncherProcessIds();
                int preferredProcessId = SelectLauncherProcessId(
                    launcherProcessIdsBeforeLaunch,
                    currentLauncherProcessIds,
                    shortcutProcessId,
                    allowSingleExistingProcess: true);
                if (_aresLauncherWindowService.TryFindLauncherWindow(
                        preferredProcessId,
                        out launcherWindow,
                        out lastDetectionReason))
                {
                    launcherProcessId = (int)launcherWindow.ProcessId;
                    break;
                }

                if (detectionTimer.ElapsedMilliseconds >= nextDetectionLogMs)
                {
                    _log($"[Launcher] Still waiting for a visible launcher window: {lastDetectionReason}");
                    nextDetectionLogMs += 5_000;
                }

                await DelayWithinGameStartupAsync(GameStartupCheckIntervalMs, overallTimer, token);
            }

            if (launcherWindow.Handle == nint.Zero)
            {
                return Fail(
                    $"No visible Ares launcher window appeared after the {LauncherInitializationDelayMs / 1000}-second initialization wait " +
                    $"and the additional {LauncherDetectionTimeoutMs / 1000}-second detection period. {lastDetectionReason}");
            }

            LogLauncherWindow(launcherWindow, launcherProcessId);

            GameStartupObservation startupObservation = await WaitForGameStartupAsync(
                gameProcessIdsBeforeLaunch,
                initialWaitMs: 0,
                overallTimer,
                token);
            if (startupObservation == GameStartupObservation.WindowDetected)
                return await CompleteLoginResultAsync(gameProcessIdsBeforeLaunch, useMageLogin, token);
            if (startupObservation == GameStartupObservation.ProcessStartedWithoutWindow)
            {
                return Fail("The Ares process started, but its visible game window did not appear within the startup grace period.");
            }

            for (int attempt = 1; attempt <= MaxPlayClickAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                EnsureGameStartupHasTime(overallTimer);

                HashSet<int> currentLauncherProcessIds = _aresLauncherWindowService.CaptureLauncherProcessIds();
                int preferredProcessId = currentLauncherProcessIds.Contains(launcherProcessId)
                    ? launcherProcessId
                    : 0;
                if (!_aresLauncherWindowService.TryFindLauncherWindow(
                        preferredProcessId,
                        out launcherWindow,
                        out string findReason))
                {
                    return Fail($"The launcher was no longer available before PLAY attempt {attempt}: {findReason}");
                }
                launcherProcessId = (int)launcherWindow.ProcessId;

                _log(
                    $"[Launcher] Attempt {attempt}/{MaxPlayClickAttempts}: detected HWND=0x{launcherWindow.Handle.ToInt64():X}, " +
                    $"PID={launcherWindow.ProcessId}, client origin=({launcherWindow.ClientScreenBounds.X},{launcherWindow.ClientScreenBounds.Y}), " +
                    $"size={launcherWindow.ClientScreenBounds.Width}x{launcherWindow.ClientScreenBounds.Height}.");

                if (!_aresLauncherWindowService.TryFocusLauncherWindow(launcherWindow, out string focusReason))
                {
                    _log($"[Launcher] PLAY attempt {attempt} skipped because the launcher could not be activated: {focusReason}");
                }
                else if (!_aresLauncherWindowService.TryRefreshLauncherWindow(
                             launcherWindow,
                             out AresLauncherWindowInfo currentWindow,
                             out string refreshReason))
                {
                    _log($"[Launcher] PLAY attempt {attempt} skipped because the launcher changed before clicking: {refreshReason}");
                }
                else if (!AresLauncherWindowService.TryCalculatePlayButtonPosition(
                             currentWindow.ClientScreenBounds,
                             out System.Drawing.Point playPosition,
                             out string coordinateReason))
                {
                    return Fail($"Could not calculate a safe PLAY position: {coordinateReason}");
                }
                else
                {
                    _lastDetectedLauncherWindow = currentWindow;
                    GameStartupObservation beforeClickObservation = await WaitForGameStartupAsync(
                        gameProcessIdsBeforeLaunch,
                        initialWaitMs: 0,
                        overallTimer,
                        token);
                    if (beforeClickObservation == GameStartupObservation.WindowDetected)
                        return await CompleteLoginResultAsync(gameProcessIdsBeforeLaunch, useMageLogin, token);
                    if (beforeClickObservation == GameStartupObservation.ProcessStartedWithoutWindow)
                    {
                        return Fail("The Ares process started before the next PLAY click, but its visible game window did not appear within the startup grace period. No duplicate click was made.");
                    }

                    int scaledRightOffset = currentWindow.ClientScreenBounds.Right - playPosition.X;
                    int scaledBottomOffset = currentWindow.ClientScreenBounds.Bottom - playPosition.Y;
                    _log(
                        $"[Launcher] PLAY click position=({playPosition.X},{playPosition.Y}), " +
                        $"client origin=({currentWindow.ClientScreenBounds.X},{currentWindow.ClientScreenBounds.Y}), " +
                        $"size={currentWindow.ClientScreenBounds.Width}x{currentWindow.ClientScreenBounds.Height}, " +
                        $"edge offsets right={scaledRightOffset}, bottom={scaledBottomOffset}, attempt={attempt}.");

                    token.ThrowIfCancellationRequested();
                    bool clickExecuted = await Task.Run(
                        () => _aresLauncherWindowService.TryLeftClick(playPosition.X, playPosition.Y),
                        token);
                    if (clickExecuted)
                        _log("[Launcher] Left click executed.");
                    else
                        _log($"[Launcher] PLAY attempt {attempt} could not move/click the mouse; checking game startup before retrying.");
                }

                _log("[Launcher] Waiting for the actual Ares game process/window to appear.");
                startupObservation = await WaitForGameStartupAsync(
                    gameProcessIdsBeforeLaunch,
                    GameStartupWaitAfterClickMs,
                    overallTimer,
                    token);
                if (startupObservation == GameStartupObservation.WindowDetected)
                    return await CompleteLoginResultAsync(gameProcessIdsBeforeLaunch, useMageLogin, token);
                if (startupObservation == GameStartupObservation.ProcessStartedWithoutWindow)
                {
                    return Fail("The Ares process started, but its visible game window did not appear within the startup grace period. No duplicate PLAY click was made.");
                }

                HashSet<int> launcherProcessIdsAfterAttempt = _aresLauncherWindowService.CaptureLauncherProcessIds();
                int preferredAfterAttempt = launcherProcessIdsAfterAttempt.Contains(launcherProcessId)
                    ? launcherProcessId
                    : 0;
                if (!_aresLauncherWindowService.TryFindLauncherWindow(
                        preferredAfterAttempt,
                        out launcherWindow,
                        out string retryFindReason))
                {
                    return Fail($"The Ares launcher closed before a verified game window appeared: {retryFindReason}");
                }
                launcherProcessId = (int)launcherWindow.ProcessId;
                _lastDetectedLauncherWindow = launcherWindow;

                if (attempt < MaxPlayClickAttempts)
                {
                    _log(
                        $"[Launcher] No new Ares process/window appeared after attempt {attempt}; " +
                        $"launcher is still present, retrying in {PlayClickRetryDelayMs / 1000} seconds.");
                    await DelayWithinGameStartupAsync(PlayClickRetryDelayMs, overallTimer, token);
                }
            }

            return Fail($"Ares did not start after {MaxPlayClickAttempts} PLAY click attempts.");
        }

        private async Task<GameClientStartupResult> CompleteLoginResultAsync(
            HashSet<int> gameProcessIdsBeforeLaunch,
            bool useMageLogin,
            CancellationToken token)
        {
            bool completed = await CompleteGameStartupLoginAsync(gameProcessIdsBeforeLaunch, useMageLogin, token);
            if (!completed)
            {
                return Fail("The automatic login/character selection sequence did not complete.");
            }

            return new GameClientStartupResult(
                GameClientStartupStatus.Started,
                StartedGameProcessId,
                "The game client was started and the login/character selection inputs were dispatched.");
        }

        private enum GameStartupObservation
        {
            NotStarted,
            ProcessStartedWithoutWindow,
            WindowDetected
        }

        private async Task<GameStartupObservation> WaitForGameStartupAsync(
            HashSet<int> gameProcessIdsBeforeLaunch,
            int initialWaitMs,
            Stopwatch overallTimer,
            CancellationToken token)
        {
            var phaseTimer = Stopwatch.StartNew();
            long? firstNewProcessAtMs = null;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                EnsureGameStartupHasTime(overallTimer);

                if (!_aresLauncherWindowService.TryHasNewVisibleGameWindow(
                        gameProcessIdsBeforeLaunch,
                        out bool gameWindowDetected,
                        out string windowDetectionError))
                {
                    throw new InvalidOperationException($"Could not inspect Ares windows: {windowDetectionError}");
                }
                if (gameWindowDetected)
                    return GameStartupObservation.WindowDetected;

                if (!_aresLauncherWindowService.TryHasNewGameProcess(
                        gameProcessIdsBeforeLaunch,
                        out bool gameProcessDetected,
                        out string processDetectionError))
                {
                    throw new InvalidOperationException($"Could not inspect Ares processes: {processDetectionError}");
                }
                if (firstNewProcessAtMs == null && gameProcessDetected)
                {
                    firstNewProcessAtMs = phaseTimer.ElapsedMilliseconds;
                    _log("[Launcher] A new Ares game process appeared; suppressing further PLAY clicks while its window initializes.");
                }

                if (firstNewProcessAtMs.HasValue)
                {
                    if (phaseTimer.ElapsedMilliseconds - firstNewProcessAtMs.Value >= GameWindowStartupGraceMs)
                        return GameStartupObservation.ProcessStartedWithoutWindow;
                }
                else if (phaseTimer.ElapsedMilliseconds >= initialWaitMs)
                {
                    return GameStartupObservation.NotStarted;
                }

                await DelayWithinGameStartupAsync(GameStartupCheckIntervalMs, overallTimer, token);
            }
        }

        private static int SelectLauncherProcessId(
            HashSet<int> processIdsBeforeLaunch,
            HashSet<int> currentProcessIds,
            int preferredProcessId,
            bool allowSingleExistingProcess)
        {
            if (preferredProcessId > 0 && currentProcessIds.Contains(preferredProcessId))
                return preferredProcessId;

            int[] newProcessIds = currentProcessIds.Except(processIdsBeforeLaunch).ToArray();
            if (newProcessIds.Length == 1)
                return newProcessIds[0];
            if (allowSingleExistingProcess && newProcessIds.Length == 0 && currentProcessIds.Count == 1)
                return currentProcessIds.First();

            return 0;
        }

        private void LogLauncherWindow(AresLauncherWindowInfo window, int processId)
        {
            _lastDetectedLauncherWindow = window;
            _log(
                $"[Launcher] Ares launcher detected. Process=Launcher.exe PID={processId}, " +
                $"HWND=0x{window.Handle.ToInt64():X}, title='{window.Title}'.");
            _log(
                $"[Launcher] Client origin=({window.ClientScreenBounds.X},{window.ClientScreenBounds.Y}), " +
                $"size={window.ClientScreenBounds.Width}x{window.ClientScreenBounds.Height}, " +
                $"window bounds={window.WindowScreenBounds}.");
        }

        private async Task<bool> CompleteGameStartupLoginAsync(
            HashSet<int> gameProcessIdsBeforeLaunch,
            bool useMageLogin,
            CancellationToken token)
        {
            string characterName = useMageLogin ? "Mage" : "Knight";
            string username = useMageLogin ? MageLoginUsername : KnightLoginUsername;
            _log($"[Login] Step 1/8: selected {characterName}; username configured (password will not be logged).");
            _log("[Login] Step 2/8: waiting 15 seconds for the login screen.");
            await Task.Delay(GameLoginDelayMs, token);
            _log("[Login] Step 2/8 complete.");

            _log("[Login] Step 3/8: locating the new Ares game window.");
            if (!_aresLauncherWindowService.TryFindNewVisibleGameWindow(
                    gameProcessIdsBeforeLaunch,
                    out bool gameWindowFound,
                    out AresLauncherWindowInfo gameWindow,
                    out string findReason))
            {
                _log($"[Login] Could not inspect the Ares game window; automatic login skipped. {findReason}");
                return false;
            }

            if (!gameWindowFound)
            {
                _log("[Login] The Ares game window was no longer visible; automatic login skipped.");
                return false;
            }

            Interlocked.Exchange(ref _startedGameProcessId, (int)gameWindow.ProcessId);
            _log(
                $"[Login] Step 3/8 complete: HWND=0x{gameWindow.Handle.ToInt64():X}, PID={gameWindow.ProcessId}, " +
                $"title='{gameWindow.Title}', client origin=({gameWindow.ClientScreenBounds.X},{gameWindow.ClientScreenBounds.Y}), " +
                $"size={gameWindow.ClientScreenBounds.Width}x{gameWindow.ClientScreenBounds.Height}.");

            _log("[Login] Step 4/8: waiting 500 ms, then focusing the game window.");
            await Task.Delay(500, token);
            if (!_aresLauncherWindowService.TryFocusGameWindow(gameWindow, out string focusReason))
            {
                _log($"[Login] Could not focus the Ares game window; automatic login skipped. {focusReason}");
                return false;
            }
            _log("[Login] Step 4/8 complete: game window is foreground.");

            _log("[Login] Step 5/8: typing the selected username.");
            await Task.Run(() => GameInput.TypeText(username), token);
            _log("[Login] Step 5/8 complete: username typed.");

            _log("[Login] Step 6/8: pressing Tab.");
            await Task.Run(() => GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB), token);
            _log("[Login] Step 6/8 complete: Tab pressed.");

            _log("[Login] Step 7/8: typing the password (value hidden).");
            await Task.Run(() => GameInput.TypeText(GameLoginPassword), token);
            _log("[Login] Step 7/8 complete: password typed.");

            _log("[Login] Step 8/8: pressing Enter.");
            await Task.Run(() => GameInput.PressKey(GameInput.VK_ENTER, GameInput.SCAN_ENTER), token);
            _log("[Login] Step 8/8 complete: Enter pressed.");

            return await ClickPhoenixSelectionAsync(gameProcessIdsBeforeLaunch, token);
        }

        private async Task<bool> ClickPhoenixSelectionAsync(
            HashSet<int> gameProcessIdsBeforeLaunch,
            CancellationToken token)
        {
            _log("[Login][Phoenix] Step 1/5: waiting 5 seconds for the selection screen.");
            await Task.Delay(PhoenixSelectionDelayMs, token);
            _log("[Login][Phoenix] Step 1/5 complete.");

            _log("[Login][Phoenix] Step 2/5: locating the Ares game window.");
            if (!_aresLauncherWindowService.TryFindNewVisibleGameWindow(
                    gameProcessIdsBeforeLaunch,
                    out bool gameWindowFound,
                    out AresLauncherWindowInfo gameWindow,
                    out string findReason))
            {
                _log($"[Login] Could not inspect the Ares window for Phoenix selection. {findReason}");
                return false;
            }

            if (!gameWindowFound)
            {
                _log("[Login] The Ares window was not visible for Phoenix selection; five-click sequence skipped.");
                return false;
            }

            Interlocked.Exchange(ref _startedGameProcessId, (int)gameWindow.ProcessId);
            _log(
                $"[Login][Phoenix] Step 2/5 complete: HWND=0x{gameWindow.Handle.ToInt64():X}, PID={gameWindow.ProcessId}, " +
                $"title='{gameWindow.Title}', client origin=({gameWindow.ClientScreenBounds.X},{gameWindow.ClientScreenBounds.Y}), " +
                $"size={gameWindow.ClientScreenBounds.Width}x{gameWindow.ClientScreenBounds.Height}.");

            _log("[Login][Phoenix] Step 3/5: activating the game window.");
            if (!_aresLauncherWindowService.TryFocusGameWindow(gameWindow, out string focusReason))
            {
                _log($"[Login] Could not focus the Ares window for Phoenix selection; five-click sequence skipped. {focusReason}");
                return false;
            }
            _log("[Login][Phoenix] Step 3/5 complete: game window is foreground.");

            System.Drawing.Rectangle client = gameWindow.ClientScreenBounds;
            int relativeX = (int)Math.Round(PhoenixSelectionOffsetX * (double)client.Width / LoginReferenceClientWidth);
            int relativeY = (int)Math.Round(PhoenixSelectionOffsetY * (double)client.Height / LoginReferenceClientHeight);
            int screenX = client.Left + relativeX;
            int screenY = client.Top + relativeY;
            _log(
                $"[Login][Phoenix] Step 4/5: offset=({relativeX},{relativeY}) scaled from " +
                $"{LoginReferenceClientWidth}x{LoginReferenceClientHeight}; current client origin=({client.Left},{client.Top}), " +
                $"size={client.Width}x{client.Height}; desktop target=({screenX},{screenY}).");

            if (!await TryStartupClickSequenceAsync(screenX, screenY, "Phoenix", token))
                return false;

            _log($"[Login][Phoenix] Step 4/5 complete: all {StartupClickCount} click inputs were dispatched; game response is not directly observable.");
            _log("[Login][Phoenix] Step 5/5: continuing to the character selection screen.");
            return await DoubleClickCharacterSelectionPointAsync(gameProcessIdsBeforeLaunch, token);
        }

        private async Task<bool> DoubleClickCharacterSelectionPointAsync(
            HashSet<int> gameProcessIdsBeforeLaunch,
            CancellationToken token)
        {
            _log("[Login][Character] Step 1/5: waiting 10 seconds for the character selection screen.");
            await Task.Delay(CharacterSelectionDelayMs, token);
            _log("[Login][Character] Step 1/5 complete.");

            _log("[Login][Character] Step 2/5: locating the Ares game window.");
            if (!_aresLauncherWindowService.TryFindNewVisibleGameWindow(
                    gameProcessIdsBeforeLaunch,
                    out bool gameWindowFound,
                    out AresLauncherWindowInfo gameWindow,
                    out string findReason))
            {
                _log($"[Login] Could not inspect the Ares window for character selection. {findReason}");
                return false;
            }

            if (!gameWindowFound)
            {
                _log("[Login] The Ares window was not visible for character selection; five-click sequence skipped.");
                return false;
            }

            Interlocked.Exchange(ref _startedGameProcessId, (int)gameWindow.ProcessId);
            _log(
                $"[Login][Character] Step 2/5 complete: HWND=0x{gameWindow.Handle.ToInt64():X}, PID={gameWindow.ProcessId}, " +
                $"client origin=({gameWindow.ClientScreenBounds.X},{gameWindow.ClientScreenBounds.Y}), " +
                $"size={gameWindow.ClientScreenBounds.Width}x{gameWindow.ClientScreenBounds.Height}.");

            _log("[Login][Character] Step 3/5: activating the game window.");
            if (!_aresLauncherWindowService.TryFocusGameWindow(gameWindow, out string focusReason))
            {
                _log($"[Login] Could not focus the Ares window for character selection; five-click sequence skipped. {focusReason}");
                return false;
            }
            _log("[Login][Character] Step 3/5 complete: game window is foreground.");

            SaveLoginStageScreenshot(gameWindow, "Character selection screen");

            System.Drawing.Rectangle client = gameWindow.ClientScreenBounds;
            if (client.Width <= 0 || client.Height <= 0)
            {
                _log("[Login] Character selection skipped because the game client has an empty size.");
                return false;
            }

            int relativeX = (int)Math.Round(CharacterSelectionOffsetX * (double)client.Width / LoginReferenceClientWidth);
            int relativeY = (int)Math.Round(CharacterSelectionOffsetY * (double)client.Height / LoginReferenceClientHeight);
            if (relativeX < 0 || relativeX >= client.Width || relativeY < 0 || relativeY >= client.Height)
            {
                _log(
                    $"[Login] Character selection offset ({relativeX},{relativeY}) is outside the game client " +
                    $"({client.Width}x{client.Height}); five-click sequence skipped.");
                return false;
            }

            int screenX = client.Left + relativeX;
            int screenY = client.Top + relativeY;
            _log(
                $"[Login][Character] Step 4/5: calibrated offset=({relativeX},{relativeY}) scaled from " +
                $"({CharacterSelectionOffsetX},{CharacterSelectionOffsetY}) at {LoginReferenceClientWidth}x{LoginReferenceClientHeight}; " +
                $"current client origin=({client.Left},{client.Top}), size={client.Width}x{client.Height}; " +
                $"desktop target=({screenX},{screenY}).");

            bool clicked = await TryStartupClickSequenceAsync(screenX, screenY, "Character", token);
            if (clicked)
            {
                _log($"[Login][Character] Step 5/5 complete: all {StartupClickCount} click inputs were dispatched.");
                _log($"[Login][Character] Waiting {CharacterEnterDelayMs} ms, then pressing Enter to confirm the character selection.");
                await Task.Delay(CharacterEnterDelayMs, token);
                await Task.Run(() => GameInput.PressKey(GameInput.VK_ENTER, GameInput.SCAN_ENTER), token);
                _log("[Login][Character] Enter pressed after the character clicks.");
                _log("[Login][Character] Waiting 1 second for the game screen to respond before saving a result screenshot.");
                await Task.Delay(1_000, token);
                SaveLoginStageScreenshot(gameWindow, "After character selection click");
                _log("[Login] Startup input sequence complete. The game does not expose a reliable confirmation that a character entered the world.");
            }

            return clicked;
        }

        private void SaveLoginStageScreenshot(AresLauncherWindowInfo gameWindow, string stage)
        {
            try
            {
                System.Drawing.Bitmap? frame = _aresLauncherWindowService.CaptureClientArea(
                    gameWindow,
                    out _,
                    out string captureReason);
                if (frame == null)
                {
                    _log($"[Login] Could not capture the {stage} for diagnosis: {captureReason}");
                    return;
                }

                using (frame)
                    ScreenshotService.SaveLoginDiagnosticScreenshot(frame, stage, _log);
            }
            catch (Exception ex)
            {
                _log($"[Login] {stage} screenshot failed: {ex.Message}");
            }
        }

        private async Task<bool> TryStartupClickSequenceAsync(
            int screenX,
            int screenY,
            string stepName,
            CancellationToken token)
        {
            _log($"[Login][{stepName}] Sending {StartupClickCount} consecutive clicks at desktop position ({screenX},{screenY}) using DPI-aware input.");
            bool allClicksSucceeded = true;
            for (int clickNumber = 1; clickNumber <= StartupClickCount; clickNumber++)
            {
                if (clickNumber > 1)
                {
                    _log($"[Login][{stepName}] Waiting {StartupClickIntervalMs} ms before click {clickNumber}/{StartupClickCount}.");
                    await Task.Delay(StartupClickIntervalMs, token);
                }

                _log($"[Login][{stepName}] Sending click {clickNumber}/{StartupClickCount} at desktop position ({screenX},{screenY}) using DPI-aware input.");
                (bool Succeeded, string FailureReason) click = await Task.Run(() =>
                {
                    bool succeeded = _aresLauncherWindowService.TryLeftClick(screenX, screenY, 0, out string failureReason);
                    return (succeeded, failureReason);
                }, token);
                if (!click.Succeeded)
                {
                    _log($"[Login][{stepName}] Click {clickNumber}/{StartupClickCount} failed: {click.FailureReason}");
                    allClicksSucceeded = false;
                    continue;
                }

                _log($"[Login][{stepName}] Click {clickNumber}/{StartupClickCount} dispatched; cursor position verified.");
            }

            return allClicksSucceeded;
        }

        private GameClientStartupResult Fail(string reason)
        {
            _log($"[Launcher] Startup failed: {reason}");
            SaveLauncherFailureScreenshot(reason);
            return new GameClientStartupResult(GameClientStartupStatus.Failed, 0, reason);
        }

        private void SaveLauncherFailureScreenshot(string reason)
        {
            if (_launcherFailureScreenshotAttempted)
                return;

            _launcherFailureScreenshotAttempted = true;
            if (_lastDetectedLauncherWindow is AresLauncherWindowInfo launcherWindow)
            {
                try
                {
                    System.Drawing.Bitmap? frame = _aresLauncherWindowService.CaptureClientArea(
                        launcherWindow,
                        out _,
                        out string captureReason);
                    if (frame != null)
                    {
                        string? savedPath;
                        using (frame)
                            savedPath = ScreenshotService.SaveLauncherDiagnosticScreenshot(frame, reason, _log);
                        if (savedPath != null)
                            return;
                    }

                    _log($"[Launcher] Could not capture launcher client area: {captureReason}");
                }
                catch (Exception ex)
                {
                    _log($"[Launcher] Launcher client screenshot failed: {ex.Message}");
                }
            }

            ScreenshotService.CaptureFullScreen("Launcher", _log, "[Launcher]");
        }

        private static async Task DelayWithinGameStartupAsync(
            int delayMs,
            Stopwatch overallTimer,
            CancellationToken token)
        {
            int remainingMs = OverallGameStartupTimeoutMs - (int)overallTimer.ElapsedMilliseconds;
            if (remainingMs <= 0)
                throw new TimeoutException($"Ares startup exceeded the {OverallGameStartupTimeoutMs / 1000}-second timeout.");

            await Task.Delay(Math.Min(delayMs, remainingMs), token);
            EnsureGameStartupHasTime(overallTimer);
        }

        private static void EnsureGameStartupHasTime(Stopwatch overallTimer)
        {
            if (overallTimer.ElapsedMilliseconds >= OverallGameStartupTimeoutMs)
                throw new TimeoutException($"Ares startup exceeded the {OverallGameStartupTimeoutMs / 1000}-second timeout.");
        }
    }
}
