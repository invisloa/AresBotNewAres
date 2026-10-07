using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Input;
using DriverScanTester.Services;
using DriverScanTester.Utils;

namespace DriverScanTester.ViewModels
{
    public enum PixelDetectionTestKind
    {
        OtherPlayerMarkers,
        SodSopLoot
    }

    /// <summary>Runs read-only visual detection tests against a fresh game-client frame.</summary>
    public sealed class PixelDetectionTestingViewModel : BaseViewModel
    {
        private readonly Action _focusGameWindow;
        private readonly Action<string> _mainLog;
        private readonly PlayerMarkerPixelDetector _playerMarkerDetector = new();
        private string _logText = string.Empty;
        private bool _isTesting;
        private PixelDetectionTestKind _selectedTest = PixelDetectionTestKind.OtherPlayerMarkers;

        public PixelDetectionTestingViewModel(Action focusGameWindow, Action<string> mainLog)
        {
            _focusGameWindow = focusGameWindow ?? throw new ArgumentNullException(nameof(focusGameWindow));
            _mainLog = mainLog ?? (_ => { });
            TestCommand = new RelayCommand(async _ => await RunTestAsync(), _ => !IsTesting);
            AppendLog("Ready. Select a detector and click Test. Tests do not move the mouse or collect items.");
        }

        public PixelDetectionTestKind SelectedTest
        {
            get => _selectedTest;
            set => SetProperty(ref _selectedTest, value);
        }

        public string LogText
        {
            get => _logText;
            private set => SetProperty(ref _logText, value);
        }

        public bool IsTesting
        {
            get => _isTesting;
            private set
            {
                if (SetProperty(ref _isTesting, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand TestCommand { get; }

        private async Task RunTestAsync()
        {
            if (IsTesting)
                return;

            IsTesting = true;
            PixelDetectionTestKind selectedTest = SelectedTest;
            string testName = selectedTest == PixelDetectionTestKind.OtherPlayerMarkers
                ? "Other-player marker detection"
                : "SOD/SOP pink loot detection";
            AppendLog($"Starting {testName}...");
            _mainLog($"[Detection Test] Starting {testName}.");

            try
            {
                _focusGameWindow();
                await Task.Delay(250);

                string result = await Task.Run(() => RunSelectedTestOnCapturedFrame(selectedTest));
                AppendLog(result);
                _mainLog("[Detection Test] " + result);
            }
            catch (Exception ex)
            {
                string error = $"Test failed: {ex.Message}";
                AppendLog(error);
                _mainLog("[Detection Test] " + error);
            }
            finally
            {
                IsTesting = false;
            }
        }

        private string RunSelectedTestOnCapturedFrame(PixelDetectionTestKind selectedTest)
        {
            using Bitmap? frame = ScreenshotService.CaptureGameClientFrame();
            if (frame == null)
                return "Could not capture the game client. Make sure Legend of Ares is open.";

            if (selectedTest == PixelDetectionTestKind.OtherPlayerMarkers)
            {
                MobPoint localPlayerAnchor = MobMarkerDetector.GetPlayerAnchor(frame.Width, frame.Height);
                bool found = _playerMarkerDetector.ContainsOtherPlayerMarker(
                    frame, localPlayerAnchor.X, localPlayerAnchor.Y);
                return found
                    ? $"Other-player marker FOUND in {frame.Width}x{frame.Height} client frame. (The local-player anchor area is excluded.)"
                    : $"No other-player marker found in {frame.Width}x{frame.Height} client frame.";
            }

            var result = LootSystem.AnalyzePinkFrame(frame);
            string status = result.LootCandidates > 0 ? "SOD/SOP candidate(s) FOUND" : "No SOD/SOP candidates found";
            return $"{status}: pink pixels={result.PinkPixels}, components={result.Components}, " +
                   $"loot candidates={result.LootCandidates}, artificial marker rings rejected={result.MobMarkerRings}, " +
                   $"tiny components skipped={result.TinyComponents}. No mouseover or pickup was attempted.";
        }

        private void AppendLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogText = string.IsNullOrEmpty(LogText)
                ? line
                : LogText + Environment.NewLine + line;
        }
    }
}
