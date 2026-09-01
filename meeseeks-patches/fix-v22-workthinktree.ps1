$path = 'meeseeks/Source/CM_Meeseeks_Box/Patches/MeeseeksVanillaWorkThinkTreePatches.cs'
$text = Get-Content $path -Raw

# 1) The v21 StartJob interception was too low-level. Remove that entire safety-net class.
$guardMarker = '    /// <summary>`r`n    /// A WorkType mission must never accidentally turn into Meeseeks-on-Meeseeks combat.'
$guardIndex = $text.IndexOf($guardMarker)
if ($guardIndex -lt 0) {
    $guardMarker = "    /// <summary>`n    /// A WorkType mission must never accidentally turn into Meeseeks-on-Meeseeks combat."
    $guardIndex = $text.IndexOf($guardMarker)
}
if ($guardIndex -lt 0) { throw 'v21 friendly-attack StartJob guard marker not found' }
$text = $text.Substring(0, $guardIndex).TrimEnd() + "`r`n}`r`n"

# 2) Make the conditional side-effect free. The child work giver will initialize work settings.
$oldSatisfied = @'
        protected override bool Satisfied(Pawn pawn)
        {
            if (!MeeseeksVanillaWorkMissionUtility.TryGetWorkMission(pawn, out WorkTypeDef workType))
                return false;

            MeeseeksVanillaWorkMissionUtility.LockPawnToMissionWorkType(pawn, workType);
            return true;
        }
'@
$newSatisfied = @'
        protected override bool Satisfied(Pawn pawn)
        {
            try
            {
                return MeeseeksVanillaWorkMissionUtility.TryGetWorkMission(pawn, out _);
            }
            catch (Exception ex)
            {
                Log.Error("Mister Meeseeks v22: mission-condition exception for " + pawn + ": " + ex);
                return false;
            }
        }
'@
if (-not $text.Contains($oldSatisfied)) { throw 'v21 mission conditional block not found' }
$text = $text.Replace($oldSatisfied, $newSatisfied)

# 3) Put a very small adapter around RimWorld's real JobGiver_Work. It does not choose targets
# itself; it only guarantees summoned helpers have initialized work settings before vanilla work.
$utilityMarker = '    public static class MeeseeksVanillaWorkMissionUtility'
$utilityIndex = $text.IndexOf($utilityMarker)
if ($utilityIndex -lt 0) { throw 'MeeseeksVanillaWorkMissionUtility marker not found' }
$wrapper = @'
    /// <summary>
    /// v22 adapter around RimWorld's normal work scheduler.  Target selection, reservations,
    /// construction transitions and queued jobs remain entirely vanilla.
    /// </summary>
    public class JobGiver_MeeseeksMissionWork : JobGiver_Work
    {
        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            if (!MeeseeksVanillaWorkMissionUtility.TryGetWorkMission(pawn, out WorkTypeDef workType))
                return ThinkResult.NoJob;

            try
            {
                MeeseeksVanillaWorkMissionUtility.LockPawnToMissionWorkType(pawn, workType);
                ThinkResult result = base.TryIssueJobPackage(pawn, jobParams);
                if (result.IsValid)
                    pawn.GetComp<CompMeeseeksMissionState>()?.NotifyRunnableWork();
                return result;
            }
            catch (Exception ex)
            {
                CompMeeseeksMemory memory = pawn?.GetComp<CompMeeseeksMemory>();
                string saved = memory?.savedJob?.def?.defName ?? "<null>";
                string initialized = pawn?.workSettings == null ? "null" : pawn.workSettings.Initialized.ToString();
                Log.Error(
                    "Mister Meeseeks v22: vanilla work wrapper exception. pawn=" + pawn +
                    ", workType=" + (workType?.defName ?? "<null>") +
                    ", savedJob=" + saved +
                    ", workSettingsInitialized=" + initialized +
                    ".\n" + ex);
                return ThinkResult.NoJob;
            }
        }
    }

'@
$text = $text.Insert($utilityIndex, $wrapper)

Set-Content $path $text -Encoding UTF8

# 4) Replace the raw JobGiver_Work XML child with the adapter.
$tree = 'meeseeks/Defs/ThinkTreeDefs/ThinkTrees_Meeseeks.xml'
$xmlText = Get-Content $tree -Raw
$raw = '<li Class="JobGiver_Work" />'
$wrapped = '<li Class="CM_Meeseeks_Box.JobGiver_MeeseeksMissionWork" />'
if ($xmlText -notmatch [regex]::Escape('ThinkNode_ConditionalMeeseeksWorkMission')) {
    throw 'v21 work-mission conditional is missing from think tree'
}
if ($xmlText -notmatch [regex]::Escape($raw)) {
    throw 'v21 raw JobGiver_Work child not found in think tree'
}
$xmlText = $xmlText.Replace($raw, $wrapped)
Set-Content $tree $xmlText -Encoding UTF8
[xml]$xml = Get-Content $tree -Raw
$xml.Save((Resolve-Path $tree))

# Static checks for the regression we are removing.
$final = Get-Content $path -Raw
if ($final -match 'MeeseeksWorkMissionFriendlyAttackGuard' -or $final -match 'HarmonyPatch\(typeof\(Pawn_JobTracker\), nameof\(Pawn_JobTracker.StartJob\)\)') {
    throw 'Unsafe v21 StartJob interception remains'
}
foreach ($marker in @('JobGiver_MeeseeksMissionWork','base.TryIssueJobPackage','workSettingsInitialized','LockPawnToMissionWorkType')) {
    if ($final -notmatch [regex]::Escape($marker)) { throw "v22 work wrapper marker missing: $marker" }
}
Write-Host 'v22: removed StartJob interception and wrapped vanilla JobGiver_Work with summoned-helper work-settings initialization and diagnostics.'
