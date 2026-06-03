namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: per-tank tally of <see cref="BlockKindFlags"/> bits across all blocks.
    /// 18 ints (72 B), readonly struct per Decision #11 — inline on VehicleModelSnapshot,
    /// no heap allocation. Walker/Drill/Repair bits stay reserved-unreachable in v0.1.
    ///
    /// Identity classifier (SmartIdentityClassifier) reads counts to firm up Base/Gatherer
    /// detection per Step 9 (Anchor &gt; 0 -> Base; Conveyor | Holder &gt; 0 -> Gatherer).
    /// </summary>
    public readonly struct BlockKindCounts
    {
        public readonly int Structural;
        public readonly int Wheel;
        public readonly int Hover;
        public readonly int Booster;
        public readonly int Wing;
        public readonly int Walker;          // reserved-unreachable v0.1
        public readonly int WeaponFixed;
        public readonly int WeaponTurret;
        public readonly int Anchor;
        public readonly int Conveyor;
        public readonly int Holder;
        public readonly int Producer;
        public readonly int Repair;          // reserved-unreachable v0.1
        public readonly int Shield;
        public readonly int EnergyStore;
        public readonly int Generator;
        public readonly int Drill;           // reserved-unreachable v0.1
        public readonly int GyroStabilizer;

        public BlockKindCounts(
            int structural, int wheel, int hover, int booster, int wing, int walker,
            int weaponFixed, int weaponTurret,
            int anchor, int conveyor, int holder, int producer, int repair, int shield,
            int energyStore, int generator, int drill, int gyroStabilizer)
        {
            Structural = structural;
            Wheel = wheel;
            Hover = hover;
            Booster = booster;
            Wing = wing;
            Walker = walker;
            WeaponFixed = weaponFixed;
            WeaponTurret = weaponTurret;
            Anchor = anchor;
            Conveyor = conveyor;
            Holder = holder;
            Producer = producer;
            Repair = repair;
            Shield = shield;
            EnergyStore = energyStore;
            Generator = generator;
            Drill = drill;
            GyroStabilizer = gyroStabilizer;
        }

        public static readonly BlockKindCounts Empty = new BlockKindCounts(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// Sum BlockKindFlags bits across all poses' archetypes. Single linear pass.
        /// </summary>
        public static BlockKindCounts From(BlockInstancePose[] poses, TypedBlockCatalog catalog)
        {
            if (poses == null || poses.Length == 0 || catalog == null) return Empty;

            int structural = 0, wheel = 0, hover = 0, booster = 0, wing = 0, walker = 0;
            int weaponFixed = 0, weaponTurret = 0;
            int anchor = 0, conveyor = 0, holder = 0, producer = 0, repair = 0, shield = 0;
            int energyStore = 0, generator = 0, drill = 0, gyroStabilizer = 0;

            for (int i = 0; i < poses.Length; i++)
            {
                var archetype = catalog.TryGet(poses[i].TypeKey);
                if (archetype == null) continue;
                var k = archetype.Kinds;
                if ((k & BlockKindFlags.Structural)     != 0) structural++;
                if ((k & BlockKindFlags.Wheel)          != 0) wheel++;
                if ((k & BlockKindFlags.Hover)          != 0) hover++;
                if ((k & BlockKindFlags.Booster)        != 0) booster++;
                if ((k & BlockKindFlags.Wing)           != 0) wing++;
                if ((k & BlockKindFlags.Walker)         != 0) walker++;
                if ((k & BlockKindFlags.WeaponFixed)    != 0) weaponFixed++;
                if ((k & BlockKindFlags.WeaponTurret)   != 0) weaponTurret++;
                if ((k & BlockKindFlags.Anchor)         != 0) anchor++;
                if ((k & BlockKindFlags.Conveyor)       != 0) conveyor++;
                if ((k & BlockKindFlags.Holder)         != 0) holder++;
                if ((k & BlockKindFlags.Producer)       != 0) producer++;
                if ((k & BlockKindFlags.Repair)         != 0) repair++;
                if ((k & BlockKindFlags.Shield)         != 0) shield++;
                if ((k & BlockKindFlags.EnergyStore)    != 0) energyStore++;
                if ((k & BlockKindFlags.Generator)      != 0) generator++;
                if ((k & BlockKindFlags.Drill)          != 0) drill++;
                if ((k & BlockKindFlags.GyroStabilizer) != 0) gyroStabilizer++;
            }

            return new BlockKindCounts(
                structural, wheel, hover, booster, wing, walker,
                weaponFixed, weaponTurret,
                anchor, conveyor, holder, producer, repair, shield,
                energyStore, generator, drill, gyroStabilizer);
        }
    }
}
