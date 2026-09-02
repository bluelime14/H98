using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using RimWorld;
using Verse;
using Verse.AI;

namespace CM_Meeseeks_Box
{
    public static class DesignatorUtility
    {
        private static readonly Dictionary<Designator, DesignationDef> cachedDefs = new Dictionary<Designator, DesignationDef>();
        private static readonly Dictionary<JobDriver_PlantWork, DesignationDef> cachedPlantWorkDefs = new Dictionary<JobDriver_PlantWork, DesignationDef>();
        private static List<Designator> cachedDesignators;
        private static readonly List<Designation> savedDesignations = new List<Designation>();
        private static readonly List<Designation> temporaryDesignations = new List<Designation>();
        private static bool busy;

        public static bool getFudgedForWorkgiverCheck = false;
        public static bool getFudgedForToilCheck = false;
        public static DesignationDef lastCheckedDef = null;

        public static DesignationDef GetDesignationDef(this Designator designator)
        {
            if (cachedDefs.TryGetValue(designator, out DesignationDef cached))
                return cached;

            PropertyInfo getter = typeof(Designator).GetProperty("Designation", BindingFlags.NonPublic | BindingFlags.Instance);
            DesignationDef designationDef = getter?.GetValue(designator) as DesignationDef;
            if (designationDef != null)
                cachedDefs[designator] = designationDef;
            return designationDef;
        }

        public static DesignationDef GetRequiredDesignationDef(this JobDriver_PlantWork jobDriver)
        {
            if (cachedPlantWorkDefs.TryGetValue(jobDriver, out DesignationDef cached))
                return cached;

            PropertyInfo getter = typeof(JobDriver_PlantWork).GetProperty("RequiredDesignation", BindingFlags.NonPublic | BindingFlags.Instance);
            DesignationDef designationDef = getter?.GetValue(jobDriver) as DesignationDef;
            if (designationDef != null)
                cachedPlantWorkDefs[jobDriver] = designationDef;
            return designationDef;
        }

        private static void EnsureDesignatorCache()
        {
            if (cachedDesignators != null)
                return;

            cachedDesignators = DefDatabase<DesignationCategoryDef>.AllDefsListForReading
                .SelectMany(category => category.ResolvedAllowedDesignators)
                .GroupBy(designator => designator.GetType())
                .Select(group => group.First())
                .Where(designator => !(designator is Designator_Zone) &&
                                     !(designator is Designator_Plan) &&
                                     (designator.GetDesignationDef() != null || designator is Designator_RemoveFloor))
                .ToList();

            foreach (Designator designator in cachedDesignators)
            {
                if (designator is Designator_RemoveFloor && !cachedDefs.ContainsKey(designator))
                    cachedDefs[designator] = DesignationDefOf.RemoveFloor;
            }
        }

        private static void SaveAndRemoveDesignationsAt(IntVec3 cell, Map map)
        {
            savedDesignations.Clear();
            temporaryDesignations.Clear();

            foreach (Designation designation in map.designationManager.AllDesignations.ToList())
            {
                if (designation.target.Cell != cell)
                    continue;

                savedDesignations.Add(designation);
                map.designationManager.RemoveDesignation(designation);
            }
        }

        private static void AddTemporaryDesignation(Designation designation, Map map)
        {
            if (designation == null)
                return;

            // Avoid double-designations. The 1.6 manager indexes designations by target and def,
            // so direct list mutation (used by the old mod) is no longer safe.
            if (designation.target.HasThing)
            {
                if (map.designationManager.DesignationOn(designation.target.Thing, designation.def) != null)
                    return;
            }
            else if (map.designationManager.DesignationAt(designation.target.Cell, designation.def) != null)
            {
                return;
            }

            map.designationManager.AddDesignation(designation);
            temporaryDesignations.Add(designation);
        }

        public static void ForceAllDesignationsOnCell(IntVec3 cell, Map map)
        {
            if (busy)
            {
                Logger.WarningFormat(cell, "Trying to force designations before restoring designations.");
                return;
            }

            busy = true;
            getFudgedForWorkgiverCheck = true;
            EnsureDesignatorCache();
            SaveAndRemoveDesignationsAt(cell, map);

            foreach (Designator designator in cachedDesignators)
            {
                DesignationDef designationDef = designator.GetDesignationDef();
                if (designationDef == null)
                    continue;

                if (designationDef.targetType == TargetType.Cell)
                {
                    if (designator.CanDesignateCell(cell).Accepted)
                        AddTemporaryDesignation(new Designation(cell, designationDef), map);
                }
                else if (designationDef.targetType == TargetType.Thing)
                {
                    foreach (Thing thing in cell.GetThingList(map).ToList())
                    {
                        if (designator.CanDesignateThing(thing).Accepted)
                            AddTemporaryDesignation(new Designation(thing, designationDef), map);
                    }
                }
            }
        }

        public static void ForceDesignationOnThingsInCell(IntVec3 cell, Map map, DesignationDef designationDef, Func<Thing, bool> validator = null)
        {
            if (designationDef == null)
                return;

            if (busy)
            {
                Logger.WarningFormat(cell, "Trying to force a designation before restoring designations.");
                return;
            }

            busy = true;
            getFudgedForWorkgiverCheck = true;
            SaveAndRemoveDesignationsAt(cell, map);

            if (designationDef.targetType == TargetType.Thing)
            {
                foreach (Thing thing in cell.GetThingList(map).ToList())
                {
                    if (validator == null || validator(thing))
                        AddTemporaryDesignation(new Designation(thing, designationDef), map);
                }
            }
        }

        public static void RestoreDesignationsOnCell(IntVec3 cell, Map map)
        {
            if (!busy)
            {
                Logger.MessageFormat(cell, "Trying to restore designations without having forced them.");
                return;
            }

            getFudgedForWorkgiverCheck = false;

            // Remove only the temporary designations created by this compatibility layer.
            foreach (Designation designation in temporaryDesignations.ToList())
            {
                if (map.designationManager.AllDesignations.Contains(designation))
                    map.designationManager.RemoveDesignation(designation);
            }
            temporaryDesignations.Clear();

            // Restore the exact designations that were present before the temporary workgiver check.
            foreach (Designation designation in savedDesignations.ToList())
            {
                if (designation.target.HasThing)
                {
                    if (designation.target.Thing == null || designation.target.Thing.Destroyed)
                        continue;
                    if (map.designationManager.DesignationOn(designation.target.Thing, designation.def) == null)
                        map.designationManager.AddDesignation(designation);
                }
                else if (designation.target.Cell.IsValid && map.designationManager.DesignationAt(designation.target.Cell, designation.def) == null)
                {
                    map.designationManager.AddDesignation(designation);
                }
            }

            savedDesignations.Clear();
            busy = false;
        }
    }
}
