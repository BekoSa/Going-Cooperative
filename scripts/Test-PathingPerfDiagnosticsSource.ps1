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
Require-Text $motionSource 'RecordReplicationPathingCornerExtraction\(' "corner extraction timing"
Require-Text $motionSource 'RecordReplicationPathingMotionEvent\(' "semantic event counters"
Require-Text $deltaSource 'RecordReplicationPathingRetryScan\(' "reliable retry work timing"
Require-Text $trackedConfig '(?m)^pathingPerfDiagnostics=false$' "safe tracked default"

$diagnosticsContent = Get-Content -LiteralPath $diagnosticsSource -Raw
if ($diagnosticsContent -match 'FindObjectsOfType|FindObjectsOfTypeAll|GetMethod\(|GetProperty\(|GetField\(') {
    throw "Diagnostic implementation must not introduce scene scans or reflection"
}

Write-Host "PASS PathingPerfDiagnosticsSource gate/timing/aggregate/no-scan contracts"
