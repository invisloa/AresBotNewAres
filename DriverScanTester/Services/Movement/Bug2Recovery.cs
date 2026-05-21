using System;
using System.Collections.Generic;
using System.Linq;

namespace DriverScanTester.Services
{
    internal enum Bug2NavState
    {
        None,
        MoveToTarget,
        FollowBoundary
    }

    /// <summary>
    /// Bug2 boundary-following navigation algorithm.
    /// All Bug2 state and logic is encapsulated here.
    /// The caller (MovementSystem) provides MovementSystem-owned operations
    /// via delegates so this class can remain independent.
    /// </summary>
    internal class Bug2Recovery
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly LocalNavigationMap _localNavigationMap;

        // ── Callbacks for MovementSystem-owned operations ──

        private readonly Action _stopMoving;
        private readonly Action _forceStartMoving;
        private readonly Action<float, float, float, float> _moveTowards;
        private readonly Func<float, float, bool> _advanceReachedWaypoints;
        private readonly Action _saveLocalMapIfDirty;
        private readonly Func<float, float, Waypoint, System.Collections.Generic.Queue<Waypoint>, Action, Action, Action, bool> _trySkip;
        private readonly Action _resetBearingState;
        private readonly Action _resetActionStuckTracking;

        // ── Callbacks for camera / bearing state ──

        private readonly Func<float> _getLastSetGameAngle;
        private readonly Func<bool> _getHasLastGameAngle;
        private readonly Action<float> _setLastSetBearingDeg;
        private readonly Action<bool> _setHasLastGameAngle;
        private readonly Action<float> _setLastSetGameAngle;
        private readonly Action<float> _steerToBearingDeg;

        // ── Bug2 state ──

        private Bug2NavState _bug2State = Bug2NavState.None;
        private Waypoint? _bug2Target;
        private (float X, float Y) _bug2StartPoint;
        private (float X, float Y) _bug2HitPoint;
        private float _bug2HitDistanceToTarget;
        private bool _bug2FollowLeft = true;
        private DateTime _bug2StartedAt;
        private int _bug2StepCount;
        private int _bug2FailedMoveCount;
        private int _bug2SameSideSteps;

        // Bug2 candidate testing (multi-tick)
        private bool _bug2CandidateIssued;
        private int _bug2CandidateIndex;
        private float _bug2CandidateTestBearing;
        private int _bug2CandidateTestCellX;
        private int _bug2CandidateTestCellY;
        private int _bug2CandidateAttemptedDirX;
        private int _bug2CandidateAttemptedDirY;
        private DateTime _bug2CandidateStartTime;

        // ── Constants ──

        private const double BUG2_CANDIDATE_OBSERVE_MS = 700.0;
        private const int BUG2_MAX_TOTAL_STEPS = 40;
        private const int BUG2_MAX_FAILED_MOVES = 12;
        private const double BUG2_MAX_DURATION_SECONDS = 20.0;
        private const float BUG2_M_LINE_TOLERANCE = 1.5f;
        private const float BUG2_LEAVE_MIN_IMPROVEMENT = 1.0f;
        private const int BUG2_MAX_STEPS_BEFORE_SIDE_SWITCH = 8;

        // ── Public properties ──

        public bool IsActive => _bug2State != Bug2NavState.None;

        // ── Constructor ──

        public Bug2Recovery(
            GameMemoryService memory,
            Action<string> log,
            LocalNavigationMap localNavigationMap,
            Action stopMoving,
            Action forceStartMoving,
            Action<float, float, float, float> moveTowards,
            Func<float, float, bool> advanceReachedWaypoints,
            Action saveLocalMapIfDirty,
            Func<float, float, Waypoint, Queue<Waypoint>, Action, Action, Action, bool> trySkip,
            Action resetBearingState,
            Action resetActionStuckTracking,
            Func<float> getLastSetGameAngle,
            Func<bool> getHasLastGameAngle,
            Action<float> setLastSetBearingDeg,
            Action<bool> setHasLastGameAngle,
            Action<float> setLastSetGameAngle,
            Action<float> steerToBearingDeg)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _localNavigationMap = localNavigationMap ?? throw new ArgumentNullException(nameof(localNavigationMap));
            _stopMoving = stopMoving ?? throw new ArgumentNullException(nameof(stopMoving));
            _forceStartMoving = forceStartMoving ?? throw new ArgumentNullException(nameof(forceStartMoving));
            _moveTowards = moveTowards ?? throw new ArgumentNullException(nameof(moveTowards));
            _advanceReachedWaypoints = advanceReachedWaypoints ?? throw new ArgumentNullException(nameof(advanceReachedWaypoints));
            _saveLocalMapIfDirty = saveLocalMapIfDirty ?? throw new ArgumentNullException(nameof(saveLocalMapIfDirty));
            _trySkip = trySkip ?? throw new ArgumentNullException(nameof(trySkip));
            _resetBearingState = resetBearingState ?? throw new ArgumentNullException(nameof(resetBearingState));
            _resetActionStuckTracking = resetActionStuckTracking ?? throw new ArgumentNullException(nameof(resetActionStuckTracking));
            _getLastSetGameAngle = getLastSetGameAngle ?? throw new ArgumentNullException(nameof(getLastSetGameAngle));
            _getHasLastGameAngle = getHasLastGameAngle ?? throw new ArgumentNullException(nameof(getHasLastGameAngle));
            _setLastSetBearingDeg = setLastSetBearingDeg ?? throw new ArgumentNullException(nameof(setLastSetBearingDeg));
            _setHasLastGameAngle = setHasLastGameAngle ?? throw new ArgumentNullException(nameof(setHasLastGameAngle));
            _setLastSetGameAngle = setLastSetGameAngle ?? throw new ArgumentNullException(nameof(setLastSetGameAngle));
            _steerToBearingDeg = steerToBearingDeg ?? throw new ArgumentNullException(nameof(steerToBearingDeg));
        }

        // ── Enter / Reset ──

        public void Enter(float currX, float currY, Waypoint target)
        {
            _bug2Target = target;
            _bug2StartPoint = (currX, currY);
            _bug2HitPoint = (currX, currY);
            _bug2HitDistanceToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            _bug2FollowLeft = true;
            _bug2StartedAt = DateTime.Now;
            _bug2StepCount = 0;
            _bug2FailedMoveCount = 0;
            _bug2SameSideSteps = 0;
            _bug2State = Bug2NavState.FollowBoundary;
            _bug2CandidateIssued = false;
            _bug2CandidateIndex = 0;

            _log($"[Bug2] Enter. Start=({currX:F1},{currY:F1}), Hit=({currX:F1},{currY:F1}), " +
                 $"Target=({target.X:F1},{target.Y:F1}), HitDist={_bug2HitDistanceToTarget:F2}");
        }

        public void Reset()
        {
            if (_bug2State != Bug2NavState.None)
            {
                _log("[Bug2] Reset.");
            }

            _bug2State = Bug2NavState.None;
            _bug2Target = null;
            _bug2StartPoint = (0, 0);
            _bug2HitPoint = (0, 0);
            _bug2HitDistanceToTarget = float.MaxValue;
            _bug2StepCount = 0;
            _bug2FailedMoveCount = 0;
            _bug2SameSideSteps = 0;
            _bug2CandidateIssued = false;

            _saveLocalMapIfDirty();
        }

        public void RunStep(
            float currX, float currY,
            Waypoint target,
            Queue<Waypoint> waypoints,
            Func<Waypoint, float> getReachThreshold,
            Action resetProgressTracker)
        {
            // 1. Advance reached waypoints (including the Bug2 target if reached)
            _advanceReachedWaypoints(currX, currY);

            // 2. Check if waypoints became empty
            if (waypoints.Count == 0)
            {
                _log("[Bug2] Waypoints empty after advance. Resetting Bug2.");
                Reset();
                _saveLocalMapIfDirty();
                return;
            }

            // 3. Soft skip if stuck very close to current waypoint
            Waypoint currentTarget = waypoints.Peek();
            if (_trySkip(currX, currY, currentTarget, waypoints,
                         _resetBearingState, resetProgressTracker, _resetActionStuckTracking))
            {
                _log("[Bug2] Waypoint skipped during Bug2. Resetting Bug2.");
                Reset();
                _saveLocalMapIfDirty();
                return;
            }

            // 4. Check Bug2 limits
            if (LimitsExceeded())
            {
                _log("[Bug2] Limits exceeded. Falling back.");
                Reset();
                _stopMoving();
                _saveLocalMapIfDirty();
                return;
            }

            // 5. Check if we can leave boundary following
            if (CanLeaveBoundary(currX, currY, currentTarget))
            {
                _log("[Bug2] On m-line and closer than hit point. Leaving boundary.");
                Reset();
                _moveTowards(currX, currY, currentTarget.X, currentTarget.Y);
                _saveLocalMapIfDirty();
                return;
            }

            // 6. Perform boundary following step
            TryBoundaryStep(currX, currY, currentTarget);
        }

        // ── Limits ──

        private bool LimitsExceeded()
        {
            if (_bug2StepCount >= BUG2_MAX_TOTAL_STEPS)
            {
                _log($"[Bug2] Max total steps reached ({_bug2StepCount}/{BUG2_MAX_TOTAL_STEPS}).");
                return true;
            }

            if (_bug2FailedMoveCount >= BUG2_MAX_FAILED_MOVES)
            {
                _log($"[Bug2] Max failed moves reached ({_bug2FailedMoveCount}/{BUG2_MAX_FAILED_MOVES}).");
                return true;
            }

            double elapsed = (DateTime.Now - _bug2StartedAt).TotalSeconds;
            if (elapsed >= BUG2_MAX_DURATION_SECONDS)
            {
                _log($"[Bug2] Max duration reached ({elapsed:F1}s/{BUG2_MAX_DURATION_SECONDS:F0}s).");
                return true;
            }

            return false;
        }

        // ── Leave condition ──

        private bool CanLeaveBoundary(float currX, float currY, Waypoint target)
        {
            // Condition 1: Close to m-line
            float distToMLine = DistancePointToLine(
                currX, currY,
                _bug2StartPoint.X, _bug2StartPoint.Y,
                target.X, target.Y);

            if (distToMLine > BUG2_M_LINE_TOLERANCE)
                return false;

            // Condition 2: Closer to target than hit point
            float distToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            if (distToTarget >= _bug2HitDistanceToTarget - BUG2_LEAVE_MIN_IMPROVEMENT)
                return false;

            // Condition 3: No blocked cells on line to target
            if (IsKnownPathBlockedToTarget(currX, currY, target))
                return false;

            // Condition 4: Not currently action-stuck
            byte action = _memory.GetCurrentAction();
            if (StuckDetector.IsActionIdleOrStuck(action))
                return false;

            return true;
        }

        private static float DistancePointToLine(
            float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float lengthSq = dx * dx + dy * dy;

            if (lengthSq < 0.0001f)
            {
                return GeometryUtils.Distance(px, py, ax, ay);
            }

            float t = ((px - ax) * dx + (py - ay) * dy) / lengthSq;
            t = Math.Clamp(t, 0f, 1f);

            float closestX = ax + t * dx;
            float closestY = ay + t * dy;

            return GeometryUtils.Distance(px, py, closestX, closestY);
        }

        private bool IsKnownPathBlockedToTarget(float currX, float currY, Waypoint target)
        {
            float dx = target.X - currX;
            float dy = target.Y - currY;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            if (dist < 0.5f)
                return false;

            float steps = Math.Max(2, (int)Math.Ceiling(dist));
            float stepX = dx / steps;
            float stepY = dy / steps;

            for (int i = 1; i < steps; i++)
            {
                float sampleX = currX + stepX * i;
                float sampleY = currY + stepY * i;
                (int cx, int cy) = LocalNavigationMap.WorldToCell(sampleX, sampleY);

                if (_localNavigationMap.IsBlockedCell(cx, cy))
                    return true;
            }

            return false;
        }

        // ── Boundary following ──

        private void TryBoundaryStep(float currX, float currY, Waypoint target)
        {
            if (_bug2CandidateIssued)
            {
                CheckCandidateResult(currX, currY, target);
                return;
            }

            IssueCandidate(currX, currY, target);
        }

        private void IssueCandidate(float currX, float currY, Waypoint target)
        {
            float[] offsets = _bug2FollowLeft
                ? new[] { -90f, -45f, 0f, 45f, 90f, 135f, -135f, 180f }
                : new[] { 90f, 45f, 0f, -45f, -90f, -135f, 135f, 180f };

            float baseBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);

            (int sourceCellX, int sourceCellY) = LocalNavigationMap.WorldToCell(currX, currY);

            var viableCandidates = new List<(int Index, float Bearing, int CellX, int CellY, int DirX, int DirY, bool IsRisky)>();

            for (int i = 0; i < offsets.Length; i++)
            {
                float candidateBearing = GeometryUtils.NormalizeBearingDeg(baseBearing + offsets[i]);
                float rad = GeometryUtils.DegToRad(candidateBearing);
                float ddx = (float)Math.Sin(rad);
                float ddy = (float)Math.Cos(rad);

                int dirX = ddx > 0.3f ? 1 : (ddx < -0.3f ? -1 : 0);
                int dirY = ddy > 0.3f ? 1 : (ddy < -0.3f ? -1 : 0);

                int cellX = sourceCellX + dirX;
                int cellY = sourceCellY + dirY;

                LocalCellState cellState = _localNavigationMap.GetCell(cellX, cellY);

                if (cellState == LocalCellState.Blocked)
                {
                    _log($"[Bug2] Candidate bearing={candidateBearing:F1} direction=({dirX},{dirY}) cell=({cellX},{cellY}) mapState=Blocked. Skipping.");
                    continue;
                }

                bool isRisky = cellState == LocalCellState.Risky;
                viableCandidates.Add((i, candidateBearing, cellX, cellY, dirX, dirY, isRisky));
            }

            if (viableCandidates.Count == 0)
            {
                _log("[Bug2] All candidates blocked by local map. No viable direction.");
                _bug2FailedMoveCount++;
                _bug2SameSideSteps++;
                CheckSideSwitch();
                return;
            }

            var bestCandidates = viableCandidates.Where(c => !c.IsRisky).ToList();
            if (bestCandidates.Count == 0)
            {
                bestCandidates = viableCandidates;
            }

            var chosen = bestCandidates[0];

            _bug2CandidateIssued = true;
            _bug2CandidateIndex = chosen.Index;
            _bug2CandidateTestBearing = chosen.Bearing;
            _bug2CandidateTestCellX = chosen.CellX;
            _bug2CandidateTestCellY = chosen.CellY;
            _bug2CandidateAttemptedDirX = chosen.DirX;
            _bug2CandidateAttemptedDirY = chosen.DirY;
            _bug2CandidateStartTime = DateTime.Now;

            // Set camera angle directly to the candidate bearing
            _setLastSetBearingDeg(chosen.Bearing);
            _steerToBearingDeg(chosen.Bearing);

            _log($"[Bug2] Try candidate bearing={chosen.Bearing:F1} direction=({chosen.DirX},{chosen.DirY}) " +
                 $"cell=({chosen.CellX},{chosen.CellY}) " +
                 $"mapState={(_localNavigationMap.GetCell(chosen.CellX, chosen.CellY) == LocalCellState.Risky ? "Risky" : "Free/Unknown")}");

            _forceStartMoving();
        }

        private void CheckCandidateResult(float currX, float currY, Waypoint target)
        {
            double elapsed = (DateTime.Now - _bug2CandidateStartTime).TotalMilliseconds;
            if (elapsed < BUG2_CANDIDATE_OBSERVE_MS)
            {
                return;
            }

            byte currentAction = _memory.GetCurrentAction();

            if (StuckDetector.IsActionRunning(currentAction))
            {
                _log($"[Bug2] Candidate succeeded by action running. Action={currentAction}.");

                _localNavigationMap.MarkFree(currX, currY, "bug2-candidate-success");

                _bug2StepCount++;
                _bug2SameSideSteps = 0;
                _bug2CandidateIssued = false;
                _saveLocalMapIfDirty();
            }
            else if (StuckDetector.IsActionIdleOrStuck(currentAction))
            {
                _log($"[Bug2] Candidate failed by action stuck. Action={currentAction}. Marking attempted cell.");

                _stopMoving();

                (int sourceCellX, int sourceCellY) = LocalNavigationMap.WorldToCell(currX, currY);
                (int targetCellX, int targetCellY) = LocalNavigationMap.WorldToCell(target.X, target.Y);

                _localNavigationMap.MarkStuckAttemptedCell(
                    _bug2CandidateTestCellX + 0.5f, _bug2CandidateTestCellY + 0.5f,
                    "blocked-when-moving-to-waypoint",
                    _bug2CandidateTestBearing,
                    _bug2CandidateAttemptedDirX, _bug2CandidateAttemptedDirY,
                    sourceCellX, sourceCellY,
                    targetCellX, targetCellY);

                _bug2FailedMoveCount++;
                _bug2SameSideSteps++;
                _bug2CandidateIssued = false;
                _saveLocalMapIfDirty();

                CheckSideSwitch();
            }
            else
            {
                _log($"[Bug2] Candidate unknown action={currentAction}. Aborting candidate.");
                _bug2CandidateIssued = false;
                _bug2SameSideSteps++;
            }
        }

        private void CheckSideSwitch()
        {
            if (_bug2SameSideSteps >= BUG2_MAX_STEPS_BEFORE_SIDE_SWITCH)
            {
                _bug2FollowLeft = !_bug2FollowLeft;
                _bug2SameSideSteps = 0;
                _log($"[Bug2] Switching side {(_bug2FollowLeft ? "Right -> Left" : "Left -> Right")}.");
                _log($"[Bug2] State=FollowBoundary Side={(_bug2FollowLeft ? "Left" : "Right")} " +
                     $"Step={_bug2StepCount} Failed={_bug2FailedMoveCount}");
            }
            else
            {
                _log($"[Bug2] State=FollowBoundary Side={(_bug2FollowLeft ? "Left" : "Right")} " +
                     $"Step={_bug2StepCount} Failed={_bug2FailedMoveCount}");
            }
        }
    }
}
