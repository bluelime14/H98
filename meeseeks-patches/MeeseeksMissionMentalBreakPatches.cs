using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CM_Meeseeks_Box
{
    /// <summary>
    /// The original mod allows the KillCreator MentalBreakDef to be selected by the normal
    /// mood mental-break lottery. In the persistent 1.6 mission system that conflicts with
    /// mission handling: a Meeseeks can randomly enter creator-murder mode while a perfectly
    /// valid Construction/Mining/etc. mission is still running.
    ///
    /// The 1.6 mission state already has its own deliberate impossible-task frustration timer
    /// and directly starts CM_Meeseeks_Box_MentalState_MeeseeksKillCreator when that timer is
    /// exhausted. Blocking the MentalBreakDef here therefore removes only the random mood
    /// route; the intended "existence is pain / impossible task" failure behavior remains.
    /// </summary>
    [HarmonyPatch(typeof(MentalBreakWorker), "BreakCanOccur")]
    public static class MeeseeksMissionMentalBreakPatches
    {
        [HarmonyPostfix]
        public static void PreventRandomCreatorMurder(
            MentalBreakWorker __instance,
            Pawn pawn,
            ref bool __result)
        {
            if (!__result || pawn == null || pawn.GetComp<CompMeeseeksMemory>() == null)
                return;

            if (__instance?.def?.defName == "CM_Meeseeks_Box_MentalBreak_MeeseeksKillCreator")
                __result = false;
        }
    }
}
