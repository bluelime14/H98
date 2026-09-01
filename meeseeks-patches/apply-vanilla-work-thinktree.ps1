$path = 'meeseeks/Defs/ThinkTreeDefs/ThinkTrees_Meeseeks.xml'
$text = Get-Content $path -Raw
$needle = '<li Class="CM_Meeseeks_Box.ThinkNode_MeeseeksCompleteTask" />'
if ($text -notmatch [regex]::Escape($needle)) { throw 'Meeseeks complete-task node not found' }
if ($text -notmatch 'CM_Meeseeks_Box.ThinkNode_ConditionalMeeseeksWorkMission') {
  $insert = @'
<li Class="CM_Meeseeks_Box.ThinkNode_ConditionalMeeseeksWorkMission">
  <subNodes>
    <li Class="JobGiver_Work" />
  </subNodes>
</li>
'@
  $text = $text.Replace($needle, $insert + "`r`n" + $needle)
}
Set-Content $path $text -Encoding UTF8

[xml]$xml = Get-Content $path -Raw
$xml.Save((Resolve-Path $path))
Write-Host 'Inserted vanilla JobGiver_Work into the Meeseeks think tree behind the active WorkType mission condition.'
