using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// FIFO queue with a stated capacity and drop-oldest overflow policy.
    /// Standard pattern for tactical/optimizer request streams where the freshest
    /// state is what matters; older requests on stale snapshots produce results
    /// the caller no longer wants.
    ///
    /// Contract: <see cref="THREADING-CONTRACT.md §5"/>. <see cref="Enqueue"/> never
    /// blocks. When approximate count exceeds capacity at enqueue time, oldest
    /// entries are dequeued silently (from the producer's perspective) and
    /// <see cref="DroppedCount"/> increments. Per [ARCHITECTURE.md §4 silent-failure
    /// rule + THREADING-CONTRACT §5.3], drops are visible via <see cref="DroppedCount"/>
    /// and via <see cref="ThreadingDiagnostics.RaiseRequestsDropped"/> events; the
    /// producer not blocking is intentional, the system being unaware of the drop
    /// is forbidden.
    ///
    /// Capacity is a per-usage decision (per [FORM-SPECIFICATION.md §5.4] — no
    /// defensive bounds without stated reason). Smart's BoundedQueue does not
    /// pre-pick a default; the consuming subsystem passes capacity at construction.
    /// </summary>
    public sealed class BoundedQueue<T>
    {
        private readonly ConcurrentQueue<T> _queue;
        private readonly int _capacity;
        private readonly string _name;
        private long _count;
        private long _dropped;

        public int Capacity => _capacity;
        public int ApproximateCount => (int)Interlocked.Read(ref _count);
        public long DroppedCount => Interlocked.Read(ref _dropped);
        public string Name => _name;

        /// <param name="capacity">Per-usage maximum. Must be &gt;= 1; rationale required per FORM-SPEC §5.4.</param>
        /// <param name="name">Optional label used in diagnostic events; "" if unnamed.</param>
        public BoundedQueue(int capacity, string name = "")
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "BoundedQueue capacity must be >= 1.");
            _capacity = capacity;
            _name = name ?? string.Empty;
            _queue = new ConcurrentQueue<T>();
        }

        public void Enqueue(T item)
        {
            _queue.Enqueue(item);
            Interlocked.Increment(ref _count);

            // Drop-oldest: if we're over capacity, evict from the head until we're back to capacity.
            // Approximate under contention (multiple producers may race), but the bound is honored
            // within bounded slack proportional to producer parallelism.
            int dropped = 0;
            while (Interlocked.Read(ref _count) > _capacity)
            {
                if (_queue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _count);
                    Interlocked.Increment(ref _dropped);
                    dropped++;
                }
                else
                {
                    // Race: another consumer drained the queue. Bail; ApproximateCount may briefly
                    // disagree with the queue's actual content but converges.
                    break;
                }
            }

            if (dropped > 0)
                ThreadingDiagnostics.RaiseRequestsDropped(_name, dropped);
        }

        public bool TryDequeue(out T item)
        {
            if (_queue.TryDequeue(out item))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }
            return false;
        }
    }
}
