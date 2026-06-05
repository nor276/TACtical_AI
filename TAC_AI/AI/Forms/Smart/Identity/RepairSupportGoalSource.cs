using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    /// <summary>
    /// P6 Item 26 (REV 5/7): RepairSupport goal source. Scans friendlies for the lowest
    /// HpFraction within <see cref="CandidateRange"/>; if any are below
    /// <see cref="DamagedThreshold"/>, orbits the most-damaged ally at
    /// <see cref="OrbitRadius"/>. No candidate → falls back to Hunter goal source
    /// (sustain combat presence).
    ///
    /// HP source: <see cref="SmartRuntime.Health"/> sidecar populated by
    /// <c>SmartForm.ObserveWorldTechsIfDue</c> (per-tank block-survival ratio matching
    /// TankAIHelper.cs:3267 production semantic).
    ///
    /// P11 T6 Item 68 (post-decompile): facing-threat refinement is now LIVE. The orbit
    /// places the repair tech opposite the average of recent attacker bearings (read from
    /// <see cref="DamageHintBuffer"/>.TryGetRecent), so the ally is between the repairer
    /// and the threat direction. Falls back to plain perpendicular orbit when no attacker
    /// hints are available for the damaged ally.
    ///
    /// Defaults preserving v0.1: gated by <see cref="SmartIdentityTuning.EnableRepairSupport"/>
    /// (default false). With OFF, no tech classifies as RepairSupport so this goal source
    /// never runs.
    /// </summary>
    public sealed class RepairSupportGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.RepairSupport;

        public const float CandidateRange = 80f;
        public const float OrbitRadius = 12f;
        public const float DamagedThreshold = 0.75f;
        public const float OrbitSpeed = 0.6f;
        public const float LookAheadSec = 1.0f;

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            // Scan friendlies for lowest-HpFraction within CandidateRange.
            // Field names match verified shape: b.Id, b.Team, b.PositionMean per BeliefState.cs:97-111.
            // Friendliness check mirrors TacticalOptimizer.cs:141 pattern (pair.Value.Team.Equals(ownTeam)).
            BeliefState candidate = null;
            float lowestHp = DamagedThreshold;
            foreach (var kv in beliefs.ByTech)
            {
                var b = kv.Value;
                if (!b.Team.Equals(ownBelief.Team)) continue;   // skip non-friendlies
                if (b.Id.Equals(ctx.SelfTechId)) continue;       // skip self
                float dSq = (b.PositionMean - ownBelief.PositionMean).sqrMagnitude;
                if (dSq > CandidateRange * CandidateRange) continue;
                float hp = SmartRuntime.Health != null ? SmartRuntime.Health.Get(b.Id) : 1f;
                if (hp < lowestHp) { lowestHp = hp; candidate = b; }
            }
            if (candidate != null)
            {
                Vector3 toAlly = candidate.PositionMean - ownBelief.PositionMean;
                if (toAlly.sqrMagnitude < 1e-4f) toAlly = Vector3.forward;

                // P11 T6 Item 68: facing-threat orbit. Average the world-space attacker
                // bearings from the ally's recent DamageHints; place the orbit point on the
                // far side of the ally relative to that averaged bearing so the ally sits
                // between us and the threat. Falls back to perpendicular tangent (plain
                // orbit) when no attacker hints are available — preserves v0.2 behavior for
                // newly-damaged allies before the buffer has data.
                Vector3 orbitDir = Vector3.zero;
                bool haveThreatBearing = false;
                if (SmartRuntime.DamageHints != null)
                {
                    var hintBuf = new DamageHint[8];
                    int n = SmartRuntime.DamageHints.TryGetRecent(candidate.Id, hintBuf);
                    if (n > 0)
                    {
                        Vector3 sum = Vector3.zero;
                        int contributors = 0;
                        for (int i = 0; i < n; i++)
                        {
                            var h = hintBuf[i];
                            // Prefer engine-supplied impact direction (the attacker→ally vector).
                            // If absent (zero), synthesize from the attacker's last known position.
                            Vector3 attackerToAlly = h.ImpactDirectionWorld;
                            if (attackerToAlly.sqrMagnitude < 1e-4f && h.HasAttacker)
                            {
                                var aBuf = SmartRuntime.World?.GetPerTechBuffer(h.AttackerIfKnown);
                                if (aBuf != null)
                                {
                                    var aBel = aBuf.Read();
                                    attackerToAlly = candidate.PositionMean - aBel.PositionMean;
                                }
                            }
                            if (attackerToAlly.sqrMagnitude < 1e-4f) continue;
                            sum += attackerToAlly.normalized;
                            contributors++;
                        }
                        if (contributors > 0)
                        {
                            // Average attacker→ally bearing; orbitDir points FROM ally AWAY
                            // from attacker (opposite the incoming damage direction).
                            Vector3 avg = sum / contributors;
                            if (avg.sqrMagnitude > 1e-4f)
                            {
                                orbitDir = -avg.normalized;   // place me opposite the threat
                                orbitDir.y = 0f;              // keep orbit flat
                                if (orbitDir.sqrMagnitude > 1e-4f)
                                {
                                    orbitDir = orbitDir.normalized;
                                    haveThreatBearing = true;
                                }
                            }
                        }
                    }
                }
                if (!haveThreatBearing)
                {
                    // Fallback: perpendicular tangent (the pre-P11 plain orbit shape).
                    orbitDir = Vector3.Cross(toAlly.normalized, Vector3.up);
                }

                Vector3 orbitPos = candidate.PositionMean + orbitDir * OrbitRadius;
                Vector3 toAllyFromOrbit = candidate.PositionMean - orbitPos;
                float heading = Mathf.Atan2(toAllyFromOrbit.x, toAllyFromOrbit.z);
                Vector3 velocity = (orbitPos - ownBelief.PositionMean) / LookAheadSec * OrbitSpeed;
                return new TacticalGoal(orbitPos, heading, velocity, LookAheadSec);
            }
            // No damaged ally: fall back to Hunter goal source (sustain combat presence).
            var hunter = SmartIdentityRegistry.For(SmartIdentity.Hunter);
            if (hunter != null) return hunter.Produce(ownBelief, vehicle, beliefs, ctx);

            // Defensive: registry empty — return at-current goal (safer than default(TacticalGoal)).
            return TacticalGoal.AtCurrent(ownBelief.PositionMean, ownBelief.HeadingMean);
        }
    }
}
