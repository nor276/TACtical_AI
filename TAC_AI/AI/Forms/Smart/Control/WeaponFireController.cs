using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>
    /// Aggregate decision produced each Operations tick. Carries per-weapon commits AND
    /// the primary aim point + radius (computed by the controller from the highest-priority
    /// weapon's lead solution). The engine fire-API is global, not per-weapon, so
    /// <see cref="FireAny"/> + <see cref="AimPointWorld"/> are what actually drive
    /// <c>tank.control.FireControl</c> / <c>TargetPositionWorld</c>.
    /// </summary>
    public sealed class WeaponFireDecision
    {
        public bool[] Commits;
        public bool FireAny;
        public Vector3 AimPointWorld;
        public float AimRadiusWorld;
        public TechId AimTargetId;

        public static readonly WeaponFireDecision Hold = new WeaponFireDecision
        {
            Commits = System.Array.Empty<bool>(),
            FireAny = false,
            AimPointWorld = Vector3.zero,
            AimRadiusWorld = 0f,
            AimTargetId = new TechId(-1),
        };
    }

    /// <summary>
    /// Per-tech weapon-fire decision maker. Per CONTROL-CONTRACT §10.
    /// Computes one fire-commit boolean per weapon each tick: aim check, cooldown,
    /// ammo, target alive, friendly-fire raycast, energy budget, multi-weapon coordination.
    /// Aggregates a single primary aim point for the engine-level FireControl write.
    ///
    /// State held across ticks: per-weapon target cache for hysteresis, retry counter for
    /// switching, energy reserve estimate, ammo conservation flag.
    /// </summary>
    public sealed class WeaponFireController
    {
        // Provisional thresholds per CONTROL §10. Mutable so CMA-ES (Training §5) can
        // adapt them; defaults preserve prior const semantics for non-harness callers.
        public static float SwitchValueRatio = 1.3f;
        public static int StickyTicks = 30;
        public static float FriendlyFireRadius = 5f;     // proxy for friendly footprint (v0.1.0; no bounds plumbing)
        public static float EnergyReserveThreshold = 0.2f;
        public static float AmmoConserveThreshold = 0.3f;
        public static float SalvoDamageThreshold = 50f;
        public static int SalvoMinReady = 2;
        public static float DefaultAimRadius = 1.5f;

        // Per-weapon hysteresis state (one entry per weapon index).
        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: state was keyed by belief-list slot,
        // but belief-list ordering is dictionary-iteration order which is unstable
        // across ticks (rebuilt from beliefs.ByTech each call). A weapon "sticky on
        // target A" would silently swap to whatever happened to land in that slot
        // next tick. Now key by TechId.Value so the hysteresis tracks the actual
        // target across belief-list permutations and belief-set shrinkage.
        private struct PerWeaponState
        {
            public int CurrentTargetTechIdValue; // TechId.Value of locked target; -1 = none
            public int TicksSinceSwitch;
        }
        private readonly List<PerWeaponState> _states = new List<PerWeaponState>();

        // Energy estimation (simplified — TerraTech energy access TODO).
        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5 / 2.7: previously reserve only depleted
        // inside EnforceEnergyBudget. Once it dropped below EnergyReserveThreshold every
        // energy weapon was permanently locked out for the rest of the match. Refill
        // toward 1.0 happens per Operations tick via ApplyEnergyRefillTick — driver
        // tick rate is governed by SmartForm.Operations, not by absolute time, so the
        // refill rate is expressed per call (the caller decides cadence via dt).
        private float _energyReserveFraction = 1.0f;
        public static float EnergyRefillPerSecond = 0.2f;   // Mutable for CMA-ES.
        public float EnergyReserveFraction
        {
            get => _energyReserveFraction;
            set => _energyReserveFraction = Mathf.Clamp01(value);
        }

        /// <summary>
        /// Apply energy regeneration for one driver tick. Called once per Operations
        /// frame by ContinuousController, *before* the fire-commit pass that consumes.
        /// </summary>
        public void ApplyEnergyRefillTick(float dt)
        {
            if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            _energyReserveFraction = Mathf.Clamp01(_energyReserveFraction + EnergyRefillPerSecond * dt);
        }

        /// <summary>
        /// Compute fire commits + primary aim for the next frame. Returns
        /// <see cref="WeaponFireDecision.Hold"/> when nothing can fire.
        ///
        /// <paramref name="coordinationTarget"/>: if a Coordination-assigned target id is
        /// present in <paramref name="beliefs"/>, per-weapon hysteresis is overridden per
        /// §10.6 — every weapon attempts that target first.
        /// </summary>
        public WeaponFireDecision ComputeFireCommits(
            VehicleModelSnapshot vehicle,
            KinematicState own,
            BeliefSnapshot beliefs,
            TechId? coordinationTarget,
            IReadOnlyList<Vector3> friendlyPositionsExcludingSelf,
            ITerrainMap terrain)
        {
            int wCount = vehicle.Weapons.Count;
            if (wCount == 0) return WeaponFireDecision.Hold;

            // Grow per-weapon state list if needed; trim if the tech lost weapons
            // (block destruction). Trimming avoids stale hysteresis bleeding into
            // a remounted weapon at the same index later.
            while (_states.Count < wCount) _states.Add(new PerWeaponState { CurrentTargetTechIdValue = -1, TicksSinceSwitch = StickyTicks });
            if (_states.Count > wCount) _states.RemoveRange(wCount, _states.Count - wCount);

            var commits = new bool[wCount];
            if (beliefs == null || beliefs.ByTech.Count == 0)
                return new WeaponFireDecision { Commits = commits, FireAny = false, AimPointWorld = own.PositionWorld, AimRadiusWorld = 0f, AimTargetId = new TechId(-1) };

            // Build the belief list and locate the Coordination-assigned slot (if any).
            // Slot indices are local to this tick — never persisted across ticks.
            var beliefList = new List<BeliefState>(beliefs.ByTech.Count);
            int coordSlot = -1;
            foreach (var p in beliefs.ByTech)
            {
                if (coordinationTarget.HasValue && p.Key.Value == coordinationTarget.Value.Value)
                    coordSlot = beliefList.Count;
                beliefList.Add(p.Value);
            }

            // Stage 1: per-weapon eligibility + lead direction.
            var eligibility = new bool[wCount];
            var leadDirections = new Vector3[wCount];
            var leadPoints = new Vector3[wCount];
            var targetSlots = new int[wCount];
            int salvoReady = 0;

            // Track the highest-priority eligible weapon for aim-point selection.
            int bestAimWeapon = -1;
            float bestAimPriority = float.MinValue;

            for (int i = 0; i < wCount; i++)
            {
                var w = vehicle.Weapons[i];
                var st = _states[i];
                st.TicksSinceSwitch++;

                int targetSlot;
                if (coordSlot >= 0)
                {
                    // §10.6 override: Coordination's reassignment is immediate.
                    targetSlot = coordSlot;
                    int newId = beliefList[targetSlot].Id.Value;
                    if (st.CurrentTargetTechIdValue != newId) { st.CurrentTargetTechIdValue = newId; st.TicksSinceSwitch = 0; }
                }
                else
                {
                    targetSlot = SelectTargetSlot(w, own, beliefList, st);
                    if (targetSlot < 0) { commits[i] = false; _states[i] = st; targetSlots[i] = -1; continue; }
                    int newId = beliefList[targetSlot].Id.Value;
                    if (st.CurrentTargetTechIdValue != newId)
                    {
                        st.CurrentTargetTechIdValue = newId;
                        st.TicksSinceSwitch = 0;
                    }
                }
                _states[i] = st;
                targetSlots[i] = targetSlot;

                var target = beliefList[targetSlot];

                // Lead computation (CONTROL §10.3).
                float dist = (target.PositionMean - own.PositionWorld).magnitude;
                float projTime = dist / Mathf.Max(w.ProjectileVelocity, 1f);
                Vector3 predictedPos = target.PositionMean + target.VelocityMean * projTime
                                     + LearnedResidual(target.Id, projTime);
                Vector3 toAim = predictedPos - own.PositionWorld;
                float toAimLen = toAim.magnitude;
                Vector3 requiredAim = toAimLen > 1e-3f ? toAim / toAimLen : Vector3.forward;
                leadDirections[i] = requiredAim;
                leadPoints[i] = predictedPos;

                // Aimed within arc?
                bool aimed = IsAimedWithinArc(w, own, requiredAim);
                if (!aimed) { eligibility[i] = false; continue; }
                if (dist > w.Range) { eligibility[i] = false; continue; }
                if (w.CooldownRemaining > 0f) { eligibility[i] = false; continue; }
                if (w.AmmoCurrent <= 0) { eligibility[i] = false; continue; }

                // Ammo conservation: continuous weapons skip every other tick when low.
                if (w.AmmoCapacity > 0 && (float)w.AmmoCurrent / w.AmmoCapacity < AmmoConserveThreshold)
                {
                    if (w.FireMode == WeaponFireMode.Continuous && (st.TicksSinceSwitch % 2) == 0)
                    { eligibility[i] = false; continue; }
                }

                if (w.IsEnergyWeapon && _energyReserveFraction < EnergyReserveThreshold)
                {
                    eligibility[i] = false; continue;
                }

                // §10.7 friendly-fire raycast: suppress if any friendly is on the ray
                // closer than the target and within FriendlyFireRadius of it.
                if (FriendlyOnRay(own.PositionWorld, predictedPos, friendlyPositionsExcludingSelf))
                { eligibility[i] = false; continue; }

                // §10.7 terrain occlusion: if terrain blocks the direct path to predicted
                // position, suppress (low-projectile-velocity arcing weapons get a pass —
                // they could indirect-fire over terrain; we don't model that at v0.1.0).
                if (terrain != null && !terrain.RaycastSegment(own.PositionWorld, predictedPos))
                { eligibility[i] = false; continue; }

                eligibility[i] = true;
                if (w.FireMode == WeaponFireMode.Salvo) salvoReady++;

                // Track best weapon for aim selection: salvo > continuous, then by damage.
                float priority = (w.FireMode == WeaponFireMode.Salvo ? 100f : 0f) + w.DamagePerProjectile;
                if (priority > bestAimPriority) { bestAimPriority = priority; bestAimWeapon = i; }
            }

            // Stage 2: multi-weapon coordination per §10.5.
            bool fireSalvoSet = salvoReady >= SalvoMinReady;
            for (int i = 0; i < wCount; i++)
            {
                if (!eligibility[i]) { commits[i] = false; continue; }
                var w = vehicle.Weapons[i];
                if (w.FireMode == WeaponFireMode.Continuous)
                    commits[i] = true;
                else
                    commits[i] = fireSalvoSet;
            }

            // Stage 3: energy budget enforcement for energy weapons.
            EnforceEnergyBudget(vehicle, commits);

            // Aggregate aim — use the highest-priority committed weapon's lead point.
            bool fireAny = false;
            for (int i = 0; i < wCount; i++) if (commits[i]) { fireAny = true; break; }

            Vector3 aimPoint = own.PositionWorld;
            float aimRadius = 0f;
            TechId aimTargetId = new TechId(-1);
            if (fireAny)
            {
                int pickIdx = bestAimWeapon;
                // If the best-aim weapon's commit didn't survive (energy budget dropped it),
                // fall back to any committed weapon's aim.
                if (pickIdx < 0 || !commits[pickIdx])
                {
                    for (int i = 0; i < wCount; i++) if (commits[i]) { pickIdx = i; break; }
                }
                if (pickIdx >= 0)
                {
                    aimPoint = leadPoints[pickIdx];
                    aimRadius = DefaultAimRadius;
                    int slot = targetSlots[pickIdx];
                    if (slot >= 0) aimTargetId = beliefList[slot].Id;
                }
            }

            return new WeaponFireDecision
            {
                Commits = commits,
                FireAny = fireAny,
                AimPointWorld = aimPoint,
                AimRadiusWorld = aimRadius,
                AimTargetId = aimTargetId,
            };
        }

        private int SelectTargetSlot(WeaponProfile w, KinematicState own, List<BeliefState> beliefs, PerWeaponState st)
        {
            int best = -1;
            float bestValue = float.MinValue;
            int currentSlot = -1;
            float currentValue = float.MinValue;

            for (int slot = 0; slot < beliefs.Count; slot++)
            {
                float value = ExpectedValue(w, own, beliefs[slot]);
                if (beliefs[slot].Id.Value == st.CurrentTargetTechIdValue)
                {
                    currentSlot = slot;
                    currentValue = value;
                }
                if (value > bestValue) { bestValue = value; best = slot; }
            }

            // Hysteresis: switch only if best exceeds current by SwitchValueRatio AND sticky elapsed.
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: if the locked target dropped out of the
            // belief set (lost, destroyed, demoted to stale), currentSlot stays -1 and we
            // fall through to `best` immediately (no sticky cost on lost targets).
            if (currentSlot < 0) return best;
            if (st.TicksSinceSwitch < StickyTicks) return currentSlot;
            if (bestValue > currentValue * SwitchValueRatio) return best;
            return currentSlot;
        }

        private static float ExpectedValue(WeaponProfile w, KinematicState own, BeliefState target)
        {
            float dist = (target.PositionMean - own.PositionWorld).magnitude;
            if (dist > w.Range * 1.2f) return 0f;
            float rangeFitness = 1f - Mathf.Abs(dist - w.Range * 0.75f) / Mathf.Max(w.Range, 1f);
            return Mathf.Max(0f, rangeFitness) * w.DamagePerProjectile * w.FireRateHz;
        }

        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.4: previously the arc check was a single 3D
        // half-angle test against YawArcRadians only — ignoring PitchArcRadians entirely.
        // A fixed-pitch barrel could be "in-arc" for a target 60° above horizon as long as
        // the yaw was within range. Per CONTROL-CONTRACT §10.2, yaw and pitch are
        // independent constraints. The fix rotates the required-aim vector into the
        // tech's local frame, then checks the horizontal (yaw) and vertical (pitch)
        // angles against the weapon's mount-relative forward separately.
        private static bool IsAimedWithinArc(WeaponProfile w, KinematicState own, Vector3 requiredAimWorld)
        {
            // Tech-local forward / right derived from own.HeadingWorld (yaw only; we don't
            // model tech roll/pitch in the arc test — TerraTech tanks can pitch in the world,
            // but the weapon mount arcs are expressed against the chassis frame).
            Vector3 ownFwd = own.HeadingWorld;
            float ownLen = ownFwd.magnitude;
            if (ownLen < 1e-3f) return false;
            ownFwd /= ownLen;
            float yawWorld = Mathf.Atan2(ownFwd.x, ownFwd.z);
            float cYaw = Mathf.Cos(yawWorld), sYaw = Mathf.Sin(yawWorld);

            // Rotate requiredAimWorld into chassis-local frame (inverse-yaw rotation).
            Vector3 aimLocal = new Vector3(
                cYaw * requiredAimWorld.x - sYaw * requiredAimWorld.z,
                requiredAimWorld.y,
                sYaw * requiredAimWorld.x + cYaw * requiredAimWorld.z);

            Vector3 weaponFwdLocal = w.ForwardDirectionLocal;
            float wLen = weaponFwdLocal.magnitude;
            if (wLen < 1e-3f) return false;
            weaponFwdLocal /= wLen;

            // Yaw component: project both onto the chassis XZ plane.
            Vector2 aimYaw = new Vector2(aimLocal.x, aimLocal.z);
            Vector2 wYaw = new Vector2(weaponFwdLocal.x, weaponFwdLocal.z);
            float aimYawLen = aimYaw.magnitude;
            float wYawLen = wYaw.magnitude;
            float yawDot = (aimYawLen > 1e-3f && wYawLen > 1e-3f)
                ? Vector2.Dot(aimYaw / aimYawLen, wYaw / wYawLen)
                : 1f;
            float yawAngle = Mathf.Acos(Mathf.Clamp(yawDot, -1f, 1f));
            if (yawAngle > w.YawArcRadians) return false;

            // Pitch component: angle between the full vector and its XZ projection.
            // Weapon mount's resting pitch is whatever angle its local-forward has above XZ;
            // the deviation we care about is (aim pitch) - (weapon resting pitch).
            float aimPitch = Mathf.Atan2(aimLocal.y, Mathf.Max(aimYawLen, 1e-6f));
            float wPitch   = Mathf.Atan2(weaponFwdLocal.y, Mathf.Max(wYawLen, 1e-6f));
            float pitchDelta = Mathf.Abs(aimPitch - wPitch);
            if (pitchDelta > w.PitchArcRadians) return false;

            return true;
        }

        /// <summary>
        /// True if a friendly position is closer than the target AND lies within
        /// <see cref="FriendlyFireRadius"/> perpendicular distance from the firing ray.
        /// </summary>
        private static bool FriendlyOnRay(Vector3 from, Vector3 to, IReadOnlyList<Vector3> friendlies)
        {
            if (friendlies == null || friendlies.Count == 0) return false;
            Vector3 dir = to - from;
            float len = dir.magnitude;
            if (len < 1e-3f) return false;
            dir /= len;
            for (int i = 0; i < friendlies.Count; i++)
            {
                Vector3 d = friendlies[i] - from;
                float along = Vector3.Dot(d, dir);
                if (along <= 0f || along >= len) continue;          // behind us or past target
                Vector3 closest = from + dir * along;
                if ((closest - friendlies[i]).sqrMagnitude < FriendlyFireRadius * FriendlyFireRadius)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Learned trajectory residual per CONTROL-CONTRACT §10.3 + LEARNING-CONTRACT §3.3.
        /// Slot for the trained <see cref="TrajectoryResidualModel"/>; returns zero when the
        /// model has not yet been trained on observable lead-prediction outcomes for the
        /// target tech. Phase 1.4 (FIX-PLAN.md): signature corrected to TechId — the slot
        /// is keyed by the target tech being aimed at, not by the firing weapon, matching
        /// both contract texts and the existing call site.
        /// </summary>
        private static Vector3 LearnedResidual(TechId targetId, float dt) => Vector3.zero;

        private void EnforceEnergyBudget(VehicleModelSnapshot vehicle, bool[] commits)
        {
            var energyWeapons = new List<int>();
            for (int i = 0; i < vehicle.Weapons.Count; i++)
                if (vehicle.Weapons[i].IsEnergyWeapon && commits[i]) energyWeapons.Add(i);
            if (energyWeapons.Count == 0) return;

            energyWeapons.Sort((a, b) => Priority(vehicle.Weapons[b]).CompareTo(Priority(vehicle.Weapons[a])));

            float reserve = _energyReserveFraction;
            const float CostPerEnergyShot = 0.02f; // provisional; TODO v0.2 verify against tank energy state.
            for (int i = 0; i < energyWeapons.Count; i++)
            {
                if (reserve >= CostPerEnergyShot) reserve -= CostPerEnergyShot;
                else commits[energyWeapons[i]] = false;
            }
            _energyReserveFraction = Mathf.Clamp01(reserve);
        }

        private static int Priority(WeaponProfile w)
        {
            return (w.FireMode == WeaponFireMode.Salvo ? 100 : 0) + Mathf.RoundToInt(w.DamagePerProjectile);
        }
    }
}
