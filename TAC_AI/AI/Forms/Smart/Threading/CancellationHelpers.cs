using System;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// CancellationToken patterns used across Smart. Cooperative cancellation only;
    /// Thread.Abort and Thread.Interrupt are forbidden per
    /// <see cref="THREADING-CONTRACT.md §3.2"/>.
    ///
    /// Smart's per-form-session root CTS is owned by the WorkerPool (or whoever creates
    /// the top-level scope). Subsystems create linked CTS for sub-scopes; the linked
    /// CTS cancels when the root cancels OR when its own Cancel/Dispose is called.
    /// </summary>
    public static class CancellationHelpers
    {
        /// <summary>
        /// Create a CTS linked to one or more parent tokens. Cancelling any parent
        /// (or cancelling the returned CTS directly) cancels the resulting token.
        /// Caller MUST Dispose the returned CTS when the sub-scope ends.
        /// </summary>
        public static CancellationTokenSource CreateLinked(params CancellationToken[] parents)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(parents);
        }

        /// <summary>
        /// Run <paramref name="work"/> with a timeout. Returns true if work completed before
        /// the timeout fired; false if work was cancelled by the timeout.
        /// Any other OperationCanceledException (e.g., parent cancel) is re-thrown.
        /// </summary>
        public static bool RunWithTimeout(CancellationToken parent, TimeSpan timeout, Action<CancellationToken> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(parent))
            {
                cts.CancelAfter(timeout);
                try
                {
                    work(cts.Token);
                    return true;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !parent.IsCancellationRequested)
                {
                    // Timeout fired (our linked CTS cancelled, but parent did not).
                    return false;
                }
            }
        }

        /// <summary>
        /// Convenience: throw OperationCanceledException if the token is cancelled.
        /// Equivalent to <c>token.ThrowIfCancellationRequested()</c>; the wrapper exists
        /// so a code-search for "cancellation discipline" finds the canonical call site.
        /// </summary>
        public static void ThrowIfCancelled(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
        }
    }
}
