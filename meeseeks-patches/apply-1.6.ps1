$ErrorActionPreference = 'Stop'

$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>CM_Meeseeks_Box</AssemblyName>
    <RootNamespace>CM_Meeseeks_Box</RootNamespace>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <OutputPath>..\..\Assemblies\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <DebugType>none</DebugType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Krafs.Rimworld.Ref" Version="1.6.4871" PrivateAssets="all" />
    <PackageReference Include="Lib.Harmony" Version="2.4.2" PrivateAssets="all" />
  </ItemGroup>
</Project>
'@
Set-Content -Path meeseeks/Source/CM_Meeseeks_Box/CM_Meeseeks_Box.csproj -Value $project -Encoding UTF8

$debug = 'meeseeks/Source/CM_Meeseeks_Box/Patches/DebugPatches.cs'
$text = Get-Content $debug -Raw
if ($text -notmatch 'using LudeonTK;') {
  $text = $text -replace 'using HarmonyLib;', "using HarmonyLib;`r`nusing LudeonTK;"
  Set-Content $debug $text -Encoding UTF8
}

$pps = 'meeseeks/Source/CM_Meeseeks_Box/Patches/Pawn_PlayerSettingsPatches.cs'
$text = Get-Content $pps -Raw
$text = $text -replace 'AreaRestriction', 'AreaRestrictionInPawnCurrentMap'
Set-Content $pps $text -Encoding UTF8

$letters = 'meeseeks/Source/CM_Meeseeks_Box/Patches/LetterStackPatches.cs'
$text = Get-Content $letters -Raw
$text = $text -replace 'typeof\(List<ThingDef>\), typeof\(string\) \}\)\]', 'typeof(List<ThingDef>), typeof(string), typeof(int), typeof(bool) })]'
Set-Content $letters $text -Encoding UTF8

$memory = 'meeseeks/Source/CM_Meeseeks_Box/Comps/CompMeeseeksMemory.cs'
$text = Get-Content $memory -Raw
$text = $text -replace 'PostPreApplyDamage\(DamageInfo dinfo, out bool absorbed\)', 'PostPreApplyDamage(ref DamageInfo dinfo, out bool absorbed)'
$text = $text -replace 'base\.PostPreApplyDamage\(dinfo, out absorbed\);', 'base.PostPreApplyDamage(ref dinfo, out absorbed);'
$text = $text -replace 'PostDeSpawn\(Map map\)', 'PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)'
$text = $text -replace 'base\.PostDeSpawn\(map\);', 'base.PostDeSpawn(map, mode);'
Set-Content $memory $text -Encoding UTF8

$area = 'meeseeks/Source/CM_Meeseeks_Box/Designator_AreaWorkMeeseeks.cs'
$text = Get-Content $area -Raw
$text = $text -replace '(?m)^\s*public override int DraggableDimensions => 2;\s*\r?\n', ''
$text = $text -replace 'SoundDefOf\.Designate_Harvest', 'SoundDefOf.Designate_DragStandard_Changed'
Set-Content $area $text -Encoding UTF8

# Replacement implementation handles 1.6 designation indexing correctly.
Copy-Item 'meeseeks-patches/DesignatorUtility.cs' 'meeseeks/Source/CM_Meeseeks_Box/DesignatorUtility.cs' -Force

$mote = 'meeseeks/Source/CM_Meeseeks_Box/Effecters/MoteProgressBar_Colored.cs'
$text = Get-Content $mote -Raw
$text = $text -replace 'public override void Draw\(\)', 'protected override void DrawAt(Vector3 drawLoc, bool flip = false)'
$text = $text -replace 'exactScale\.x, exactScale\.z', 'linearScale.x, linearScale.z'
Set-Content $mote $text -Encoding UTF8

$submote = 'meeseeks/Source/CM_Meeseeks_Box/Effecters/SubEffecter_ProgressBar_Colored.cs'
$text = Get-Content $submote -Raw
$text = $text -replace 'exactScale', 'linearScale'
Set-Content $submote $text -Encoding UTF8

$mental = 'meeseeks/Source/CM_Meeseeks_Box/MentalStates/MentalState_MeeseeksKillCreator.cs'
$text = Get-Content $mental -Raw
$text = $text -replace 'string letterText = this\.GetBeginLetterText\(\);', 'TaggedString letterText = this.GetBeginLetterText();'
$text = $text -replace 'public override string GetBeginLetterText\(\)', 'public override TaggedString GetBeginLetterText()'
$text = $text -replace 'public override void MentalStateTick\(\)', 'public override void MentalStateTick(int delta)'
Set-Content $mental $text -Encoding UTF8

foreach ($foodFile in @(
  'meeseeks/Source/CM_Meeseeks_Box/Thinking/JobSelectors/MeeseeksJobSelector_Train.cs',
  'meeseeks/Source/CM_Meeseeks_Box/Thinking/JobSelectors/MeeseeksJobSelector_Tame.cs')) {
  $text = Get-Content $foodFile -Raw
  $text = $text -replace '(?m)^\s*FoodUtility\.bestFoodSourceOnMap_minNutrition_NewTemp\s*=.*\r?\n', ''
  $oldCall = 'FoodUtility.BestFoodSourceOnMap(pawn, tamee, false, out foodDef, FoodPreferability.RawTasty, allowPlant: false, allowDrug: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false)'
  $newCall = 'FoodUtility.BestFoodSourceOnMap(pawn, tamee, desperate: false, out foodDef, FoodPreferability.RawTasty, allowPlant: false, allowDrug: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false, allowForbidden: false, allowSociallyImproper: false, allowHarvest: false, forceScanWholeMap: false, ignoreReservations: false, calculateWantedStackCount: false, FoodPreferability.Undefined, JobDriver_InteractAnimal.RequiredNutritionPerFeed(tamee) * 2f * 4f)'
  $text = $text.Replace($oldCall, $newCall)
  $text = $text -replace 'FoodUtility\.GetNutrition\(thing, foodDef\)', 'FoodUtility.GetNutrition(tamee, thing, foodDef)'
  Set-Content $foodFile $text -Encoding UTF8
}

$kill = 'meeseeks/Source/CM_Meeseeks_Box/Jobs/JobDriver_Kill.cs'
$text = Get-Content $kill -Raw
$text = $text -replace 'Toils_Combat\.GotoCastPosition\(TargetIndex\.A, true, 4\.0f\)', 'Toils_Combat.GotoCastPosition(TargetIndex.A, TargetIndex.None, true, 4.0f)'
$text = $text -replace 'Notify_SlaughteredAnimal\(\)', 'Notify_SlaughteredTarget()'
Set-Content $kill $text -Encoding UTF8

$equip = 'meeseeks/Source/CM_Meeseeks_Box/Jobs/JobDriver_AcquireEquipment.cs'
$text = Get-Content $equip -Raw
$text = $text -replace 'EquipmentUtility\.IsBiocoded\(', 'CompBiocodable.IsBiocoded('
$text = $text -replace 'apparel is ShieldBelt', 'apparel != null && apparel.def.defName == "Apparel_ShieldBelt"'
Set-Content $equip $text -Encoding UTF8

$killer = 'meeseeks/Source/CM_Meeseeks_Box/Jobs/JobGiver_KillCreator.cs'
$text = Get-Content $killer -Raw
$text = $text -replace 'canBash: true', 'canBashDoors: true, canBashFences: true'
$text = $text -replace '\.pathFinder\.FindPath\(', '.pathFinder.FindPathNow('
$text = $text -replace 'job2\.canBash = true;', 'job2.canBashDoors = true; job2.canBashFences = true;'
Set-Content $killer $text -Encoding UTF8

$saved = 'meeseeks/Source/CM_Meeseeks_Box/SavedJob.cs'
$text = Get-Content $saved -Raw
$text = $text -replace 'canBash = job\.canBash;', 'canBash = job.canBashDoors || job.canBashFences;'
$text = $text -replace 'newJob\.canBash = canBash;', 'newJob.canBashDoors = canBash; newJob.canBashFences = canBash;'
Set-Content $saved $text -Encoding UTF8

$util = 'meeseeks/Source/CM_Meeseeks_Box/MeeseeksUtility.cs'
$text = Get-Content $util -Raw
$text = $text -replace 'Thing smoke = ThingMaker\.MakeThing\(ThingDefOf\.Gas_Smoke\);\s*GenSpawn\.Spawn\(smoke, summonPosition, map\);\s*MeeseeksUtility\.PlayPoofInSound\(smoke\);', 'GasUtility.AddGas(summonPosition, map, GasType.BlindSmoke, 255); MeeseeksUtility.PlayPoofInSound(mrMeeseeksLookAtMe);'
$text = $text -replace 'Thing smoke = ThingMaker\.MakeThing\(ThingDefOf\.Gas_Smoke\);\s*GenSpawn\.Spawn\(smoke, mrMeeseeksLookAtMe\.PositionHeld, mrMeeseeksLookAtMe\.MapHeld\);', 'GasUtility.AddGas(mrMeeseeksLookAtMe.PositionHeld, mrMeeseeksLookAtMe.MapHeld, GasType.BlindSmoke, 255);'
Set-Content $util $text -Encoding UTF8

$selection = 'meeseeks/Source/CM_Meeseeks_Box/Patches/SelectionDrawerPatches.cs'
$text = Get-Content $selection -Raw
$text = $text -replace 'WorldRendererUtility\.WorldRenderedNow == false', 'true'
Set-Content $selection $text -Encoding UTF8

# The original 1.2 float menu patch cannot compile against 1.6. It is intentionally disabled
# during the API sweep and will be replaced by a native 1.6 provider-compatible implementation.
Rename-Item 'meeseeks/Source/CM_Meeseeks_Box/Patches/FloatMenuMakerMapPatches.cs' 'FloatMenuMakerMapPatches.cs.disabled'
