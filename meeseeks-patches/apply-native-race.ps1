$ErrorActionPreference = 'Stop'

# Metadata: RimWorld 1.6 + Harmony only.
Copy-Item 'meeseeks-patches/About_1.6.xml' 'meeseeks/About/About.xml' -Force
Copy-Item 'meeseeks-patches/Manifest_1.6.xml' 'meeseeks/About/Manifest.xml' -Force

# Replace the HAR race def with a native ThingDef race using RimWorld 1.6's Humanlike render tree.
Copy-Item 'meeseeks-patches/AlienRace_Meeseeks_1.6.xml' 'meeseeks/Defs/AlienRaces/AlienRace_Meeseeks.xml' -Force

# Native HeadTypeDef points at the original custom Meeseeks head textures.
New-Item -ItemType Directory -Force -Path 'meeseeks/Defs/HeadTypeDefs' | Out-Null
Copy-Item 'meeseeks-patches/HeadTypes_Meeseeks.xml' 'meeseeks/Defs/HeadTypeDefs/HeadTypes_Meeseeks.xml' -Force

# Small C# compatibility layer replaces HAR's forced appearance, need filtering and thought filtering.
Copy-Item 'meeseeks-patches/MeeseeksNativeRacePatches.cs' 'meeseeks/Source/CM_Meeseeks_Box/Patches/MeeseeksNativeRacePatches.cs' -Force

# --- Old 1.2/HAR XML -> RimWorld 1.6 XML compatibility ---

# HAR supplied its own BackstoryDef subclass. RimWorld 1.6 has a native BackstoryDef,
# and baseDescription was renamed to baseDesc.
$backstory = 'meeseeks/Defs/BackstoryDefs/Backstories_Meeseeks.xml'
$text = Get-Content $backstory -Raw
$text = $text.Replace('<AlienRace.BackstoryDef>', '<BackstoryDef>')
$text = $text.Replace('</AlienRace.BackstoryDef>', '</BackstoryDef>')
$text = $text.Replace('<baseDescription>', '<baseDesc>')
$text = $text.Replace('</baseDescription>', '</baseDesc>')
Set-Content $backstory $text -Encoding UTF8

# FactionDef no longer owns the old hairTags field. Appearance is already forced by
# the native Meeseeks pawn-generation patch, so this obsolete faction-level filter is unnecessary.
$faction = 'meeseeks/Defs/FactionDefs/Factions_Hidden.xml'
$text = Get-Content $faction -Raw
$text = [regex]::Replace($text, '(?s)\s*<hairTags>.*?</hairTags>', '')
Set-Content $faction $text -Encoding UTF8

# MentalStateDef renamed unspawnedCanDo to the more precise unspawnedNotInCaravanCanDo.
$mentalDefs = 'meeseeks/Defs/MentalStateDefs/MentalStates_Mood.xml'
$text = Get-Content $mentalDefs -Raw
$text = $text.Replace('<unspawnedCanDo>', '<unspawnedNotInCaravanCanDo>')
$text = $text.Replace('</unspawnedCanDo>', '</unspawnedNotInCaravanCanDo>')
Set-Content $mentalDefs $text -Encoding UTF8

# RimWorld 1.6 validates humanlike PawnKinds for prisoner resistance/will ranges.
# Meeseeks should have no prisoner resistance or will, so explicitly use zero ranges.
$pawnKind = 'meeseeks/Defs/PawnKindDefs/PawnKinds_Meeseeks.xml'
$text = Get-Content $pawnKind -Raw
if ($text -notmatch '<initialResistanceRange>') {
    $insert = "`r`n`t`t<initialResistanceRange>(0,0)</initialResistanceRange>`r`n`t`t<initialWillRange>(0,0)</initialWillRange>"
    $text = $text.Replace('</PawnKindDef>', "$insert`r`n`t</PawnKindDef>")
}
Set-Content $pawnKind $text -Encoding UTF8

# The old sound XML used AudioGrain_Folder while pointing at individual .ogg files.
# In 1.6 AudioGrain_Folder resolves a directory; AudioGrain_Clip is the correct single-file grain.
$soundDefs = 'meeseeks/Defs/SoundDefs/SoundDef.xml'
$text = Get-Content $soundDefs -Raw
$text = $text.Replace('Class="AudioGrain_Folder"', 'Class="AudioGrain_Clip"')
Set-Content $soundDefs $text -Encoding UTF8

# This is a local test package, not the original Workshop publication.
if (Test-Path 'meeseeks/About/PublishedFileId.txt') {
    Remove-Item 'meeseeks/About/PublishedFileId.txt' -Force
}

# Fail if any active runtime metadata/defs still contain HAR-specific declarations or
# the specific obsolete XML fields that caused the 1.6 startup errors in the first test log.
$activeXml = Get-ChildItem 'meeseeks/About','meeseeks/Defs' -Recurse -Filter *.xml
$obsoletePatterns = @(
    'erdelf\.humanoidalienraces',
    'AlienRace\.ThingDef_AlienRace',
    '<alienRace>',
    'AlienRace\.BackstoryDef',
    '<baseDescription>',
    '<unspawnedCanDo>',
    '<ToxicSensitivity>',
    'Pawn_Melee_Punch_HitBuilding<',
    'Class="AudioGrain_Folder"'
)
foreach ($file in $activeXml) {
    $content = Get-Content $file.FullName -Raw
    foreach ($pattern in $obsoletePatterns) {
        if ($content -match $pattern) {
            throw "Obsolete/HAR XML survived 1.6 conversion in $($file.FullName): $pattern"
        }
    }
}

Write-Host 'Native Meeseeks race layer applied: Harmony required, HAR removed, legacy XML converted for RimWorld 1.6.'
