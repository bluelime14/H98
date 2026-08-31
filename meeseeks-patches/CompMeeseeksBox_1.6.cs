using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace CM_Meeseeks_Box
{
    [StaticConstructorOnStartup]
    public class CompMeeseeksBox : ThingComp
    {
        public CompProperties_MeeseeksBox Props => props as CompProperties_MeeseeksBox;

        public int cooldownTicksRemaining = 0;

        public int cooldownTicksTotal = 0;

        private Effecter progressBar = null;

        public bool Coolingdown => cooldownTicksRemaining > 0;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            QualityCategory boxQuality = QualityCategory.Normal;
            parent.TryGetQuality(out boxQuality);
            int qualityInt = (int)boxQuality;

            float cooldownMultiplier = ((float)(qualityInt + 1)) / ((int)QualityCategory.Legendary + 1);

            cooldownTicksTotal = (int)(Props.cooldownTicksBase * cooldownMultiplier * cooldownMultiplier);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownTicksRemaining, "cooldownTicksRemaining", 0);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
                yield return gizmo;

            if (Prefs.DevMode && DebugSettings.godMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Reset Meeseeks box cooldown",
                    defaultDesc = "Immediately clears the Mister Meeseeks Box summon cooldown for testing.",
                    action = ResetCooldown
                };
            }
        }

        private void CleanupProgressBar()
        {
            if (progressBar == null)
                return;

            progressBar.Cleanup();
            progressBar = null;
        }

        private void ResetCooldown()
        {
            cooldownTicksRemaining = 0;
            CleanupProgressBar();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            // The cooldown bar is a free-standing Effecter/Mote. If the box disappears while
            // cooling down, it will otherwise remain on the map with no owner to clean it up.
            CleanupProgressBar();
            base.PostDeSpawn(map, mode);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            // PostDestroy can follow PostDeSpawn depending on how the box is removed. The helper
            // is idempotent, so calling it here as well covers God Mode destruction and other paths.
            CleanupProgressBar();
            base.PostDestroy(mode, previousMap);
        }

        public override void CompTick()
        {
            base.CompTick();

            if (Coolingdown)
                cooldownTicksRemaining -= 1;

            if (!Coolingdown)
            {
                CleanupProgressBar();
            }
            else
            {
                if (progressBar == null)
                {
                    EffecterDef progressBarDef = MeeseeksDefOf.CM_Meeseeks_Box_Effecter_Progress_Bar;
                    progressBar = progressBarDef.Spawn();
                }
                else
                {
                    progressBar.EffectTick(this.parent, TargetInfo.Invalid);

                    MoteProgressBar_Colored mote = ((SubEffecter_ProgressBar_Colored)progressBar.children[0]).mote;
                    if (mote != null)
                    {
                        mote.SetFilledColor(new Color(0.95f, 0.10f, 0.15f));
                        mote.progress = Mathf.Clamp01(((float)cooldownTicksRemaining / cooldownTicksTotal));
                        mote.offsetZ = -0.5f;
                    }
                }
            }
        }

        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);

            if (Coolingdown)
                return;

            if (signal.StartsWith("CM_Meeseeks_Box_Button_Presser:"))
            {
                Logger.MessageFormat(this, "Got pressed");
                int colon = signal.IndexOf(":") + 1;
                bool validPresserSignal = (colon >= 0 && colon < signal.Length);
                if (validPresserSignal)
                {
                    string presserID = signal.Substring(colon);

                    Pawn presser = parent.MapHeld.mapPawns.AllPawns.Where(pawn => pawn.ThingID == presserID).FirstOrDefault();

                    if (presser != null)
                    {
                        Logger.MessageFormat(this, "Make Meeseeks");
                        SpawnMeeseeks(presser);
                    }
                }
            }
        }

        private void SpawnMeeseeks(Pawn creator)
        {
            // Only consume the cooldown after pawn creation succeeds. If generation throws,
            // the player can immediately retry instead of being left with an empty cooldown.
            MeeseeksUtility.SpawnMeeseeks(creator, parent, creator.MapHeld);

            if (!Prefs.DevMode || !MeeseeksMod.settings.screenShotDebug)
                cooldownTicksRemaining = cooldownTicksTotal;
        }
    }
}
