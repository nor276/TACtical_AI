using System;
using System.Collections.Generic;
using System.Linq;

namespace TAC_AI.AI
{
    public class TeamIndex
    {   // 
        public HashSet<Tank> Teammates = new HashSet<Tank>();
        public HashSet<Tank> NonHostile = new HashSet<Tank>();
        public HashSet<Tank> Targets = new HashSet<Tank>();
    }
}
