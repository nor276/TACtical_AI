using TAC_AI.AI.Engine;

namespace TAC_AI.AI.Behaviors
{
    /// <summary>
    /// Base for behavior modules. Cuts the IBehavior boilerplate (default no-op install/teardown) and gives
    /// wrappers the concrete-context bridge: the existing R*/B* handlers take (helper, tank, [mind,] ref
    /// EControlOperatorSet), so a wrapper casts the context back to AIContext to reach Helper/Tank/Mind and a
    /// by-ref handle on the live drive operator. New-from-scratch behaviors should prefer the IAIContext
    /// surface directly; wrappers bridge the preserved legacy logic.
    /// </summary>
    public abstract class BehaviorBase : IBehavior
    {
        public abstract AIBehaviorCategory Category { get; }
        public abstract VehicleClass VehicleClass { get; }
        public abstract string Id { get; }
        public abstract void Tick(IAIContext ctx);
        public virtual void OnInstall(IAIContext ctx) { }
        public virtual void OnRemove(IAIContext ctx) { }

        protected static AIContext C(IAIContext ctx) => (AIContext)ctx;
    }
}
