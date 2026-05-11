using System;
using System.Collections.Generic;
using System.Linq;
using TerraTechETCUtil;

namespace TAC_AI.Templates
{
    public enum RawTechOffset
    {
        OnGround,
        RaycastTerrainAndScenery,
        OffGround60Meters,
        Exact,
    }
    public class RawTechPopParams
    {
        public static RawTechPopParams Default => new RawTechPopParams();
        public RawTechPopParams()
        {
            _purposes = new HashSet<BasePurpose>();

            Faction = FactionSubTypes.NULL;
            Progression = RawTechLoader.TryGetPlayerLicenceLevel();
            Terrain = BaseTerrain.Any;
            Offset = RawTechOffset.Exact;
            TargetFactionGrade = 99;
            MaxPrice = 0;
            RandSkins = true;
            TeamSkins = true;
            ForceAnchor = false;
            IsPopulation = true;
            SpawnCharged = true;
            //AllowAutominers = AIGlobals.AllowInfAutominers,
            BlockConveyors = false;
            SearchAttract = AIGlobals.IsAttract;
            ExcludeErad = !KickStart.EnemyEradicators || SpecialAISpawner.Eradicators.Count >= AIGlobals.MaxEradicatorTechs;
            Disarmed = false;
            ForceCompleted = false;
        }
        /// <summary>
        /// WARNING OVERWRITES Purposes!!!
        /// </summary>
        /// <param name="tech"></param>
        public RawTechPopParams(RawTech tech)
        {
            _purposes = tech.purposes;

            Faction = FactionSubTypes.NULL;
            Progression = RawTechLoader.TryGetPlayerLicenceLevel();
            Terrain = BaseTerrain.Any;
            Offset = RawTechOffset.Exact;
            TargetFactionGrade = 99;
            MaxPrice = 0;
            RandSkins = true;
            TeamSkins = true;
            IsPopulation = true;
            SpawnCharged = true;
            ForceCompleted = false;
        }
        /// <summary>
        /// The dominant faction that composes the Tech, and what faction it should spawn under
        /// Built at runtime, so it should be accurate
        /// </summary>
        public FactionSubTypes Faction {
            get => _Faction;
            set
            {
                FactionHash = RawTechUtil.GetFactionShortName(value).GetHashCode();
                _Faction = value;
            } }
        private FactionSubTypes _Faction = FactionSubTypes.NULL;
        public int FactionHash { get; private set; }
        /// <summary>
        /// How far the player is in terms of progression.  This is weighed in against the highest FactionLevel block on the Tech.
        /// To prevent Techs with high-level blocks from spawning in too early.
        /// </summary>
        public FactionLevel Progression;
        public BaseTerrain Terrain;
        private HashSet<BasePurpose> _purposes;
        /// <summary>
        /// WARNING: DO NOT ASSIGN A HASHSET THAT IS ALREADY USED ELSEWHERE
        /// </summary>
        public HashSet<BasePurpose> Purposes
        {
            get => _purposes;
            set
            {
                if (value == null)
                    throw new NullReferenceException("purposes cannot be null"); 
                _purposes = value;
            }
        }
        public BasePurpose Purpose
        {
            get => _purposes.First();
            set
            {
                _purposes.Clear();
                _purposes.Add(value);
            }
        }
        public RawTechOffset Offset;
        /// <summary>
        /// DO NOT GIVE THIS A VALUE IF Faction IS UNSET - IT WILL NOT BE RESPECTED
        /// </summary>
        public int TargetFactionGrade;
        public int MaxPrice;
        public bool ForceAnchor
        {
            get => !_purposes.Contains(BasePurpose.NotStationary);
            set
            {
                if (value)
                    _purposes.Remove(BasePurpose.NotStationary);
                else
                    _purposes.Add(BasePurpose.NotStationary);
            }
        }
        public bool SnapTerrain => Offset == RawTechOffset.OnGround || 
            Offset == RawTechOffset.RaycastTerrainAndScenery || ForceAnchor;
        public bool IsPopulation;
        public bool SpawnCharged;
        public bool RandSkins;
        public bool TeamSkins;
        public bool ExcludeErad
        {
            get => !_purposes.Contains(BasePurpose.NANI);
            set
            {
                if (value)
                    _purposes.Remove(BasePurpose.NANI);
                else
                    _purposes.Add(BasePurpose.NANI);
            }
        }
        public bool Disarmed
        {
            get => _purposes.Contains(BasePurpose.NoWeapons);
            set
            {
                if (value)
                    _purposes.Add(BasePurpose.NoWeapons);
                else
                    _purposes.Remove(BasePurpose.NoWeapons);
            }
        }
        public bool SearchAttract
        {
            get => _purposes.Contains(BasePurpose.AttractTech);
            set
            {
                if (value)
                    _purposes.Add(BasePurpose.AttractTech);
                else
                    _purposes.Remove(BasePurpose.AttractTech);
            }
        }
        public bool BlockConveyors
        {
            get => !_purposes.Contains(BasePurpose.MPUnsafe);
            set
            {
                if (value)
                    _purposes.Remove(BasePurpose.MPUnsafe);
                else
                    _purposes.Add(BasePurpose.MPUnsafe);
            }
        }
        public bool AllowAutominers
        {
            get => _purposes.Contains(BasePurpose.Autominer);
            set
            {
                if (value)
                    _purposes.Add(BasePurpose.Autominer);
                else
                    _purposes.Remove(BasePurpose.Autominer);
            }
        }
        public bool AllowSnipers
        {
            get => _purposes.Contains(BasePurpose.Sniper);
            set
            {
                if (value)
                    _purposes.Add(BasePurpose.Sniper);
                else
                    _purposes.Remove(BasePurpose.Sniper);
            }
        }
        public bool ForceCompleted;
    }
}
