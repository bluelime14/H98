using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace CM_Meeseeks_Box
{
    /// <summary>
    /// Restores the intended "Meeseeks can make more Meeseeks for help" behavior.
    /// Pressing a Meeseeks Box before the pawn has received its real task is treated as
    /// a free helper action, not as the one task whose completion makes that Meeseeks vanish.
    /// Once the first real task is accepted, that task is copied recursively to every
    /// Meeseeks descended from that pawn so the whole helper chain works on it together.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MeeseeksChainPatches
    {
        private static bool IsUseMeeseeksBoxJob(Job job)
        {
            return job != null && job.def == MeeseeksDefOf.CM_Meeseeks_Box_Job_UseMeeseeksBox;
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

                // A child spawned by another Meeseeks starts temporarily blocked so its greeting
                // and the creator's request do not race each other. Receiving the family task
                // should always release that block.
                childMemory.temporarilyBlockTask = false;
                childMemory.CopyJobDataFrom(taskSource);

                // Make the child stop waiting/pressing a box and immediately re-evaluate its
                // think tree using the newly copied real task.
                if (child.Spawned && child.jobs != null && child.CurJob != null)
                    child.jobs.EndCurrentJob(JobCondition.InterruptOptional);

                // Descendants can themselves have created Meeseeks, so carry the same root task
                // all the way down the chain instead of only updating direct children.
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

                // If a taskless Meeseeks is told to press a Meeseeks Box, that action exists to
                // recruit help. Do not let the private JobStarted method record the box press as
                // the Meeseeks' one real task, otherwise the cooldown makes the task immediately
                // "complete" and the creator poofs.
                if (!__instance.givenTask && IsUseMeeseeksBoxJob(job))
                    return false;

                return true;
            }

            [HarmonyPostfix]
            public static void Postfix(CompMeeseeksMemory __instance, Job job, bool __state)
            {
                // JobStarted may be reached twice during an ordered job. Only propagate on the
                // transition from no task -> real task.
                if (!__state && __instance.givenTask && !IsUseMeeseeksBoxJob(job))
                    PropagateTaskToDescendants(__instance);
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
                // Some special Meeseeks orders use ForceNewJob rather than the normal ordered-job
                // path. Give descendants the same behavior there as well.
                if (!__state && __instance.givenTask)
                    PropagateTaskToDescendants(__instance);
            }
        }
    }
}
