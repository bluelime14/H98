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

# Emergency per-pawn throttle: a WorkGiver that produces a job which immediately ends should
# not be able to regenerate the exact same job ten times in a single tick.
$fieldMarker = '        private bool snapped = false;'
if (-not $text.Contains($fieldMarker)) {
    throw 'Could not locate Meeseeks mission-state fields.'
}
$fieldInsert = @'
        private bool snapped = false;
        private int lastIssuedMissionJobTick = -999999;
        private string lastIssuedMissionJobSignature = null;
'@
$text = $text.Replace($fieldMarker, $fieldInsert.TrimEnd())

$methodMarker = '        public void NotifyRunnableWork()'
if (-not $text.Contains($methodMarker)) {
    throw 'Could not locate NotifyRunnableWork insertion point.'
}
$methodInsert = @'
        public bool ShouldThrottleRepeatedJob(Job job)
        {
            if (job == null || job.def == null)
                return false;

            int now = Find.TickManager.TicksGame;
            string signature = job.def.defName + "|" +
                               job.GetTarget(TargetIndex.A).ToString() + "|" +
                               job.GetTarget(TargetIndex.B).ToString() + "|" +
                               job.GetTarget(TargetIndex.C).ToString();

            if (signature == lastIssuedMissionJobSignature &&
                now - lastIssuedMissionJobTick < 30)
            {
                return true;
            }

            lastIssuedMissionJobSignature = signature;
            lastIssuedMissionJobTick = now;
            return false;
        }

        public void NotifyRunnableWork()
'@
$text = $text.Replace($methodMarker, $methodInsert.TrimEnd())

$issuePattern = 'if \(missionJob != null\)\s*\{\s*missionState\?\.NotifyRunnableWork\(\);\s*return new ThinkResult\(missionJob, this, JobTag\.MiscWork, fromQueue: false\);\s*\}'
$issueRegex = [regex]::new($issuePattern)
if ($issueRegex.Matches($text).Count -ne 1) {
    throw 'Could not uniquely locate mission-job issue block.'
}
$issueReplacement = @'
                if (missionJob != null)
                {
                    if (missionState?.ShouldThrottleRepeatedJob(missionJob) == true)
                    {
                        missionState.NotifyWaitingOnFamily();
                        Job retryWait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 30);
                        return new ThinkResult(retryWait, this, JobTag.MiscWork, fromQueue: false);
                    }

                    missionState?.NotifyRunnableWork();
                    return new ThinkResult(missionJob, this, JobTag.MiscWork, fromQueue: false);
                }
'@
$text = $issueRegex.Replace($text, $issueReplacement.TrimEnd(), 1)

Set-Content $task $text -Encoding UTF8

# Fail the build immediately if any critical portion of the hardening patch did not land.
$verify = Get-Content $task -Raw
foreach ($marker in @(
    'scanner.HasJobOnThing(pawn, thing, true)',
    'scanner.HasJobOnCell(pawn, cell, true)',
    'job.TryMakePreToilReservations(pawn, false)',
    'ShouldThrottleRepeatedJob')) {
    if ($verify -notmatch [regex]::Escape($marker)) {
        throw "Mission scan hardening marker missing: $marker"
    }
}
