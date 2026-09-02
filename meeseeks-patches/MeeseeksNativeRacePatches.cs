using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CM_Meeseeks_Box
{
    /// <summary>
    /// Replaces the appearance/need/thought pieces that the original mod delegated to
    /// Humanoid Alien Races. The pawn remains a real custom ThingDef race and uses
    /// RimWorld 1.6's native Humanlike render tree.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MeeseeksNativeRacePatches
    {
        private static readonly Color MeeseeksSkin = new Color(0.40f, 0.80f, 0.93f, 1f);
        private static readonly Color MeeseeksHair = new Color(0.95f, 0.35f, 0.05f, 1f);

        private static readonly HashSet<string> SuppressedThoughts = new HashSet<string>
        {
            "Expectations", "EnvironmentDark", "ApparelDamaged", "WrongApparelGender",
            "DeadMansApparel", "HumanLeatherApparelSad", "EnvironmentCold", "EnvironmentHot",
            "NeedFood", "NeedRest", "NeedJoy", "NeedComfort", "NeedBeauty", "NeedRoomSize",
            "NeedOutdoors", "PrisonCell", "PrisonBarracks", "HospitalPatientRoomStats",
            "ColonistLeftUnburied", "Naked", "Pain", "PsychicDrone", "KnowGuestExecuted",
            "KnowColonistExecuted", "KnowPrisonerDiedInnocent", "KnowColonistDied", "ColonistLost",
            "AteWithoutTable", "SleepDisturbed", "NewColonyOptimism", "NewColonyHope",
            "SleptOutside", "SleptOnGround", "SleptInCold", "SleptInHeat", "KnowPrisonerSold",
            "FreedFromSlavery", "ReleasedHealthyPrisoner", "KnowGuestOrganHarvested",
            "KnowColonistOrganHarvested", "MyOrganHarvested", "WasImprisoned", "Catharsis",
            "KnowBuriedInSarcophagus", "SoakingWet", "ButcheredHumanlikeCorpse",
            "KnowButcheredHumanlikeCorpse", "ObservedLayingCorpse", "ObservedLayingRottingCorpse",
            "WitnessedDeathAlly", "DefeatedHostileFactionLeader", "DefeatedHostileFactionLeaderOpinion",
            "DefeatedMechCluster", "ColonistBanished", "ColonistBanishedToDie", "PrisonerBanishedToDie",
            "AteInImpressiveDiningRoom", "JoyActivityInImpressiveRecRoom", "SleptInBedroom", "SleptInBarracks"
        };

        private static bool IsMeeseeks(Pawn pawn)
        {
            return pawn != null &&
                   (pawn.def == MeeseeksDefOf.MeeseeksRace || pawn.kindDef == MeeseeksDefOf.MeeseeksKind);
        }

        public static void ApplyNativeAppearance(Pawn pawn)
        {
            if (!IsMeeseeks(pawn) || pawn.story == null)
                return;

            pawn.gender = Gender.Male;
            pawn.story.bodyType = BodyTypeDefOf.Thin;
            pawn.story.skinColorOverride = MeeseeksSkin;
            pawn.story.HairColor = MeeseeksHair;

            HeadTypeDef head = DefDatabase<HeadTypeDef>.GetNamedSilentFail("CM_Meeseeks_Box_Head_Happy");
            if (head != null)
                pawn.story.headType = head;

            HairDef hair = DefDatabase<HairDef>.GetNamedSilentFail("CM_Meeseeks_Box_Hair_Meeseeks");
            if (hair != null)
                pawn.story.hairDef = hair;

            if (pawn.style != null)
            {
                pawn.style.beardDef = BeardDefOf.NoBeard;
                pawn.style.FaceTattoo = null;
                pawn.style.BodyTattoo = null;
            }

            if (pawn.needs != null)
            {
                pawn.needs.AddOrRemoveNeedsAsAppropriate();
                pawn.needs.SetInitialLevels();
            }

            if (pawn.Drawer != null && pawn.Drawer.renderer != null)
                pawn.Drawer.renderer.SetAllGraphicsDirty();
        }

        [HarmonyPatch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new Type[] { typeof(PawnGenerationRequest) })]
        public static class PawnGenerator_GeneratePawn_NativeMeeseeksAppearance
        {
            [HarmonyPostfix]
            public static void Postfix(ref Pawn __result)
            {
                ApplyNativeAppearance(__result);
            }
        }

        // The original race intentionally names every summoned pawn "Meeseeks". RimWorld 1.6's
        // race-level NameGenerator path rejects a generated NameSingle if that exact name has ever
        // been used in the game, so the second summon repeatedly generates "Meeseeks", rejects it,
        // and logs "Could not get new name". Skip the uniqueness-based name generator only for this
        // race and preserve the original repeated-name behavior.
        [HarmonyPatch(typeof(PawnBioAndNameGenerator), nameof(PawnBioAndNameGenerator.GeneratePawnName),
            new Type[] { typeof(Pawn), typeof(NameStyle), typeof(string), typeof(bool), typeof(XenotypeDef) })]
        public static class PawnBioAndNameGenerator_GeneratePawnName_Meeseeks
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn pawn, ref Name __result)
            {
                if (!IsMeeseeks(pawn))
                    return true;

                __result = new NameSingle("Meeseeks");
                return false;
            }
        }

        // RimWorld 1.6's humanlike renderer assumes several spawned-pawn trackers and a
        // silhouette are available at draw time. HAR used to provide/normalize this state.
        // Repair any missing spawned trackers before rendering. The finalizer is deliberately
        // scoped to Meeseeks and only absorbs NullReferenceException from this renderer method;
        // this keeps a harmless late-frame null from flooding the log while retaining every
        // other rendering exception for diagnosis.
        [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
        public static class PawnRenderer_RenderPawnAt_NativeMeeseeks
        {
            [HarmonyPrefix]
            public static void Prefix(Pawn ___pawn)
            {
                if (!IsMeeseeks(___pawn))
                    return;

                try
                {
                    if (___pawn.Spawned &&
                        (___pawn.pather == null || ___pawn.roping == null ||
                         ___pawn.flight == null || ___pawn.rotationTracker == null))
                    {
                        PawnComponentsUtility.AddComponentsForSpawn(___pawn);
                    }

                    LifeStageDef stage = ___pawn.ageTracker?.CurLifeStage;
                    if (stage != null && stage.silhouetteGraphicData == null)
                    {
                        LifeStageDef vanillaAdult = DefDatabase<LifeStageDef>.GetNamedSilentFail("HumanlikeAdult");
                        if (vanillaAdult?.silhouetteGraphicData != null)
                            stage.silhouetteGraphicData = vanillaAdult.silhouetteGraphicData;
                    }
                }
                catch (Exception ex)
                {
                    Log.WarningOnce("Mister Meeseeks: render-state repair failed before draw: " + ex, 191061601);
                }
            }

            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception, Pawn ___pawn)
            {
                if (__exception is NullReferenceException && IsMeeseeks(___pawn))
                {
                    LifeStageDef stage = null;
                    try
                    {
                        stage = ___pawn.ageTracker?.CurLifeStage;
                    }
                    catch
                    {
                    }

                    string diagnostics =
                        "Mister Meeseeks: contained RimWorld 1.6 PawnRenderer null. " +
                        "Spawned=" + ___pawn.Spawned +
                        ", stances=" + (___pawn.stances != null) +
                        ", pather=" + (___pawn.pather != null) +
                        ", roping=" + (___pawn.roping != null) +
                        ", flight=" + (___pawn.flight != null) +
                        ", ageTracker=" + (___pawn.ageTracker != null) +
                        ", lifeStage=" + (stage?.defName ?? "null") +
                        ", silhouette=" + (stage?.silhouetteGraphicData != null) + ".";

                    Log.WarningOnce(diagnostics, 191061602);
                    return null;
                }

                return __exception;
            }
        }

        // Meeseeks keep Mood because the mod's Existence Is Pain mechanic depends on it.
        // This must be a PREFIX. During initial pawn construction RimWorld's vanilla
        // ShouldHaveNeed checks DevelopmentalStage before the custom pawn's age/life-stage
        // data has finished initializing, which can throw for chemical needs. HAR used to
        // shield the race from that path. For Meeseeks we know the answer up-front, so skip
        // the vanilla method entirely and only create Mood.
        [HarmonyPatch(typeof(Pawn_NeedsTracker), "ShouldHaveNeed")]
        public static class PawnNeedsTracker_ShouldHaveNeed_Meeseeks
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn ___pawn, NeedDef nd, ref bool __result)
            {
                if (!IsMeeseeks(___pawn))
                    return true;

                __result = nd != null && nd.defName == "Mood";
                return false;
            }
        }

        [HarmonyPatch(typeof(ThoughtUtility), nameof(ThoughtUtility.CanGetThought), new Type[] { typeof(Pawn), typeof(ThoughtDef), typeof(bool) })]
        public static class ThoughtUtility_CanGetThought_Meeseeks
        {
            [HarmonyPostfix]
            public static void Postfix(Pawn pawn, ThoughtDef def, ref bool __result)
            {
                if (!__result || !IsMeeseeks(pawn) || def == null)
                    return;

                if (SuppressedThoughts.Contains(def.defName))
                    __result = false;
            }
        }
    }
}
