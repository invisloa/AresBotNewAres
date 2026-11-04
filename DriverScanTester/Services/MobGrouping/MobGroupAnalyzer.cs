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

            // Local (outlier-tolerant) cluster assessment. This is the primary movement
            // suppression signal: TWO compact markers are already enough to declare the local
            // formation usable, so a distant outlier can no longer drag the bot into a gather
            // walk. It is bounded O(n^2) nearest-neighbour work, never an exponential subset
            // search, and it deliberately does NOT try to predict a forward-AoE hit count:
            // screen-space proximity is only used as a conservative reason NOT to move.
            LocalClusterAssessment local = AssessLocalCluster(valid, averageRadius);

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

            float confidence = confidenceSum / valid.Length;
            bool reliableForMovement =
                valid.Length >= BotConstants.MobGrouping.MinimumGatherMarkers &&
                valid.Length <= BotConstants.MobGrouping.MaximumMarkersForEvaluation &&
                confidence >= BotConstants.MobGrouping.MinimumDetectionConfidence;

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
                Confidence = confidence,
                CentralMob = centralMob,
                EffectiveMarkerRadius = averageRadius,
                BestLocalClusterSize = local.BestClusterSize,
                BestLocalClusterCenter = local.BestClusterCenter,
                BestPairDistancePx = local.BestPairDistancePx,
                BestPairDistanceNormalized = local.BestPairDistanceNormalized,
                MeanNearestNeighborDistancePx = local.MeanNearestNeighborDistancePx,
                MeanNearestNeighborDistanceNormalized = local.MeanNearestNeighborDistanceNormalized,
                LocalCompactnessScore = local.LocalCompactnessScore,
                FormationQuality = local.FormationQuality,
                HasCompactLocalGroup = local.HasCompactLocalGroup,
                IsMovementSampleReliable = reliableForMovement
            };
        }

        private readonly record struct LocalClusterAssessment(
            int BestClusterSize,
            MobPoint BestClusterCenter,
            float BestPairDistancePx,
            float BestPairDistanceNormalized,
            float MeanNearestNeighborDistancePx,
            float MeanNearestNeighborDistanceNormalized,
            float LocalCompactnessScore,
            float FormationQuality,
            bool HasCompactLocalGroup);

        /// <summary>
        /// Bounded O(n^2) local cluster assessment.
        /// <para>
        /// 1) Every marker is validated (finite coordinates/radius/confidence) by the caller.
        /// 2) All geometric thresholds are normalized by the effective marker radius, so the
        ///    values do not depend on the client resolution.
        /// 3) The most compact pair is located and its neighborhood is counted: remote outliers
        ///    never influence whether an already useful local cluster exists.
        /// 4) Two derived, deterministic scores are exposed: the compact-pair score and the
        ///    (mean nearest-neighbour) local formation quality used for temporal comparison.
        /// </para>
        /// No melee/ranged classification and no forward-AoE hit prediction is derived here.
        /// </summary>
        private static LocalClusterAssessment AssessLocalCluster(MobMarker[] markers, float effectiveRadius)
        {
            int count = markers.Length;
            float radius = effectiveRadius > 0.0001f
                ? effectiveRadius
                : BotConstants.MobGrouping.ExpectedMarkerRadiusPx;

            if (count < 2)
            {
                // A single marker has nothing to group: report the neutral values so the
                // temporal score never produces a fake improvement out of a 1-marker frame.
                return new LocalClusterAssessment(
                    count, markers.Length == 1 ? markers[0].Center : default,
                    0f, 0f, 0f, 0f, 1f, 1f, false);
            }

            // n is bounded by MaximumMarkersForEvaluation for movement decisions; a larger
            // (cluttered) detection is still analysed, but stack bounds stay safe.
            int scratchCapacity = Math.Max(count, 1);
            Span<float> nearestSquared = scratchCapacity > 64
                ? new float[scratchCapacity]
                : stackalloc float[64];
            if (nearestSquared.Length != count)
                nearestSquared = nearestSquared.Slice(0, count);
            for (int i = 0; i < count; i++)
                nearestSquared[i] = float.MaxValue;

            float bestDistance = float.MaxValue;
            int bestFirst = 0;
            int bestSecond = 1;

            for (int i = 0; i < count; i++)
            {
                MobPoint first = markers[i].Center;
                for (int j = i + 1; j < count; j++)
                {
                    MobPoint second = markers[j].Center;
                    float dx = second.X - first.X;
                    float dy = second.Y - first.Y;
                    float distanceSquared = dx * dx + dy * dy;

                    if (distanceSquared < nearestSquared[i])
                        nearestSquared[i] = distanceSquared;
                    if (distanceSquared < nearestSquared[j])
                        nearestSquared[j] = distanceSquared;

                    if (distanceSquared < bestDistance)
                    {
                        bestDistance = distanceSquared;
                        bestFirst = i;
                        bestSecond = j;
                    }
                }
            }

            float bestPairDistance = MathF.Sqrt(bestDistance);
            float bestPairNormalized = bestPairDistance / radius;

            // Compact neighborhood around the best pair. Counting from the pair midpoint keeps
            // the result independent of the (outlier-dragged) whole-formation centroid.
            float midpointX = (markers[bestFirst].Center.X + markers[bestSecond].Center.X) * 0.5f;
            float midpointY = (markers[bestFirst].Center.Y + markers[bestSecond].Center.Y) * 0.5f;
            float neighborhoodRadius =
                BotConstants.MobGrouping.CompactClusterNeighborhoodFactor * radius;
            float neighborhoodRadiusSquared = neighborhoodRadius * neighborhoodRadius;

            int bestClusterSize = 0;
            float nearestSum = 0f;
            for (int i = 0; i < count; i++)
            {
                MobPoint center = markers[i].Center;
                float dx = center.X - midpointX;
                float dy = center.Y - midpointY;
                if (dx * dx + dy * dy <= neighborhoodRadiusSquared)
                    bestClusterSize++;

                nearestSum += MathF.Sqrt(nearestSquared[i]);
            }

            float meanNearestDistance = nearestSum / count;
            float meanNearestNormalized = meanNearestDistance / radius;

            bool hasCompactLocalGroup =
                bestClusterSize >= BotConstants.MobGrouping.MinimumCompactClusterSize &&
                bestPairNormalized <= BotConstants.MobGrouping.CompactPairDistanceFactor;

            return new LocalClusterAssessment(
                bestClusterSize,
                new MobPoint(midpointX, midpointY),
                bestPairDistance,
                bestPairNormalized,
                meanNearestDistance,
                meanNearestNormalized,
                // 1 = perfectly compact pair, approaching 0 for a fully scattered pair.
                1f / (1f + bestPairNormalized),
                // 1 = perfectly compact formation, approaching 0 while it is spread out.
                1f / (1f + meanNearestNormalized),
                hasCompactLocalGroup);
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
