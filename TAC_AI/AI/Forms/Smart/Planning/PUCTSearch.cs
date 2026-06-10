using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Planning
{
    /// <summary>
    /// One step along the selection path during a PUCT iteration. Named struct instead
    /// of a ValueTuple because the project targets .NET Framework 4.6.1 where
    /// System.ValueTuple is not in the BCL — adding the NuGet would violate the
    /// project's BCL-only constraint, and a tiny readonly struct is the same shape
    /// (used identically in OnlineTrainer.TrainStepResult for the same reason).
    /// </summary>
    internal readonly struct PathStep
    {
        public readonly PUCTNode Node;
        public readonly int ActionHash;
        public PathStep(PUCTNode node, int actionHash) { Node = node; ActionHash = actionHash; }
    }

    /// <summary>
    /// PUCT (Predictor + UCT) search node. Per PLANNING-CONTRACT §2.
    /// Phase 9 (FIX-PLAN.md) — AUDIT R1 §3.6: pooled. Reset() clears mutable state so a
    /// node can be reused without re-allocating its four internal Dictionaries.
    /// </summary>
    internal sealed class PUCTNode
    {
        public StrategicState State;
        public Dictionary<int, PUCTNode> Children;     // keyed by ActionHash
        public Dictionary<int, PlanLibrary.StrategicPlan> ActionsByHash;
        public Dictionary<int, int> VisitsByAction;
        public Dictionary<int, float> ValueByAction;
        public Dictionary<int, float> PriorByAction;
        public int VisitCount;
        public bool Expanded;

        public PUCTNode()
        {
            Children = new Dictionary<int, PUCTNode>();
            ActionsByHash = new Dictionary<int, PlanLibrary.StrategicPlan>();
            VisitsByAction = new Dictionary<int, int>();
            ValueByAction = new Dictionary<int, float>();
            PriorByAction = new Dictionary<int, float>();
        }

        public void Reset(StrategicState state)
        {
            State = state;
            Children.Clear();
            ActionsByHash.Clear();
            VisitsByAction.Clear();
            ValueByAction.Clear();
            PriorByAction.Clear();
            VisitCount = 0;
            Expanded = false;
        }
    }

    /// <summary>
    /// PUCT search. Prior pulls from <see cref="SmartRuntime.IntentSidecar"/>'s published
    /// opponent-intent snapshots when available; falls back to uniform when no producer
    /// has published (sidecar null, no entries for hostiles in state, or stale).
    /// </summary>
    public sealed class PUCTSearch
    {
        // Globally tuned by CMA-ES (Training §5).
        public static float CPuct = 1.4f;
        public static int NodeBudget = 5000;
        public static int MinVisitsPerAction = 5;

        private PUCTNode _root;
        private int _nodeCount;
        // System.Random — fully qualified to avoid UnityEngine.Random ambiguity from
        // the using UnityEngine import. Reserved for stochastic policy sampling if/when
        // we switch from deterministic argmax to a softmax over visit counts at the root.
        private readonly System.Random _rng = new System.Random();

        // Phase 9 (FIX-PLAN.md): per-Search node pool. Search rebuilds the tree every
        // call (no tree reuse at v0.1.0 — see Search()), and at the budget of 5000 nodes
        // that meant 5000 × (PUCTNode + 4 × Dictionary) allocs per strategic tick. The
        // pool retains every node from the previous tick and recycles them in Acquire.
        private readonly Stack<PUCTNode> _nodePool = new Stack<PUCTNode>(256);
        private readonly List<PUCTNode> _allAllocatedNodes = new List<PUCTNode>(256);

        private PUCTNode Acquire(StrategicState state)
        {
            PUCTNode n;
            if (_nodePool.Count > 0) { n = _nodePool.Pop(); n.Reset(state); }
            else
            {
                n = new PUCTNode();
                n.Reset(state);
                _allAllocatedNodes.Add(n);
            }
            return n;
        }

        private void ReleaseAllToPool()
        {
            _nodePool.Clear();
            for (int i = 0; i < _allAllocatedNodes.Count; i++) _nodePool.Push(_allAllocatedNodes[i]);
        }

        /// <summary>Run search from a fresh root. Tree is rebuilt every tick (no reuse); the
        /// per-tick node pool from <see cref="ReleaseAllToPool"/> keeps that cheap.</summary>
        public PlanLibrary.StrategicPlan Search(StrategicState rootState, CancellationToken cancellation)
        {
            // Recycle all nodes allocated in any previous Search call back to the pool.
            ReleaseAllToPool();
            _root = Acquire(rootState);
            _nodeCount = 1;

            while (_nodeCount < NodeBudget && !cancellation.IsCancellationRequested)
            {
                if (!Iterate(_root, cancellation)) break;
                if (AllActionsVisitedSufficiently(_root)) break;
            }

            // Pick child by visit count.
            int bestActionHash = -1;
            int bestVisits = -1;
            foreach (var pair in _root.VisitsByAction)
            {
                if (pair.Value > bestVisits) { bestVisits = pair.Value; bestActionHash = pair.Key; }
            }
            if (bestActionHash < 0)
                return PlanLibrary.StrategicPlan.Hold;

            return _root.ActionsByHash[bestActionHash];
        }

        /// <summary>One iteration: select → expand → simulate → backpropagate.</summary>
        private bool Iterate(PUCTNode root, CancellationToken cancellation)
        {
            var path = new List<PathStep>(8);
            var current = root;

            while (current.Expanded && current.ActionsByHash.Count > 0)
            {
                if (cancellation.IsCancellationRequested) return false;
                int actionHash = SelectAction(current);
                if (actionHash < 0) return false;
                path.Add(new PathStep(current, actionHash));
                if (current.Children.TryGetValue(actionHash, out var child) && child != null)
                {
                    current = child;
                }
                else
                {
                    // Expand: rollout from this state with the chosen action.
                    var action = current.ActionsByHash[actionHash];
                    var nextState = StrategicRollout.Simulate(current.State, action);
                    var newChild = Acquire(nextState);
                    current.Children[actionHash] = newChild;
                    _nodeCount++;
                    float value = StrategicValueFunction.Evaluate(nextState);
                    Backpropagate(path, value);
                    return true;
                }
            }

            // Reached an unexpanded leaf: expand.
            if (!current.Expanded)
            {
                Expand(current);
                if (current.ActionsByHash.Count == 0)
                {
                    // Terminal-ish state. Evaluate and backprop.
                    float value = StrategicValueFunction.Evaluate(current.State);
                    Backpropagate(path, value);
                    return true;
                }
                // Roll out one action immediately for value seeding.
                int seedHash = FirstAction(current);
                var seedAction = current.ActionsByHash[seedHash];
                var rolloutState = StrategicRollout.Simulate(current.State, seedAction);
                var rolloutChild = Acquire(rolloutState);
                current.Children[seedHash] = rolloutChild;
                _nodeCount++;
                path.Add(new PathStep(current, seedHash));
                float rolloutValue = StrategicValueFunction.Evaluate(rolloutState);
                Backpropagate(path, rolloutValue);
            }
            return true;
        }

        private void Expand(PUCTNode node)
        {
            var actions = PlanLibrary.LegalActions(node.State);
            if (actions.Count == 0) { node.Expanded = true; return; }

            // Aggregate hostile intent from the sidecar so we can bias plan selection
            // toward the right macro-response. Null-safe: missing sidecar / no entries
            // collapses to a flat distribution which makes the prior uniform.
            float aggressing, retreating, flanking, repositioning, holding, idle;
            int matched = SampleHostileIntent(node.State,
                out aggressing, out retreating, out flanking, out repositioning, out holding, out idle);

            float uniform = 1f / actions.Count;
            float[] priors = new float[actions.Count];
            float sum = 0f;
            for (int i = 0; i < actions.Count; i++)
            {
                float w = matched > 0
                    ? PriorForPlan(actions[i], aggressing, retreating, flanking, repositioning, holding, idle)
                    : uniform;
                if (w < 1e-6f) w = 1e-6f;   // keep every legal action explorable
                priors[i] = w;
                sum += w;
            }
            if (sum < 1e-6f) { for (int i = 0; i < priors.Length; i++) priors[i] = uniform; sum = 1f; }
            else { for (int i = 0; i < priors.Length; i++) priors[i] /= sum; }

            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                int hash = PlanLibrary.ActionHash(a);
                if (node.ActionsByHash.ContainsKey(hash)) continue;
                node.ActionsByHash[hash] = a;
                node.VisitsByAction[hash] = 0;
                node.ValueByAction[hash] = 0f;
                node.PriorByAction[hash] = priors[i];
            }
            node.Expanded = true;
        }

        // Pull every hostile's published IntentSnapshot, average the category masses, and
        // return how many were matched (zero -> fall back to uniform). Categories use the
        // IntentCategories constants (Aggressing=0..Idle=5). One snapshot per tech; if a
        // tech isn't in the sidecar we silently skip it (producer hasn't published).
        private int SampleHostileIntent(StrategicState state,
            out float aggressing, out float retreating, out float flanking,
            out float repositioning, out float holding, out float idle)
        {
            aggressing = retreating = flanking = repositioning = holding = idle = 0f;
            var sidecar = SmartRuntime.IntentSidecar;
            if (sidecar == null || sidecar.Count == 0) return 0;

            int matched = 0;
            for (int i = 0; i < state.Hostile.Count; i++)
            {
                if (!sidecar.TryGet(state.Hostile[i].Id, out IntentSnapshot snap)) continue;
                float w = Mathf.Clamp01(snap.Confidence);
                if (w <= 0f) continue;
                switch (snap.CategoryIndex)
                {
                    case IntentCategories.Aggressing:   aggressing   += w; break;
                    case IntentCategories.Retreating:   retreating   += w; break;
                    case IntentCategories.Flanking:     flanking     += w; break;
                    case IntentCategories.Repositioning:repositioning+= w; break;
                    case IntentCategories.Holding:      holding      += w; break;
                    case IntentCategories.Idle:         idle         += w; break;
                    default: continue;
                }
                matched++;
            }
            if (matched > 0)
            {
                float inv = 1f / matched;
                aggressing *= inv; retreating *= inv; flanking *= inv;
                repositioning *= inv; holding *= inv; idle *= inv;
            }
            return matched;
        }

        // Hand-crafted intent->plan weight. Aggressing opponents bias toward defense /
        // disengage; idle/holding opponents bias toward initiative (Engage/Skirmish);
        // Flanking opponents bias toward Bait + counter-Flank. Numbers are coarse priors
        // — PUCT visits will dominate once exploration warms.
        private static float PriorForPlan(PlanLibrary.StrategicPlan a,
            float aggressing, float retreating, float flanking,
            float repositioning, float holding, float idle)
        {
            float passive = holding + idle;
            float closing = aggressing + flanking;
            switch (a.Type)
            {
                case PlanLibrary.PlanType.EngageFocused:      return 0.5f + 0.8f * passive + 0.4f * retreating;
                case PlanLibrary.PlanType.EngageDistributed:  return 0.5f + 0.7f * passive + 0.3f * repositioning;
                case PlanLibrary.PlanType.Skirmish:           return 0.6f + 0.4f * repositioning + 0.3f * idle;
                case PlanLibrary.PlanType.Flank:              return 0.5f + 0.6f * (holding + repositioning) + 0.4f * flanking;
                case PlanLibrary.PlanType.Bait:               return 0.4f + 0.7f * aggressing + 0.5f * flanking;
                case PlanLibrary.PlanType.DefensivePerimeter: return 0.3f + 0.9f * aggressing + 0.3f * flanking;
                case PlanLibrary.PlanType.MobileScreen:       return 0.4f + 0.5f * flanking + 0.3f * repositioning;
                case PlanLibrary.PlanType.FightingRetreat:    return 0.2f + 0.9f * closing;
                case PlanLibrary.PlanType.Disengage:          return 0.2f + 0.8f * closing + 0.2f * retreating;
                case PlanLibrary.PlanType.Hold:               return 0.4f + 0.5f * (passive + retreating);
                default:                                       return 0.5f;
            }
        }

        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: dictionary enumeration order is not
        // contractually deterministic in .NET (it just happens to be insertion-order
        // since .NET Core 3). For training reproducibility, the seed action must be
        // picked by a stable rule — minimum hash. Same goes for tie-breaks in
        // SelectAction below.
        private int FirstAction(PUCTNode node)
        {
            int min = int.MaxValue;
            foreach (var k in node.ActionsByHash.Keys) if (k < min) min = k;
            return min == int.MaxValue ? -1 : min;
        }

        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: defensive TryGetValue. If Expand was
        // partial (mid-add cancellation, OOM during dict grow) the auxiliary
        // VisitsByAction / ValueByAction / PriorByAction dicts could miss a key while
        // ActionsByHash has it — a direct indexer would throw KeyNotFoundException
        // mid-iteration. TryGetValue with sensible defaults keeps the search alive.
        // Deterministic tie-break: prefer lower hash (matches FirstAction policy).
        private int SelectAction(PUCTNode node)
        {
            float bestScore = float.MinValue;
            int bestHash = -1;
            int n = node.VisitCount;
            float sqrtN = Mathf.Sqrt(Mathf.Max(1, n));

            foreach (var pair in node.ActionsByHash)
            {
                int hash = pair.Key;
                int visits = node.VisitsByAction.TryGetValue(hash, out int v) ? v : 0;
                float totalValue = node.ValueByAction.TryGetValue(hash, out float val) ? val : 0f;
                float prior = node.PriorByAction.TryGetValue(hash, out float p) ? p : 0f;
                float q = visits > 0 ? totalValue / visits : 0f;
                float exploration = CPuct * prior * sqrtN / (1f + visits);
                float score = q + exploration;
                if (score > bestScore || (score == bestScore && (bestHash < 0 || hash < bestHash)))
                {
                    bestScore = score;
                    bestHash = hash;
                }
            }
            return bestHash;
        }

        private void Backpropagate(List<PathStep> path, float value)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var step = path[i];
                var node = step.Node;
                int actionHash = step.ActionHash;
                node.VisitCount++;
                int prevVisits = node.VisitsByAction.TryGetValue(actionHash, out int v) ? v : 0;
                float prevTotal = node.ValueByAction.TryGetValue(actionHash, out float t) ? t : 0f;
                node.VisitsByAction[actionHash] = prevVisits + 1;
                node.ValueByAction[actionHash] = prevTotal + value;
            }
        }

        private bool AllActionsVisitedSufficiently(PUCTNode root)
        {
            foreach (var pair in root.VisitsByAction)
                if (pair.Value < MinVisitsPerAction) return false;
            return true;
        }
    }
}
