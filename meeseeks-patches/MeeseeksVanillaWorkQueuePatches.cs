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
    /// WorkType missions should not reimplement RimWorld's work scheduler.
    ///
    /// The first forced order still defines the Meeseeks mission WorkType, and the helper
    /// chain still propagates that mission.  Once the mission exists, however, vanilla
    /// JobGiver_Work chooses the actual construction/mining/growing/doctoring/etc. jobs.
    /// This preserves RimWorld's own WorkGiver ordering, reservations, hauling queues,
    /// construction transitions, and third-party WorkGiver behavior.
    ///
    /// Hunt-all and raid-clear missions remain handled by the custom Meeseeks mission node
    /// because they intentionally allow multiple Meeseeks to share a combat target.
    /// </summary>
    [HarmonyPatch(typeof(ThinkNode_MeeseeksCompleteTask), nameof(ThinkNode_MeeseeksCompleteTask.TryIssueJobPackage))]
    public static class ThinkNode_MeeseeksCompleteTask_VanillaWorkQueue
    {
        private const int RetryTicks = 60;

        [HarmonyPrefix]
        public static bool Prefix(
            ThinkNode_MeeseeksCompleteTask __instance,
            Pawn pawn,
            JobIssueParams jobParams,
            ref ThinkResult __result)
        {
            CompMeeseeksMemory memory = pawn?.GetComp<CompMeeseeksMemory>();
            if (memory == null || !memory.GivenTask || memory.taskCompleted || memory.savedJob == null)
                return true;

            if (!MeeseeksMissionUtility.TryClassifyMission(
                    pawn,
                    memory,
                    out MeeseeksMissionKind missionKind,
                    out WorkTypeDef workType) ||
                missionKind != MeeseeksMissionKind.WorkType ||
                workType == null)
            {
                // Keep the existing custom handling for hunt-all, raid-clear and special jobs.
                return true;
            }

            CompMeeseeksMissionState missionState = pawn.GetComp<CompMeeseeksMissionState>();

            LockPawnToMissionWorkType(pawn, workType);

            // Let RimWorld's own scheduler choose the next job.  Importantly, JobGiver_Work
            // performs ordinary (not forced) WorkGiver checks, so current reservations made by
            // another Meeseeks are respected and a different valid target can be selected.
            ThinkResult vanillaResult = ThinkResult.NoJob;
            try
            {
                JobGiver_Work vanillaWork = new JobGiver_Work();
                vanillaResult = vanillaWork.TryIssueJobPackage(pawn, jobParams);
            }
            catch (Exception ex)
            {
                Log.WarningOnce(
                    "Mister Meeseeks: vanilla work scheduler threw while resolving mission " +
                    workType.defName + " for " + pawn + ": " + ex,
                    Gen.HashCombineInt(0x4D565751, pawn.thingIDNumber));
            }

            if (vanillaResult.IsValid)
            {
                missionState?.NotifyRunnableWork();

                // Keep the Meeseeks mission node as the source while preserving the vanilla
                // job, WorkGiverDef, tag and any job-internal target queues created by RimWorld.
                __result = new ThinkResult(
                    vanillaResult.Job,
                    __instance,
                    vanillaResult.Tag,
                    vanillaResult.FromQueue);
                return false;
            }

            bool familyWorking = MeeseeksMissionUtility.FamilyHasActiveMissionWork(
                memory,
                MeeseeksMissionKind.WorkType,
                workType);

            if (familyWorking)
            {
                missionState?.NotifyWaitingOnFamily();
                __result = MakeWait(__instance);
                return false;
            }

            // No runnable vanilla job exists.  Distinguish "the category is finished" from
            // "the category still exists but is currently impossible".  The latter keeps the
            // Meeseeks alive and feeds the existing Existence-Is-Pain frustration system.
            if (HasOutstandingWorkSignal(pawn, memory, workType))
            {
                missionState?.NotifyBlocked();
                __result = MakeWait(__instance);
                return false;
            }

            bool finished = missionState == null || missionState.NotifyNoWorkAndCheckComplete();
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

        /// <summary>
        /// Restrict the pawn's normal work table to the WorkType defined by the first real order.
        /// JobGiver_Work can then use exactly the same WorkGiver queue as a normal colonist without
        /// wandering into unrelated work categories.
        /// </summary>
        private static void LockPawnToMissionWorkType(Pawn pawn, WorkTypeDef workType)
        {
            if (pawn == null || workType == null)
                return;

            if (pawn.workSettings == null)
                pawn.workSettings = new Pawn_WorkSettings(pawn);

            pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();

            bool alreadyLocked = true;
            List<WorkTypeDef> allWorkTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            for (int i = 0; i < allWorkTypes.Count; i++)
            {
                WorkTypeDef candidate = allWorkTypes[i];
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

        /// <summary>
        /// Completion/blocked detection only.  This method never makes or reserves a work job.
        /// Actual target selection is exclusively vanilla JobGiver_Work.
        /// </summary>
        private static bool HasOutstandingWorkSignal(
            Pawn pawn,
            CompMeeseeksMemory memory,
            WorkTypeDef workType)
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
                {
                    return true;
                }

                if (map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)
                    .Any(t => t != null && !t.Destroyed && t.Faction == Faction.OfPlayer))
                {
                    return true;
                }

                if (map.areaManager?.BuildRoof?.TrueCount > 0 ||
                    map.areaManager?.NoRoof?.TrueCount > 0)
                {
                    return true;
                }

                if (HasDesignation(map, DesignationDefOf.Deconstruct) ||
                    HasDesignation(map, DesignationDefOf.Uninstall) ||
                    HasDesignation(map, DesignationDefOf.SmoothFloor) ||
                    HasDesignation(map, DesignationDefOf.RemoveFloor) ||
                    HasDesignation(map, DesignationDefOf.RemoveFoundation) ||
                    HasDesignation(map, DesignationDefOf.SmoothWall))
                {
                    return true;
                }
            }

            if (workType.defName == "Mining")
            {
                if (HasDesignation(map, DesignationDefOf.Mine) ||
                    HasDesignation(map, DesignationDefOf.MineVein))
                {
                    return true;
                }
            }

            if (workType.defName == "PlantCutting")
            {
                if (HasDesignation(map, DesignationDefOf.CutPlant) ||
                    HasDesignation(map, DesignationDefOf.HarvestPlant))
                {
                    return true;
                }
            }

            if (workType.defName == "Growing" && HasPendingSowing(map))
                return true;

            if (WorkTypeUsesBills(workType) && HasAnyActiveBill(map))
                return true;

            // Many vanilla dynamic categories (Doctor, Childcare, Firefighter, Warden, etc.)
            // have a meaningful ShouldSkip implementation.  It is safe to use as a pending-work
            // signal because we do not call JobOnThing/JobOnCell or reserve anything here.
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
                        // Third-party WorkGiver detection should never break the mission loop.
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
                {
                    return true;
                }
            }

            foreach (Pawn mapPawn in map.mapPawns.AllPawnsSpawned)
            {
                if (mapPawn is IBillGiver billGiver &&
                    billGiver.BillStack != null &&
                    billGiver.BillStack.AnyShouldDoNow)
                {
                    return true;
                }
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

        private static bool OriginalAssignedTargetStillOutstanding(
            Pawn pawn,
            CompMeeseeksMemory memory)
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
                    if (targetInfo?.HasThing == true &&
                        targetInfo.Thing != null &&
                        !targetInfo.Thing.Destroyed)
                    {
                        return true;
                    }
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
                    if (targetInfo == null ||
                        !targetInfo.Cell.IsValid ||
                        !targetInfo.Cell.InBounds(pawn.Map))
                    {
                        continue;
                    }

                    ThingDef wanted = WorkGiver_Grower.CalculateWantedPlantDef(
                        targetInfo.Cell,
                        pawn.Map);
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
}
