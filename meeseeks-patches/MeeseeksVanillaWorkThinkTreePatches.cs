using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CM_Meeseeks_Box
{
    /// <summary>
    /// v21: WorkType missions use RimWorld's normal JobGiver_Work as an actual node in the
    /// Meeseeks think tree.  This conditional only identifies/locks the mission WorkType;
    /// it does not manufacture jobs, choose targets, or make reservations.
    /// </summary>
    public class ThinkNode_ConditionalMeeseeksWorkMission : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn)
        {
            if (!MeeseeksVanillaWorkMissionUtility.TryGetWorkMission(pawn, out WorkTypeDef workType))
                return false;

            MeeseeksVanillaWorkMissionUtility.LockPawnToMissionWorkType(pawn, workType);
            return true;
        }
    }

    public static class MeeseeksVanillaWorkMissionUtility
    {
        public static bool TryGetWorkMission(Pawn pawn, out WorkTypeDef workType)
        {
            workType = null;
            CompMeeseeksMemory memory = pawn?.GetComp<CompMeeseeksMemory>();
            if (memory == null || !memory.GivenTask || memory.taskCompleted || memory.savedJob == null)
                return false;

            if (!MeeseeksMissionUtility.TryClassifyMission(
                    pawn,
                    memory,
                    out MeeseeksMissionKind kind,
                    out workType))
            {
                workType = null;
                return false;
            }

            if (kind != MeeseeksMissionKind.WorkType || workType == null)
            {
                workType = null;
                return false;
            }

            return true;
        }

        public static void LockPawnToMissionWorkType(Pawn pawn, WorkTypeDef workType)
        {
            if (pawn == null || workType == null)
                return;

            if (pawn.workSettings == null)
                pawn.workSettings = new Pawn_WorkSettings(pawn);

            pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();

            List<WorkTypeDef> all = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            bool alreadyLocked = true;
            for (int i = 0; i < all.Count; i++)
            {
                WorkTypeDef candidate = all[i];
                bool active = pawn.workSettings.GetPriority(candidate) > 0;
                bool shouldBeActive = candidate == workType && !pawn.WorkTypeIsDisabled(candidate);
                if (active != shouldBeActive)
                {
                    alreadyLocked = false;
                    break;
                }
            }

            if (alreadyLocked)
                return;

            pawn.workSettings.DisableAll();
            if (!pawn.WorkTypeIsDisabled(workType))
                pawn.workSettings.SetPriority(workType, 1);
        }
    }

    /// <summary>
    /// If the think tree reaches ThinkNode_MeeseeksCompleteTask for a WorkType mission,
    /// the vanilla JobGiver_Work node immediately before it already found no runnable job.
    /// At that point this patch only decides wait/blocked/complete.  It never calls JobOnThing,
    /// JobOnCell, JobGiver_Work, or TryMakePreToilReservations.
    /// </summary>
    [HarmonyPatch(typeof(ThinkNode_MeeseeksCompleteTask), nameof(ThinkNode_MeeseeksCompleteTask.TryIssueJobPackage))]
    public static class ThinkNode_MeeseeksCompleteTask_VanillaThinkTreeCompletion
    {
        private const int RetryTicks = 60;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(
            ThinkNode_MeeseeksCompleteTask __instance,
            Pawn pawn,
            ref ThinkResult __result)
        {
            if (!MeeseeksVanillaWorkMissionUtility.TryGetWorkMission(pawn, out WorkTypeDef workType))
                return true;

            CompMeeseeksMemory memory = pawn.GetComp<CompMeeseeksMemory>();
            CompMeeseeksMissionState state = pawn.GetComp<CompMeeseeksMissionState>();

            if (MeeseeksMissionUtility.FamilyHasActiveMissionWork(
                    memory,
                    MeeseeksMissionKind.WorkType,
                    workType))
            {
                state?.NotifyWaitingOnFamily();
                __result = MakeWait(__instance);
                return false;
            }

            if (HasOutstandingWorkSignal(pawn, memory, workType))
            {
                state?.NotifyBlocked();
                __result = MakeWait(__instance);
                return false;
            }

            bool finished = state == null || state.NotifyNoWorkAndCheckComplete();
            if (!finished)
            {
                __result = MakeWait(__instance);
                return false;
            }

            Job finishJob = JobMaker.MakeJob(MeeseeksDefOf.CM_Meeseeks_Box_Job_EmbraceTheVoid);
            __result = new ThinkResult(finishJob, __instance, JobTag.MiscWork, fromQueue: false);
            return false;
        }

        private static ThinkResult MakeWait(ThinkNode source)
        {
            Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, RetryTicks);
            return new ThinkResult(wait, source, JobTag.MiscWork, fromQueue: false);
        }

        private static bool HasOutstandingWorkSignal(Pawn pawn, CompMeeseeksMemory memory, WorkTypeDef workType)
        {
            Map map = pawn?.Map;
            if (map == null || workType == null)
                return false;

            if (OriginalAssignedTargetStillOutstanding(pawn, memory))
                return true;

            if (workType == WorkTypeDefOf.Construction || workType.defName == "Construction")
            {
                if (map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                    .Any(t => t != null && !t.Destroyed && t.Faction == Faction.OfPlayer))
                    return true;

                if (map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)
                    .Any(t => t != null && !t.Destroyed && t.Faction == Faction.OfPlayer))
                    return true;

                if (map.areaManager?.BuildRoof?.TrueCount > 0 || map.areaManager?.NoRoof?.TrueCount > 0)
                    return true;

                if (HasDesignation(map, DesignationDefOf.Deconstruct) ||
                    HasDesignation(map, DesignationDefOf.Uninstall) ||
                    HasDesignation(map, DesignationDefOf.SmoothFloor) ||
                    HasDesignation(map, DesignationDefOf.RemoveFloor) ||
                    HasDesignation(map, DesignationDefOf.RemoveFoundation) ||
                    HasDesignation(map, DesignationDefOf.SmoothWall))
                    return true;
            }

            if (workType.defName == "Mining" &&
                (HasDesignation(map, DesignationDefOf.Mine) || HasDesignation(map, DesignationDefOf.MineVein)))
                return true;

            if (workType.defName == "PlantCutting" &&
                (HasDesignation(map, DesignationDefOf.CutPlant) || HasDesignation(map, DesignationDefOf.HarvestPlant)))
                return true;

            if (workType.defName == "Growing" && HasPendingSowing(map))
                return true;

            if (WorkTypeUsesBills(workType) && HasAnyActiveBill(map))
                return true;

            // Dynamic work categories such as Doctor/Childcare can report that work exists
            // through ShouldSkip without us asking them to make a job.
            if (workType.workGiversByPriority != null)
            {
                for (int i = 0; i < workType.workGiversByPriority.Count; i++)
                {
                    WorkGiver giver = workType.workGiversByPriority[i]?.Worker;
                    if (giver == null || !OverridesShouldSkip(giver))
                        continue;
                    try
                    {
                        if (!giver.ShouldSkip(pawn, forced: false))
                            return true;
                    }
                    catch
                    {
                    }
                }
            }

            return false;
        }

        private static bool HasDesignation(Map map, DesignationDef def)
        {
            return def != null && map.designationManager.SpawnedDesignationsOfDef(def).Any();
        }

        private static bool WorkTypeUsesBills(WorkTypeDef workType)
        {
            return workType.workGiversByPriority != null &&
                   workType.workGiversByPriority.Any(def => def?.Worker is WorkGiver_DoBill);
        }

        private static bool HasAnyActiveBill(Map map)
        {
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (building is IBillGiver billGiver &&
                    billGiver.BillStack != null &&
                    billGiver.BillStack.AnyShouldDoNow)
                    return true;
            }

            foreach (Pawn mapPawn in map.mapPawns.AllPawnsSpawned)
            {
                if (mapPawn is IBillGiver billGiver &&
                    billGiver.BillStack != null &&
                    billGiver.BillStack.AnyShouldDoNow)
                    return true;
            }

            return false;
        }

        private static bool HasPendingSowing(Map map)
        {
            List<Zone> zones = map.zoneManager.AllZones;
            for (int i = 0; i < zones.Count; i++)
            {
                if (!(zones[i] is Zone_Growing growZone) || !growZone.allowSow)
                    continue;

                List<IntVec3> cells = growZone.Cells;
                for (int c = 0; c < cells.Count; c++)
                {
                    IntVec3 cell = cells[c];
                    ThingDef wanted = WorkGiver_Grower.CalculateWantedPlantDef(cell, map);
                    if (wanted == null)
                        continue;
                    Plant existing = cell.GetPlant(map);
                    if (existing == null || existing.def != wanted)
                        return true;
                }
            }

            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (!(building is Building_PlantGrower grower))
                    continue;
                ThingDef wanted = grower.GetPlantDefToGrow();
                if (wanted == null)
                    continue;
                foreach (IntVec3 cell in grower.OccupiedRect())
                {
                    Plant existing = cell.GetPlant(map);
                    if (existing == null || existing.def != wanted)
                        return true;
                }
            }

            return false;
        }

        private static bool OverridesShouldSkip(WorkGiver giver)
        {
            MethodInfo method = giver.GetType().GetMethod(
                nameof(WorkGiver.ShouldSkip),
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(Pawn), typeof(bool) },
                modifiers: null);
            return method != null && method.DeclaringType != typeof(WorkGiver);
        }

        private static bool OriginalAssignedTargetStillOutstanding(Pawn pawn, CompMeeseeksMemory memory)
        {
            SavedJob savedJob = memory?.savedJob;
            WorkGiver giver = savedJob?.workGiverDef?.Worker;
            if (savedJob == null || memory.jobTargets == null || pawn?.Map == null)
                return false;

            if (savedJob.IsConstruction)
            {
                foreach (SavedTargetInfo targetInfo in memory.jobTargets)
                {
                    if (targetInfo == null || !targetInfo.IsValid)
                        continue;
                    ConstructionStatus status = targetInfo.TargetConstructionStatus(pawn.Map);
                    if (status != ConstructionStatus.Complete && status != ConstructionStatus.Invalid)
                        return true;
                }
                return false;
            }

            if (giver is WorkGiver_Miner)
            {
                foreach (SavedTargetInfo targetInfo in memory.jobTargets)
                {
                    if (targetInfo?.HasThing == true && targetInfo.Thing != null && !targetInfo.Thing.Destroyed)
                        return true;
                }
                return false;
            }

            if (giver is WorkGiver_DoBill)
            {
                Bill savedBill = savedJob.bill;
                if (savedBill != null && !savedBill.deleted && savedBill.ShouldDoNow())
                    return true;
                foreach (SavedTargetInfo targetInfo in memory.jobTargets)
                {
                    Bill targetBill = targetInfo?.bill;
                    if (targetBill != null && !targetBill.deleted && targetBill.ShouldDoNow())
                        return true;
                }
                return false;
            }

            if (giver is WorkGiver_GrowerSow)
            {
                foreach (SavedTargetInfo targetInfo in memory.jobTargets)
                {
                    if (targetInfo == null || !targetInfo.Cell.IsValid || !targetInfo.Cell.InBounds(pawn.Map))
                        continue;
                    ThingDef wanted = WorkGiver_Grower.CalculateWantedPlantDef(targetInfo.Cell, pawn.Map);
                    if (wanted == null)
                        continue;
                    Plant existing = targetInfo.Cell.GetPlant(pawn.Map);
                    if (existing == null || existing.def != wanted)
                        return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// A WorkType mission must never accidentally turn into Meeseeks-on-Meeseeks combat.
    /// Deliberate KillCreator rage is still allowed, as are attacks on actual hostiles.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class MeeseeksWorkMissionFriendlyAttackGuard
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn ___pawn, Job newJob)
        {
            Pawn pawn = ___pawn;
            if (pawn == null || newJob?.def == null)
                return true;

            if (!MeeseeksVanillaWorkMissionUtility.TryGetWorkMission(pawn, out _))
                return true;

            // The mission-frustration system intentionally starts this state when a task has
            // genuinely become impossible for long enough.  Do not interfere with that ending.
            if (pawn.MentalStateDef?.defName == "CM_Meeseeks_Box_MentalState_MeeseeksKillCreator")
                return true;

            string defName = newJob.def.defName ?? string.Empty;
            bool aggressive =
                newJob.def == JobDefOf.AttackMelee ||
                newJob.def == JobDefOf.AttackStatic ||
                newJob.def == MeeseeksDefOf.CM_Meeseeks_Box_Job_Kill ||
                defName.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                defName.IndexOf("SocialFight", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!aggressive)
                return true;

            Pawn target = newJob.targetA.Thing as Pawn ??
                          newJob.targetB.Thing as Pawn ??
                          newJob.targetC.Thing as Pawn;
            if (target == null || target.Dead)
                return true;

            // Real hostile combat is still valid.  Only reject accidental friendly/family attacks.
            if (target.HostileTo(pawn) || pawn.HostileTo(target))
                return true;

            Log.WarningOnce(
                "Mister Meeseeks: blocked accidental friendly attack during active WorkType mission. " +
                pawn + " tried " + newJob.def.defName + " on " + target + ".",
                Gen.HashCombineInt(0x4D563231, pawn.thingIDNumber));

            JobMaker.ReturnToPool(newJob);
            return false;
        }
    }
}
