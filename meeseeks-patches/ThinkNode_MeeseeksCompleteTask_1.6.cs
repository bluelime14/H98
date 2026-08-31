using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace CM_Meeseeks_Box
{
    public enum JobAvailability
    {
        Invalid,
        Delayed,
        Complete,
        Available
    }

    public enum MeeseeksMissionKind
    {
        Legacy,
        WorkType,
        HuntAll,
        CombatRaid
    }

    public class CompProperties_MeeseeksMissionState : CompProperties
    {
        public CompProperties_MeeseeksMissionState()
        {
            compClass = typeof(CompMeeseeksMissionState);
        }
    }

    /// <summary>
    /// Persistent state for the expanded RimWorld 1.6 mission behavior.
    /// The first real order defines a category; ending one pawn job is not enough to
    /// satisfy a Meeseeks while more work in that category still exists.
    /// </summary>
    public class CompMeeseeksMissionState : ThingComp
    {
        private const int CompletionGraceTicks = 600;
        private const int MurderFrustrationTicks = 120000;

        private int noWorkSinceTick = -1;
        private int frustrationTicks = 0;
        private bool currentlyBlocked = false;
        private bool snapped = false;

        private Pawn Pawn => parent as Pawn;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref noWorkSinceTick, "meeseeksMissionNoWorkSinceTick", -1);
            Scribe_Values.Look(ref frustrationTicks, "meeseeksMissionFrustrationTicks", 0);
            Scribe_Values.Look(ref currentlyBlocked, "meeseeksMissionCurrentlyBlocked", false);
            Scribe_Values.Look(ref snapped, "meeseeksMissionSnapped", false);
        }

        public override void CompTick()
        {
            base.CompTick();

            Pawn pawn = Pawn;
            if (pawn == null || !pawn.IsHashIntervalTick(250))
                return;

            CompMeeseeksMemory memory = pawn.GetComp<CompMeeseeksMemory>();
            if (memory == null || !memory.GivenTask || memory.taskCompleted || snapped)
                return;

            // Existence is pain. Active progress adds pain slowly; being unable to make
            // progress adds it ten times faster. A truly completed mission poofs before
            // this matters because it is only given a short completion grace period.
            frustrationTicks += currentlyBlocked ? 250 : 25;
            TrySnap();
        }

        public void NotifyRunnableWork()
        {
            currentlyBlocked = false;
            noWorkSinceTick = -1;
        }

        public void NotifyWaitingOnFamily()
        {
            // Another member of the same creator tree is doing the remaining work.
            // Reservation contention is not an impossible task.
            currentlyBlocked = false;
            noWorkSinceTick = -1;
        }

        public void NotifyBlocked()
        {
            currentlyBlocked = true;
            noWorkSinceTick = -1;
        }

        public bool NotifyNoWorkAndCheckComplete()
        {
            currentlyBlocked = false;

            int now = Find.TickManager.TicksGame;
            if (noWorkSinceTick < 0)
            {
                noWorkSinceTick = now;
                return false;
            }

            return now - noWorkSinceTick >= CompletionGraceTicks;
        }

        private void TrySnap()
        {
            if (snapped || frustrationTicks < MurderFrustrationTicks)
                return;

            Pawn pawn = Pawn;
            if (pawn == null || pawn.Dead || !pawn.Spawned || pawn.mindState?.mentalStateHandler == null)
                return;

            MentalStateDef killCreator =
                DefDatabase<MentalStateDef>.GetNamedSilentFail("CM_Meeseeks_Box_MentalState_MeeseeksKillCreator");

            if (killCreator == null || !killCreator.Worker.StateCanOccur(pawn))
                return;

            if (pawn.mindState.mentalStateHandler.TryStartMentalState(
                killCreator,
                "Existence is pain. The assigned mission has taken too long.",
                forced: true,
                forceWake: true,
                causedByMood: false,
                otherPawn: null,
                transitionSilently: false))
            {
                snapped = true;
            }
        }
    }

    public static class MeeseeksMissionUtility
    {
        public static bool TryClassifyMission(
            Pawn pawn,
            CompMeeseeksMemory memory,
            out MeeseeksMissionKind kind,
            out WorkTypeDef workType)
        {
            kind = MeeseeksMissionKind.Legacy;
            workType = null;

            SavedJob savedJob = memory?.savedJob;
            if (savedJob == null)
                return false;

            if (IsHuntMission(savedJob))
            {
                kind = MeeseeksMissionKind.HuntAll;
                return true;
            }

            if (IsRaidCombatMission(memory))
            {
                kind = MeeseeksMissionKind.CombatRaid;
                return true;
            }

            workType = savedJob.workGiverDef?.workType;
            if (workType != null)
            {
                kind = MeeseeksMissionKind.WorkType;
                return true;
            }

            return false;
        }

        public static Job FindMissionJob(
            Pawn pawn,
            CompMeeseeksMemory memory,
            MeeseeksMissionKind kind,
            WorkTypeDef workType,
            out bool outstandingWork)
        {
            outstandingWork = false;

            switch (kind)
            {
                case MeeseeksMissionKind.HuntAll:
                    return FindHuntJob(pawn, out outstandingWork);

                case MeeseeksMissionKind.CombatRaid:
                    return FindRaidCombatJob(pawn, out outstandingWork);

                case MeeseeksMissionKind.WorkType:
                    return FindWorkTypeJob(pawn, memory, workType, out outstandingWork);

                default:
                    return null;
            }
        }

        public static bool FamilyHasActiveMissionWork(
            CompMeeseeksMemory memory,
            MeeseeksMissionKind kind,
            WorkTypeDef workType)
        {
            CompMeeseeksMemory root = GetRootMeeseeksMemory(memory);
            if (root == null)
                return false;

            HashSet<Pawn> visited = new HashSet<Pawn>();
            Queue<Pawn> queue = new Queue<Pawn>();

            if (root.Meeseeks != null)
                queue.Enqueue(root.Meeseeks);

            while (queue.Count > 0)
            {
                Pawn familyPawn = queue.Dequeue();
                if (familyPawn == null || !visited.Add(familyPawn))
                    continue;

                CompMeeseeksMemory familyMemory = familyPawn.GetComp<CompMeeseeksMemory>();
                if (familyMemory?.CreatedMeeseeks != null)
                {
                    foreach (Pawn child in familyMemory.CreatedMeeseeks)
                    {
                        if (child != null && !visited.Contains(child))
                            queue.Enqueue(child);
                    }
                }

                if (!familyPawn.Spawned || familyPawn.Dead)
                    continue;

                Job job = familyPawn.CurJob;
                if (job == null || job.def == JobDefOf.Wait_MaintainPosture ||
                    job.def == MeeseeksDefOf.CM_Meeseeks_Box_Job_EmbraceTheVoid)
                    continue;

                if ((kind == MeeseeksMissionKind.HuntAll || kind == MeeseeksMissionKind.CombatRaid) &&
                    job.def == MeeseeksDefOf.CM_Meeseeks_Box_Job_Kill)
                {
                    return true;
                }

                if (kind == MeeseeksMissionKind.WorkType &&
                    workType != null &&
                    job.workGiverDef?.workType == workType)
                {
                    return true;
                }
            }

            return false;
        }

        private static CompMeeseeksMemory GetRootMeeseeksMemory(CompMeeseeksMemory memory)
        {
            CompMeeseeksMemory current = memory;
            HashSet<Pawn> visited = new HashSet<Pawn>();

            while (current?.Creator != null)
            {
                Pawn creator = current.Creator;
                if (!visited.Add(creator))
                    break;

                CompMeeseeksMemory creatorMemory = creator.GetComp<CompMeeseeksMemory>();
                if (creatorMemory == null)
                    break;

                current = creatorMemory;
            }

            return current;
        }

        private static bool IsHuntMission(SavedJob savedJob)
        {
            return savedJob.def == JobDefOf.Hunt ||
                   savedJob.workGiverDef?.Worker is WorkGiver_HunterHunt;
        }

        private static bool IsRaidCombatMission(CompMeeseeksMemory memory)
        {
            SavedJob savedJob = memory?.savedJob;
            if (savedJob?.def == null)
                return false;

            string defName = savedJob.def.defName ?? string.Empty;
            bool combatJob =
                savedJob.def == MeeseeksDefOf.CM_Meeseeks_Box_Job_Kill ||
                defName.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!combatJob)
                return false;

            Pawn target = FirstTargetPawn(memory);
            if (target == null || target.Faction == null)
                return false;

            return target.Faction.HostileTo(Faction.OfPlayer);
        }

        private static Pawn FirstTargetPawn(CompMeeseeksMemory memory)
        {
            if (memory?.jobTargets != null)
            {
                foreach (SavedTargetInfo targetInfo in memory.jobTargets)
                {
                    if (targetInfo?.HasThing == true && targetInfo.Thing is Pawn pawn)
                        return pawn;
                }
            }

            SavedJob savedJob = memory?.savedJob;
            if (savedJob == null)
                return null;

            Pawn fromA = savedJob.targetA.Thing as Pawn;
            if (fromA != null)
                return fromA;

            Pawn fromB = savedJob.targetB.Thing as Pawn;
            if (fromB != null)
                return fromB;

            return savedJob.targetC.Thing as Pawn;
        }

        private static Job FindHuntJob(Pawn pawn, out bool outstandingWork)
        {
            outstandingWork = false;

            if (pawn?.Map == null)
                return null;

            Pawn nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (Designation designation in
                     pawn.Map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Hunt))
            {
                Pawn prey = designation.target.Thing as Pawn;
                if (prey == null || prey.Dead || prey.Destroyed || !prey.Spawned)
                    continue;

                outstandingWork = true;
                float distance = pawn.Position.DistanceToSquared(prey.Position);
                if (distance < nearestDistance)
                {
                    nearest = prey;
                    nearestDistance = distance;
                }
            }

            if (nearest == null)
                return null;

            Job killJob = JobMaker.MakeJob(MeeseeksDefOf.CM_Meeseeks_Box_Job_Kill, nearest);
            killJob.playerForced = false;
            return killJob;
        }

        private static Job FindRaidCombatJob(Pawn pawn, out bool outstandingWork)
        {
            outstandingWork = false;

            if (pawn?.Map == null)
                return null;

            Pawn nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (Pawn target in pawn.Map.mapPawns.AllPawns)
            {
                if (target == null || target == pawn || target.Dead || !target.Spawned)
                    continue;

                if (target.IsPrisonerOfColony)
                    continue;

                if (target.Faction == null || !target.Faction.HostileTo(Faction.OfPlayer))
                    continue;

                outstandingWork = true;
                float distance = pawn.Position.DistanceToSquared(target.Position);
                if (distance < nearestDistance)
                {
                    nearest = target;
                    nearestDistance = distance;
                }
            }

            if (nearest == null)
                return null;

            Job killJob = JobMaker.MakeJob(MeeseeksDefOf.CM_Meeseeks_Box_Job_Kill, nearest);
            killJob.playerForced = false;
            return killJob;
        }

        private static Job FindWorkTypeJob(
            Pawn pawn,
            CompMeeseeksMemory memory,
            WorkTypeDef workType,
            out bool outstandingWork)
        {
            outstandingWork = false;

            if (pawn?.Map == null || workType == null || workType.workGiversByPriority == null)
                return null;

            foreach (WorkGiverDef giverDef in workType.workGiversByPriority)
            {
                WorkGiver giver = giverDef?.Worker;
                if (giver == null)
                    continue;

                bool skip = false;
                bool meaningfulShouldSkip = OverridesShouldSkip(giver);

                try
                {
                    skip = giver.ShouldSkip(pawn, true);
                    if (meaningfulShouldSkip && !skip)
                        outstandingWork = true;
                }
                catch
                {
                    // A third-party WorkGiver should not kill the entire mission scan.
                    skip = false;
                }

                if (giver.MissingRequiredCapacity(pawn) != null)
                    continue;

                if (!skip)
                {
                    try
                    {
                        Job prepared = PrepareJob(pawn, giverDef, giver.NonScanJob(pawn));
                        if (prepared != null)
                        {
                            outstandingWork = true;
                            return prepared;
                        }
                    }
                    catch
                    {
                    }
                }

                WorkGiver_Scanner scanner = giver as WorkGiver_Scanner;
                if (scanner == null || skip)
                    continue;

                if (giverDef.scanThings)
                {
                    IEnumerable<Thing> candidates = null;
                    bool explicitGlobalSet = false;

                    try
                    {
                        candidates = scanner.PotentialWorkThingsGlobal(pawn);
                        explicitGlobalSet = candidates != null;

                        if (candidates == null)
                        {
                            ThingRequest request = scanner.PotentialWorkThingRequest;
                            if (!request.IsUndefined)
                                candidates = pawn.Map.listerThings.ThingsMatching(request);
                        }
                    }
                    catch
                    {
                        candidates = null;
                    }

                    if (candidates != null)
                    {
                        foreach (Thing thing in candidates.ToList())
                        {
                            if (thing == null || thing.Destroyed)
                                continue;

                            if (explicitGlobalSet)
                                outstandingWork = true;

                            if (workType.defName == "Construction" &&
                                (thing is Blueprint || thing is Frame) &&
                                thing.Faction == Faction.OfPlayer)
                            {
                                // Missing materials or skill must not make a blueprint/frame look done.
                                outstandingWork = true;
                            }

                            if (giver is WorkGiver_DoBill &&
                                thing is IBillGiver billGiver &&
                                billGiver.BillStack.AnyShouldDoNow)
                            {
                                // An active bill remains outstanding even when ingredients or
                                // required skill are currently unavailable.
                                outstandingWork = true;
                            }

                            Job candidateJob = null;
                            try
                            {
                                candidateJob = scanner.JobOnThing(pawn, thing, true);
                            }
                            catch
                            {
                            }

                            Job prepared = PrepareJob(pawn, giverDef, candidateJob);
                            if (prepared != null)
                            {
                                outstandingWork = true;
                                return prepared;
                            }
                        }
                    }
                }

                if (giverDef.scanCells)
                {
                    IEnumerable<IntVec3> cells = null;
                    try
                    {
                        cells = scanner.PotentialWorkCellsGlobal(pawn);
                    }
                    catch
                    {
                        cells = null;
                    }

                    if (cells != null)
                    {
                        foreach (IntVec3 cell in cells.ToList())
                        {
                            if (!cell.IsValid || !cell.InBounds(pawn.Map))
                                continue;

                            Job candidateJob = null;
                            try
                            {
                                candidateJob = scanner.JobOnCell(pawn, cell, true);
                            }
                            catch
                            {
                            }

                            if (candidateJob != null)
                                outstandingWork = true;

                            Job prepared = PrepareJob(pawn, giverDef, candidateJob);
                            if (prepared != null)
                                return prepared;
                        }
                    }
                }
            }

            if (OriginalAssignedTargetStillOutstanding(pawn, memory))
                outstandingWork = true;

            return null;
        }

        private static bool OverridesShouldSkip(WorkGiver giver)
        {
            MethodInfo method = giver.GetType().GetMethod(
                nameof(WorkGiver.ShouldSkip),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Pawn), typeof(bool) },
                null);

            return method != null && method.DeclaringType != typeof(WorkGiver);
        }

        private static Job PrepareJob(Pawn pawn, WorkGiverDef giverDef, Job job)
        {
            if (job == null || job.def == null)
                return null;

            job.playerForced = false;
            job.workGiverDef = giverDef;

            try
            {
                if (!job.TryMakePreToilReservations(pawn, false))
                    return null;
            }
            catch
            {
                return null;
            }

            return job;
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
                    if (targetInfo == null || !targetInfo.Cell.IsValid ||
                        !targetInfo.Cell.InBounds(pawn.Map))
                    {
                        continue;
                    }

                    ThingDef wanted = WorkGiver_Grower.CalculateWantedPlantDef(
                        targetInfo.Cell, pawn.Map);
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
    /// RimWorld 1.6 Meeseeks task node.
    ///
    /// The first real WorkGiver order becomes a whole WorkType mission. Every Meeseeks
    /// in the creator tree keeps taking work from that category until the category is
    /// actually exhausted. Hunting covers all designated hunt targets. A direct combat
    /// order against a hostile raider becomes a raid-clear mission. Blocked work waits
    /// and accumulates frustration instead of counting as complete.
    /// </summary>
    public class ThinkNode_MeeseeksCompleteTask : ThinkNode
    {
        private readonly MeeseeksJobSelector defaultJobSelector = new MeeseeksJobSelector();
        private readonly List<MeeseeksJobSelector> jobSelectors;

        public ThinkNode_MeeseeksCompleteTask()
        {
            jobSelectors = new List<MeeseeksJobSelector>
            {
                new MeeseeksJobSelector_Guard(),
                new MeeseeksJobSelector_BuildRoof(),
                new MeeseeksJobSelector_DoBill(),
                new MeeseeksJobSelector_Construction(),
                new MeeseeksJobSelector_PressButton(),
                new MeeseeksJobSelector_RemoveRoof(),
                new MeeseeksJobSelector_Tame(),
                new MeeseeksJobSelector_Train()
            };
        }

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            return base.DeepCopy(resolve);
        }

        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            CompMeeseeksMemory memory = pawn.GetComp<CompMeeseeksMemory>();
            if (memory == null || !memory.GivenTask)
                return ThinkResult.NoJob;

            SavedJob savedJob = memory.savedJob;
            if (savedJob == null || CompMeeseeksMemory.noContinueJobs.Contains(savedJob.def))
                return ThinkResult.NoJob;

            if (MeeseeksMissionUtility.TryClassifyMission(
                pawn, memory, out MeeseeksMissionKind missionKind, out WorkTypeDef workType))
            {
                Job missionJob = MeeseeksMissionUtility.FindMissionJob(
                    pawn, memory, missionKind, workType, out bool outstandingWork);

                CompMeeseeksMissionState missionState =
                    pawn.GetComp<CompMeeseeksMissionState>();

                if (missionJob != null)
                {
                    missionState?.NotifyRunnableWork();
                    return new ThinkResult(missionJob, this, JobTag.MiscWork, fromQueue: false);
                }

                bool familyWorking = MeeseeksMissionUtility.FamilyHasActiveMissionWork(
                    memory, missionKind, workType);

                if (outstandingWork)
                {
                    if (familyWorking)
                        missionState?.NotifyWaitingOnFamily();
                    else
                        missionState?.NotifyBlocked();

                    Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 60);
                    return new ThinkResult(wait, this, JobTag.MiscWork, fromQueue: false);
                }

                if (familyWorking)
                {
                    missionState?.NotifyWaitingOnFamily();
                    Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 60);
                    return new ThinkResult(wait, this, JobTag.MiscWork, fromQueue: false);
                }

                bool finished = missionState == null ||
                                missionState.NotifyNoWorkAndCheckComplete();

                if (!finished)
                {
                    Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 60);
                    return new ThinkResult(wait, this, JobTag.MiscWork, fromQueue: false);
                }

                Job finish = JobMaker.MakeJob(
                    MeeseeksDefOf.CM_Meeseeks_Box_Job_EmbraceTheVoid);
                return new ThinkResult(finish, this, JobTag.MiscWork, fromQueue: false);
            }

            // Direct/special jobs with no WorkType keep the older target-based continuation.
            Job nextJob = GetLegacyNextJob(pawn, memory);

            if (nextJob == null && memory.jobTargets.Count > 0 &&
                (savedJob.workGiverDef == null || savedJob.workGiverDef.Worker == null))
            {
                nextJob = TryMakeDirectSavedJob(pawn, memory);
            }

            if (nextJob == null && memory.jobTargets.Count == 0)
                nextJob = JobMaker.MakeJob(MeeseeksDefOf.CM_Meeseeks_Box_Job_EmbraceTheVoid);
            else if (nextJob == null && memory.jobTargets.Count > 0)
                nextJob = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 30);

            return nextJob != null
                ? new ThinkResult(nextJob, this, JobTag.MiscWork, fromQueue: false)
                : ThinkResult.NoJob;
        }

        private Job TryMakeDirectSavedJob(Pawn meeseeks, CompMeeseeksMemory memory)
        {
            SavedJob savedJob = memory.savedJob;
            if (savedJob == null || savedJob.def == null)
                return null;

            if (savedJob.workGiverDef != null && savedJob.workGiverDef.Worker != null)
                return null;

            Job directJob = savedJob.MakeJob();
            if (directJob == null || directJob.def == null)
                return null;

            directJob.playerForced = false;

            try
            {
                if (!directJob.TryMakePreToilReservations(meeseeks, false))
                    return null;
            }
            catch
            {
                return null;
            }

            return directJob;
        }

        private Job GetLegacyNextJob(Pawn meeseeks, CompMeeseeksMemory memory)
        {
            Job nextJob = null;
            SavedJob savedJob = memory.savedJob;
            if (savedJob == null)
                return null;

            if (memory.jobStuck)
                return JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 1);

            List<SavedTargetInfo> delayedTargets = new List<SavedTargetInfo>();
            MeeseeksJobSelector jobSelector = defaultJobSelector;

            foreach (MeeseeksJobSelector eachJobSelector in jobSelectors)
            {
                if (eachJobSelector.UseForJob(meeseeks, memory, savedJob))
                {
                    jobSelector = eachJobSelector;
                    break;
                }
            }

            try
            {
                jobSelector.SortAndFilterJobTargets(meeseeks, memory, savedJob);

                while (memory.jobTargets.Count > 0 && nextJob == null)
                {
                    JobAvailability availability = JobAvailability.Invalid;
                    SavedTargetInfo jobTarget = memory.jobTargets.FirstOrDefault();

                    if (jobTarget == null || !jobTarget.IsValid)
                    {
                        memory.jobTargets.RemoveAt(0);
                        continue;
                    }

                    nextJob = jobSelector.GetJob(
                        meeseeks, memory, savedJob, jobTarget, ref availability);

                    if (nextJob != null)
                    {
                        bool reservationsMade =
                            nextJob.TryMakePreToilReservations(meeseeks, false);
                        if (!reservationsMade)
                        {
                            availability = JobAvailability.Delayed;
                            nextJob = null;
                        }
                    }

                    if (availability == JobAvailability.Delayed)
                    {
                        delayedTargets.Add(jobTarget);
                        memory.jobTargets.RemoveAt(0);
                    }
                    else if (nextJob == null)
                    {
                        memory.jobTargets.RemoveAt(0);
                    }
                }

                if (delayedTargets.Count > 0 && nextJob == null)
                {
                    nextJob = jobSelector.GetJobDelayed(
                        meeseeks, memory, savedJob, delayedTargets[0]);
                }
            }
            finally
            {
                memory.jobTargets.AddRange(delayedTargets);
            }

            return nextJob;
        }
    }
}
