using System;
using System.Collections.Generic;

using UnityEngine;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CM_Meeseeks_Box
{
    [StaticConstructorOnStartup]
    public static class FloatMenuMakerMapPatches
    {
        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn))]
        public static class MeeseeksShouldGenerateFloatMenu
        {
            [HarmonyPostfix]
            public static void Postfix(Pawn pawn, ref AcceptanceReport __result)
            {
                if (pawn == null)
                    return;

                CompMeeseeksMemory memory = pawn.GetComp<CompMeeseeksMemory>();
                if (memory == null)
                    return;

                if (!memory.CanTakeOrders())
                    __result = false;
            }
        }

        public sealed class MenuPatchState
        {
            public Pawn pawn;
            public IntVec3 cell;
            public bool forcedDesignations;
        }

        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
        public static class FloatMenuMakerMap_GetOptions_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(List<Pawn> selectedPawns, Vector3 clickPos, ref MenuPatchState __state)
            {
                __state = new MenuPatchState();
                if (selectedPawns == null || selectedPawns.Count != 1)
                    return;

                Pawn pawn = selectedPawns[0];
                if (pawn == null)
                    return;

                CompMeeseeksMemory memory = pawn.GetComp<CompMeeseeksMemory>();
                if (memory == null || !memory.CanTakeOrders() || pawn.MapHeld == null)
                    return;

                IntVec3 cell = IntVec3.FromVector3(clickPos);
                if (!cell.InBounds(pawn.MapHeld))
                    return;

                __state.pawn = pawn;
                __state.cell = cell;
                DesignatorUtility.ForceAllDesignationsOnCell(cell, pawn.MapHeld);
                __state.forcedDesignations = true;
            }

            [HarmonyPostfix]
            public static void Postfix(List<FloatMenuOption> __result, ref MenuPatchState __state)
            {
                if (__result == null || __state == null || __state.pawn == null)
                    return;

                Pawn pawn = __state.pawn;
                CompMeeseeksMemory memory = pawn.GetComp<CompMeeseeksMemory>();
                if (memory == null || !memory.CanTakeOrders())
                    return;

                FloatMenuOption guardOption = GuardLocationOption(memory, __state.cell, pawn);
                if (guardOption != null)
                    __result.Add(guardOption);
            }

            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception, ref MenuPatchState __state)
            {
                if (__state != null && __state.forcedDesignations && __state.pawn?.MapHeld != null)
                    DesignatorUtility.RestoreDesignationsOnCell(__state.cell, __state.pawn.MapHeld);
                return __exception;
            }

            private static FloatMenuOption GuardLocationOption(CompMeeseeksMemory memory, IntVec3 clickCell, Pawn pawn)
            {
                int num = GenRadial.NumCellsInRadius(2.9f);
                for (int i = 0; i < num; i++)
                {
                    IntVec3 curLoc = GenRadial.RadialPattern[i] + clickCell;
                    if (!curLoc.Standable(pawn.Map))
                        continue;
                    if (curLoc == pawn.Position)
                        return null;
                    if (!pawn.CanReach(curLoc, PathEndMode.OnCell, Danger.Deadly))
                        return new FloatMenuOption("CannotGoNoPath".Translate(), null);

                    Action action = delegate
                    {
                        memory.guardPosition = curLoc;
                        Job job = JobMaker.MakeJob(JobDefOf.Goto, curLoc);
                        job.playerForced = true;
                        pawn.drafter.Drafted = true;
                        if (!pawn.jobs.TryTakeOrderedJob(job))
                            pawn.drafter.Drafted = false;
                    };

                    return new FloatMenuOption("CM_Meeseeks_Box_GuardHere".Translate(), action, MenuOptionPriority.GoHere)
                    {
                        autoTakeable = false,
                        autoTakeablePriority = 10f
                    };
                }
                return null;
            }
        }
    }
}
