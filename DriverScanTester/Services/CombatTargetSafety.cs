using System;
using System.Drawing;

namespace DriverScanTester.Services
{
    /// <summary>Classification of the currently selected game target.</summary>
    public enum TargetEntityKind
    {
        /// <summary>No target is selected (raw id &lt;= 0).</summary>
        None,

        /// <summary>
        /// The target could not be classified with trustworthy evidence (failed/unavailable
        /// memory read or other-player scan). NEVER authorizes an attack.
        /// </summary>
        Unknown,

        /// <summary>Confirmed monster: mob-domain target id AND a completed scan proving no other player is on screen.</summary>
        Monster,

        /// <summary>Confirmed other player: player-domain target id or a confirmed other-player marker on screen.</summary>
        OtherPlayer,

        /// <summary>Non-combat target (the player is inside the city, i.e. an NPC area).</summary>
        Npc
    }

    /// <summary>
    /// Result of one attack-permission decision for the currently selected target.
    /// <see cref="AttackAllowed"/> is true ONLY for <see cref="TargetEntityKind.Monster"/>.
    /// </summary>
    public readonly record struct CombatTargetValidation(
        int TargetId,
        TargetEntityKind Kind,
        string Source,
        bool AttackAllowed,
        string Reason,
        PlayerMarkerCandidate? PlayerMarker)
    {
        public bool IsConfirmedMonster => Kind == TargetEntityKind.Monster;

        public string Describe()
            => $"TargetId={TargetId} Type={Kind} Source={Source} Valid={AttackAllowed}" +
               (string.IsNullOrEmpty(Reason) ? "" : $" Reason={Reason}");
    }

    /// <summary>State of the independent "is another player visible" evidence source.</summary>
    public enum CombatPlayerPresenceStatus
    {
        /// <summary>No scan has been performed yet.</summary>
        NotScanned,

        /// <summary>A completed scan of the game client found no other-player faction marker.</summary>
        NoOtherPlayer,

        /// <summary>A completed scan found a confirmed other-player faction marker.</summary>
        OtherPlayer,

        /// <summary>The scan could not produce a trustworthy answer (no client frame/detector failure).</summary>
        Unavailable
    }

    /// <summary>One other-player presence observation.</summary>
    public readonly record struct CombatPlayerPresence(
        CombatPlayerPresenceStatus Status,
        PlayerMarkerCandidate? Candidate,
        DateTime CapturedAtUtc,
        string Detail)
    {
        public bool HasAnswer =>
            Status == CombatPlayerPresenceStatus.NoOtherPlayer ||
            Status == CombatPlayerPresenceStatus.OtherPlayer;
    }

    /// <summary>
    /// Independent evidence source for "is another player visible on the game client".
    /// Implementations must report <see cref="CombatPlayerPresenceStatus.Unavailable"/>
    /// when they cannot read a trustworthy answer instead of assuming "no player".
    /// </summary>
    public interface ICombatPlayerPresenceScanner
    {
        CombatPlayerPresence Observe(bool forceCapture);
    }

    /// <summary>
    /// Screen-based other-player presence scanner. It reuses the project's existing
    /// <see cref="PlayerMarkerPixelDetector"/> (Empire yellow / Alliance green faction
    /// markers) but deliberately uses a COMPACT exclusion radius around the local
    /// character instead of the legacy loot exclusion rectangle.
    ///
    /// Why: the loot rectangle is 160x170 px. On 2026-10-29 another player ("Gissel")
    /// stood ~89 px from the character anchor with their marker's nearest pixel ~57 px
    /// away, so the marker was inside that rectangle and the existing attack-start check
    /// could not see the player. With the compact radius the local player's own marker is
    /// still ignored, while an adjacent other player is detected.
    /// </summary>
    internal sealed class ScreenCombatPlayerPresenceScanner : ICombatPlayerPresenceScanner
    {
        private readonly Action<string> _log;
        private readonly Func<DateTime> _now;
        private readonly Func<Bitmap?> _captureFrame;
        private readonly PlayerMarkerPixelDetector _detector = new();
        private readonly int _scanIntervalMs;
        private readonly object _gate = new();

        private CombatPlayerPresence _last = new(
            CombatPlayerPresenceStatus.NotScanned, null, DateTime.MinValue, "not scanned yet");
        private CombatPlayerPresenceStatus _loggedStatus = CombatPlayerPresenceStatus.NotScanned;

        public ScreenCombatPlayerPresenceScanner(
            Action<string> log,
            Func<DateTime>? now = null,
            Func<Bitmap?>? captureFrame = null,
            int scanIntervalMs = BotConstants.CombatSafety.PlayerScanIntervalMs)
        {
            _log = log ?? (_ => { });
            _now = now ?? (() => DateTime.UtcNow);
            _captureFrame = captureFrame ?? (() => ScreenshotService.CaptureGameClientFrame());
            _scanIntervalMs = Math.Max(0, scanIntervalMs);
        }

        public CombatPlayerPresence Observe(bool forceCapture)
        {
            lock (_gate)
            {
                DateTime now = _now();
                if (!forceCapture &&
                    _last.HasAnswer &&
                    (now - _last.CapturedAtUtc).TotalMilliseconds < _scanIntervalMs)
                {
                    return _last;
                }

                CombatPlayerPresence presence = Scan(now);
                _last = presence;
                LogTransition(presence);
                return presence;
            }
        }

        private CombatPlayerPresence Scan(DateTime now)
        {
            Bitmap? frame = null;
            try
            {
                frame = _captureFrame();
                if (frame == null)
                {
                    return new CombatPlayerPresence(
                        CombatPlayerPresenceStatus.Unavailable, null, now,
                        "game client frame could not be captured");
                }

                PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(frame.Width, frame.Height);
                float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(frame.Height);

                bool found = _detector.TryFindOtherPlayerMarker(
                    frame,
                    Rectangle.Empty,
                    anchor,
                    radius,
                    allowClassEmblems: true,
                    out PlayerMarkerCandidate candidate);

                if (!found)
                {
                    return new CombatPlayerPresence(
                        CombatPlayerPresenceStatus.NoOtherPlayer, null, now,
                        "no other-player faction marker on screen");
                }

                float dx = candidate.Center.X - anchor.X;
                float dy = candidate.Center.Y - anchor.Y;
                float distance = MathF.Sqrt((dx * dx) + (dy * dy));

                SaveDiagnosticScreenshot(frame, candidate, anchor, radius);

                return new CombatPlayerPresence(
                    CombatPlayerPresenceStatus.OtherPlayer,
                    candidate,
                    now,
                    $"faction marker {candidate.Faction} bounds={candidate.Bounds} " +
                    $"distanceFromCharacter={distance:F0}px (local exclusion radius {radius:F0}px)");
            }
            catch (Exception ex)
            {
                return new CombatPlayerPresence(
                    CombatPlayerPresenceStatus.Unavailable, null, now,
                    $"other-player scan failed: {ex.Message}");
            }
            finally
            {
                frame?.Dispose();
            }
        }

        private void LogTransition(CombatPlayerPresence presence)
        {
            if (presence.Status == _loggedStatus)
                return;

            CombatPlayerPresenceStatus previous = _loggedStatus;
            _loggedStatus = presence.Status;

            switch (presence.Status)
            {
                case CombatPlayerPresenceStatus.OtherPlayer:
                    PlayerMarkerCandidate candidate = presence.Candidate!.Value;
                    _log($"[PlayerDetection] OtherPlayerDetected Faction={candidate.Faction} " +
                         $"Bounds={candidate.Bounds} Center=({candidate.Center.X:F0},{candidate.Center.Y:F0}) " +
                         $"Pixels={candidate.PixelCount} Source=combat-player-marker {presence.Detail}.");
                    break;

                case CombatPlayerPresenceStatus.NoOtherPlayer when
                    previous == CombatPlayerPresenceStatus.OtherPlayer:
                    _log("[PlayerDetection] OtherPlayerMarkerCleared — no other-player marker on screen.");
                    break;

                case CombatPlayerPresenceStatus.Unavailable:
                    _log($"[PlayerDetection] PlayerMarkerScanUnavailable — {presence.Detail}; " +
                         "attacks stay blocked until the scan can produce a trustworthy answer.");
                    break;
            }
        }

        private void SaveDiagnosticScreenshot(Bitmap frame, PlayerMarkerCandidate candidate, PointF anchor, float radius)
        {
            try
            {
                Rectangle exclusionArea = Rectangle.FromLTRB(
                    (int)Math.Round(anchor.X - radius),
                    (int)Math.Round(anchor.Y - radius),
                    (int)Math.Round(anchor.X + radius),
                    (int)Math.Round(anchor.Y + radius));
                ScreenshotService.SavePlayerMarkerDiagnosticScreenshot(frame, candidate, exclusionArea);
            }
            catch (Exception ex)
            {
                // A failing diagnostic must never change the safety decision.
                _log($"[PlayerDetection] Diagnostic screenshot failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Single authoritative target-classification and attack-permission mechanism.
    ///
    /// Rules (all sources are independent, and only a completed scan may authorize):
    ///   • raw target id &lt;= 0                            → None        → attack NO
    ///   • target id in the player-id domain               → OtherPlayer → attack NO
    ///   • player is inside the city                       → Npc         → attack NO
    ///   • other-player faction marker on screen           → OtherPlayer → attack NO
    ///   • scan unavailable/failed or stale evidence        → Unknown     → attack NO
    ///   • mob-domain id + completed scan, no player mark  → Monster     → attack YES
    ///
    /// The legacy mob/player id boundary can only ever BLOCK an attack. It is never
    /// sufficient proof of a monster: on 2026-10-29 a player had TargetId=718, below the
    /// boundary, and was attacked. The screen evidence is therefore mandatory.
    /// </summary>
    public sealed class CombatTargetSafety
    {
        private readonly Func<int> _readTargetId;
        private readonly ICombatPlayerPresenceScanner _presence;
        private readonly Func<bool> _isInCity;
        private readonly Action<string> _log;
        private readonly Func<DateTime> _now;
        private readonly int _maxPresenceAgeMs;

        private int _lastTargetId = int.MinValue;
        private string _lastLoggedSignature = string.Empty;
        private string _lastBlockedSignature = string.Empty;

        public CombatTargetSafety(
            Func<int> readTargetId,
            ICombatPlayerPresenceScanner presence,
            Func<bool> isInCity,
            Action<string> log,
            Func<DateTime>? now = null,
            int maxPresenceAgeMs = BotConstants.CombatSafety.PlayerScanMaxAgeMs)
        {
            _readTargetId = readTargetId ?? throw new ArgumentNullException(nameof(readTargetId));
            _presence = presence ?? throw new ArgumentNullException(nameof(presence));
            _isInCity = isInCity ?? throw new ArgumentNullException(nameof(isInCity));
            _log = log ?? (_ => { });
            _now = now ?? (() => DateTime.UtcNow);
            _maxPresenceAgeMs = Math.Max(0, maxPresenceAgeMs);
        }

        /// <summary>
        /// Classifies the currently selected target and decides whether an attack may be
        /// initiated, resumed or retried. <paramref name="forcePlayerScan"/> requests fresh
        /// other-player evidence (used when a new target was selected and before the first
        /// attack of a target).
        /// </summary>
        public CombatTargetValidation Validate(bool forcePlayerScan = false)
        {
            int targetId = _readTargetId();
            if (targetId != _lastTargetId)
                forcePlayerScan = true;

            CombatTargetValidation validation = Classify(targetId, forcePlayerScan);

            _lastTargetId = targetId;
            LogValidation(validation);
            return validation;
        }

        /// <summary>
        /// True only for a confirmed monster. Convenience wrapper for code paths that only
        /// need the boolean (e.g. mob-grouping combat context).
        /// </summary>
        public bool IsConfirmedMonster(bool forcePlayerScan = false)
            => Validate(forcePlayerScan).IsConfirmedMonster;

        private CombatTargetValidation Classify(int targetId, bool forcePlayerScan)
        {
            if (targetId <= 0)
            {
                return new CombatTargetValidation(
                    targetId, TargetEntityKind.None, "target-read", false,
                    "no target selected", null);
            }

            if (GameMemoryService.IsPlayerTargetId(targetId))
            {
                return new CombatTargetValidation(
                    targetId, TargetEntityKind.OtherPlayer, "target-id-player-domain", false,
                    "target id belongs to the player-id domain", null);
            }

            if (_isInCity())
            {
                return new CombatTargetValidation(
                    targetId, TargetEntityKind.Npc, "in-city", false,
                    "player is in the city (NPC/non-combat area)", null);
            }

            CombatPlayerPresence presence = _presence.Observe(forcePlayerScan);
            if (presence.Status == CombatPlayerPresenceStatus.OtherPlayer)
            {
                return new CombatTargetValidation(
                    targetId, TargetEntityKind.OtherPlayer, "other-player-marker", false,
                    presence.Detail, presence.Candidate);
            }

            if (!presence.HasAnswer)
            {
                return new CombatTargetValidation(
                    targetId, TargetEntityKind.Unknown, "other-player-marker", false,
                    presence.Detail, null);
            }

            double ageMs = (_now() - presence.CapturedAtUtc).TotalMilliseconds;
            if (ageMs > _maxPresenceAgeMs)
            {
                return new CombatTargetValidation(
                    targetId, TargetEntityKind.Unknown, "other-player-marker", false,
                    $"other-player evidence is stale ({ageMs:F0} ms > {_maxPresenceAgeMs} ms)", null);
            }

            return new CombatTargetValidation(
                targetId, TargetEntityKind.Monster, "mob-target-id+no-player-marker", true,
                null, null);
        }

        private void LogValidation(CombatTargetValidation validation)
        {
            string signature = $"{validation.TargetId}|{validation.Kind}|{validation.Source}|{validation.AttackAllowed}";
            if (signature != _lastLoggedSignature)
            {
                _lastLoggedSignature = signature;
                _log($"[TargetValidation] {validation.Describe()}.");
            }

            if (validation.AttackAllowed)
            {
                _lastBlockedSignature = string.Empty;
                return;
            }

            string blockedSignature = $"{validation.TargetId}|{validation.Kind}|{validation.Reason}";
            if (blockedSignature != _lastBlockedSignature)
            {
                _lastBlockedSignature = blockedSignature;
                _log($"[CombatSafety] AttackBlocked TargetId={validation.TargetId} " +
                     $"Reason={validation.Kind} Source={validation.Source} Detail={validation.Reason}.");
            }
        }
    }
}
