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

# This is a local test package, not the original Workshop publication.
if (Test-Path 'meeseeks/About/PublishedFileId.txt') {
    Remove-Item 'meeseeks/About/PublishedFileId.txt' -Force
}

# Fail if any active runtime metadata/defs still contain HAR-specific declarations.
$activeXml = Get-ChildItem 'meeseeks/About','meeseeks/Defs' -Recurse -Filter *.xml
foreach ($file in $activeXml) {
    $content = Get-Content $file.FullName -Raw
    if ($content -match 'erdelf\.humanoidalienraces|AlienRace\.ThingDef_AlienRace|<alienRace>') {
        throw "HAR dependency survived native-race conversion in $($file.FullName)"
    }
}

Write-Host 'Native Meeseeks race layer applied: Harmony required, HAR removed from active runtime XML.'
