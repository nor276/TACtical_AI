# THREADING-CONTRACT.md

**Subsystem:** Threading/
**Form:** Smart
**Version:** 0.3.0
**Status:** AUTHORITATIVE — Defines Smart's threading primitives: worker pool, double-buffer, bounded queue, cancellable tasks, worker lifecycle registry, canonical marshalling patterns, diagnostic event hub.

---

## CHANGES SINCE 0.2.0

Workflow step 1.4 implementation landed under `TAC_AI/AI/Forms/Smart/Threading/`. File layout in §9 updated from 6 files to 7 files: `ThreadingDiagnostics.cs` added as a dedicated event-hub file (separating event publication from `WorkerLifecycleRegistry`'s lifecycle-tracking concern). The 7-file count is at the upper end of FORM-SPEC §5.10's range; justification noted in §9.

## CHANGES SINCE 0.1.0

Verification pass against actual codebase (Unity 2018.4.13f1 LTS; .NET Framework 4.6.1 scripting backend; C# LangVersion 7.3) revealed several spec claims that referenced unavailable BCL types or C# 9 language features. Corrections:

- **§4.1 immutable shapes rewritten** to stay within BCL 4.6.1 + C# 7.3. `init`-only property setters (C# 9) and `record` types (C# 9) removed from the acceptable-shapes list. `ImmutableArray<T>` / `ImmutableDictionary<K,V>` removed as recommendations (would require adding the `System.Collections.Immutable` NuGet package); recommendation now is `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` (BCL interfaces) with discipline-enforced immutability.
- **New §4.1a (.NET and C# version constraints)** documenting the target framework limits and the allowed/disallowed BCL surface explicitly.
- **§5.2 `Channel<T>` reference replaced** with `ConcurrentQueue<T>` + `ManualResetEventSlim` (BCL primitives). `System.Threading.Channels` was added in .NET Core 2.1 and requires a NuGet add for .NET Framework 4.6.1; v0.1.0 stays BCL-only.
- **§8.1 example fixed**: `ModuleDamage.DamageInfo` → `ManDamage.DamageInfo` (verified against `TankAIHelper.cs:1326` actual handler signature).

---

## SECTION 0: AUTHORITY AND DEFERENCE

**This document is AUTHORITATIVE for:**
- The implementation shape of Smart's worker pool (own threads + .NET concurrent collections).
- The double-buffer primitive used for cross-thread snapshot publication.
- The bounded queue primitive used for worker request distribution.
- The cancellation model (`CancellationToken`-based cooperative).
- The worker error-recovery policy (restart with retry budget N=3).
- The worker lifecycle registry that satisfies [ARCHITECTURE.md §5 I3](ARCHITECTURE.md#section-5-cross-subsystem-invariants).
- The canonical marshalling patterns subsystems use to move data between main thread and workers.

**This document DEFERS TO:**
- [FORM-SPECIFICATION.md](FORM-SPECIFICATION.md) for project-level goals, AI-collaborator directives, file-granularity rules.
- [ARCHITECTURE.md](ARCHITECTURE.md) for the cross-cutting threading model (§2.1 pool sizing, §2.2 marshalling discipline, §2.3 compute-budget gating, §3.2 MP host/client gating, §5 invariants).
- [SHELL-API-GUIDE.md](SHELL-API-GUIDE.md) for which shell calls run on which thread.

**This document GOVERNS:**
- Every other Smart subsystem contract — they consume Threading's primitives. World's perception worker, Planning's optimizer workers, Pathing's pathing worker, Learning's online trainer, and Training's self-play harness all build on the primitives defined here.

Reading conventions per [FORM-SPECIFICATION.md §0](FORM-SPECIFICATION.md#section-0-authority-and-reading-order).

---

## SECTION 1: SCOPE AND RESOLVED DECISIONS

### 1.1 What this contract owns

[NORMATIVE] Threading/ owns six primitives, one per file:

1. **Worker pool** — a fixed-size set of own-Thread workers that consume tasks from a shared queue with simple FIFO distribution (work-stealing upgrade path noted in §11).
2. **Worker lifecycle registry** — centralized tracking of live workers so `DeInitGlobal` can enumerate and cancel them.
3. **Double-buffer** — two-slot atomic reference swap for publishing immutable snapshots from one writer to many readers.
4. **Bounded queue with drop-oldest** — a capacity-bounded queue for optimizer/request distribution; oldest entry is discarded when full.
5. **Cancellable-task helpers** — `CancellationToken`/`CancellationTokenSource` patterns used across Smart, including linked-token composition.
6. **Marshalling patterns** — canonical pattern examples and helper methods for moving data between main-thread shell hooks and form-owned workers.

### 1.2 Decisions inherited from FORM-SPEC and ARCHITECTURE

[NORMATIVE]
- Pool size: `Math.Max(2, Environment.ProcessorCount - 2)` (FORM-SPEC §7 OD-4, ARCHITECTURE §2.1).
- Thread priority: normal (ARCHITECTURE §2.1).
- Workers MUST terminate within `DeInitGlobal` (ARCHITECTURE §5 I3).
- R1: workers MUST NOT touch any TerraTech engine object or Unity API (ARCHITECTURE §2.2).
- R2: cross-thread data crosses via double-buffered snapshots (ARCHITECTURE §2.2).
- MP host/client gating: workers run only when `host==true` (ARCHITECTURE §3.2). The gate is at the **dispatch boundary** (the place that calls `WorkerPool.Enqueue`), not inside each worker.

### 1.3 Decisions resolved at v0.1.0

[NORMATIVE] These were open before this contract was authored. Each is resolved here.

- **Implementation foundation:** **Hybrid.** Smart owns its own `Thread` instances (explicit lifecycle, no sharing with .NET's `ThreadPool`). Smart uses `System.Collections.Concurrent.ConcurrentQueue<T>`, `System.Threading.CancellationToken`/`CancellationTokenSource`, and `Interlocked` operations from the BCL for the data and signaling primitives.
- **Cancellation model:** **`CancellationToken`-based cooperative.** Workers check the token at known yield points and exit gracefully.
- **Backpressure for bounded queues:** **Drop-oldest.** When a bounded queue is full, the oldest pending entry is discarded to make room for the new one.
- **Worker error recovery:** **Restart with retry budget N=3 per form session.** After budget exhausted, worker terminates permanently and the dependent subsystem is notified via the registry event.
- **Snapshot publish shape:** **Two-buffer with atomic reference swap** via `Interlocked.Exchange`. Triple-buffer is not adopted at v0.1.0; the upgrade path is noted in §11.
- **Worker registration:** **Centralized.** Every worker registers itself with `WorkerLifecycleRegistry` at construction and deregisters on clean exit.

---

## SECTION 2: WORKER POOL

### 2.1 Behavioral contract

[NORMATIVE] `WorkerPool` is the per-form-session pool. It owns its threads, distributes work, and cooperates with `WorkerLifecycleRegistry` for shutdown.

Public surface (sketch — exact API tuned during implementation):

```
public sealed class WorkerPool : IDisposable
{
    public WorkerPool(string name, int workerCount, CancellationToken external);
    public int WorkerCount { get; }
    public bool IsRunning { get; }

    // Enqueue a unit of work; runs on some worker; never blocks the caller.
    public void Enqueue(Action<CancellationToken> work);

    // Cancel + join all workers; called by Smart's DeInitGlobal via the registry.
    public void Dispose();
}
```

[NORMATIVE] `Enqueue` MUST NOT block. If the underlying queue is bounded and full, the drop-oldest policy applies (§5). Callers do not opt out.

[NORMATIVE] One `WorkerPool` instance serves all of Smart in v0.1.0 (the single work-stealing pool from ARCHITECTURE §2.1 OD-4). The pool is constructed in `InitGlobal` and disposed in `DeInitGlobal`.

### 2.2 Worker loop

[NORMATIVE] Each worker thread runs the following loop:

```
while (!cancellationToken.IsCancellationRequested)
{
    if (queue.TryDequeue(out var work))
    {
        try
        {
            work(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown; loop ends on next IsCancellationRequested check
        }
        catch (Exception ex)
        {
            OnWorkerException(workerName, ex);   // §6: triggers retry-budget logic
            if (retryBudgetExhausted) return;
        }
    }
    else
    {
        // idle: short SpinWait or Thread.Sleep(0) to yield
    }
}
RegistryDeregisterSelf();
```

[NORMATIVE] Workers MUST yield to cancellation:
- At the top of each loop iteration (between dequeues).
- Inside long-running tasks at known yield points (the consuming subsystem decides when; for gradient/MCTS loops, after each step is typical).
- Before publishing results (discard the result if cancellation was signaled mid-compute).

[NORMATIVE] Cancellation latency MUST be bounded at **100 ms** at the 99th percentile. A worker that does not check the token for longer than 100 ms violates the contract; the consuming subsystem inserts checks more frequently.

[RATIONALE] 100 ms is chosen so a form swap (which calls `DeInitGlobal` synchronously from the registry) does not introduce visible UI lag. This is a stated bound traceable to a stated reason (UI responsiveness during form switches), per FORM-SPEC §5.4.

### 2.3 Why "own threads," not `Task.Run` or `ThreadPool`

[RATIONALE]
- **`ThreadPool` is shared** with the .NET runtime, TerraTech itself, and any other mod in the process. Smart cannot guarantee its sizing formula `Math.Max(2, ProcessorCount-2)` on a shared pool. Smart cannot guarantee normal-priority semantics on a shared pool. Smart cannot enumerate "all my workers" on a shared pool for `DeInitGlobal`.
- **`Task.Run` with custom `TaskScheduler`** is closer but adds an indirection — every task allocates a `Task` object, scheduler picks a worker, abstractions stack. Direct `Thread` + `ConcurrentQueue` is fewer moving parts.
- **Own threads with `ConcurrentQueue` for data** gives Smart the lifecycle control it needs without reinventing data structures.

This trade is the "hybrid" choice from FORM-SPEC §7 OD-4 (resolved v0.2.2).

---

## SECTION 3: CANCELLATION MODEL

### 3.1 Token hierarchy

[NORMATIVE] Smart owns a **root `CancellationTokenSource`** created in `InitGlobal`. Every worker, every `Enqueue`d work item, and every long-running async operation across Smart receives a `CancellationToken` derived from this root.

[NORMATIVE] `DeInitGlobal` calls `rootCts.Cancel()`. This propagates synchronously through every linked token. Workers observe the cancellation at their next check point and exit.

[NORMATIVE] Subsystems MAY create **linked CTS** for sub-scopes (e.g., a single perception-update operation that has its own timeout). Linked CTS MUST be linked to the root via `CancellationTokenSource.CreateLinkedTokenSource(root.Token, …)`. Linked CTS MUST be disposed when the sub-scope ends.

### 3.2 Forbidden patterns

[NORMATIVE — CANNOT CHANGE]
- `Thread.Abort()` is forbidden. Deprecated in modern .NET; unsafe; corrupts shared state.
- `Thread.Interrupt()` is forbidden. Throws `ThreadInterruptedException` from unpredictable points; same correctness problem as Abort.
- Polling timers (e.g., spinning on `DateTime.Now`) as cancellation substitutes are forbidden. They miss cancellation if the worker is mid-compute.

[NORMATIVE] All long-running compute paths MUST take a `CancellationToken` parameter and check it.

### 3.3 OperationCanceledException discipline

[NORMATIVE] When a worker observes cancellation mid-compute, it MUST throw `OperationCanceledException(cancellationToken)`. The worker loop catches `OperationCanceledException` and treats it as a clean exit signal, not as an error. This MUST NOT trigger the retry budget.

[RATIONALE] Distinguishing cancellation from genuine exceptions keeps the retry budget reserved for unexpected failures.

---

## SECTION 4: DOUBLE-BUFFER PRIMITIVE

### 4.1 Behavioral contract

[NORMATIVE] `DoubleBuffer<T>` where `T` is an immutable reference type:

```
public sealed class DoubleBuffer<T> where T : class
{
    public DoubleBuffer(T initial);
    public T Read();                  // returns the currently-published snapshot
    public void Write(T newSnapshot); // atomically swaps to newSnapshot; previous read pointer remains valid for in-flight readers
}
```

Implementation: a single `volatile T current;` field, written via `Interlocked.Exchange`, read directly. The "back buffer" is implicitly whatever the writer is constructing before the swap.

[NORMATIVE] `T` MUST be immutable. Acceptable shapes (constrained by .NET Framework 4.6.1 + C# 7.3 — see [verification note below](#41a-net-and-c-version-constraints)):
- A `sealed class` with `readonly` fields set in the constructor. (`init`-only setters are a C# 9 feature and are NOT available.)
- A `readonly struct` if it fits in a pointer-sized atomic operation; otherwise use a sealed class wrapper.
- Collections inside `T` MUST be treated as immutable by discipline: typed as `IReadOnlyList<U>` / `IReadOnlyDictionary<K,V>` (BCL interfaces; available in 4.6.1), backed by a `List<U>` / `Dictionary<K,V>` that the writer fills in the constructor and never references after publishing. Element mutation by writer or reader is a contract violation; the type system does not prevent it, but code review does. (Compile-time immutable types — `ImmutableArray<U>`, `ImmutableDictionary<K,V>` — would require adding the `System.Collections.Immutable` NuGet package; v0.1.0 elects to stay BCL-only.)

### 4.1a Unity, .NET, and C# version constraints

[NORMATIVE] The TAC AI project targets **Unity 2018.4.13f1 LTS**, .NET Framework 4.6.1 scripting backend, and C# language version 7.3 (per `TAC_AI/TAC_AI.csproj`). Smart's threading primitives MUST stay within these constraints:

- **Allowed BCL types:** `Thread`, `CancellationToken`, `CancellationTokenSource`, `Interlocked`, `ConcurrentQueue<T>`, `ConcurrentDictionary<K,V>`, `ManualResetEventSlim`, `SemaphoreSlim`, `TaskCompletionSource<T>`, `IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`, `ValueTask` (via System.Threading.Tasks.Extensions if needed; otherwise plain `Task`).
- **Allowed Unity types** (UnityEngine 2018.4): `MonoBehaviour`, `Vector3`/`Vector2`/`Quaternion`/`Matrix4x4`, `Time.deltaTime`/`Time.timeScale`, `Transform`, `Rigidbody`, `Physics.Raycast`, `Debug.Log`/`LogWarning`/`LogError`, `GameObject.AddComponent<T>()`/`Destroy`/`DestroyImmediate`, `Gizmos`/`Handles` for editor draws, `Time.realtimeSinceStartup`.
- **Unity types NOT available** (require Unity 2019+ or specific packages): `Unity.Mathematics.float3` (use `UnityEngine.Vector3`), `Unity.Burst` attributes, `Unity.Jobs` / `IJobParallelFor` (Smart's worker pool uses `System.Threading.Thread` directly, NOT Unity Jobs), `UnityEngine.UIElements` (use IMGUI / `UnityEngine.UI`).
- **NOT available without NuGet additions:** `System.Threading.Channels.Channel<T>` (added in .NET Core 2.1; no compatible NuGet referenced in `.csproj`), `System.Collections.Immutable` types (no NuGet referenced).
- **C# 9 features NOT available:** `init`-only setters, `record` types, target-typed `new`, pattern enhancements introduced after 7.3.
- **C# 7.3 features Smart uses:** `in` parameters, `readonly struct`, `ref struct`, expression-bodied members, tuples, pattern matching, `nameof`.

[NORMATIVE] If a future Smart contract or implementation finds that BCL-only primitives are insufficient, adding NuGet dependencies is a contract revision discussed before the addition lands. v0.1.0 stays BCL-only.

[NORMATIVE] Mutating a snapshot after publish is **undefined behavior**. The writer MUST construct a fresh snapshot for each `Write`.

### 4.2 Reader semantics

[NORMATIVE] `Read` returns a snapshot pointer. The reader holds the pointer for as long as it needs; subsequent writes do not invalidate it (the writer published a new pointer; the reader still references the old one). Readers are inherently eventually consistent — a reader running across two ticks may see snapshot N on first read and snapshot N+1 (or N+2) on second read.

### 4.3 Memory ordering

[NORMATIVE] `Interlocked.Exchange` provides full-fence semantics on .NET. Writes to the `T` instance's fields that complete before `Write` are observable to any subsequent `Read` on any thread. No additional barriers required.

[RATIONALE] Triple-buffering reduces contention when readers are mid-read at the moment of write. In Smart's pattern (workers read once at the top of a compute scope), the contention window is sub-microsecond. Two-buffer is sufficient and simpler.

---

## SECTION 5: BOUNDED QUEUE (DROP-OLDEST)

### 5.1 Behavioral contract

[NORMATIVE] `BoundedQueue<T>` is a thread-safe FIFO queue with a stated capacity and drop-oldest overflow policy:

```
public sealed class BoundedQueue<T>
{
    public BoundedQueue(int capacity);
    public int Capacity { get; }
    public int ApproximateCount { get; }
    public void Enqueue(T item);           // drop oldest if full; never blocks
    public bool TryDequeue(out T item);    // false if empty
}
```

`Enqueue` never returns failure. If `ApproximateCount >= Capacity` at enqueue time, the queue dequeues the oldest entry (discarding it silently) before enqueueing the new one.

[NORMATIVE] Capacity MUST be passed by the consuming subsystem. There is no default. The capacity is a per-usage decision based on the subsystem's workload and traces to a stated reason (per FORM-SPEC §5.4).

### 5.2 Why drop-oldest

[RATIONALE] Smart's optimizer requests are time-sensitive. A tactical-positioning request enqueued 50 ms ago, computing on a 50-ms-old world snapshot, produces a result the caller no longer wants — by the time it lands the world has moved on. Drop-oldest favors freshness.

For request streams that should NOT drop (e.g., one-shot user-initiated actions), the consuming subsystem uses a different primitive — a `ConcurrentQueue<T>` directly, paired with `ManualResetEventSlim` for consumer signaling. `BoundedQueue<T>` is specifically for the drop-oldest semantics. (`System.Threading.Channels.Channel<T>` is NOT available in the .NET Framework 4.6.1 target; see §4.1a.)

### 5.3 Drop telemetry

[NORMATIVE] `BoundedQueue<T>` exposes a `DroppedCount` counter (incremented on each silent drop). The Diagnostics subsystem (when authored) reads this for the starved-frame log (ARCHITECTURE §4 E3).

[NORMATIVE] Silent drops with no observable signal are forbidden per ARCHITECTURE §4. `DroppedCount > 0` MUST be visible to operators via diagnostics. The "silent" in "silent drop" refers to the producer — the producer does not block or fail — not to the system; the drop is loud to whoever reads `DroppedCount`.

---

## SECTION 6: WORKER ERROR RECOVERY

### 6.1 The retry-budget protocol

[NORMATIVE] Each worker has a per-form-session retry budget of **N=3**. Sequence on an uncaught (non-`OperationCanceledException`) exception:

1. The worker-loop wrapper catches the exception.
2. The wrapper logs `WARNING` via `DebugTAC_AI.LogWarning`: `"Smart Worker '<name>' threw <type>: <message>. Retry <count>/3."` plus stack trace.
3. If `retryCount < 3`: increment `retryCount`, restart the worker thread, continue.
4. If `retryCount >= 3`: log `ERROR` via `DebugTAC_AI.LogError`: `"Smart Worker '<name>' exceeded retry budget; terminated permanently."`. Do NOT restart. Notify the worker lifecycle registry (§7) which raises a `WorkerTerminated` event.

[NORMATIVE] The retry budget resets on `InitGlobal` (form activation). Across a single form session, total retries per worker is at most 3.

[NORMATIVE] **N=3 is a stated bound.** Rationale traceable to: "3 retries catches transient issues (one-off bad snapshots, single-tick concurrency edge cases) without permitting infinite restart loops. A persistent bug exhausts the budget within seconds." Per FORM-SPEC §5.4, this is the kind of bound that needs a stated reason; the stated reason is recorded here. Future measurement MAY revise N; the bound is provisional.

### 6.2 Subsystem fallback hook

[NORMATIVE] Subsystems consuming workers MUST register a **fallback** with the worker registry: a callback that fires when a worker the subsystem owns terminates permanently. The fallback decides what to do:
- Fall back to last-known-good output and degrade.
- Fall back to a heuristic default.
- Mark the subsystem itself as degraded and propagate (e.g., Planning marks itself unavailable; consumers of Planning see no plan and use defaults).

[NORMATIVE] Subsystems that fail to register a fallback are an invariant violation visible at construction time — `WorkerPool.Enqueue` accepts the work, but the registry requires the producer to identify itself and provide a fallback before the work begins. (Implementation detail tuned during code; the spec requires the discipline.)

---

## SECTION 7: WORKER LIFECYCLE REGISTRY

### 7.1 Behavioral contract

[NORMATIVE] `WorkerLifecycleRegistry` is a process-wide (form-session-wide) tracker of live workers. Behavioral surface:

```
public static class WorkerLifecycleRegistry
{
    public static void Register(WorkerHandle worker);     // at construction
    public static void Deregister(WorkerHandle worker);   // on clean exit
    public static IReadOnlyList<WorkerHandle> Live();     // snapshot for DeInitGlobal
    public static void CancelAllAndJoin(TimeSpan timeout); // DeInitGlobal calls this
}
```

`WorkerHandle` carries the worker name, its `CancellationTokenSource`, a reference to the `Thread`, and a callback for permanent-termination notification.

### 7.2 DeInitGlobal cooperation

[NORMATIVE] `Smart.DeInitGlobal` MUST call `WorkerLifecycleRegistry.CancelAllAndJoin(TimeSpan.FromSeconds(2))`. Sequence:

1. Snapshot the live workers list.
2. For each live worker, call `worker.CancellationSource.Cancel()`.
3. For each live worker, `worker.Thread.Join(timeout / workerCount)` (proportional share of the total timeout).
4. After the join phase, verify the registry is empty. Any straggler is a violation; log an `ERROR` naming the worker.
5. Dispose the root `CancellationTokenSource`.

[NORMATIVE] The 2-second total timeout is a stated bound (FORM-SPEC §5.4). Rationale: a form swap that takes longer than 2 seconds to release workers is a user-visible UI hang and is itself a bug; the bound forces the bug to be visible.

### 7.3 Mid-session worker churn

[NORMATIVE] Workers MAY be added or removed during a session (e.g., a subsystem activates a new worker when the player enters multiplayer). The registry tolerates this — `Register` and `Deregister` are thread-safe and may be called any time the form is active.

[NORMATIVE] Workers MUST NOT be registered after `CancelAllAndJoin` has begun. Late registration is an invariant violation; the registry rejects it loudly.

---

## SECTION 8: MARSHALLING PATTERNS

### 8.1 Pattern A — Main thread observes; worker reads

[INFORMATIVE EXAMPLE]

```
// Main thread, in a damage event handler:
void OnHit(ManDamage.DamageInfo info)
{
    var tankPos = info.tank.boundsCentreWorldNoCheck;
    var tankVel = info.tank.rbody.velocity;
    var observation = new TechObservation(tankPos, tankVel, info.damage, /* ... */);
    worldModelDoubleBuffer.Write(BuildUpdatedSnapshot(observation));
}

// Worker, in perception loop:
void PerceptionWorker_Step(CancellationToken ct)
{
    var snapshot = worldModelDoubleBuffer.Read();
    // process snapshot... never reference info.tank or any TerraTech engine object.
    ct.ThrowIfCancellationRequested();
    var updatedBelief = ComputeBeliefUpdate(snapshot);
    beliefDoubleBuffer.Write(updatedBelief);
}
```

[NORMATIVE] Snapshot construction MUST copy values out of engine objects on the main thread. Workers MUST NOT hold references to engine objects.

### 8.2 Pattern B — Worker computes; main thread consumes

[INFORMATIVE EXAMPLE]

```
// Worker, in strategic planner loop:
void StrategicWorker_Step(CancellationToken ct)
{
    var snapshot = beliefDoubleBuffer.Read();
    var plan = ComputeStrategicPlan(snapshot, ct);  // long-running; checks ct periodically
    ct.ThrowIfCancellationRequested();
    currentPlanDoubleBuffer.Write(plan);
}

// Main thread, in Operations:
void Operations(TankAIHelper helper, bool host)
{
    if (!host) return;  // §3.2 host-authority gate
    var plan = currentPlanDoubleBuffer.Read();
    ApplyPlanToTacticalGoals(plan, helper);
}
```

### 8.3 Pattern C — Main thread requests; worker computes; result published

[INFORMATIVE EXAMPLE]

```
// Main thread:
void RequestTacticalUpdate(TechId techId, GoalState goal)
{
    var req = new TacticalRequest(techId, goal);
    tacticalRequestQueue.Enqueue(req);  // BoundedQueue; drops oldest if saturated
    workerPool.Enqueue(ct => ProcessTacticalRequest(ct));
}

// Worker:
void ProcessTacticalRequest(CancellationToken ct)
{
    if (!tacticalRequestQueue.TryDequeue(out var req)) return;  // someone else got it
    var snapshot = beliefDoubleBuffer.Read();
    var solution = SolveTactical(req, snapshot, ct);
    tacticalSolutionsPerTech[req.TechId].Write(solution);
}
```

### 8.4 What this pattern set does NOT cover

[NORMATIVE] These three patterns are the canonical Smart usages. Other patterns (request-response with completion, fan-out fan-in, pipeline) are permitted but their helper implementations are owned by the consuming subsystem, not by Threading. Threading provides the primitives (`WorkerPool`, `BoundedQueue`, `DoubleBuffer`, `CancellationToken`); composition is the consumer's responsibility.

---

## SECTION 9: FILE LAYOUT

[NORMATIVE] `TAC_AI/AI/Forms/Smart/Threading/` contains seven files:

| File | Owns |
|---|---|
| `WorkerPool.cs` | The pool, the worker loop, the shared request queue, the `Enqueue` surface, retry-budget enforcement, Dispose protocol. |
| `WorkerLifecycleRegistry.cs` | `WorkerHandle` type, registration, deregistration, `CancelAllAndJoin`, straggler detection. |
| `DoubleBuffer.cs` | `DoubleBuffer<T>` and its memory-ordering invariants. |
| `BoundedQueue.cs` | `BoundedQueue<T>` with drop-oldest, `DroppedCount` telemetry, queue-name labeling. |
| `CancellationHelpers.cs` | Linked-token helpers (`CreateLinked`), `RunWithTimeout`, `ThrowIfCancelled` discipline wrapper. |
| `MarshallingPatterns.cs` | Documented pattern examples + helper methods (`BuildAndPublish<T>` for Pattern A, `EnqueueRequestAndCompute<T>` for Pattern C). |
| `ThreadingDiagnostics.cs` | Event hub (`WorkerStarted`, `WorkerTerminated`, `WorkerException`, `QueueDepthSampled`, `WorkerIdle`, `RequestsDropped`) + `TerminationReason` enum + `InstallDefaultHandlers` routing to `DebugTAC_AI`. |

Per [FORM-SPECIFICATION.md §5.10](FORM-SPECIFICATION.md#section-5-ai-collaborator-directives), seven files for a foundational subsystem sits at the upper end of the ~3–7 range. Justification: each file owns one coherent primitive with non-overlapping responsibility. `ThreadingDiagnostics` was separated from `WorkerLifecycleRegistry` because event hub and registry are different concerns (lifecycle tracking vs. event publication); folding them would mix orthogonal responsibilities into one file.

---

## SECTION 10: DIAGNOSTICS INTEGRATION

[NORMATIVE] Threading exposes diagnostic events as a static event surface that the Diagnostics subsystem subscribes to when authored:

```
public static class ThreadingDiagnostics
{
    public static event Action<string> WorkerStarted;
    public static event Action<string, TerminationReason> WorkerTerminated;
    public static event Action<string, Exception, int> WorkerException;
    public static event Action<string, int> QueueDepthSampled;
    public static event Action<string, TimeSpan> WorkerIdle;
    public static event Action<string, int> RequestsDropped;  // queue name, drop count
}
```

[NORMATIVE] Threading itself does NOT log these events directly. The Diagnostics subsystem (when authored) registers handlers and decides what to do (log line, counter increment, alert).

[INFORMATIVE] If Diagnostics is not yet authored at workflow step 1.4 (it is opportunistic per WORKFLOW.md §3), Threading registers default handlers that call `DebugTAC_AI.Log` at INFO level for `WorkerStarted`/`WorkerTerminated` and `DebugTAC_AI.LogWarning` for `WorkerException` and `RequestsDropped`. These defaults are replaced when Diagnostics takes ownership.

---

## SECTION 11: OPEN ITEMS

[OPEN] **Work-stealing upgrade trigger.** v0.1.0 uses a single shared `ConcurrentQueue<T>` for work distribution. If measurement (workflow step 1.11 self-play; or earlier profiling) shows queue contention as a bottleneck, the upgrade path is per-worker Chase-Lev deques with work-stealing on idle. Trigger: contention measured above N% (specific threshold from profiling). Resolution: revise §2 implementation at that point.

[OPEN] **Per-queue capacity values.** Threading provides the `BoundedQueue<T>` primitive with mandatory capacity argument. The specific capacity for each consumer (Planning's tactical-request queue, Pathing's request queue, etc.) is decided per-usage in the consuming contract. No capacity is pre-committed here.

[OPEN] **Retry-budget revision.** N=3 is stated and reasoned (§6.1). Measurement during workflow steps 1.5–1.9 (substantive workloads) may show N should be 2 (faster fail-loud) or 5 (more tolerance for genuinely transient issues). Open until that measurement.

[OPEN] **Triple-buffer trigger.** v0.1.0 uses two-buffer. If reader contention (writer-during-read collisions) is observed as a measurable issue, the upgrade path is triple-buffering. The trigger is empirical.

---

## SECTION 12: WHAT THIS CONTRACT IS NOT

[INFORMATIVE]

This contract is not the implementation. It is the contract the implementation satisfies. Specific algorithm choices for the worker loop's idle-handling (SpinWait spin count, Sleep duration) are implementation details decided in code.

This contract does not own RNG. Per [ARCHITECTURE.md §3.2](ARCHITECTURE.md#32-mp-hostclient-gating), Smart is host-authority and ambient RNG is permitted. Each subsystem owns its own RNG instance.

This contract does not own work content. What a worker computes is owned by the consuming subsystem (World's perception loop, Planning's optimizer, Pathing's trajectory solver, etc.). Threading provides the building blocks for *how* workers run, not *what* they compute.

This contract does not own scheduling policy beyond "shared queue, FIFO, work-stealing upgrade later." Smart does not need priority scheduling between subsystems at v0.1.0; each work item is treated equally. If measurement shows a need for priority scheduling (e.g., tactical requests preempting strategic), the upgrade path is a multi-priority queue, owned by this contract at that point.

---

END OF THREADING-CONTRACT.md v0.1.0
