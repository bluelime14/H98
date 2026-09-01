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

# Mission scanning chooses work; the actual JobDriver owns reservations when StartJob runs.
# Pre-reserving here can make the JobDriver's own reservation/start sequence fail immediately.
$reservePattern = 'try\s*\{\s*if \(!job\.TryMakePreToilReservations\(pawn, false\)\)\s*return null;\s*\}\s*catch\s*\{\s*return null;\s*\}\s*return job;'
$reserveRegex = [regex]::new($reservePattern)
if ($reserveRegex.Matches($text).Count -lt 1) {
    throw 'Could not locate PrepareJob pre-reservation block.'
}
$reserveReplacement = @'
            // JobDrivers own pre-toil reservations. Mission scanning only validates and chooses work.
            return job;
'@
$text = $reserveRegex.Replace($text, $reserveReplacement.TrimEnd(), 1)

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
    'JobDrivers own pre-toil reservations',
    'ShouldThrottleRepeatedJob')) {
    if ($verify -notmatch [regex]::Escape($marker)) {
        throw "Mission scan hardening marker missing: $marker"
    }
}
