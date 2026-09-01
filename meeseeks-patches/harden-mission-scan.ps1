$ErrorActionPreference = 'Stop'

$task = 'meeseeks/Source/CM_Meeseeks_Box/Thinking/ThinkNode_MeeseeksCompleteTask.cs'
$text = Get-Content $task -Raw

# RimWorld's JobGiver_Work filters scanner candidates with HasJobOnThing/HasJobOnCell
# before asking the WorkGiver to make a job. The persistent Meeseeks mission scanner
# must do the same or WorkGivers such as BuildRoof can manufacture invalid jobs.
$thingPattern = 'Job candidateJob = null;\s*try\s*\{\s*candidateJob = scanner\.JobOnThing\(pawn, thing, true\);\s*\}\s*catch\s*\{\s*\}'
$thingRegex = [regex]::new($thingPattern)
if ($thingRegex.Matches($text).Count -ne 1) {
    throw 'Could not uniquely locate mission JobOnThing scan block.'
}
$thingReplacement = @'
                            bool hasJobOnThing = false;
                            try
                            {
                                hasJobOnThing = !thing.IsForbidden(pawn) &&
                                                scanner.HasJobOnThing(pawn, thing, true);
                            }
                            catch
                            {
                                hasJobOnThing = false;
                            }

                            if (!hasJobOnThing)
                                continue;

                            Job candidateJob = null;
                            try
                            {
                                candidateJob = scanner.JobOnThing(pawn, thing, true);
                            }
                            catch
                            {
                            }
'@
$text = $thingRegex.Replace($text, $thingReplacement.TrimEnd(), 1)

$cellPattern = 'Job candidateJob = null;\s*try\s*\{\s*candidateJob = scanner\.JobOnCell\(pawn, cell, true\);\s*\}\s*catch\s*\{\s*\}'
$cellRegex = [regex]::new($cellPattern)
if ($cellRegex.Matches($text).Count -ne 1) {
    throw 'Could not uniquely locate mission JobOnCell scan block.'
}
$cellReplacement = @'
                            bool hasJobOnCell = false;
                            try
                            {
                                hasJobOnCell = !cell.IsForbidden(pawn) &&
                                               scanner.HasJobOnCell(pawn, cell, true);
                            }
                            catch
                            {
                                hasJobOnCell = false;
                            }

                            if (!hasJobOnCell)
                                continue;

                            Job candidateJob = null;
                            try
                            {
                                candidateJob = scanner.JobOnCell(pawn, cell, true);
                            }
                            catch
                            {
                            }
'@
$text = $cellRegex.Replace($text, $cellReplacement.TrimEnd(), 1)

# Keep the original PrepareJob TryMakePreToilReservations preflight. RimWorld's
# ReservationManager treats a repeat reservation by the same pawn/job as success, so
# StartJob can safely reserve the selected job again. Reserving here is important for
# Meeseeks families: the first helper claims its selected steel/material immediately,
# and later helpers then choose a different available resource instead of all selecting
# one stack and failing when their HaulToContainer drivers start.
if ($text -notmatch 'job\.TryMakePreToilReservations\(pawn, false\)') {
    throw 'PrepareJob reservation preflight is missing.'
}

# Do not add a post-selection throttle here. Once PrepareJob pre-reserves a mission job,
# every returned job must be allowed to start so the normal JobTracker can own/release
# those reservations. HasJobOnThing/HasJobOnCell plus the reservation preflight are the
# anti-loop and anti-collision checks.
Set-Content $task $text -Encoding UTF8

$verify = Get-Content $task -Raw
foreach ($marker in @(
    'scanner.HasJobOnThing(pawn, thing, true)',
    'scanner.HasJobOnCell(pawn, cell, true)',
    'job.TryMakePreToilReservations(pawn, false)')) {
    if ($verify -notmatch [regex]::Escape($marker)) {
        throw "Mission scan hardening marker missing: $marker"
    }
}
if ($verify -match 'ShouldThrottleRepeatedJob') {
    throw 'Reservation-preflight build must not contain the old post-reservation job throttle.'
}
