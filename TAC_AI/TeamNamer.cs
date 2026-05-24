using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TAC_AI.Templates;

namespace TAC_AI
{
    internal static class TeamNamer
    {

        private const string lonely = "Lone Prospector";
        private const string teamer = "The Enemy";
        private const string playerTeam = "Your Team";
        private const string mPlayerTeam = "MP Player Team";
        private const string neutralTeam = "Services Group";
        private const string trollerTeam = "Trader Trolls United";

        private static StringBuilder build = new StringBuilder();

        public static string GetTeamName(int Team)
        {
            build.Clear();
            int teamNameDetermine = Mathf.Abs(Team) % 10000;
            if (Team == AIGlobals.LonerEnemyTeam)
            {
                build.Append(lonely);
                return build.ToString();
            }
            else if (Team == AIGlobals.DefaultEnemyTeam)
            {
                build.Append(teamer);
                return build.ToString();
            }
            else if(Team == ManPlayer.inst.PlayerTeam)
            {
                build.Append(playerTeam);
                return build.ToString();
            }
            else if (AIGlobals.IsMPPlayerTeam(Team))
            {
                build.Append(mPlayerTeam);
                return build.ToString();
            }
            else if (Team == SpecialAISpawner.trollTeam)
            {
                build.Append(trollerTeam);
                return build.ToString();
            }
            else if (Team == ManSpawn.NeutralTeam)
            {
                build.Append(neutralTeam);
                return build.ToString();
            }
            else if (Mathf.Repeat(teamNameDetermine, 3) != 0)
            {
                int mod1 = teamNameDetermine % Adjective.Count;
                int mod2 = teamNameDetermine % Noun.Count;
                build.Append(Adjective.ElementAt(mod1));
                build.Append(" ");
                build.Append(Noun.ElementAt(mod2));
            }
            else
            {
                int mod1 = teamNameDetermine % AdjectiveAlt.Count;
                int mod2 = teamNameDetermine % NounAlt.Count;
                build.Append(AdjectiveAlt.ElementAt(mod1));
                build.Append(" ");
                build.Append(NounAlt.ElementAt(mod2));
            }

            return build.ToString();
        }
        public static string EnemyTeamName(EnemyMind mind)
        {
            try
            {
                if (mind.AIControl.tank.Team == AIGlobals.LonerEnemyTeam)
                {
                    build.Append(lonely);
                    return build.ToString();
                }
                else if (mind.AIControl.tank.Team == AIGlobals.DefaultEnemyTeam)
                {
                    build.Append(teamer);
                    return build.ToString();
                }
                else
                {
                    int teamNameDetermine = Mathf.Abs(mind.AIControl.tank.Team);
                    if (Mathf.Repeat(teamNameDetermine, 3) != 0)
                    {
                        int mod1 = teamNameDetermine % Adjective.Count;
                        int mod2 = teamNameDetermine % Noun.Count;
                        build.Append(Adjective.ElementAt(mod1));
                        build.Append(" ");
                        build.Append(Noun.ElementAt(mod2));
                    }
                    else
                    {
                        int mod1 = teamNameDetermine % AdjectiveAlt.Count;
                        int mod2 = teamNameDetermine % NounAlt.Count;
                        build.Append(AdjectiveAlt.ElementAt(mod1));
                        build.Append(" ");
                        build.Append(NounAlt.ElementAt(mod2));
                    }
                }

                return build.ToString();
            }
            finally
            {
                build.Clear();
            }
        }

        private static List<string> Adjective
        {
            get
            {
                return new List<string>
                {
            {   "Old"
            },{ "Group"
            },{ "Grand"
            },{ "Core"
            },{ "GEO"
            },{ "Rock"
            },{ "Power"
            },{ "Turbo"
            },{ "Gold"
            },{ "Big Tony"
            },
                };
            }
        }
        private static List<string> Noun
        {
            get
            {
                return new List<string>
        {
            {   "Prospectors"
            },{ "Organization"
            },{ "Chunks"
            },{ "Expedition"
            },{ "Moles"
            },{ "Spade"
            },{ "Rushers"
            },{ "Maulers"
            },{ "Crushers"
            },{ "Off-World"
            },{ "Cubes"
            },{ "Blocks"
            },{ "Drill"
            },{ "Grinders"
            },{ "Operations"
            },
        };
            }
        }
        private static List<string> AdjectiveAlt
        {
            get
            {
                return new List<string>
        {
            {   "Miners of"
            },{ "Diggers of"
            },{ "Robots of"
            },{ "Explorers of"
            },{ "Masters of"
            },{ "Followers of"
            },{ "Scientests of"
            },{ "Techs of"
            },
        };
            }
        }
        private static List<string> NounAlt
        {
            get
            {
                return new List<string>
        {
            {   "Sacred Rock"
            },{ "Chrome"
            },{ "Rubble"
            },{ "Ore"
            },{ "F*bron"
            },{ "Rough Rubber"
            },{ "Plumbite"
            },{ "Titania"
            },{ "Erudite"
            },{ "Rodius"
            },{ "Luxite"
            },{ "Oleius"
            },{ "Ignite"
            },{ "Celestite"
            },{ "Carbite"
            },
        };
            }
        }

    }
}
