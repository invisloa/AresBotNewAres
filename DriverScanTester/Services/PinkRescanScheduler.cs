using System;

namespace DriverScanTester.Services
{
    internal enum PinkRescanPassOutcome
    {
        NoItem,
        ItemCollected,
        CaptureFailed,
        Cancelled
    }

    internal readonly record struct PinkRescanTicket(long Epoch, long Generation, DateTime StartedAtUtc);

    internal readonly record struct PinkRescanSnapshot(
        bool Pending,
        bool Active,
        DateTime DueAtUtc,
        long Generation,
        long Epoch);

    /// <summary>
    /// Thread-safe, coalescing scheduler for the delayed SOD/SOP scan. A due time only
    /// moves earlier when new kills arrive. A kill during an active pass is recorded in
    /// a newer generation and therefore survives an empty result from that pass.
    /// </summary>
    internal sealed class PinkRescanScheduler
    {
        private readonly object _gate = new();
        private long _generation;
        private long _epoch;
        private bool _pending;
        private bool _active;
        private DateTime _dueAtUtc = DateTime.MinValue;
        private PinkRescanTicket _activeTicket;

        internal PinkRescanSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                    return new(_pending, _active, _dueAtUtc, _generation, _epoch);
            }
        }

        internal void ScheduleKill(DateTime nowUtc, TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delay));

            lock (_gate)
            {
                _generation++;
                _pending = true;
                KeepEarliestDueLocked(nowUtc + delay);
            }
        }

        internal bool TryBegin(
            DateTime nowUtc,
            bool modeEnabled,
            bool immediatePinkPassActive,
            out PinkRescanTicket ticket)
        {
            lock (_gate)
            {
                ticket = default;
                if (!modeEnabled)
                {
                    CancelLocked();
                    return false;
                }

                if (_active || immediatePinkPassActive || !_pending || nowUtc < _dueAtUtc)
                    return false;

                _active = true;
                _pending = false;
                _dueAtUtc = DateTime.MinValue;
                ticket = new PinkRescanTicket(_epoch, _generation, nowUtc);
                _activeTicket = ticket;
                return true;
            }
        }

        internal void Complete(
            PinkRescanTicket ticket,
            DateTime nowUtc,
            PinkRescanPassOutcome outcome,
            TimeSpan successfulPickupFollowUpDelay,
            TimeSpan captureFailureRetryDelay)
        {
            lock (_gate)
            {
                if (!_active || ticket != _activeTicket)
                    return;

                _active = false;
                _activeTicket = default;
                if (ticket.Epoch != _epoch)
                    return;

                bool killArrivedDuringPass = _generation > ticket.Generation;
                switch (outcome)
                {
                    case PinkRescanPassOutcome.ItemCollected:
                        // Preserve the existing drain-until-empty behavior. Any newer
                        // kill remains coalesced into this same next pass.
                        _pending = true;
                        KeepEarliestDueLocked(nowUtc + successfulPickupFollowUpDelay);
                        break;

                    case PinkRescanPassOutcome.CaptureFailed:
                        // A failed frame is not an empty scan. Retry the covered work;
                        // newer kills keep their earlier deadline if they are sooner.
                        _pending = true;
                        KeepEarliestDueLocked(nowUtc + captureFailureRetryDelay);
                        break;

                    case PinkRescanPassOutcome.NoItem:
                        if (killArrivedDuringPass || _pending)
                        {
                            _pending = true;
                            if (_dueAtUtc == DateTime.MinValue)
                                _dueAtUtc = nowUtc;
                        }
                        else
                        {
                            _pending = false;
                            _dueAtUtc = DateTime.MinValue;
                        }
                        break;

                    case PinkRescanPassOutcome.Cancelled:
                        CancelLocked();
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(outcome));
                }
            }
        }

        internal void Cancel()
        {
            lock (_gate)
                CancelLocked();
        }

        private void KeepEarliestDueLocked(DateTime candidateUtc)
        {
            if (_dueAtUtc == DateTime.MinValue || candidateUtc < _dueAtUtc)
                _dueAtUtc = candidateUtc;
        }

        private void CancelLocked()
        {
            _epoch++;
            _generation++;
            _pending = false;
            _dueAtUtc = DateTime.MinValue;
            _active = false;
            _activeTicket = default;
        }
    }
}
