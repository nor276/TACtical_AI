using System;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// Canonical patterns for moving data between Smart's main-thread shell hooks
    /// and form-owned workers. Each pattern is documented in
    /// <see cref="THREADING-CONTRACT.md §8"/> with concrete examples.
    ///
    /// Two disciplines apply to every cross-thread flow (THREADING-CONTRACT §2.2):
    ///   R1. Workers never touch TerraTech engine objects (Tank, TankBlock, Visible,
    ///       Rigidbody, Transform, ManXxx.inst, Singleton.*). The Unity API is
    ///       main-thread-only. Worker code reads plain-data snapshots constructed
    ///       on the main thread.
    ///   R2. Snapshots are double-buffered. The main thread writes the next snapshot;
    ///       workers read the published snapshot via <see cref="DoubleBuffer{T}.Read"/>.
    ///       Atomic publish via <see cref="DoubleBuffer{T}.Write"/>.
    ///
    /// The helpers below are thin conveniences; subsystems may inline equivalent code
    /// when the helpers don't fit.
    /// </summary>
    public static class MarshallingPatterns
    {
        /// <summary>
        /// Pattern A helper — construct an immutable snapshot on the main thread and
        /// publish it via <see cref="DoubleBuffer{T}"/>. The factory MUST run on the
        /// caller's thread (typically main thread inside a shell hook or event
        /// handler). Workers later <see cref="DoubleBuffer{T}.Read"/> from the same
        /// buffer.
        ///
        /// Pattern A: main thread observes; worker reads.
        /// </summary>
        public static void BuildAndPublish<T>(DoubleBuffer<T> buffer, Func<T> factory) where T : class
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var snapshot = factory();
            buffer.Write(snapshot);
        }

        /// <summary>
        /// Pattern C helper — main thread enqueues a request to a bounded queue (which
        /// supplies drop-oldest semantics if requests pile up) and schedules a worker
        /// task. The worker dequeues the request and computes; results are published
        /// via the caller's own <see cref="DoubleBuffer{T}"/>.
        ///
        /// Pattern C: main thread requests; worker computes; result published.
        /// </summary>
        public static void EnqueueRequestAndCompute<TRequest>(
            WorkerPool pool,
            BoundedQueue<TRequest> requestQueue,
            TRequest request,
            Action<TRequest, CancellationToken> compute)
        {
            if (pool == null) throw new ArgumentNullException(nameof(pool));
            if (requestQueue == null) throw new ArgumentNullException(nameof(requestQueue));
            if (compute == null) throw new ArgumentNullException(nameof(compute));

            requestQueue.Enqueue(request);
            pool.Enqueue(token =>
            {
                // Drain whichever request is at the head; under drop-oldest semantics
                // it may not be the one we just enqueued, which is intentional —
                // the freshest pending request wins.
                if (requestQueue.TryDequeue(out var req))
                    compute(req, token);
            });
        }
    }
}
