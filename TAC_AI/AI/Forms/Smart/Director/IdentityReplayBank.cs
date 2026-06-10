using System;
using System.Collections.Concurrent;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// Source byte on EventEnvelope. Live (0) at tee, Replay (1) when ReplayBankAction
    /// re-emits. SEPARATE from IdentityOutcome.Source byte (which packs guard ordinal +
    /// Replay nibble); no shared constant.
    /// </summary>
    public enum ReplaySource : byte
    {
        Live = 0,
        Replay = 1,
    }

    /// <summary>
    /// Wraps a typed training event with the bank-side meta. Per-envelope multiplicity
    /// cap = 2 enforced via ReplayCount.
    /// </summary>
    public readonly struct EventEnvelope<T>
    {
        public readonly byte Source;
        public readonly SmartIdentity Identity;
        public readonly T Event;
        public readonly byte ReplayCount;

        public EventEnvelope(byte source, SmartIdentity identity, T ev, byte replayCount)
        {
            Source = source;
            Identity = identity;
            Event = ev;
            ReplayCount = replayCount;
        }
    }

    /// <summary>
    /// Per-(SmartIdentity, ModelId) typed BoundedQueue ring. Four type-specialized
    /// dicts so drain dispatches without boxing on the 30 Hz trainer hot path.
    /// Capacity 4096 per cell; per-envelope multiplicity cap = 2.
    ///
    /// Tee points are at the typed-queue enqueue sites in LearningService /
    /// TargetObservationSequenceBuffer / LeadResidualRecorder / ContinuousController.
    /// Live tees stamp Source=Live (0), ReplayCount=0; ReplayBankAction.Apply re-emits
    /// with Source=Replay (1) and increments ReplayCount.
    /// </summary>
    public static class IdentityReplayBank
    {
        public const int CapacityPerCell = 4096;
        public const byte MaxReplayCount = 2;

        // 4 type-specialized dicts keyed by SmartIdentity. ModelId is implicit per dict.
        private static readonly ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<IntentEvent>>> _intentDict
            = new ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<IntentEvent>>>();
        private static readonly ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<ActionValueEvent>>> _actionValueDict
            = new ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<ActionValueEvent>>>();
        private static readonly ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<ResidualEvent>>> _residualDict
            = new ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<ResidualEvent>>>();
        private static readonly ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<ThreatEvent>>> _threatDict
            = new ConcurrentDictionary<SmartIdentity, BoundedQueue<EventEnvelope<ThreatEvent>>>();

        // TechId → SmartIdentity lookup. Set by LearningService.Init from
        // SmartRuntime.TryGetTechState. Tee sites call ResolveIdentity to stamp without
        // having to walk teams themselves.
        private static Func<TechId, SmartIdentity> _identityLookup;

        public static void SetIdentityLookup(Func<TechId, SmartIdentity> lookup)
        {
            _identityLookup = lookup;
        }

        public static SmartIdentity ResolveIdentity(TechId id)
        {
            var f = _identityLookup;
            if (f == null) return SmartIdentity.Generic;
            try { return f(id); }
            catch { return SmartIdentity.Generic; }
        }

        /// <summary>
        /// Drop all queues — called on world reset and Director shutdown.
        /// </summary>
        public static void Reset()
        {
            _intentDict.Clear();
            _actionValueDict.Clear();
            _residualDict.Clear();
            _threatDict.Clear();
        }

        // ---- Tee writers (call from typed-queue enqueue sites). Source=Live, RC=0. ----

        public static void TeeIntent(SmartIdentity identity, IntentEvent ev)
        {
            var q = _intentDict.GetOrAdd(identity, _ => new BoundedQueue<EventEnvelope<IntentEvent>>(CapacityPerCell, "ReplayBank.Intent"));
            q.Enqueue(new EventEnvelope<IntentEvent>((byte)ReplaySource.Live, identity, ev, 0));
        }

        public static void TeeActionValue(SmartIdentity identity, ActionValueEvent ev)
        {
            var q = _actionValueDict.GetOrAdd(identity, _ => new BoundedQueue<EventEnvelope<ActionValueEvent>>(CapacityPerCell, "ReplayBank.ActionValue"));
            q.Enqueue(new EventEnvelope<ActionValueEvent>((byte)ReplaySource.Live, identity, ev, 0));
        }

        public static void TeeResidual(SmartIdentity identity, ResidualEvent ev)
        {
            var q = _residualDict.GetOrAdd(identity, _ => new BoundedQueue<EventEnvelope<ResidualEvent>>(CapacityPerCell, "ReplayBank.Residual"));
            q.Enqueue(new EventEnvelope<ResidualEvent>((byte)ReplaySource.Live, identity, ev, 0));
        }

        public static void TeeThreat(SmartIdentity identity, ThreatEvent ev)
        {
            var q = _threatDict.GetOrAdd(identity, _ => new BoundedQueue<EventEnvelope<ThreatEvent>>(CapacityPerCell, "ReplayBank.Threat"));
            q.Enqueue(new EventEnvelope<ThreatEvent>((byte)ReplaySource.Live, identity, ev, 0));
        }

        // ---- BankFullness — read by ActionPicker / ReplayBankAction to gate SKIPPED-INSUFFICIENT. ----

        public static int BankFullness(SmartIdentity identity, ModelId model)
        {
            switch (model)
            {
                case ModelId.Intent:
                    return _intentDict.TryGetValue(identity, out var qi) ? qi.ApproximateCount : 0;
                case ModelId.ActionValue:
                    return _actionValueDict.TryGetValue(identity, out var qa) ? qa.ApproximateCount : 0;
                case ModelId.TrajectoryResidual:
                    return _residualDict.TryGetValue(identity, out var qr) ? qr.ApproximateCount : 0;
                case ModelId.ThreatAssessment:
                    return _threatDict.TryGetValue(identity, out var qt) ? qt.ApproximateCount : 0;
            }
            return 0;
        }

        // ---- Drain helpers used by ReplayBankAction. Each pops up to count envelopes
        //      from the per-(identity, model) cell, increments ReplayCount, drops cap-
        //      exceeded envelopes, and dispatches the underlying event into the model's
        //      EventQueue. Caller holds model.SaveMutex via Monitor.TryEnter. ----

        public static int DrainIntent(SmartIdentity identity, OpponentIntentClassifier model, int count)
        {
            if (model == null) return 0;
            if (!_intentDict.TryGetValue(identity, out var q)) return 0;
            int drained = 0;
            for (int i = 0; i < count; i++)
            {
                EventEnvelope<IntentEvent> env;
                if (!q.TryDequeue(out env)) break;
                byte nextRc = (byte)(env.ReplayCount + 1);
                if (nextRc > MaxReplayCount) continue; // silently drop
                model.EventQueue.Enqueue(env.Event);
                drained++;
                // Re-enqueue with bumped ReplayCount + Source=Replay so future drains see it.
                q.Enqueue(new EventEnvelope<IntentEvent>((byte)ReplaySource.Replay, identity, env.Event, nextRc));
            }
            return drained;
        }

        public static int DrainActionValue(SmartIdentity identity, ActionValueEstimator model, int count)
        {
            if (model == null) return 0;
            if (!_actionValueDict.TryGetValue(identity, out var q)) return 0;
            int drained = 0;
            for (int i = 0; i < count; i++)
            {
                EventEnvelope<ActionValueEvent> env;
                if (!q.TryDequeue(out env)) break;
                byte nextRc = (byte)(env.ReplayCount + 1);
                if (nextRc > MaxReplayCount) continue;
                model.EventQueue.Enqueue(env.Event);
                drained++;
                q.Enqueue(new EventEnvelope<ActionValueEvent>((byte)ReplaySource.Replay, identity, env.Event, nextRc));
            }
            return drained;
        }

        public static int DrainResidual(SmartIdentity identity, TrajectoryResidualModel model, int count)
        {
            if (model == null) return 0;
            if (!_residualDict.TryGetValue(identity, out var q)) return 0;
            int drained = 0;
            for (int i = 0; i < count; i++)
            {
                EventEnvelope<ResidualEvent> env;
                if (!q.TryDequeue(out env)) break;
                byte nextRc = (byte)(env.ReplayCount + 1);
                if (nextRc > MaxReplayCount) continue;
                model.EventQueue.Enqueue(env.Event);
                drained++;
                q.Enqueue(new EventEnvelope<ResidualEvent>((byte)ReplaySource.Replay, identity, env.Event, nextRc));
            }
            return drained;
        }

        public static int DrainThreat(SmartIdentity identity, ThreatAssessmentModel model, int count)
        {
            if (model == null) return 0;
            if (!_threatDict.TryGetValue(identity, out var q)) return 0;
            int drained = 0;
            for (int i = 0; i < count; i++)
            {
                EventEnvelope<ThreatEvent> env;
                if (!q.TryDequeue(out env)) break;
                byte nextRc = (byte)(env.ReplayCount + 1);
                if (nextRc > MaxReplayCount) continue;
                model.EventQueue.Enqueue(env.Event);
                drained++;
                q.Enqueue(new EventEnvelope<ThreatEvent>((byte)ReplaySource.Replay, identity, env.Event, nextRc));
            }
            return drained;
        }
    }
}
