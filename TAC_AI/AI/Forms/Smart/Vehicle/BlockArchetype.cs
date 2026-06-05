using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: immutable per-BlockType data, populated once by ArchetypeProbe at first
    /// sighting of the type in a session. Lives in <see cref="TypedBlockCatalog"/>.
    ///
    /// Per the threading model: this class is sealed with all readonly fields; the
    /// constructor sets every field; references published into the catalog dictionary
    /// via ConcurrentDictionary.GetOrAdd are safe to read from any thread. Workers never
    /// trigger probing -- only main-thread ChassisCapture calls GetOrProbe.
    /// </summary>
    public sealed class BlockArchetype
    {
        public readonly int TypeKey;
        public readonly BlockKindFlags Kinds;
        public readonly float SpecMass;              // block.CurrentMass at probe time on the prefab
        // P3 Item 5: per-archetype spec HP from ModuleDamage.maxHealth on the prefab.
        // Falls back to SpecMass on probe miss (preserves v0.1 mass-as-HP magnitudes).
        // Consumed by ArmorMap.Compute (catalog overload) — pose.HpFraction * archetype.SpecHP.
        public readonly float SpecHP;
        public readonly int CellCount;
        public readonly ThrustEmitter[] Emitters;    // length 0 if non-propulsion; never null
        public readonly bool HasWeapon;
        public readonly WeaponSpec Weapon;           // valid only when HasWeapon == true
        public readonly Bounds LocalBounds;          // block-local AABB

        public BlockArchetype(
            int typeKey,
            BlockKindFlags kinds,
            float specMass,
            int cellCount,
            ThrustEmitter[] emitters,
            bool hasWeapon,
            WeaponSpec weapon,
            Bounds localBounds,
            float specHP = -1f)
        {
            TypeKey = typeKey;
            Kinds = kinds;
            SpecMass = specMass;
            // Default sentinel -1 means "not probed" — fall back to specMass so v0.1
            // mass-as-HP magnitudes are preserved when the catalog/probe is older or
            // ArmorHpProbe was never called for this archetype.
            SpecHP = specHP > 0f ? specHP : specMass;
            CellCount = cellCount;
            Emitters = emitters ?? System.Array.Empty<ThrustEmitter>();
            HasWeapon = hasWeapon;
            Weapon = weapon;
            LocalBounds = localBounds;
        }

        // Sentinel for failed probes (per Failure mode #1). NOT cached under the real key;
        // returned to the caller so a single sighting failure doesn't poison the catalog.
        public static readonly BlockArchetype Unknown = new BlockArchetype(
            typeKey: 0,
            kinds: BlockKindFlags.None,
            specMass: 1f,
            cellCount: 1,
            emitters: System.Array.Empty<ThrustEmitter>(),
            hasWeapon: false,
            weapon: default(WeaponSpec),
            localBounds: new Bounds(Vector3.zero, Vector3.one));
    }
}
