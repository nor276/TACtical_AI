using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Guard ordinals. Source-byte high-nibble of IdentityOutcome carries the ordinal,
    // capped at 14 + the 15-wildcard sentinel. Order matches §9.8.
    public enum GuardId : byte
    {
        BackwardsLock = 0,
        WedgedNoProgress = 1,
        WaterloggedTech = 2,
        GroundedAircraft = 3,
        IdleGatherer = 4,
        OverdueDelivery = 5,
        Homesickness = 6,
        LostAlly = 7,
        OrbitNoFire = 8,
        FriendlyFire = 9,
        OverextendedHunter = 10,
    }

    // Static registry of guard singletons. Worker iterates the array and skips nulls.
    public static class GuardRegistry
    {
        public const int GuardCount = 11;
        public const byte WildcardOrdinal = 15;

        private static readonly IBehaviorGuard[] _guards = new IBehaviorGuard[GuardCount];

        // Index by guard ordinal -> mailbox slot, or -1 when the guard is not hard-correct.
        // GroundedAircraft owns slot 0; slot 1 (AntiCrash) is reserved but unshipped.
        private static readonly sbyte[] _hardCorrectSlotIndex = new sbyte[GuardCount]
        {
            -1, -1, -1, 0, -1, -1, -1, -1, -1, -1, -1,
        };

        static GuardRegistry()
        {
            _guards[(int)GuardId.BackwardsLock] = new Movement.BackwardsLockGuard();
            _guards[(int)GuardId.WedgedNoProgress] = new Movement.WedgedNoProgressGuard();
            _guards[(int)GuardId.WaterloggedTech] = new Movement.WaterloggedTechGuard();
            _guards[(int)GuardId.GroundedAircraft] = new Vehicle.GroundedAircraftGuard();
            _guards[(int)GuardId.IdleGatherer] = new Role.IdleGathererGuard();
            _guards[(int)GuardId.OverdueDelivery] = new Role.OverdueDeliveryGuard();
            _guards[(int)GuardId.Homesickness] = new Role.HomesicknessGuard();
            _guards[(int)GuardId.LostAlly] = new Role.LostAllyGuard();
            _guards[(int)GuardId.OrbitNoFire] = new Combat.OrbitNoFireGuard();
            _guards[(int)GuardId.FriendlyFire] = new Combat.FriendlyFireGuard();
            _guards[(int)GuardId.OverextendedHunter] = new Combat.OverextendedHunterGuard();
        }

        // Lookup the guard's Name by ordinal. Used for digest emission on chronic
        // suppression edge in GuardTelemetry.
        public static string GetName(int ordinal)
        {
            if (ordinal < 0 || ordinal >= GuardCount) return "Unknown";
            var g = _guards[ordinal];
            return g != null ? g.Name : "Unknown";
        }

        public static IBehaviorGuard Get(int ordinal)
        {
            if (ordinal < 0 || ordinal >= GuardCount) return null;
            return _guards[ordinal];
        }

        public static int Count => GuardCount;

        // Bucket-map: guard ordinal → OutcomeKind. Used by GuardWorker on rising edge to
        // pick which IdentityOutcome.Kind to publish.
        public static OutcomeKind GetBucket(int ordinal)
        {
            switch ((GuardId)ordinal)
            {
                case GuardId.BackwardsLock:
                case GuardId.WedgedNoProgress:
                case GuardId.WaterloggedTech:
                case GuardId.GroundedAircraft:
                    return OutcomeKind.GuardViolation_Movement;
                case GuardId.IdleGatherer:
                case GuardId.OverdueDelivery:
                case GuardId.Homesickness:
                case GuardId.LostAlly:
                    return OutcomeKind.GuardViolation_Role;
                case GuardId.OrbitNoFire:
                case GuardId.FriendlyFire:
                case GuardId.OverextendedHunter:
                    return OutcomeKind.GuardViolation_Combat;
            }
            return OutcomeKind.GuardViolation_Movement;
        }

        // Returns the per-tech mailbox slot index for a hard-correct guard, or -1 when
        // the guard is not hard-correct (= "no slot, telemetry-only or observe").
        public static int HardCorrectSlotIndexFor(GuardId guardId)
        {
            int ordinal = (int)guardId;
            if (ordinal < 0 || ordinal >= GuardCount) return -1;
            return _hardCorrectSlotIndex[ordinal];
        }

        // Source-byte assembly. High-nibble = guard ordinal (0..10 or 15 wildcard);
        // low-nibble = Live(0) / Replay(1).
        public static byte BuildSource(int guardOrdinal, bool isReplay)
        {
            byte hi = (byte)(guardOrdinal & 0x0F);
            byte lo = (byte)(isReplay ? 1 : 0);
            return (byte)((hi << 4) | (lo & 0x0F));
        }
    }
}
