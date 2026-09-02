$path = 'meeseeks/Source/CM_Meeseeks_Box/Patches/MeeseeksCommandPatches.cs'
$text = Get-Content $path -Raw

$old = @'
                if (___pawn.kindDef == MeeseeksDefOf.MeeseeksKind)
                {
                    __result = Pawn_WorkSettings.DefaultPriority;
                    //Logger.MessageFormat(__instance, "Forcing default work priority of: {0}", __result);
                }
'@

$new = @'
                // RimWorld 1.6 builds WorkGiversInOrderNormal by calling GetPriority for every
                // WorkTypeDef. Preserve zero for disabled work types so a Meeseeks locked to a
                // mission category (Construction, Mining, etc.) does not appear enabled for every
                // work type on the map. Only normalize an already-enabled priority.
                if (___pawn.kindDef == MeeseeksDefOf.MeeseeksKind && __result > 0)
                {
                    __result = Pawn_WorkSettings.DefaultPriority;
                    //Logger.MessageFormat(__instance, "Forcing default work priority of: {0}", __result);
                }
'@

if (-not $text.Contains($old)) {
    throw 'Original Meeseeks GetPriority postfix block not found; refusing to patch blindly.'
}

$text = $text.Replace($old, $new)
Set-Content $path $text -Encoding UTF8

$final = Get-Content $path -Raw
if ($final -notmatch 'Pawn_WorkSettings_GetPriority_IgnorePriorityChanges' -or
    $final -notmatch '___pawn\.kindDef == MeeseeksDefOf\.MeeseeksKind && __result > 0') {
    throw 'Meeseeks work priority gating fix did not apply.'
}

Write-Host 'Meeseeks GetPriority now preserves disabled work types (priority 0) and only normalizes enabled priorities.'
