using System;
using System.Collections.Generic;

namespace DriverScanTester.Services
{
    /// <summary>
    /// One mouseover reading taken while the cursor rests on a scan point.
    ///
    /// A sample is produced from TWO reads of the same address. <see cref="Stable"/> is
    /// true only when both reads succeeded and returned the same value, so a transient
    /// failed read or a value that is still settling after the cursor move is never
    /// treated as "an object is under the cursor".
    /// </summary>
    public readonly record struct MouseOverValueSample(
        bool ReadSucceeded,
        bool Stable,
        int Value,
        bool NpcHighlighted)
    {
        /// <summary>
        /// Builds a sample from the two reads. The value is only reported when both reads
        /// succeeded; a failed read must never be confused with a real value (not even 0).
        /// </summary>
        public static MouseOverValueSample FromReads(
            bool firstReadSucceeded,
            int firstValue,
            bool confirmReadSucceeded,
            int confirmValue,
            bool npcHighlighted)
        {
            bool readSucceeded = firstReadSucceeded && confirmReadSucceeded;
            bool stable = readSucceeded && firstValue == confirmValue;
            return new MouseOverValueSample(
                readSucceeded,
                stable,
                readSucceeded ? firstValue : 0,
                npcHighlighted);
        }
    }

    /// <summary>Why a mouseover sample was, or was not, accepted as a testable candidate.</summary>
    public enum MouseOverCandidateDecision
    {
        /// <summary>The sample is a stable, novel, non-zero value — validate it in-game.</summary>
        Candidate = 0,

        /// <summary>The pointer chain / memory read failed for at least one of the two reads.</summary>
        ReadFailed,

        /// <summary>The two reads disagreed (the value was still settling).</summary>
        Unstable,

        /// <summary>Value 0 — nothing is under the cursor.</summary>
        NothingHovered,

        /// <summary>The value is the currently active detection value (Stage 1 would have found it).</summary>
        MatchesActiveValue,

        /// <summary>This value was already validated and rejected during this discovery run.</summary>
        AlreadyRejected,

        /// <summary>The value did not follow the cursor: it did not change when the cursor left the point.</summary>
        HoverChangeNotObserved,

        /// <summary>No more candidates may be validated in this discovery run.</summary>
        TestBudgetExhausted
    }

    /// <summary>
    /// Bookkeeping for TWO-STAGE NPC detection, stage 2 (slow discovery and automatic
    /// calibration).
    ///
    /// The bot compares the value read at <c>[Ares.exe + 0x4883E8] + 0xC</c> with the
    /// configured NPC value while the cursor sweeps the game window. That value is the
    /// hovered object's heap address/ID (see MouseOverValues_Log.txt), so it changes
    /// between game launches and a value captured in an older session no longer matches.
    /// Stage 2 therefore looks for *any* stable, novel value that follows the cursor,
    /// treats it as a CANDIDATE (never as a confirmed NPC) and validates it through a real
    /// right-click + NPC-dialogue check (implemented by the caller).
    ///
    /// This class owns only the value/candidate bookkeeping:
    ///  1. which values may be tested (novel, stable, non-zero, not the active value),
    ///  2. which values were already rejected (never right-click the same value twice),
    ///  3. the bounded candidate-test budget,
    ///  4. applying the confirmed value through the caller-supplied write delegate, so the
    ///     active value never exists twice (single source of truth).
    ///
    /// It performs no IO, so the candidate rules are unit-testable without the game.
    /// The same class can drive MouseOver ITEM discovery later by supplying a different
    /// sample source and a different in-game validation; nothing here is NPC specific.
    /// </summary>
    public sealed class MouseOverValueDiscovery
    {
        /// <summary>Default number of candidates that may be validated with a right-click per run.</summary>
        public const int DefaultMaxCandidateTests = 8;

        /// <summary>Default number of rejected values the run remembers.</summary>
        public const int DefaultMaxRejectedValues = 32;

        private readonly Func<int> _readActiveValue;
        private readonly Action<int> _writeActiveValue;
        private readonly Action<string> _log;
        private readonly HashSet<int> _rejectedValues = new();
        private readonly Dictionary<int, int> _observationCounts = new();
        private readonly int _maxCandidateTests;
        private readonly int _maxRejectedValues;
        private readonly string _label;
        private bool _rejectOverflowLogged;
        private bool _budgetExhaustedLogged;

        /// <param name="readActiveValue">
        /// Reads the CURRENT active MouseOver detection value (the same value the fast scan
        /// compares against). It is read live, so a confirmed value is never cached twice.
        /// </param>
        /// <param name="writeActiveValue">Applies a confirmed value to that same state.</param>
        public MouseOverValueDiscovery(
            Func<int> readActiveValue,
            Action<int> writeActiveValue,
            Action<string> log,
            int maxCandidateTests = DefaultMaxCandidateTests,
            int maxRejectedValues = DefaultMaxRejectedValues,
            string label = "NPC")
        {
            if (maxCandidateTests < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCandidateTests));
            if (maxRejectedValues < 1)
                throw new ArgumentOutOfRangeException(nameof(maxRejectedValues));

            _readActiveValue = readActiveValue ?? throw new ArgumentNullException(nameof(readActiveValue));
            _writeActiveValue = writeActiveValue ?? throw new ArgumentNullException(nameof(writeActiveValue));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _maxCandidateTests = maxCandidateTests;
            _maxRejectedValues = maxRejectedValues;
            _label = string.IsNullOrWhiteSpace(label) ? "NPC" : label;
        }

        /// <summary>
        /// The single active MouseOver detection value used by the normal (fast) scan.
        /// It only changes through <see cref="Confirm"/>.
        /// </summary>
        public int ActiveValue => _readActiveValue();

        /// <summary>How many candidates were already validated with a right-click in this run.</summary>
        public int CandidateTestCount { get; private set; }

        /// <summary>Upper bound of candidate validations for this run.</summary>
        public int MaxCandidateTests => _maxCandidateTests;

        /// <summary>True when no further candidate may be validated.</summary>
        public bool IsTestBudgetExhausted => CandidateTestCount >= _maxCandidateTests;

        /// <summary>Number of values that were rejected during this run.</summary>
        public int RejectedValueCount => _rejectedValues.Count;

        /// <summary>
        /// Decides whether a stable sample is worth a right-click. Only
        /// <see cref="MouseOverCandidateDecision.Candidate"/> means "test it"; every other
        /// result must be skipped without clicking.
        /// </summary>
        /// <param name="sample">The stable sample read at the current scan point.</param>
        /// <param name="hoverChangeProbe">
        /// Invoked only when the sample passed every other check, so the caller can decide
        /// cheaply whether the value really follows the cursor (it may have to move the mouse
        /// away and back, which costs time). Return true when a hover change was observed or
        /// cannot be checked, false only when it was contradicted.
        /// </param>
        public MouseOverCandidateDecision Evaluate(in MouseOverValueSample sample, Func<bool> hoverChangeProbe)
        {
            if (!sample.ReadSucceeded)
                return MouseOverCandidateDecision.ReadFailed;

            if (!sample.Stable)
                return MouseOverCandidateDecision.Unstable;

            if (sample.Value == 0)
                return MouseOverCandidateDecision.NothingHovered;

            _observationCounts[sample.Value] = ObservationCount(sample.Value) + 1;

            if (sample.Value == ActiveValue)
                return MouseOverCandidateDecision.MatchesActiveValue;

            if (_rejectedValues.Contains(sample.Value))
                return MouseOverCandidateDecision.AlreadyRejected;

            if (IsTestBudgetExhausted)
            {
                LogBudgetExhaustedOnce();
                return MouseOverCandidateDecision.TestBudgetExhausted;
            }

            if (hoverChangeProbe == null)
                throw new ArgumentNullException(nameof(hoverChangeProbe));

            if (!hoverChangeProbe())
                return MouseOverCandidateDecision.HoverChangeNotObserved;

            return MouseOverCandidateDecision.Candidate;
        }

        /// <summary>
        /// Registers that the value is about to be validated with a right-click and returns
        /// the 1-based attempt number.
        /// </summary>
        public int StartCandidateTest(int value)
        {
            CandidateTestCount++;
            return CandidateTestCount;
        }

        /// <summary>
        /// Marks a value as validated-and-rejected for the rest of this run, so the same
        /// object is never right-clicked again without new evidence.
        /// </summary>
        public void Reject(int value, string reason)
        {
            if (value == 0)
                return;

            if (_rejectedValues.Count >= _maxRejectedValues)
            {
                if (!_rejectOverflowLogged)
                {
                    _rejectOverflowLogged = true;
                    _log($"[{_label} Calibration] Rejected-value bookkeeping is full ({_maxRejectedValues}); further candidates are not remembered.");
                }
                return;
            }

            _rejectedValues.Add(value);
            _log($"[{_label} Calibration] Candidate {Describe(value)} rejected: {reason}");
        }

        /// <summary>True when the value was already rejected during this run.</summary>
        public bool IsRejected(int value) => _rejectedValues.Contains(value);

        /// <summary>How many times this value was observed as a stable sample.</summary>
        public int ObservationCount(int value)
            => _observationCounts.TryGetValue(value, out int count) ? count : 0;

        /// <summary>
        /// Makes a validated value the active one (through the caller-supplied write delegate)
        /// and returns the previous value.
        /// </summary>
        public int Confirm(int value)
        {
            int previousValue = _readActiveValue();
            _writeActiveValue(value);
            _log($"[{_label} Calibration] Active {_label} detection value updated: {Describe(previousValue)} -> {Describe(value)}");
            return previousValue;
        }

        /// <summary>Formats a mouseover value for the bot log (decimal + hex + unset marker).</summary>
        public static string Describe(int value)
            => value == 0 ? "0 (unset)" : $"{value} (0x{(uint)value:X8})";

        private void LogBudgetExhaustedOnce()
        {
            if (_budgetExhaustedLogged)
                return;

            _budgetExhaustedLogged = true;
            _log($"[{_label} Calibration] Candidate validation budget exhausted ({_maxCandidateTests} attempts) — no further values will be right-clicked in this run.");
        }
    }
}
