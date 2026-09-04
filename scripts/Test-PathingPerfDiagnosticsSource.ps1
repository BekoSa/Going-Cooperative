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

function Require-ConfigValue {
    param(
        [string]$Path,
        [string]$Key,
        [string]$ExpectedValue,
        [string]$Description
    )

    $matched = $false
    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or
            $line.StartsWith("#") -or
            $line.StartsWith(";")) {
            continue
        }

        $separator = $line.IndexOf("=")
        if ($separator -le 0) {
            continue
        }

        $actualKey = $line.Substring(0, $separator).Trim()
        if (-not [string]::Equals(
                $actualKey,
                $Key,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $actualValue = $line.Substring($separator + 1).Trim()
        if ($actualValue -ne $ExpectedValue) {
            throw "Invalid $Description in $Path; expected $Key=$ExpectedValue, got $Key=$actualValue"
        }

        $matched = $true
        break
    }

    if (-not $matched) {
        throw "Missing $Description in $Path; expected $Key=$ExpectedValue"
    }
}

$configSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$diagnosticsSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationPathingPerfDiagnostics.cs"
$runtimeSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$collectorSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationTransformCollector.cs"
$smoothingSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSnapshotSmoothing.cs"
$presenceSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationPresence.cs"
$presenceGuiSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Multiplayer\MultiplayerPresenceGui.cs"
$buildingCaptureSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.Building.cs"
$buildingLifecycleSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildingLifecycleV2.cs"
$motionSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationAgentMotionPresentation.cs"
$deltaSource = Join-Path $RepositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
$trackedConfig = Join-Path $RepositoryRoot "config\replication.cfg"

Require-Text $configSource 'private static bool replicationConfigPathingPerfDiagnostics;' "default-false gate"
Require-Text $configSource 'case "pathingperfdiagnostics":' "pathingPerfDiagnostics parser"
Require-Text $configSource 'ApplyReplicationPerformanceSafetyLimits\(current\);' "post-parse performance safety normalization"
Require-Text $configSource 'Math\.Min\(replicationConfigSnapshotHz, 10\)' "snapshot-rate legacy-config clamp"
Require-Text $configSource 'Math\.Min\(replicationConfigWorldObjectDeltaApplyBudgetPerFrame, 8\)' "world-delta count legacy-config clamp"
Require-Text $configSource 'Math\.Min\(replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame, 2f\)' "world-delta time legacy-config clamp"
Require-Text $configSource 'Math\.Min\(replicationConfigRuntimeMainThreadBudgetMsPerFrame, 4f\)' "runtime time legacy-config clamp"
Require-Text $configSource 'Math\.Min\(replicationConfigPresentationApplyBudgetMsPerFrame, 1\.25f\)' "presentation time legacy-config clamp"
Require-Text $configSource 'Math\.Min\(replicationConfigPresentationApplyMaxEntitiesPerFrame, 48\)' "presentation entity legacy-config clamp"
Require-Text $diagnosticsSource 'ReplicationPathingPerfWindowSeconds = 10f' "bounded aggregate window"
Require-Text $diagnosticsSource 'Going Cooperative pathing perf window side=' "single aggregate log record"
Require-Text $diagnosticsSource 'if \(!replicationConfigPathingPerfDiagnostics\)' "disabled fast path"
Require-Text $runtimeSource 'UpdateReplicationPathingPerfDiagnostics\(\);' "frame-window updater"
Require-Text $runtimeSource 'RecordReplicationPathingPump\(perfStarted, messageCount\);' "transport pump timing"
Require-Text $runtimeSource 'RecordReplicationPathingSnapshotCollection\(collectStarted, collectedSnapshot.Entities.Count\);' "snapshot collection timing"
Require-Text $runtimeSource 'RecordReplicationPathingSnapshotEncodeSend\(encodeSendStarted, wireCharacters\);' "snapshot encode/send timing"
Require-Text $collectorSource 'RecordReplicationPathingIdentity\(identityStarted, hasStableEntityId\);' "identity timing"
Require-Text $collectorSource 'RecordReplicationPathingSemantic\(' "semantic metadata timing"
Require-Text $collectorSource 'TryInstallReplicationTransformViewCacheInvalidation' "event-driven transform-view invalidation installer"
Require-Text $collectorSource '"OnEnable"' "AnimatedAgentView creation lifecycle invalidation"
Require-Text $collectorSource '"OnDestroy"' "AnimatedAgentView removal lifecycle invalidation"
Require-Text $collectorSource 'ReplicationAnimatedAgentViewLifecyclePostfix' "AnimatedAgentView lifecycle invalidation callback"
Require-Text $collectorSource 'ReplicationTransformViewCacheFallbackRefreshSeconds = 10f' "rare fallback transform-view refresh"
Require-Text $collectorSource 'replicationSemanticAnimatedAgentViewCacheDirty' "dirty transform-view cache"
Require-Text $collectorSource 'animatedAgentViewType\.IsInstanceOfType\(__instance\)' "runtime-type filtered transform-view invalidation"
Require-Text $collectorSource 'replicationTransformViewCacheFollowupRefreshRealtime > now' "coalesced transform-view lifecycle invalidation"
Require-Text $runtimeSource 'BeginReplicationMainThreadFrameBudget\(\);' "shared replication main-thread frame budget"
Require-Text $runtimeSource 'ShouldYieldReplicationMainThreadWork\(\)' "main-thread budget yield"
Require-Text $smoothingSource 'replicationConfigPresentationApplyBudgetMsPerFrame' "presentation time budget"
Require-Text $smoothingSource 'replicationConfigPresentationApplyMaxEntitiesPerFrame' "presentation entity budget"
Require-Text $smoothingSource 'ReplicationPresentationTrackOrder' "round-robin presentation ordering"
Require-Text $presenceSource 'ReplicationSelectionPollIntervalSeconds = 0\.1f' "bounded local selection discovery rate"
Require-Text $presenceSource 'now < replicationNextSelectionPollRealtime' "selection discovery early-out"
Require-Text $presenceSource 'ReplicationSelectionResolveMaxInspected = 24' "hard cap on local selection identity probes"
Require-Text $presenceSource 'ReplicationSelectionResolveBudgetMs = 0\.75f' "time budget on local selection identity probes"
Require-Text $presenceSource 'replicationLastLocalSelectionInspected\s*>=\s*ReplicationSelectionResolveMaxInspected' "selection inspected-count early stop"
Require-Text $presenceSource 'Stopwatch\.GetTimestamp\(\) - started >= budgetTicks' "selection identity time-budget stop"
Require-Text $presenceSource 'IsReplicationPresenceAgentSelectionCandidate\(selected\)' "deep identity fallback restricted to agent selections"
Require-Text $presenceSource 'ReplicationRemotePresenceScratch' "reused remote presence list"
Require-Text $presenceSource 'ReplicationRemoteSelectionWantedScratch' "reused remote selection ID set"
Require-Text $presenceSource 'ReplicationRemoteSelectionResolvedScratch' "reused remote selection transform map"
Require-Text $presenceGuiSource 'MultiplayerSelectionGuiRefreshSeconds = 1f / 15f' "bounded remote selection GUI refresh rate"
Require-Text $presenceGuiSource 'multiplayerPresenceSelectionRects' "cached selection RectTransforms"
Require-Text $presenceGuiSource 'multiplayerPresenceVisiblePeerIdsScratch' "reused visible-peer set"
Require-Text $motionSource 'RecordReplicationPathingCornerExtraction\(' "corner extraction timing"
Require-Text $motionSource 'RecordReplicationPathingMotionEvent\(' "semantic event counters"
Require-Text $deltaSource 'RecordReplicationPathingRetryScan\(' "reliable retry work timing"
Require-Text $deltaSource 'ReplicationWorldObjectDeltaRetrySendMaxPerScan = 32' "bounded reliable retry send fanout"
Require-Text $deltaSource 'ReplicationWorldObjectDeltaRetryInspectMaxPerScan = 512' "bounded reliable retry inspection fanout"
Require-Text $deltaSource 'ReplicationWorldObjectDeltaRetryScanOrder' "round-robin reliable retry ordering"
Require-Text $deltaSource 'replicationWorldObjectDeltaRetryScanCursor\+\+' "persistent reliable retry cursor"
Require-Text $deltaSource 'due\.Count >= ReplicationWorldObjectDeltaRetrySendMaxPerScan' "retry scan early stop"
Require-Text $deltaSource 'ReplicationResourcePileLocationIndexBudgetPerFrame = 2' "small client background pile-index slice"
Require-Text $deltaSource 'ReplicationResourcePileLocationIndexIntervalSeconds = 0\.05f' "paced client background pile-index cadence"
Require-Text $runtimeSource '(?s)ProcessPendingReplicationWorldObjectDeltaApplies\(\);.*?ShouldYieldReplicationMainThreadWork\(\).*?ProcessReplicationResourcePileLocationIndex\(\);' "authoritative client delta apply before background pile indexing"
Require-Text $deltaSource 'ReplicationAgentMotionPresentationDeltaKind, StringComparison\.Ordinal' "transient semantic motion presentation"
Require-Text $deltaSource 'ReplicationAgentWorkPresentationDeltaKind, StringComparison\.Ordinal' "transient semantic work presentation"
Require-Text $deltaSource 'replicationNextWorldObjectDeltaRetryScanRealtime = now \+ 0\.2f' "bounded reliable retry scheduler cadence"
Require-Text $deltaSource 'var durableThreshold =' "durable building retry threshold"
Require-Text $deltaSource '\? ReplicationWorldObjectDeltaMaxSends' "early lifecycle transition to durable retry"
Require-Text $deltaSource 'ReplicationBuildingRemovedTerminalRetrySeconds = 5\.0f' "slow removed-building safety-net retry interval"
Require-Text $deltaSource '(?s)IsReplicationBuildingRemovedLifecycleDelta\(delta\).*?return ReplicationBuildingRemovedTerminalRetrySeconds;' "removed-building first-copy ACK headroom"
Require-Text $deltaSource 'return ReplicationBuildingDurableRetrySeconds;' "slow durable building retry interval"
Require-Text $deltaSource 'building-lifecycle-v2-repair-required' "terminal negative building lifecycle acknowledgement"
Require-Text $buildingLifecycleSource 'TryHandleReplicationBuildingLifecycleRepairAckV2' "targeted lifecycle negative-ack repair"
Require-Text $buildingCaptureSource 'ReplicationClientBuildCommandChunkPlacements = 2' "bounded client build command chunk"
Require-Text $buildingCaptureSource 'ReplicationHostBuildReplayChunkPlacements = 2' "bounded host build replay chunk"
Require-Text $buildingCaptureSource 'ReplicationHostBuildReplayMaxInFlightChunks = 4' "bounded host build replay in-flight window"
Require-Text $buildingCaptureSource 'ReplicationPendingHostBuildReplayChunks' "pre-reliable host build replay queue"
Require-Text $buildingCaptureSource 'if \(durablePending >= ReplicationHostBuildReplayMaxInFlightChunks\)' "sliding-window host build replay admission"
Require-Text $runtimeSource 'ProcessPendingReplicationHostBuildReplayChunks\(\);' "runtime host build replay pump"
Require-Text $buildingLifecycleSource 'ReplicationBuildingTerminalEmitBudgetPerFrameV2 = 4' "bounded terminal lifecycle emission"
Require-Text $buildingLifecycleSource 'ReplicationPendingBuildingTerminalsV2' "deferred terminal lifecycle queue"
Require-Text $deltaSource 'deferBuildingInitialSend' "queued initial building replay/lifecycle send"
Require-ConfigValue $trackedConfig "pathingPerfDiagnostics" "false" "safe tracked default"
Require-ConfigValue $trackedConfig "snapshotHz" "10" "bounded snapshot rate"
Require-ConfigValue $trackedConfig "worldObjectDeltaApplyBudgetMsPerFrame" "2" "bounded world-delta apply time"
Require-ConfigValue $trackedConfig "runtimeMainThreadBudgetMsPerFrame" "4" "bounded aggregate runtime time"
Require-ConfigValue $trackedConfig "presentationApplyBudgetMsPerFrame" "1.25" "bounded presentation time"
Require-ConfigValue $trackedConfig "presentationApplyMaxEntitiesPerFrame" "48" "bounded presentation entity count"
Require-ConfigValue $trackedConfig "snapshotViewCacheSafetyRefreshSeconds" "0" "timer-free event-driven view cache"

$diagnosticsContent = Get-Content -LiteralPath $diagnosticsSource -Raw
if ($diagnosticsContent -match 'FindObjectsOfType|FindObjectsOfTypeAll|GetMethod\(|GetProperty\(|GetField\(') {
    throw "Diagnostic implementation must not introduce scene scans or reflection"
}

$deltaContent = Get-Content -LiteralPath $deltaSource -Raw
if ($deltaContent -match 'TrySendReplicationBuildingRepairV2\(delta, "lifecycle-retry-exhausted"\)') {
    throw "Building lifecycle retry exhaustion must not amplify into one repair row per building."
}

$presenceContent = Get-Content -LiteralPath $presenceSource -Raw
if ($presenceContent -match 'GetComponentInChildren\(type\)') {
    throw "Local selection presence must not descend through arbitrary selected-object hierarchies."
}

$collectorContent = Get-Content -LiteralPath $collectorSource -Raw
if ($collectorContent -match 'ReplicationSemanticAnimatedAgentViewCacheSeconds\s*=\s*3f' -or
    $collectorContent -match ':\s*3f\s*;\s*\r?\n\s*var safetyRefreshDue') {
    throw "Transform-view cache must not return to a mandatory 3-second global scene scan."
}

Write-Host "PASS PathingPerfDiagnosticsSource gate/timing/budgets/event-driven-cache contracts"
