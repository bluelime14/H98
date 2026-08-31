using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// RimWorld 1.6-compatible Meeseeks task driver.
    ///
    /// In addition to the original WorkGiver reconstruction path, this version can fall back to
    /// recreating the saved ordered job directly when a task has no usable WorkGiverDef. It also
    /// converts shared vanilla Hunt orders into the mod's reservation-free Meeseeks Kill job so a
    /// whole helper chain can attack the same prey instead of all but one waiting on reservations.
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

            Job nextJob = GetNextJob(pawn, memory);

            // Some player-forced/special jobs do not have a usable WorkGiverDef. The old mod could
            // remember these jobs but its generic retry selector could never recreate them for a
            // copied/helper Meeseeks. Rebuild the saved job itself in that case.
            if (nextJob == null && memory.jobTargets.Count > 0 &&
                (savedJob.workGiverDef == null || savedJob.workGiverDef.Worker == null))
            {
                nextJob = TryMakeDirectSavedJob(pawn, memory);
            }

            if (nextJob == null && memory.jobTargets.Count == 0)
            {
                nextJob = JobMaker.MakeJob(MeeseeksDefOf.CM_Meeseeks_Box_Job_EmbraceTheVoid);
            }
            else if (nextJob == null && memory.jobTargets.Count > 0)
            {
                // The task still exists but is temporarily unavailable. Stay committed and retry
                // soon instead of falling through to Relaxing Socially.
                nextJob = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 30);
            }

            return nextJob != null
                ? new ThinkResult(nextJob, this, JobTag.MiscWork, fromQueue: false)
                : ThinkResult.NoJob;
        }

        private static bool IsSharedHunt(SavedJob savedJob)
        {
            if (savedJob == null)
                return false;

            if (savedJob.def == JobDefOf.Hunt)
                return true;

            return savedJob.workGiverDef != null &&
                   savedJob.workGiverDef.Worker is WorkGiver_HunterHunt;
        }

        private Job TryMakeSharedHuntJob(CompMeeseeksMemory memory)
        {
            // Vanilla WorkGiver_HunterHunt requires CanReserve(target), which means only one hunter
            // can own the prey. Meeseeks' custom Kill job intentionally makes no such reservation,
            // allowing every member of the creator chain to pile onto the same shared objective.
            while (memory.jobTargets.Count > 0)
            {
                SavedTargetInfo targetInfo = memory.jobTargets[0];
                if (targetInfo == null || !targetInfo.IsValid || !targetInfo.HasThing)
                {
                    memory.jobTargets.RemoveAt(0);
                    continue;
                }

                Pawn prey = targetInfo.Thing as Pawn;
                if (prey == null || prey.Destroyed || prey.Dead || !prey.Spawned)
                {
                    memory.jobTargets.RemoveAt(0);
                    continue;
                }

                Job killJob = JobMaker.MakeJob(MeeseeksDefOf.CM_Meeseeks_Box_Job_Kill, prey);
                killJob.playerForced = false;
                return killJob;
            }

            return null;
        }

        private Job TryMakeDirectSavedJob(Pawn meeseeks, CompMeeseeksMemory memory)
        {
            SavedJob savedJob = memory.savedJob;
            if (savedJob == null || savedJob.def == null)
                return null;

            // Do not bypass normal WorkGiver scanning/reservations. This fallback is only for jobs
            // that genuinely have no usable WorkGiver reconstruction path.
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

        private Job GetNextJob(Pawn meeseeks, CompMeeseeksMemory memory)
        {
            Job nextJob = null;
            SavedJob savedJob = memory.savedJob;
            if (savedJob == null)
                return null;

            if (memory.jobStuck)
                return JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 1);

            // A vanilla Hunt order is a shared "make this animal dead" objective for a Meeseeks
            // chain. Do not route helpers back through WorkGiver_HunterHunt, because its reservation
            // check guarantees all but one of them will stand around waiting.
            if (IsSharedHunt(savedJob))
                return TryMakeSharedHuntJob(memory);

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

                    nextJob = jobSelector.GetJob(meeseeks, memory, savedJob, jobTarget, ref availability);

                    if (nextJob != null)
                    {
                        bool reservationsMade = nextJob.TryMakePreToilReservations(meeseeks, false);
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
                    nextJob = jobSelector.GetJobDelayed(meeseeks, memory, savedJob, delayedTargets[0]);
            }
            finally
            {
                memory.jobTargets.AddRange(delayedTargets);
            }

            return nextJob;
        }
    }
}
