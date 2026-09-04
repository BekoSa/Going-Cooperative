[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$contractFailures = New-Object System.Collections.Generic.List[string]

$paths = @{
    Config = Join-Path $repositoryRoot "config\replication.cfg"
    ConfigSource = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
    CorePolicy = Join-Path $repositoryRoot "src\GoingCooperative.Core\BuildingReplicationV2Policy.cs"
    Payloads = Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs"
    Runtime = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
    BuildBatch = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildBatch.cs"
    Capture = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.Building.cs"
    Lifecycle = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildingLifecycleV2.cs"
    WorldDeltas = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
    SaveTransfer = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Multiplayer\MultiplayerSaveTransfer.cs"
    RegionOrders = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.RegionOrders.cs"
    RuntimeActions = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntimeCommandActions.cs"
}

$sources = @{}
foreach ($name in $paths.Keys) {
    $path = $paths[$name]
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Building V2 verification input is missing: $path"
    }

    $sources[$name] = Get-Content -LiteralPath $path -Raw
}

function Require-SourcePattern {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $Pattern,
        [Parameter(Mandatory)][string] $Failure
    )

    if (-not [regex]::IsMatch(
            $Text,
            $Pattern,
            [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        $script:contractFailures.Add($Failure)
    }
}

# The rollback gate must exist in both the shipped template and runtime parser,
# and V2 is the normal default. A local-only default would create a mixed-mode pair.
Require-SourcePattern $sources.Config '(?m)^\s*buildingReplicationV2\s*=\s*true\s*(?:[#;].*)?$' `
    "The release config does not enable buildingReplicationV2 by default."
Require-SourcePattern $sources.ConfigSource 'replicationConfigBuildingReplicationV2\s*=\s*true\s*;' `
    "The runtime buildingReplicationV2 default is not enabled."
Require-SourcePattern $sources.ConfigSource 'case\s+"buildingreplicationv2"\s*:.*?TryParseConfigBool\(.*?replicationConfigBuildingReplicationV2\s*=\s*buildingReplicationV2\s*;' `
    "The buildingReplicationV2 config key is not parsed into the runtime gate."
foreach ($gate in @("beamPlacementReplication", "beamLifecycleReplication", "socketablePlacementReplication")) {
    Require-SourcePattern $sources.Config ('(?m)^\s*' + $gate + '\s*=\s*true\s*(?:[#;].*)?$') `
        ("The release config does not enable the functional gate " + $gate + ".")
}
Require-SourcePattern $sources.Config '(?m)^\s*beamReplicationDiagnostics\s*=\s*false\s*(?:[#;].*)?$' `
    "The release config must keep beamReplicationDiagnostics disabled."
Require-SourcePattern $sources.CorePolicy 'CurrentTransactionSchemaVersion\s*=\s*2\s*;' `
    "Tagged beam/socket placement did not bump the strict Building V2 schema."

# Building semantics are part of the hello fingerprint. Missing or differently
# selected capabilities must refuse rather than silently falling back to legacy.
Require-SourcePattern $sources.CorePolicy 'struct\s+BuildingReplicationCapability.*?WirePrefix\s*=\s*"building-replication-v2".*?ToWireToken\(' `
    "The strict Building V2 capability token contract is missing."
Require-SourcePattern $sources.CorePolicy 'class\s+BuildingReplicationCompatibilityPolicy.*?local\.SelectedMode\s*!=\s*remote\.SelectedMode.*?SelectedModeMismatch' `
    "Building compatibility no longer rejects differently selected V2/legacy modes."
Require-SourcePattern $sources.Runtime 'ComputeReplicationLocalBuildHashWithCapabilities\(\).*?"\|building=".*?FormatReplicationBuildingCapability\(\)' `
    "The building capability token is not included in the hello build fingerprint."
Require-SourcePattern $sources.Runtime 'TryReadReplicationBuildingCapability\(.*?TryReadReplicationCapabilitySegment\(buildHash,\s*"building".*?BuildingReplicationCapability\.TryParseWireToken' `
    "The remote building capability token is not parsed from the hello fingerprint."
Require-SourcePattern $sources.Runtime '!localHasBuildingCapability\s*\|\|\s*!remoteHasBuildingCapability.*?building-capability-missing.*?return\s+false\s*;' `
    "A peer without the building capability token is not refused."
Require-SourcePattern $sources.Runtime 'BuildingReplicationCompatibilityPolicy\.Evaluate\(.*?!buildingCompatibility\.IsCompatible.*?building-capability-incompatible.*?return\s+false\s*;' `
    "The Core building compatibility policy is not enforced by the hello path."
Require-SourcePattern $sources.Runtime 'replicationConfigBuildingReplicationV2\s*\?\s*BuildingReplicationMode\.TransactionLifecycleV2\s*:\s*BuildingReplicationMode\.LegacySnapshots' `
    "The rollback gate does not select the advertised building replication mode."

# Every V2 batch carries the current loaded-save epoch. The host checks command
# intents before dedupe/execution, and the client checks both authoritative result
# shapes before beginning exact-once native replay.
Require-SourcePattern $sources.Payloads 'CreateBuildBatchPayload\(.*?long\s+epoch\s*=\s*-1L.*?\\"epoch\\".*?epoch\.ToString' `
    "BuildBatch serialization has no epoch field."
Require-SourcePattern $sources.Payloads 'TryReadBuildBatchEpoch\(.*?TryReadLongProperty\(normalized,\s*"epoch".*?epoch\s*>=\s*0L' `
    "BuildBatch epoch parsing or nonnegative validation is missing."
Require-SourcePattern $sources.BuildBatch 'CreateReplicationBuildBatchWirePayload\(.*?LockstepCommandPayloads\.CreateBuildBatchPayload\(.*?GetReplicationBuildBatchEpoch\(\)' `
    "Captured BuildBatch commands do not serialize the active save epoch."
Require-SourcePattern $sources.Runtime 'TryReadBuildBatchEpoch\(command\.PayloadJson,\s*out\s+var\s+buildEpoch\).*?buildEpoch\s*!=\s*GetReplicationBuildBatchEpoch\(\).*?SendReplicationCommandAck\(command,\s*accepted:\s*false.*?return\s*;' `
    "The host does not reject an absent or mismatched BuildBatch epoch before execution."
Require-SourcePattern $sources.WorldDeltas 'TryApplyReplicationBuildingBlueprintBatchPlaced\(.*?TryReadReplicationWorldObjectDetailLong\(delta\.Detail,\s*"epoch".*?wireEpoch\s*!=\s*GetReplicationBuildBatchEpoch\(\).*?return\s+false\s*;' `
    "Authoritative host-placement batches are not rejected on client epoch mismatch."
Require-SourcePattern $sources.WorldDeltas 'TryApplyReplicationBuildingBlueprintBatchResult\(.*?TryReadReplicationWorldObjectDetailLong\(delta\.Detail,\s*"epoch".*?wireEpoch\s*!=\s*GetReplicationBuildBatchEpoch\(\).*?return\s+false\s*;' `
    "Authoritative client-command batch results are not rejected on client epoch mismatch."
Require-SourcePattern $sources.BuildBatch 'BuildingTransactionApplyLedger.*?TryBeginReplicationBuildTransaction\(.*?\.Begin\(.*?GetReplicationBuildBatchEpoch\(.*?CommitReplicationBuildTransaction\(.*?\.Commit\(.*?AbortReplicationBuildTransaction\(.*?\.Abort\(' `
    "The BuildBatch apply path is not wired to the epoch-scoped exact-once ledger."
Require-SourcePattern $sources.WorldDeltas 'TryApplyReplicationBuildingBlueprintBatchPlaced\(.*?TryBeginReplicationBuildTransaction\(.*?TryApplyReplicationBuildingBlueprintBatchPlacedCore\(.*?CommitReplicationBuildTransaction\(' `
    "Host-placement batch replay is not begin/apply/commit transactional."
Require-SourcePattern $sources.WorldDeltas 'TryApplyReplicationBuildingBlueprintBatchResult\(.*?TryBeginReplicationBuildTransaction\(.*?TryApplyReplicationBuildingBlueprintBatchResultCore\(.*?CommitReplicationBuildTransaction\(' `
    "Client-command batch reconciliation is not begin/apply/commit transactional."

# A host-local placement transaction must keep one semantic identity when its
# reliable envelope is retried or reissued. Transport sequence is ACK identity
# only and must never be used as the transaction-ledger key.
Require-SourcePattern $sources.Capture 'EmitHostLocalReplicationBuildPlacements\(.*?long\s+transactionId\s*,\s*int\s+groupIndex.*?semanticGroup\s*=\s*checked\(groupIndex\s*\*\s*1024\s*\+\s*chunkIndex\).*?semanticTransactionId:\s*transactionId.*?semanticGroup:\s*semanticGroup' `
    "Host-local BuildBatch emission does not assign a chunk-unique semantic transaction/group identity."
Require-SourcePattern $sources.WorldDeltas 'ReplicationBuildBatchCommitManifest\(.*?long\s+semanticTransactionId\s*=\s*-1L.*?int\s+semanticGroup\s*=\s*-1.*?" transactionId=".*?semanticTransactionId\.ToString.*?" group=".*?semanticGroup\.ToString' `
    "BuildBatch commit manifests do not serialize semantic transactionId/group tokens into the reliable delta."
Require-SourcePattern $sources.WorldDeltas 'TryApplyReplicationBuildingBlueprintBatchPlaced\(.*?"transactionId"\s*,\s*out\s+var\s+captureTransactionId.*?"group"\s*,\s*out\s+var\s+captureGroup.*?"host-placement:"\s*\+\s*captureTransactionId\.ToString.*?captureGroup\.ToString.*?TryBeginReplicationBuildTransaction\(transactionId' `
    "Host-placement replay is not keyed by its semantic transactionId/group."
if ([regex]::IsMatch(
        $sources.WorldDeltas,
        '"host-placement:"\s*\+\s*delta\.Sequence',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
    $contractFailures.Add("Host-placement transaction identity regressed to transport delta.Sequence.")
}

# A targeted full resync advances the peer checkpoint epoch on the host and the
# received epoch on the client. Building exact-once fences must read the shared
# replication epoch rather than the host-global transfer epoch, which stays zero.
Require-SourcePattern $sources.SaveTransfer 'public\s+int\s+ReplicationEpoch.*?if\s*\(!hostMode\).*?return\s+Math\.Max\(0,\s*epoch\).*?foreach\s*\(var\s+peer\s+in\s+hostPeers\.Values\).*?peer\.Epoch' `
    "Multiplayer save transfer does not expose the peer checkpoint epoch to host building replication."
Require-SourcePattern $sources.SaveTransfer 'lock\s*\(stateLock\).*?if\s*\(isResync\).*?peer\.Epoch\+\+.*?bundleEpoch\s*=\s*peer\.Epoch' `
    "Targeted resync does not atomically publish the incremented peer epoch before sending the bundle."
Require-SourcePattern $sources.BuildBatch 'GetReplicationBuildBatchEpoch\(\).*?multiplayerSaveTransfer\.ReplicationEpoch' `
    "Building transaction fences still use the host-global transfer epoch and will diverge after resync."

# Host-local large drags are staged before the reliable map and admitted only
# after the previous placement/result transaction is ACKed.
Require-SourcePattern $sources.Capture 'ReplicationPendingHostBuildReplayChunks.*?EmitHostLocalReplicationBuildPlacements\(.*?ReplicationPendingHostBuildReplayChunks\.Enqueue' `
    "Host-local build chunks are still registered into the reliable map in one placement frame."
Require-SourcePattern $sources.Capture 'ProcessPendingReplicationHostBuildReplayChunks\(\).*?IsReplicationBuildingDurableBackpressured\(out\s+var\s+durablePending\).*?if\s*\(durablePending\s*>\s*0\).*?SendReplicationWorldObjectDelta' `
    "Host-local build replay is not ACK-paced before reliable admission."
Require-SourcePattern $sources.Runtime 'UpdateReplicationBuildingLifecycleV2\(\);.*?ProcessPendingReplicationHostBuildReplayChunks\(\);.*?ShouldYieldReplicationMainThreadWork\(\)' `
    "The host runtime does not pump staged build replay inside the shared frame budget."
Require-SourcePattern $sources.Capture 'ShouldCaptureReplicationBuildTransaction\(\).*?replicationConfigHostMode.*?multiplayerLoadingInProgress.*?!replicationRuntimeStarted.*?!replicationRemoteHelloReceived.*?multiplayerHostSyncPauseApplied' `
    "Host build capture is not suppressed during native loading or peer catch-up."
Require-SourcePattern $sources.Runtime 'ResetReplicationPeerRuntimeState\(.*?ReleaseReplicationPeerWorldDeltaObligations\(.*?ClearPendingReplicationHostBuildReplayChunks\(\)' `
    "A resync can retain pre-checkpoint host build replay chunks with stale epoch manifests."

# V2 routine play must never enter the whole-scene BuildingState collector. The
# legacy scan remains available only when both peers explicitly select rollback.
Require-SourcePattern $sources.WorldDeltas 'SendHostReplicationBuildingStateSnapshotIfDue\(\).*?if\s*\(\s*replicationConfigBuildingReplicationV2\s*\)\s*\{.*?return\s*;.*?\}.*?TryCollectReplicationBuildingStates\(' `
    "Routine BuildingState whole-scene collection is not gated off before collection under V2."

# Placement on either authority side seeds a bounded tracked set. Update and reset
# hooks must be live, and stable positive host identity is a hard prerequisite.
Require-SourcePattern $sources.Lifecycle 'ReplicationBuildingLifecycleV2DeltaKind\s*=\s*"BuildingLifecycleV2".*?ReplicationBuildingProgressV2DeltaKind\s*=\s*"BuildingProgressV2"' `
    "Building V2 lifecycle/progress wire constants are missing."
Require-SourcePattern $sources.Runtime 'UpdateReplicationBuildingLifecycleV2\(\)' `
    "The Building V2 lifecycle pump is not called from runtime Update."
Require-SourcePattern $sources.Runtime 'ResetReplicationBuildTransactionLedger\(\)\s*;\s*ResetReplicationBuildingLifecycleV2\(\)' `
    "Building V2 transaction and lifecycle state are not reset together."
Require-SourcePattern $sources.Capture 'TrackReplicationBuildingLifecycleV2\(\s*placements\[i\],\s*"host-local-building-v2"\s*\)' `
    "Host-local committed BuildBatch items do not enter lifecycle tracking."
Require-SourcePattern $sources.WorldDeltas 'TrackReplicationBuildingLifecycleV2\(committed!,\s*"client-command-building-v2"\)' `
    "Client-command committed BuildBatch items do not enter lifecycle tracking."
Require-SourcePattern $sources.Capture 'TryReadReplicationWorldObjectLongMember\(.*?"UniqueId".*?uniqueId\s*<=\s*0L.*?committed-build-unique-id-invalid.*?return\s+false\s*;' `
    "Committed placement does not fail closed when authoritative building identity is missing."
Require-SourcePattern $sources.Lifecycle 'TrackReplicationBuildingLifecycleV2\(.*?placement\.UniqueId\s*<=\s*0L.*?RegisterReplicationHostIdentity\(placement\.UniqueId' `
    "Lifecycle tracking does not require and register a positive authoritative identity."

# Lifecycle rows are absolute state: epoch fences the loaded world, revision fences
# each building, and the revision cursor advances only after native application.
Require-SourcePattern $sources.Lifecycle 'EmitReplicationBuildingLifecycleV2\(.*?tracked\.Revision\+\+.*?FormatReplicationBuildingLifecycleStateV2\(' `
    "Host lifecycle deltas do not carry both current epoch and monotonic building revision."
Require-SourcePattern $sources.Lifecycle 'FormatReplicationBuildingLifecycleStateV2\(.*?"epoch="\s*\+\s*GetReplicationBuildBatchEpoch\(\).*?" revision="\s*\+\s*tracked\.Revision' `
    "Host lifecycle state formatter is missing epoch or per-building revision."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingLifecycleV2\(.*?TryReadReplicationBuildingLifecycleEnvelopeV2\(.*?EvaluateLiveDelta\(' `
    "Client lifecycle apply does not reject stale-world epochs before revision ordering."
Require-SourcePattern $sources.Lifecycle 'TryReadReplicationBuildingLifecycleEnvelopeV2\(.*?TryReadReplicationWorldObjectDetailLong\(delta\.Detail,\s*"epoch".*?epoch\s*!=\s*GetReplicationBuildBatchEpoch\(\).*?TryReadReplicationWorldObjectDetailLong\(delta\.Detail,\s*"revision"' `
    "Lifecycle envelope does not reject a mismatched save epoch before revision ordering."
Require-SourcePattern $sources.Lifecycle 'BuildingRevisionLedger.*?EvaluateLiveDelta\(.*?CommitLiveDelta\(' `
    "Client lifecycle apply has no per-building revision high-water check."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingLifecycleV2\(.*?delta\.UniqueId\s*<=\s*0L.*?return\s+false\s*;' `
    "Client lifecycle apply does not fail closed on missing authoritative identity."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingLifecycleV2\(.*?TryApplyReplicationBuildingNativeStateV2\(.*?CommitLiveDelta\(' `
    "Lifecycle rows are not applied through native removal/state paths before committing revision."
Require-SourcePattern $sources.Lifecycle 'TryInstallReplicationBuildingLifecycleV2Hooks\(.*?ConstructionStarted.*?ConstructionPaused.*?EnterFoundationState.*?ConstructionCompleted.*?SetMarkedForDestruction.*?BuildingCanceled.*?BuildingDeconstructed.*?DestroyBuilding' `
    "Production lifecycle capture is not patched to the audited native mutation surfaces."
Require-SourcePattern $sources.Lifecycle 'SetRemainingTime.*?ReplicationBuildingProgressObservedV2Postfix.*?SetRemainingTime-progress-start.*?tracked\.Progressing' `
    "Building V2 has no bounded host fallback for construction already running when the peer becomes ready."
Require-SourcePattern $sources.Lifecycle 'ReplicationBuildingProgressObservedV2Postfix\(.*?tracked\.Progressing.*?return;.*?SetRemainingTime-progress-start' `
    "Building V2 progress fallback can emit a lifecycle delta for every native timer update."
Require-SourcePattern $sources.Lifecycle 'EnsureReplicationBuildingIdentityBootstrapV2\(.*?UniqueIdBuildingDictionary.*?building-lifecycle-v2-save-bootstrap' `
    "Save-loaded buildings do not get a one-time native identity bootstrap."
Require-SourcePattern $sources.Lifecycle 'TryHandleReplicationBuildingLifecycleRepairAckV2\(.*?TrySendReplicationBuildingRepairV2.*?TryApplyReplicationBuildingRepairV2\(' `
    "Missing building identity does not activate targeted exact repair."
Require-SourcePattern $sources.Lifecycle 'ReplicationBuildingLifecycleV2MutationPrefix\(.*?ShouldSuppressReplicationClientBuildingMutationV2\(\).*?return\s+false\s*;' `
    "Client-side native lifecycle mutations are not suppressed outside authoritative replay."
Require-SourcePattern $sources.Lifecycle 'ReplicationBuildingProgressMutationV2Prefix\(\).*?return\s+!ShouldSuppressReplicationClientBuildingMutationV2\(\)' `
    "Client-side native remaining-time mutation is not guarded by host authority."
Require-SourcePattern $sources.Lifecycle 'IsReplicationPluginReferenceMissingV2\(\).*?ReferenceEquals\(instance,\s*null\)' `
    "Building V2 treats Unity's destroyed-object null overload as a lost persistent plugin runtime."
if ($sources.Lifecycle -match '(?<![A-Za-z0-9_])instance\s*==\s*null') {
    $contractFailures.Add("Building V2 still uses Unity overloaded null comparison for its persistent plugin runtime.")
}
Require-SourcePattern $sources.Lifecycle 'ShouldSuppressReplicationClientBuildingMutationV2\(\).*?replicationConfigBuildingReplicationV2.*?!replicationConfigHostMode.*?replicationRuntimeStarted.*?replicationRemoteHelloReceived.*?applyingRuntimeCommandDepth\s*<=\s*0' `
    "The client mutation guard is not scoped to active V2 client play outside authoritative application."
if ($sources.Lifecycle.Contains('ReplicationBuildingLifecycleV2PollSeconds') -or
    $sources.Lifecycle.Contains('SendHostReplicationBuildingLifecycleV2IfDue')) {
    $contractFailures.Add("Building V2 still contains a timed lifecycle poller instead of native mutation hooks.")
}
if ($sources.Lifecycle.Contains('TryApplyReplicationBuildingState(delta')) {
    $contractFailures.Add("Building V2 still falls through to the broad legacy BuildingState apply path.")
}

# Host-local area Cancel/Deconstruct must lead with one semantic region replay.
# Reliable terminal rows remain as a delayed fallback if that transient state is lost.
Require-SourcePattern $sources.RegionOrders 'ReplicationRegionSelectionActionPrefix\(.*?replicationHostLocalSemanticRegionArmed\s*=\s*false.*?TryResolveReplicationSelectionRegion\(.*?replicationHostLocalSemanticRegionArmed\s*=\s*true.*?replicationHostLocalSemanticRegionStartX\s*=\s*startX.*?replicationHostLocalSemanticRegionEndZ\s*=\s*endZ' `
    "Host-local Cancel/Deconstruct does not bind semantic replay to the exact successfully resolved drag."
Require-SourcePattern $sources.RegionOrders 'ReplicationRegionSelectionEventPostfix\(.*?applyingRuntimeCommandDepth\s*<=\s*0.*?replicationHostLocalSemanticRegionArmed.*?replicationHostLocalSemanticRegionFrame\s*==\s*Time\.frameCount.*?MarkReplicationBuildingSemanticRegionReplayV2\(\).*?SendReplicationRegionOrderState\(.*?replicationHostLocalSemanticRegionStartX.*?replicationHostLocalSemanticRegionEndZ' `
    "Host-local Cancel/Deconstruct does not publish its exact semantic region replay after native completion."
Require-SourcePattern $sources.Runtime 'SendReplicationRegionOrderStateIfSupported\(.*?IsReplicationMassBuildingRegionOrder\(orderType\).*?MarkReplicationBuildingSemanticRegionReplayV2\(\).*?SendReplicationRegionOrderState\(' `
    "Client-originated Cancel/Deconstruct does not pace durable terminals behind the canonical region state."
Require-SourcePattern $sources.RegionOrders 'replicationRegionSelectionActionDepth.*?ReplicationRegionResourceSurfacePostfix\(.*?replicationRegionSelectionActionFrame\s*==\s*Time\.frameCount.*?return\s*;' `
    "Cancel/Deconstruct side-effect resource callbacks can still emit a duplicate region command."
Require-SourcePattern $sources.RegionOrders 'ZoneSelectionFinished.*?orderType.*?None.*?replicationLastRegionOrderType.*?Cancel.*?Deconstruct.*?return\s+true\s*;' `
    "Cancel/Deconstruct can still emit a second orderType=None ZoneSelectionFinished command."
Require-SourcePattern $sources.Lifecycle 'ReplicationBuildingSemanticRegionTerminalGraceSecondsV2\s*=\s*0\.4f.*?MarkReplicationBuildingSemanticRegionReplayV2\(\).*?replicationBuildingSemanticRegionTerminalGraceUntilRealtimeV2' `
    "Building terminals do not give semantic area replay a bounded head start."
Require-SourcePattern $sources.Lifecycle 'ProcessPendingReplicationBuildingTerminalsV2\(\).*?replicationBuildingSemanticRegionTerminalGraceUntilRealtimeV2.*?return\s*;' `
    "Reliable terminal replay can race the semantic area operation in the same frame."
Require-SourcePattern $sources.Lifecycle 'IsReplicationBuildingLifecycleTerminalAlreadyAbsentV2\(.*?state.*?removed.*?TryMapReplicationLocalBuildingByNativeUniqueIdV2.*?native-id-not-found.*?RemoveReplicationHostIdentity' `
    "Already-removed semantic region terminals do not have a native-ID fast path."
Require-SourcePattern $sources.WorldDeltas 'IsReplicationBuildingLifecycleTerminalAlreadyAbsentV2\(.*?replicationBuildingLifecycleFastTerminalAcksV2\+\+.*?ApplyReplicationWorldObjectDeltaAndAck\(delta\)' `
    "Already-removed building terminals are still forced through the normal client apply queue."
Require-SourcePattern $sources.Lifecycle 'ReplicationTrackedHostBuildingsByInstanceV2.*?ReferenceObjectComparer\.Instance' `
    "Host lifecycle hooks do not maintain an O(1) building-instance tracker cache."
Require-SourcePattern $sources.Lifecycle 'TryEnsureReplicationHostBuildingTrackerV2\(.*?ReplicationTrackedHostBuildingsByInstanceV2\.TryGetValue' `
    "Mass removal still starts every lifecycle callback with full building identity reflection."
Require-SourcePattern $sources.Lifecycle 'replicationDestroyBuildingMethodV2.*?DestroyBuilding\(skipStabilityCheck=true,cachedMethod=true\)' `
    "Client terminal replay re-enumerates BuildingsManagerMain methods per removed cell."
Require-SourcePattern $sources.RuntimeActions 'TryInvokeSelectionManagerRegionAction\(.*?actionMs=' `
    "Native region-action duration is not observable in the next performance logs."
Require-SourcePattern $sources.RegionOrders '\[MP/REGION\]\s+local native area action.*?actionMs=' `
    "Local host/client native Cancel/Deconstruct duration is not observable in the next performance logs."

# Rapid lifecycle changes must bypass the generic 500 ms duplicate filter. Reliable
# lifecycle rows get the extended bounded retry budget; selected progress is visual,
# on-demand, and transient so it cannot build a resend backlog.
Require-SourcePattern $sources.WorldDeltas 'ShouldSkipDuplicateReplicationWorldObjectDelta\(.*?ReplicationBuildingLifecycleV2DeltaKind.*?ReplicationBuildingProgressV2DeltaKind.*?return\s+false\s*;' `
    "Building lifecycle/progress still pass through the generic duplicate-time filter."
Require-SourcePattern $sources.WorldDeltas 'ReplicationBuildBatchWorldObjectDeltaMaxSends\s*=\s*20\s*;' `
    "The reliable building V2 retry budget is not 20 sends."
Require-SourcePattern $sources.WorldDeltas 'ShouldDropReplicationWorldObjectDeltaAfterRetries\(.*?ReplicationBuildingLifecycleV2DeltaKind.*?ReplicationBuildBatchWorldObjectDeltaMaxSends' `
    "Building lifecycle rows do not use the extended 20-send reliability budget."
Require-SourcePattern $sources.WorldDeltas 'private\s+bool\s+SendReplicationWorldObjectDelta\(\s*ReplicationWorldObjectDelta\s+delta\s*\).*?if\s*\(!transient\).*?ReplicationBuildingLifecycleV2DeltaKind.*?ReplicationBuildingRepairV2DeltaKind.*?SupersedePendingReplicationBuildingLifecycleDeltas\(\s*delta\s*\).*?replicationPendingWorldObjectDeltas\[delta\.Sequence\]' `
    "Reliable lifecycle/repair rows are not superseded before entering the pending map."
Require-SourcePattern $sources.WorldDeltas 'FormatReplicationPendingBuildingLifecycleSupersessionKey\(.*?ReplicationBuildingLifecycleV2DeltaKind.*?ReplicationBuildingRepairV2DeltaKind.*?"\|epoch=".*?"\|uid="' `
    "Lifecycle/repair supersession is not fenced by delta kind, epoch, and building identity."
Require-SourcePattern $sources.WorldDeltas 'IsTransientReplicationWorldObjectDelta\(.*?ReplicationBuildingProgressV2DeltaKind' `
    "Selected-building progress is not transient and can enter the reliable resend queue."
Require-SourcePattern $sources.Lifecycle 'SendClientSelectedBuildingProgressRequestV2IfDue\(.*?TryResolveReplicationSelectedBuildingHostIdV2.*?SendReplicationLocalCommandIntent\(' `
    "Selected-building progress is not requested on demand by stable host identity."
Require-SourcePattern $sources.Lifecycle 'TryHandleReplicationBuildingProgressRequestV2\(.*?ReplicationTrackedHostBuildingsV2\.TryGetValue.*?ReplicationBuildingProgressV2DeltaKind' `
    "The host does not answer selected-building progress from its tracked identity set."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingProgressV2\(.*?TryResolveReplicationBuildingProgressPresentationV2\(.*?updateProgressMethod\.Invoke\(.*?CorrectReplicationClientBuildingPresentationV2\(' `
    "Selected-building progress does not correct the view-only BuildProgress presentation anchor."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingProgressV2\(.*?ReplicationClientBuildingTerminalRevisionV2\.ContainsKey\(delta\.UniqueId\).*?TryGetCursor\(buildingKey,\s*out\s+var\s+cursor\).*?revision\s*!=\s*cursor\.Revision.*?else\s+if\s*\(revision\s*!=\s*0L\)' `
    "Progress correction is not terminal-fenced and exact-revision-fenced against lifecycle state."

# Client construction playback is view-only. It may invoke BuildProgress.UpdateProgress
# from a shadow clock, but must not mutate authoritative RemainingTime in the playback pump.
$presentationPump = [regex]::Match(
    $sources.Lifecycle,
    'private\s+static\s+void\s+ProcessReplicationClientBuildingPresentationV2\(\)(?<body>.*?)private\s+static\s+bool\s+TryResolveReplicationBuildingProgressPresentationV2\(',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $presentationPump.Success -or
    -not $presentationPump.Groups['body'].Value.Contains('UpdateProgressMethod.Invoke')) {
    $contractFailures.Add("Client construction presentation does not drive BuildProgress.UpdateProgress.")
}
elseif ($presentationPump.Groups['body'].Value.Contains('TryApplyReplicationBuildingRemainingTime') -or
    $presentationPump.Groups['body'].Value.Contains('SetRemainingTime')) {
    $contractFailures.Add("Client construction presentation pump mutates authoritative RemainingTime instead of remaining view-only.")
}

# Retry exhaustion must reach the client as a durable recovery request. A lost ACK
# is not divergence, so an already-applied source sequence must suppress the reload.
Require-SourcePattern $sources.Lifecycle 'ReplicationBuildingRecoveryRequiredV2DeltaKind\s*=\s*"BuildingRecoveryRequiredV2"' `
    "Building V2 has no RecoveryRequired wire kind."
Require-SourcePattern $sources.Lifecycle 'TrySendReplicationBuildingRecoveryRequiredV2\(.*?"\s*sourceSequence="\s*\+\s*sourceDelta\.Sequence\.ToString.*?SendReplicationWorldObjectDelta\(new\s+ReplicationWorldObjectDelta\(.*?ReplicationBuildingRecoveryRequiredV2DeltaKind' `
    "The host cannot send a client-visible RecoveryRequired delta tied to the failed source sequence."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingRecoveryRequiredV2\(' `
    "The client has no RecoveryRequired apply path."
Require-SourcePattern $sources.WorldDeltas 'ShouldDropReplicationWorldObjectDeltaAfterRetries\(.*?ReplicationBuildingBlueprintBatchPlacedDeltaKind.*?durableTransaction.*?TrySendReplicationBuildingRecoveryRequiredV2\(.*?ReplicationBuildingRepairV2DeltaKind.*?TrySendReplicationBuildingRecoveryRequiredV2\(' `
    "Durable placement/repair retry exhaustion does not escalate through RecoveryRequired."
Require-SourcePattern $sources.WorldDeltas 'TryApplyReplicationWorldObjectDelta\(.*?ReplicationBuildingRecoveryRequiredV2DeltaKind.*?TryApplyReplicationBuildingRecoveryRequiredV2\(delta,\s*out\s+detail\)' `
    "The client world-delta dispatcher does not apply RecoveryRequired."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingRecoveryRequiredV2\(.*?replicationClientAppliedWorldObjectDeltaSequences\.Contains\(sourceSequence\).*?source-already-applied.*?ScheduleReplicationBuildBatchRecovery\(' `
    "RecoveryRequired cannot distinguish lost ACK from unapplied authoritative state."
Require-SourcePattern $sources.Lifecycle 'TrySendReplicationBuildingRecoveryRequiredV2\(.*?pendingRecoveryKey.*?ReplicationPendingSupersedableWorldDeltaSequenceByKey\.TryGetValue\(.*?forceRecovery="\s*\+\s*\(forceRecovery\s*\?\s*"true"\s*:\s*"false"\)' `
    "A superseded unresolved RecoveryRequired marker does not preserve aggregate divergence evidence."
Require-SourcePattern $sources.Lifecycle 'TryApplyReplicationBuildingRecoveryRequiredV2\(.*?TryReadReplicationWorldObjectDetailBool\(.*?"forceRecovery".*?if\s*\(\s*!forceRecovery\s*&&\s*replicationClientAppliedWorldObjectDeltaSequences\.Contains\(sourceSequence\)' `
    "Forced aggregate recovery can still be suppressed by the replacement marker's source sequence."

if ($contractFailures.Count -gt 0) {
    throw "BuildingReplicationV2 source contract failed:`n - $($contractFailures -join "`n - ")"
}

Write-Host "PASS BuildingReplicationV2SourceContract"
