param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

function Require-Text {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Description
    )

    $content = Get-Content -LiteralPath $Path -Raw
    if ($content -notmatch $Pattern) {
        throw "Missing $Description in $Path"
    }
}

$configSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$diagnosticsSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationPathingPerfDiagnostics.cs"
$runtimeSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$collectorSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationTransformCollector.cs"
$smoothingSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSnapshotSmoothing.cs"
$motionSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationAgentMotionPresentation.cs"
$deltaSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
$trackedConfig = Join-Path $RepositoryRoot "config\replication.cfg"

Require-Text $configSource 'private static bool replicationConfigPathingPerfDiagnostics;' "default-false gate"
Require-Text $configSource 'case "pathingperfdiagnostics":' "pathingPerfDiagnostics parser"
Require-Text $diagnosticsSource 'ReplicationPathingPerfWindowSeconds = 10f' "bounded aggregate window"
Require-Text $diagnosticsSource 'Going Cooperative pathing perf window side=' "single aggregate log record"
Require-Text $diagnosticsSource 'if \(!replicationConfigPathingPerfDiagnostics\)' "disabled fast path"
Require-Text $runtimeSource 'UpdateReplicationPathingPerfDiagnostics\(\);' "frame-window updater"
Require-Text $runtimeSource 'RecordReplicationPathingPump\(perfStarted, messageCount\);' "transport pump timing"
Require-Text $runtimeSource 'RecordReplicationPathingSnapshotCollection\(collectStarted, snapshot.Entities.Count\);' "snapshot collection timing"
Require-Text $runtimeSource 'RecordReplicationPathingSnapshotEncodeSend\(encodeSendStarted, wireCharacters\);' "snapshot encode/send timing"
Require-Text $collectorSource 'RecordReplicationPathingIdentity\(identityStarted, hasStableEntityId\);' "identity timing"
Require-Text $collectorSource 'RecordReplicationPathingSemantic\(' "semantic metadata timing"
Require-Text $collectorSource 'TryInstallReplicationTransformViewCacheInvalidation' "event-driven transform-view invalidation installer"
Require-Text $collectorSource '"AddCreature"' "CreatureManager AddCreature invalidation"
Require-Text $collectorSource '"RemoveCreature"' "CreatureManager RemoveCreature invalidation"
Require-Text $collectorSource 'replicationSemanticAnimatedAgentViewCacheDirty' "dirty transform-view cache"
Require-Text $runtimeSource 'BeginReplicationMainThreadFrameBudget\(\);' "shared replication main-thread frame budget"
Require-Text $runtimeSource 'ShouldYieldReplicationMainThreadWork\(\)' "main-thread budget yield"
Require-Text $smoothingSource 'replicationConfigPresentationApplyBudgetMsPerFrame' "presentation time budget"
Require-Text $smoothingSource 'replicationConfigPresentationApplyMaxEntitiesPerFrame' "presentation entity budget"
Require-Text $smoothingSource 'ReplicationPresentationTrackOrder' "round-robin presentation ordering"
Require-Text $motionSource 'RecordReplicationPathingCornerExtraction\(' "corner extraction timing"
Require-Text $motionSource 'RecordReplicationPathingMotionEvent\(' "semantic event counters"
Require-Text $deltaSource 'RecordReplicationPathingRetryScan\(' "reliable retry work timing"
Require-Text $trackedConfig '(?m)^pathingPerfDiagnostics=false$' "safe tracked default"
Require-Text $trackedConfig '(?m)^snapshotHz=10$' "bounded snapshot rate"
Require-Text $trackedConfig '(?m)^worldObjectDeltaApplyBudgetMsPerFrame=2$' "bounded world-delta apply time"
Require-Text $trackedConfig '(?m)^runtimeMainThreadBudgetMsPerFrame=4$' "bounded aggregate runtime time"
Require-Text $trackedConfig '(?m)^presentationApplyBudgetMsPerFrame=1\.25$' "bounded presentation time"
Require-Text $trackedConfig '(?m)^presentationApplyMaxEntitiesPerFrame=48$' "bounded presentation entity count"
Require-Text $trackedConfig '(?m)^snapshotViewCacheSafetyRefreshSeconds=0$' "timer-free event-driven view cache"

$diagnosticsContent = Get-Content -LiteralPath $diagnosticsSource -Raw
if ($diagnosticsContent -match 'FindObjectsOfType|FindObjectsOfTypeAll|GetMethod\(|GetProperty\(|GetField\(') {
    throw "Diagnostic implementation must not introduce scene scans or reflection"
}

$collectorContent = Get-Content -LiteralPath $collectorSource -Raw
if ($collectorContent -match 'ReplicationSemanticAnimatedAgentViewCacheSeconds\s*=\s*3f') {
    throw "Transform-view cache must not return to a mandatory 3-second global scene scan."
}

Write-Host "PASS PathingPerfDiagnosticsSource gate/timing/budgets/event-driven-cache contracts"
