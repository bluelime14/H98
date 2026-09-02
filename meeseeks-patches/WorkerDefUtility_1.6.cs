using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace CM_Meeseeks_Box
{
    public static class WorkerDefUtility
    {
        // Borrowed from Achtung: get the WorkGiverDefs that share compatible worker classes.
        // The original mod logged every successful lookup, which is just debug noise in 1.6.
        private static List<WorkGiverDef> AllWorkerDefs<T>() where T : class
        {
            try
            {
                return DefDatabase<WorkGiverDef>.AllDefsListForReading
                    .Where(def => def.giverClass != null && (def.Worker as T) != null)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error($"CM_Meeseeks_Box cannot fetch a list of WorkGiverDefs for {typeof(T).FullName}: {ex}");
                return new List<WorkGiverDef>();
            }
        }

        public static readonly List<WorkGiverDef> constructionDefs =
            AllWorkerDefs<WorkGiver_ConstructDeliverResources>()
                .Concat(AllWorkerDefs<WorkGiver_ConstructFinishFrames>())
                .ToList();

        public static List<WorkGiverDef> GetCombinedDefs(WorkGiver baseWorkGiver)
        {
            return GetCombinedDefs(baseWorkGiver.def);
        }

        public static List<WorkGiverDef> GetCombinedDefs(WorkGiverDef baseWorkGiverDef)
        {
            if (constructionDefs.Contains(baseWorkGiverDef))
                return constructionDefs.ToList();

            if (baseWorkGiverDef.giverClass != null && (baseWorkGiverDef.Worker as WorkGiver_Warden) != null)
                return AllWorkerDefs<WorkGiver_Warden>();

            return new List<WorkGiverDef> { baseWorkGiverDef };
        }

        public static List<WorkGiver_Scanner> GetCombinedWorkGiverScanners(WorkGiver_Scanner workGiverScanner)
        {
            return GetCombinedDefs(workGiverScanner)
                .Where(workGiverDef => workGiverDef.giverClass != null)
                .Select(workGiverDef => (WorkGiver_Scanner)workGiverDef.Worker)
                .ToList();
        }
    }
}
