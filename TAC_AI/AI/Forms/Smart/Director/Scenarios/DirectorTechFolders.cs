using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.Director.Scenarios
{
    // Curated per-role spawn pools. Each scenario role pulls from its own subfolder under
    // Raw Techs/Enemies/. A tech lands in a folder when the player names their snapshot with
    // the role prefix and saves it via the existing "To Enemy" button. Empty folder => that
    // role spawns nothing (the scenario degrades with a journal note).
    public static class DirectorTechFolders
    {
        public const string Brawl = "Director_Brawl";
        public const string Air = "Director_Air";
        public const string Harvester = "Director_Harvester";
        public const string HarvesterBase = "Director_HarvesterBase";
        public const string DefenseBase = "Director_DefenseBase";

        // Folder + the snapshot name-prefix that routes a saved tech into it. Ordered
        // longest-first so "HarvesterBase" matches before "Harvester".
        private static readonly KeyValuePair<string, string>[] _prefixToFolder =
        {
            new KeyValuePair<string, string>("HarvesterBase", HarvesterBase),
            new KeyValuePair<string, string>("DefenseBase", DefenseBase),
            new KeyValuePair<string, string>("Harvester", Harvester),
            new KeyValuePair<string, string>("Brawl", Brawl),
            new KeyValuePair<string, string>("Air", Air),
        };

        // folder name -> tech names living in it. Rebuilt on each library load.
        private static readonly Dictionary<string, HashSet<string>> _byFolder =
            new Dictionary<string, HashSet<string>>();

        // If a tech name begins with a known role prefix (followed by a space, dash or
        // underscore), return the folder it should save into; else null (saves to eLocal).
        public static string FolderForName(string techName)
        {
            if (string.IsNullOrEmpty(techName)) return null;
            for (int i = 0; i < _prefixToFolder.Length; i++)
            {
                string p = _prefixToFolder[i].Key;
                if (techName.Length > p.Length
                    && techName.StartsWith(p, System.StringComparison.OrdinalIgnoreCase))
                {
                    char sep = techName[p.Length];
                    if (sep == ' ' || sep == '-' || sep == '_')
                        return _prefixToFolder[i].Value;
                }
            }
            return null;
        }

        public static void ClearIndex()
        {
            _byFolder.Clear();
        }

        // Record that techName lives in folderName. Only Director_* folders are tracked.
        public static void IndexTech(string folderName, string techName)
        {
            if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(techName)) return;
            if (!folderName.StartsWith("Director_", System.StringComparison.Ordinal)) return;
            HashSet<string> set;
            if (!_byFolder.TryGetValue(folderName, out set))
            {
                set = new HashSet<string>();
                _byFolder[folderName] = set;
            }
            set.Add(techName);
        }

        public static int CountIn(string folderName)
        {
            HashSet<string> set;
            return _byFolder.TryGetValue(folderName, out set) ? set.Count : 0;
        }

        // Snapshot of the tech names in a folder, or null if the folder is empty/unknown.
        public static List<string> NamesIn(string folderName)
        {
            HashSet<string> set;
            if (!_byFolder.TryGetValue(folderName, out set) || set.Count == 0) return null;
            return new List<string>(set);
        }
    }
}
