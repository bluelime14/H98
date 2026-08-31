using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace CM_Meeseeks_Box
{
    /// <summary>
    /// Restores the intended "Meeseeks can make more Meeseeks for help" behavior.
    /// A taskless Meeseeks may press a Meeseeks Box without treating that button press
    /// as its one life-purpose task. Meeseeks spawned this way remain orderable while
    /// waiting. Once the first real task is accepted, that task is copied recursively
    /// to every Meeseeks descended from that pawn.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MeeseeksChainPatches
    {
        private static bool IsHelperBoxJob(Job job)
        {
            if (job == null)
                return false;

            // This is the special AI job used when a Meeseeks that already has a task
            // decides it needs to create more Meeseeks for help.
            if (job.def == MeeseeksDefOf.CM_Meeseeks_Box_Job_UseMeeseeksBox)
                return true;

            // Manual/right-click use of the cube goes through the generic PressButton
            // job instead. Only treat it as a free helper action when the target really
            // is a Meeseeks Box, so other press-button orders can still be real tasks.
            if (job.def == MeeseeksDefOf.CM_Meeseeks_Box_Job_PressButton)
            {
                Thing target = job.targetA.Thing;
                return target != null && target.def == MeeseeksDefOf.CM_Meeseeks_Box_Thing_Meeseeks_Box;
            }

            return false;
        }

        private static void PropagateTaskToDescendants(CompMeeseeksMemory taskSource)
        {
            if (taskSource == null || !taskSource.givenTask || taskSource.savedJob == null)
                return;

            HashSet<Pawn> visited = new HashSet<Pawn>();
            Pawn root = taskSource.Meeseeks;
            if (root != null)
                visited.Add(root);

            PropagateRecursive(taskSource, taskSource, visited);
        }

        private static void PropagateRecursive(
            CompMeeseeksMemory taskSource,
            CompMeeseeksMemory branch,
            HashSet<Pawn> visited)
        {
            if (branch == null || branch.CreatedMeeseeks == null || branch.CreatedMeeseeks.Count == 0)
                return;

            // Copy the list because interrupting jobs can cause pawn state changes while we iterate.
            List<Pawn> children = new List<Pawn>(branch.CreatedMeeseeks);
            foreach (Pawn child in children)
            {
                if (child == null || child.Destroyed || !visited.Add(child))
                    continue;

                CompMeeseeksMemory childMemory = child.GetComp<CompMeeseeksMemory>();
                if (childMemory == null)
                    continue;

                childMemory.temporarilyBlockTask = false;
                childMemory.CopyJobDataFrom(taskSource);

                // Stop an idle/wait/helper-box job so the child immediately re-evaluates
                // its think tree using the newly inherited real task.
                if (child.Spawned && child.jobs != null && child.CurJob != null)
                    child.jobs.EndCurrentJob(JobCondition.InterruptOptional);

                PropagateRecursive(taskSource, childMemory, visited);
            }
        }

        [HarmonyPatch(typeof(CompMeeseeksMemory), "JobStarted", new System.Type[] { typeof(Job) })]
        public static class CompMeeseeksMemory_JobStarted_HelperChain
        {
            [HarmonyPrefix]
            public static bool Prefix(CompMeeseeksMemory __instance, Job job, ref bool __state)
            {
                __state = __instance.givenTask;

                // A taskless Meeseeks pressing a Meeseeks Box is recruiting helpers,
                // not completing its reason for existence. Do not record either the
                // manual PressButton job or the special UseMeeseeksBox job as its task.
                if (!__instance.givenTask && IsHelperBoxJob(job))
                    return false;

                return true;
            }

            [HarmonyPostfix]
            public static void Postfix(CompMeeseeksMemory __instance, Job job, bool __state)
            {
                // JobStarted can be reached more than once for an ordered job. Only
                // propagate on the transition from no task -> real task.
                if (!__state && __instance.givenTask && !IsHelperBoxJob(job))
                    PropagateTaskToDescendants(__instance);
            }
        }

        [HarmonyPatch(typeof(MeeseeksUtility), nameof(MeeseeksUtility.SpawnMeeseeks),
            new System.Type[] { typeof(Pawn), typeof(ThingWithComps), typeof(Map) })]
        public static class MeeseeksUtility_SpawnMeeseeks_HelperChain
        {
            [HarmonyPostfix]
            public static void Postfix(Pawn creator)
            {
                CompMeeseeksMemory creatorMemory = creator?.GetComp<CompMeeseeksMemory>();
                if (creatorMemory == null)
                    return;

                Pawn child = MeeseeksUtility.lastCreatedMeeseeks;
                if (child == null || child.Destroyed)
                    return;

                CompMeeseeksMemory childMemory = child.GetComp<CompMeeseeksMemory>();
                if (childMemory == null)
                    return;

                // The original SpawnMeeseeks method blocks every child created by a Meeseeks
                // and expects JobDriver_UseMeeseeksBox to unblock it later. Manual PressButton
                // never runs that driver, which left the child permanently unable to take
                // orders and looking "stuck" on Relaxing Socially. If the creator has no real
                // task yet, this is a helper-building chain, so release the child immediately.
                if (!creatorMemory.givenTask)
                {
                    childMemory.temporarilyBlockTask = false;

                    // If RimWorld already selected an idle/social job during the spawn frame,
                    // interrupt it so the pawn becomes immediately responsive to player orders.
                    if (child.Spawned && child.jobs != null && child.CurJob != null)
                        child.jobs.EndCurrentJob(JobCondition.InterruptOptional);
                }
            }
        }

        [HarmonyPatch(typeof(CompMeeseeksMemory), nameof(CompMeeseeksMemory.ForceNewJob))]
        public static class CompMeeseeksMemory_ForceNewJob_HelperChain
        {
            [HarmonyPrefix]
            public static void Prefix(CompMeeseeksMemory __instance, ref bool __state)
            {
                __state = __instance.givenTask;
            }

            [HarmonyPostfix]
            public static void Postfix(CompMeeseeksMemory __instance, bool __state)
            {
                if (!__state && __instance.givenTask)
                    PropagateTaskToDescendants(__instance);
            }
        }
    }
}
