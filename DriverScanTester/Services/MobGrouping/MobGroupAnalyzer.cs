using System;
using System.Collections.Generic;
using System.Linq;

namespace DriverScanTester.Services
{
    /// <summary>Pure geometry analysis for already-detected mob marker centers.</summary>
    public static class MobGroupAnalyzer
    {
        public static MobGroupAnalysis Analyze(IEnumerable<MobMarker> markers, MobPoint playerAnchor)
        {
            if (markers == null)
                return MobGroupAnalysis.Invalid(playerAnchor);

            MobMarker[] valid = markers
                .Where(marker => IsFinite(marker.Center.X) && IsFinite(marker.Center.Y) &&
                                 IsFinite(marker.Radius) && marker.Radius >= 0 &&
                                 IsFinite(marker.Confidence) && marker.Confidence > 0)
                .ToArray();

            if (valid.Length == 0)
            {
                return new MobGroupAnalysis
                {
                    IsValid = true,
                    PlayerAnchor = playerAnchor,
                    RecommendedEscapeDirection = MobGroupingDirection.North,
                    IsSufficientlyClustered = true
                };
            }

            float centroidX = 0;
            float centroidY = 0;
            foreach (MobMarker marker in valid)
            {
                centroidX += marker.Center.X;
                centroidY += marker.Center.Y;
            }
            centroidX /= valid.Length;
            centroidY /= valid.Length;
            var centroid = new MobPoint(centroidX, centroidY);

            float spreadSum = 0;
            float maximumSpread = 0;
            float directionX = 0;
            float directionY = 0;
            float confidenceSum = 0;
            MobMarker? centralMob = null;
            float centralDistanceSquared = float.MaxValue;

            foreach (MobMarker marker in valid)
            {
                float dx = marker.Center.X - centroidX;
                float dy = marker.Center.Y - centroidY;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                spreadSum += distance;
                maximumSpread = Math.Max(maximumSpread, distance);
                confidenceSum += marker.Confidence;

                float fromPlayerX = marker.Center.X - playerAnchor.X;
                float fromPlayerY = marker.Center.Y - playerAnchor.Y;
                float playerDistance = MathF.Sqrt(fromPlayerX * fromPlayerX + fromPlayerY * fromPlayerY);
                if (playerDistance > 0.001f)
                {
                    directionX += fromPlayerX / playerDistance;
                    directionY += fromPlayerY / playerDistance;
                }

                float centroidDistanceSquared = dx * dx + dy * dy;
                if (centroidDistanceSquared < centralDistanceSquared)
                {
                    centralDistanceSquared = centroidDistanceSquared;
                    centralMob = marker;
                }
            }

            float averageSpread = spreadSum / valid.Length;
            float aggregateLength = MathF.Sqrt(directionX * directionX + directionY * directionY);
            if (aggregateLength <= valid.Length * 0.15f)
            {
                // Symmetric groups cancel the sum of their normalized directions; use the
                // centroid-to-player vector as the deterministic aggregate fallback.
                directionX = centroidX - playerAnchor.X;
                directionY = centroidY - playerAnchor.Y;
                aggregateLength = MathF.Sqrt(directionX * directionX + directionY * directionY);
            }

            bool hasDirection = aggregateLength > 0.001f;
            if (hasDirection)
            {
                directionX /= aggregateLength;
                directionY /= aggregateLength;
            }
            else
            {
                directionX = 0;
                directionY = -1;
            }

            // A screen vector to the mobs is opposed for the escape direction.
            MobGroupingDirection escapeDirection =
                MobGroupingDirectionMath.QuantizeScreenVector(-directionX, -directionY);

            float averageRadius = valid.Average(marker => marker.Radius > 0
                ? marker.Radius
                : BotConstants.MobGrouping.ExpectedMarkerRadiusPx);
            float resolutionScale = averageRadius / BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            float triggerAverage = BotConstants.MobGrouping.TriggerAverageSpreadPx * resolutionScale;
            float triggerMaximum = BotConstants.MobGrouping.TriggerMaximumSpreadPx * resolutionScale;
            float successAverage = BotConstants.MobGrouping.SuccessAverageSpreadPx * resolutionScale;
            float successMaximum = BotConstants.MobGrouping.SuccessMaximumSpreadPx * resolutionScale;

            bool enoughMobs = valid.Length >= BotConstants.MobGrouping.MinimumMobs;
            bool scattered = enoughMobs &&
                (averageSpread >= triggerAverage || maximumSpread >= triggerMaximum);
            bool clustered = valid.Length < BotConstants.MobGrouping.MinimumMobs ||
                (averageSpread <= successAverage && maximumSpread <= successMaximum);

            // When many mobs are visible, the whole-formation spread can stay bad because of
            // outliers even though a local group is already a perfect AOE target. Only then
            // run the exact combination-of-AttackReadyClusterMobCount subset search. The
            // whole-formation IsSufficientlyClustered result keeps its original meaning.
            bool hasAttackReadyCluster = false;
            int attackReadyClusterMobCount = 0;
            if (valid.Length > BotConstants.MobGrouping.AttackReadyClusterMobCount &&
                TryFindAttackReadyCluster(valid))
            {
                hasAttackReadyCluster = true;
                attackReadyClusterMobCount = BotConstants.MobGrouping.AttackReadyClusterMobCount;
            }

            return new MobGroupAnalysis
            {
                IsValid = true,
                DetectedMobCount = valid.Length,
                PlayerAnchor = playerAnchor,
                Centroid = centroid,
                AverageSpread = averageSpread,
                MaximumSpread = maximumSpread,
                DominantMobDirectionX = directionX,
                DominantMobDirectionY = directionY,
                HasDominantMobDirection = hasDirection,
                RecommendedEscapeDirection = escapeDirection,
                IsScattered = scattered,
                IsSufficientlyClustered = clustered,
                HasAttackReadyCluster = hasAttackReadyCluster,
                AttackReadyClusterMobCount = attackReadyClusterMobCount,
                Confidence = confidenceSum / valid.Length,
                CentralMob = centralMob
            };
        }

        /// <summary>
        /// Exact check whether ANY combination of exactly
        /// <see cref="BotConstants.MobGrouping.AttackReadyClusterMobCount"/> valid markers
        /// forms a cluster that satisfies the same success thresholds used for the whole
        /// formation, scaled by the subset's own marker radii. The search is deterministic
        /// and stops at the first matching combination; no centroid/nearest-neighbour
        /// heuristic is involved, because the real cluster need not be centred on the
        /// complete formation.
        /// </summary>
        private static bool TryFindAttackReadyCluster(MobMarker[] markers)
        {
            int clusterSize = BotConstants.MobGrouping.AttackReadyClusterMobCount;
            if (markers.Length < clusterSize)
                return false;

            // One reusable index buffer: no per-combination allocations.
            int[] indices = new int[clusterSize];
            for (int i = 0; i < clusterSize; i++)
                indices[i] = i;

            while (true)
            {
                if (IsSubsetAttackReady(markers, indices))
                    return true;

                int position = clusterSize - 1;
                while (position >= 0 && indices[position] == markers.Length - clusterSize + position)
                    position--;
                if (position < 0)
                    return false;

                indices[position]++;
                for (int i = position + 1; i < clusterSize; i++)
                    indices[i] = indices[i - 1] + 1;
            }
        }

        /// <summary>
        /// Computes the subset centroid, average/maximum spread and resolution scale from the
        /// subset's own marker radii, then applies the existing success thresholds.
        /// </summary>
        private static bool IsSubsetAttackReady(MobMarker[] markers, int[] indices)
        {
            float centroidX = 0;
            float centroidY = 0;
            float radiusSum = 0;
            foreach (int index in indices)
            {
                MobMarker marker = markers[index];
                centroidX += marker.Center.X;
                centroidY += marker.Center.Y;
                radiusSum += marker.Radius > 0
                    ? marker.Radius
                    : BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            }

            float count = indices.Length;
            centroidX /= count;
            centroidY /= count;

            float spreadSum = 0;
            float maximumSpread = 0;
            foreach (int index in indices)
            {
                float dx = markers[index].Center.X - centroidX;
                float dy = markers[index].Center.Y - centroidY;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                spreadSum += distance;
                maximumSpread = Math.Max(maximumSpread, distance);
            }

            float averageSpread = spreadSum / count;
            float averageRadius = radiusSum / count;
            float resolutionScale = averageRadius / BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            return averageSpread <= BotConstants.MobGrouping.SuccessAverageSpreadPx * resolutionScale &&
                   maximumSpread <= BotConstants.MobGrouping.SuccessMaximumSpreadPx * resolutionScale;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
