$ErrorActionPreference = 'Stop'

# v29 incorporates the three remaining source-level fixes independently identified in the
# v27 investigation, but applies them directly to the clean v28 source build.

# -----------------------------------------------------------------------------
# Fix 1: immediate helper task hand-off in MeeseeksChainPatches.cs
# -----------------------------------------------------------------------------
$chainPath = 'meeseeks/Source/CM_Meeseeks_Box/Patches/MeeseeksChainPatches.cs'
$chain = (Get-Content $chainPath -Raw).Replace("`r`n", "`n")

$oldSpawnBlock = @'
                // The original SpawnMeeseeks method blocks every child created by a Meeseeks
                // and expects JobDriver_UseMeeseeksBox to unblock it later. Manual PressButton
                // never runs that driver, which left the child permanently unable to take
                // orders and looking "stuck" on Relaxing Socially. If the creator has no real
                // task yet, this is a helper-building chain, so release the child immediately.
                if (!creatorMemory.givenTask)
                {
                    childMemory.temporarilyBlockTask = false;

                    // Clear any idle job selected during the spawn frame and let the child's
                    // normal job tracker choose its next action on its own next tick.
                    if (child.Spawned && child.jobs != null && child.CurJob != null)
                        child.jobs.EndCurrentJob(JobCondition.InterruptOptional, false);
                }
'@
$oldSpawnBlock = $oldSpawnBlock.Replace("`r`n", "`n")

$newSpawnBlock = @'
                // Every Meeseeks child starts temporarily blocked. Release it immediately rather
                // than waiting for JobDriver_UseMeeseeksBox's delayed hand-off toil. If the creator
                // already has its life-purpose task, inherit that task now; otherwise this is still
                // the original taskless helper-building case and the child simply becomes orderable.
                childMemory.temporarilyBlockTask = false;
                if (creatorMemory.givenTask)
                    childMemory.CopyJobDataFrom(creatorMemory);

                // Clear any spawn-frame idle/helper job. The child's normal job tracker will pick
                // up the inherited task (or remain available for an order) on its own next tick.
                if (child.Spawned && child.jobs != null && child.CurJob != null)
                    child.jobs.EndCurrentJob(JobCondition.InterruptOptional, false);
'@
$newSpawnBlock = $newSpawnBlock.Replace("`r`n", "`n")

if (-not $chain.Contains($oldSpawnBlock)) {
    throw 'v29 Fix 1: expected v28 SpawnMeeseeks helper block not found; refusing to patch blindly.'
}
$chain = $chain.Replace($oldSpawnBlock, $newSpawnBlock)
Set-Content $chainPath $chain -Encoding UTF8

# -----------------------------------------------------------------------------
# Fix 2: defensive delayed hand-off toil in JobDriver_UseMeeseeksBox.cs
# -----------------------------------------------------------------------------
$driverPath = 'meeseeks/Source/CM_Meeseeks_Box/Jobs/JobDriver_UseMeeseeksBox.cs'
$driver = (Get-Content $driverPath -Raw).Replace("`r`n", "`n")

if ($driver -notmatch '(?m)^using System;$') {
    $driver = $driver.Replace('using System.Collections.Generic;', "using System;`nusing System.Collections.Generic;")
}

$oldDelayedBlock = @'
                    if (newestCreated != null)
                    {
                        CompMeeseeksMemory newCreatedMemory = newestCreated.GetComp<CompMeeseeksMemory>();

                        if (newCreatedMemory != null)
                        {
                            newCreatedMemory.CopyJobDataFrom(compMeeseeksMemory);
                            if (compMeeseeksMemory.givenTask)
                                newestCreated.jobs.EndCurrentJob(JobCondition.InterruptOptional);
                            else
                                newCreatedMemory.temporarilyBlockTask = false;
                        }
                    }
'@
$oldDelayedBlock = $oldDelayedBlock.Replace("`r`n", "`n")

$newDelayedBlock = @'
                    if (newestCreated != null)
                    {
                        if (!newestCreated.Destroyed && newestCreated.Spawned)
                        {
                            try
                            {
                                CompMeeseeksMemory newCreatedMemory = newestCreated.GetComp<CompMeeseeksMemory>();

                                if (newCreatedMemory != null)
                                {
                                    newCreatedMemory.CopyJobDataFrom(compMeeseeksMemory);
                                    if (compMeeseeksMemory.givenTask)
                                        newestCreated.jobs.EndCurrentJob(JobCondition.InterruptOptional);
                                    else
                                        newCreatedMemory.temporarilyBlockTask = false;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning("Mister Meeseeks: delayed helper hand-off failed for " + newestCreated + ": " + ex);
                            }
                        }
                        else
                        {
                            Log.Warning("Mister Meeseeks: delayed helper hand-off skipped because the newest helper was destroyed or despawned: " + newestCreated);
                        }
                    }
'@
$newDelayedBlock = $newDelayedBlock.Replace("`r`n", "`n")

if (-not $driver.Contains($oldDelayedBlock)) {
    throw 'v29 Fix 2: expected v28 JobDriver_UseMeeseeksBox delayed hand-off block not found; refusing to patch blindly.'
}
$driver = $driver.Replace($oldDelayedBlock, $newDelayedBlock)
Set-Content $driverPath $driver -Encoding UTF8

# -----------------------------------------------------------------------------
# Fix 3: restore vanilla configurable hostility response in the constant think tree
# -----------------------------------------------------------------------------
$treePath = 'meeseeks/Defs/ThinkTreeDefs/ThinkTrees_Meeseeks.xml'
$tree = Get-Content $treePath -Raw

# In the original XML the node is inside a two-line comment:
#   <!-- Hostility response
#       <li Class="JobGiver_ConfigurableHostilityResponse" /> -->
# Replace the whole comment, not just the <li> text inside it.
$commentedHostility = '(?s)<!--\s*Hostility response\s*<li\s+Class="JobGiver_ConfigurableHostilityResponse"\s*/>\s*-->'
$activeHostility = '<!-- Hostility response -->' + "`r`n" + '            <li Class="JobGiver_ConfigurableHostilityResponse" />'

if ($tree -match $commentedHostility) {
    $tree = [regex]::Replace($tree, $commentedHostility, $activeHostility, 1)
}

Set-Content $treePath $tree -Encoding UTF8
[xml]$xml = Get-Content $treePath -Raw
$xml.Save((Resolve-Path $treePath))

# -----------------------------------------------------------------------------
# Validation
# -----------------------------------------------------------------------------
$chainFinal = Get-Content $chainPath -Raw
if ($chainFinal -notmatch 'childMemory\.temporarilyBlockTask = false;\s*if \(creatorMemory\.givenTask\)\s*childMemory\.CopyJobDataFrom\(creatorMemory\);') {
    throw 'v29 validation: immediate helper task hand-off is missing.'
}

$driverFinal = Get-Content $driverPath -Raw
foreach ($marker in @('using System;','!newestCreated.Destroyed && newestCreated.Spawned','catch (Exception ex)','delayed helper hand-off failed')) {
    if ($driverFinal -notmatch [regex]::Escape($marker)) {
        throw "v29 validation: guarded delayed hand-off marker missing: $marker"
    }
}

# Validate through the XML DOM so text inside a comment cannot produce a false positive.
[xml]$treeXml = Get-Content $treePath -Raw
$hostilityNodes = $treeXml.SelectNodes("//ThinkTreeDef[defName='MeeseeksConstantThinkTree']//li[@Class='JobGiver_ConfigurableHostilityResponse']")
if ($null -eq $hostilityNodes -or $hostilityNodes.Count -ne 1) {
    throw "v29 validation: expected exactly one active JobGiver_ConfigurableHostilityResponse under MeeseeksConstantThinkTree; found $($hostilityNodes.Count)."
}

Write-Host 'v29 helper safety applied: immediate inherited-task hand-off, guarded delayed hand-off, and constant-think-tree hostility response are active.'
