using System;
using System.Collections.Generic;

namespace DriverScanTester.Services
{
    /// <summary>
    /// One bounded temporal formation observation. It intentionally holds only the values
    /// needed to judge whether the LOCAL formation is improving: no screenshots, no marker
    /// identity and no melee/ranged classification (neither can be established reliably).
    /// Screen-space geometry is only comparable while the camera and the client size are stable.
    /// </summary>
    public readonly record struct MobFormationObservation(
        DateTime TimestampUtc,
        /// <summary>True only when detection succeeded AND the sample is usable for a movement decision.</summary>
        bool IsReliable,
        /// <summary>Normalized local formation quality in (0..1]; higher is more compact.</summary>
        float FormationQuality,
        int VisibleMarkerCount,
        float DetectionConfidence,
        bool PlayerPositionValid,
        float PlayerX,
        float PlayerY,
        bool HasCameraYaw,
        float CameraYawDegrees,
        int ClientWidth,
        int ClientHeight);

    /// <summary>Classification of the bounded formation trend.</summary>
    public enum MobFormationTrend
    {
        /// <summary>Not enough comparable evidence; callers must behave conservatively.</summary>
        Unknown,
        Improving,
        Stagnant,
        Deteriorating
    }

    /// <summary>Result of one trend evaluation. Diagnostic + decision data, no behaviour.</summary>
    public readonly record struct MobFormationTrendResult(
        MobFormationTrend Trend,
        int ComparableObservations,
        float QualityBefore,
        float QualityAfter,
        float RelativeChange,
        string Reason)
    {
        public static MobFormationTrendResult Unknown(string reason) =>
            new(MobFormationTrend.Unknown, 0, 0f, 0f, 0f, reason);

        /// <summary>True only with two consecutive improvement steps (three observations).</summary>
        public bool IsImproving => Trend == MobFormationTrend.Improving;
    }

    /// <summary>
    /// Small bounded history (last ~<see cref="BotConstants.MobGrouping.FormationHistoryWindowMs"/>)
    /// of formation observations plus the deterministic trend evaluation. It never tracks
    /// individual monster identities: the comparison is purely over the normalized local
    /// formation-quality score.
    /// </summary>
    public sealed class MobFormationHistory
    {
        private readonly List<MobFormationObservation> _observations =
            new(BotConstants.MobGrouping.FormationHistoryMaxObservations);

        public int Count => _observations.Count;

        public void Clear() => _observations.Clear();

        /// <summary>Adds one observation and prunes everything outside the bounded window.</summary>
        public void Add(in MobFormationObservation observation)
        {
            Prune(observation.TimestampUtc);
            if (_observations.Count >= BotConstants.MobGrouping.FormationHistoryMaxObservations)
                _observations.RemoveAt(0);
            _observations.Add(observation);
        }

        /// <summary>
        /// Deterministic trend evaluation over the retained observations.
        /// Deterioration is reported when either the newest comparable step or the whole
        /// comparable window regresses by more than the noise tolerance; improvement requires
        /// the whole comparable window to improve by more than the noise tolerance with two
        /// consecutive improvement steps (three observations) and no regressing step.
        /// </summary>
        public MobFormationTrendResult Evaluate(DateTime nowUtc)
        {
            Prune(nowUtc);
            int count = _observations.Count;
            if (count == 0)
                return MobFormationTrendResult.Unknown("no observation");

            MobFormationObservation newest = _observations[count - 1];
            if (!IsUsable(newest))
                return MobFormationTrendResult.Unknown("newest sample unreliable");

            int anchor = count - 1;
            int comparable = 1;
            bool anyRegression = false;
            float newestStepChange = 0f;
            bool hasNewestStep = false;

            for (int i = count - 2; i >= 0; i--)
            {
                MobFormationObservation earlier = _observations[i];
                MobFormationObservation later = _observations[anchor];
                if (!IsUsable(earlier) || !AreComparable(earlier, later))
                    break;

                float change = RelativeChange(earlier.FormationQuality, later.FormationQuality);
                if (!hasNewestStep)
                {
                    newestStepChange = change;
                    hasNewestStep = true;
                }

                if (change <= -BotConstants.MobGrouping.FormationImprovementNoise)
                    anyRegression = true;

                comparable++;
                anchor = i;
            }

            MobFormationObservation oldest = _observations[anchor];
            float windowChange = RelativeChange(oldest.FormationQuality, newest.FormationQuality);

            float noise = BotConstants.MobGrouping.FormationImprovementNoise;
            if (newestStepChange <= -noise || windowChange <= -noise)
            {
                return new MobFormationTrendResult(MobFormationTrend.Deteriorating, comparable,
                    oldest.FormationQuality, newest.FormationQuality, windowChange,
                    "formation quality regressed");
            }

            if (comparable < BotConstants.MobGrouping.MinimumTrendObservations)
            {
                return new MobFormationTrendResult(MobFormationTrend.Unknown, comparable,
                    oldest.FormationQuality, newest.FormationQuality, windowChange,
                    "not enough comparable observations");
            }

            if (!anyRegression && windowChange >= noise)
            {
                return new MobFormationTrendResult(MobFormationTrend.Improving, comparable,
                    oldest.FormationQuality, newest.FormationQuality, windowChange,
                    "local formation quality improved");
            }

            return new MobFormationTrendResult(MobFormationTrend.Stagnant, comparable,
                oldest.FormationQuality, newest.FormationQuality, windowChange,
                "no meaningful improvement");
        }

        private void Prune(DateTime nowUtc)
        {
            DateTime cutoff =
                nowUtc.AddMilliseconds(-BotConstants.MobGrouping.FormationHistoryWindowMs);
            int remove = 0;
            while (remove < _observations.Count && _observations[remove].TimestampUtc < cutoff)
                remove++;
            if (remove > 0)
                _observations.RemoveRange(0, remove);
        }

        private static bool IsUsable(in MobFormationObservation observation) =>
            observation.IsReliable && observation.PlayerPositionValid;

        /// <summary>
        /// Two observations may only be compared when the camera, the client size and the
        /// marker count are comparable. A marker-count DECREASE invalidates the comparison:
        /// losing a distant outlier from the ROI is not evidence of a better local group.
        /// </summary>
        private static bool AreComparable(
            in MobFormationObservation earlier,
            in MobFormationObservation later)
        {
            if (earlier.ClientWidth != later.ClientWidth ||
                earlier.ClientHeight != later.ClientHeight)
                return false;

            if (!earlier.HasCameraYaw || !later.HasCameraYaw)
                return false;

            if (Math.Abs(ShortestAngleDifferenceDegrees(
                    earlier.CameraYawDegrees, later.CameraYawDegrees)) >
                BotConstants.MobGrouping.CameraYawToleranceDegrees)
                return false;

            int countDelta = later.VisibleMarkerCount - earlier.VisibleMarkerCount;
            if (countDelta < 0 ||
                countDelta > BotConstants.MobGrouping.MaximumMarkerCountDeltaForTrend)
                return false;

            return true;
        }

        private static float RelativeChange(float before, float after)
        {
            if (before <= 0.0001f)
                return after > before ? 1f : 0f;
            return (after - before) / before;
        }

        private static float ShortestAngleDifferenceDegrees(float first, float second)
        {
            float difference = (second - first) % 360f;
            if (difference > 180f)
                difference -= 360f;
            else if (difference < -180f)
                difference += 360f;
            return difference;
        }
    }
}
