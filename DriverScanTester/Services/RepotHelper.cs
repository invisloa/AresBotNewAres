using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Result returned by <see cref="RepotHelper.EvaluateRepotTick"/>.
    /// Tells the caller whether the repot routine requires the caller to yield / return early.
    /// </summary>
    internal enum RepotAction
    {
        /// <summary>No repot action needed — proceed with normal movement.</summary>
        None,
        /// <summary>Repot routine is still in progress — caller should return early.</summary>
        Repotting,
        /// <summary>Report-and-go-back is still in progress — caller should return early.</summary>
        ReportAndGoBackActive,
        /// <summary>Report-and-go-back has completed — caller should continue.</summary>
        ReportAndGoBackDone
    }

    /// <summary>
    /// Encapsulates repot-related logic extracted from MovementSystem.
    /// Handles potion-count checks, town-teleport repot, and report-and-go-back flows.
    /// </summary>
    internal class RepotHelper
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly Action _stopMoving;
        private readonly Action _setGoalReached;

        // ── Repot state ──
        private bool _isRepotting;
        private int _repotStage;
        private DateTime _repotStageStartTime;

        // ── Report-and-go-back state ──
        private bool _isReportAndGoBackActive;

        public RepotHelper(
            GameMemoryService memoryService,
            Action<string> log,
            Action stopMoving,
            Action setGoalReached)
        {
            _memoryService = memoryService;
            _log = log;
            _stopMoving = stopMoving;
            _setGoalReached = setGoalReached;
        }

        /// <summary>
        /// Whether the report-and-go-back flow is active.
        /// </summary>
        public bool IsReportAndGoBackActive => _isReportAndGoBackActive;

        /// <summary>
        /// Evaluates repot state and returns the action that the caller should take.
        /// Call this every tick when <see cref="InternalRepotEnabled"/> is true.
        /// </summary>
        /// <param name="internalRepotEnabled">
        /// When false, this helper does nothing (external coordinator handles repot).
        /// </param>
        /// <returns>A <see cref="RepotAction"/> describing what the caller should do.</returns>
        public RepotAction EvaluateRepotTick(bool internalRepotEnabled)
        {
            if (!internalRepotEnabled)
            {
                return RepotAction.None;
            }

            // ── Report-and-go-back active? ──
            if (_isReportAndGoBackActive)
            {
                TickReportAndGoBackRoutine();
                return _isReportAndGoBackActive
                    ? RepotAction.ReportAndGoBackActive
                    : RepotAction.ReportAndGoBackDone;
            }

            // ── Regular repot active? ──
            if (_isRepotting)
            {
                TickRepotRoutine();
                return RepotAction.Repotting;
            }

            // ── Check whether repot is needed ──
            int hpCount = _memoryService.GetHpPotionCount();
            int manaCount = _memoryService.GetManaPotionCount();

            if (hpCount < BotConstants.Repot.MinHpPotionsInternal || manaCount < BotConstants.Repot.MinManaPotionsInternal)
            {
                _log($"Low potions (HP: {hpCount}, Mana: {manaCount}). Switching to Repot.");
                _stopMoving();
                _isRepotting = true;
                _repotStage = 0;
                return RepotAction.Repotting;
            }

            return RepotAction.None;
        }

        // ─────────────────── Report-and-go-back ───────────────────

        /// <summary>
        /// Initiates the report-and-go-back flow (teleport to town).
        /// </summary>
        public void ReportAndGoBack()
        {
            _log("ReportAndGoBack: Teleporting to town. Pressing 6.");
            _stopMoving();
            GameInput.PressKey(GameInput.VK_6, GameInput.SCAN_6);
            _repotStageStartTime = DateTime.Now;
            _isReportAndGoBackActive = true;
        }

        private void TickReportAndGoBackRoutine()
        {
            if ((DateTime.Now - _repotStageStartTime).TotalSeconds < BotConstants.Timeouts.TeleportWaitSeconds)
            {
                return;
            }

            _log("ReportAndGoBack: Teleport wait over. Checking if in city.");
            if (_memoryService.GetIsInCity())
            {
                _log("ReportAndGoBack: In city. Stopping bot.");
                _stopMoving();
                _setGoalReached();
            }
            else
            {
                _log("ReportAndGoBack: Not in city after teleport. Resuming bot.");
            }

            _isReportAndGoBackActive = false;
        }

        // ─────────────────── Normal repot ───────────────────

        private void TickRepotRoutine()
        {
            if (_repotStage == 0)
            {
                _log("Repot Routine: Starting. Pressing 6 (Town Teleport).");
                _stopMoving();
                GameInput.PressKey(GameInput.VK_6, GameInput.SCAN_6);
                _repotStageStartTime = DateTime.Now;
                _repotStage = 1;
            }
            else if (_repotStage == 1)
            {
                if ((DateTime.Now - _repotStageStartTime).TotalSeconds >= BotConstants.Timeouts.TeleportWaitSeconds)
                {
                    _log("Repot Routine: Teleport wait time over. Checking if in city.");
                    bool inCity = _memoryService.GetIsInCity();

                    if (inCity)
                    {
                        int cityMap = _memoryService.GetMapNumber();
                        _log($"Repot Routine: Player is in city (Map: {cityMap}). Calling GoToRepotPoint.");
                        GoToRepotPoint(cityMap);
                        _repotStage = 2;
                    }
                    else
                    {
                        _log("Repot Routine: Failed (Not in city). Retrying or resuming.");
                        _isRepotting = false;
                    }
                }
            }
        }

        private void GoToRepotPoint(int cityIndex)
        {
            _log($"GoToRepotPoint called for city index: {cityIndex} (Not Implemented).");
        }
    }
}
