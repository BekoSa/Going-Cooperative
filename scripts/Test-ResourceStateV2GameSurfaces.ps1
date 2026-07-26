param(
    [string]$GameRoot = ".."
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$gameRootPath = (Resolve-Path (Join-Path $root $GameRoot)).Path
$cecilPath = Join-Path $gameRootPath "BepInEx\core\Mono.Cecil.dll"
$assemblyPath = Join-Path $gameRootPath "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$sourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationResourceStateV2.cs"
$containerSourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationResourceContainers.cs"
$runtimeSourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$worldDeltaSourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
$pluginSourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Plugin.cs"
$configSourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$trackedConfigPath = Join-Path $root "config\replication.cfg"

foreach ($path in @(
    $cecilPath,
    $assemblyPath,
    $sourcePath,
    $containerSourcePath,
    $runtimeSourcePath,
    $worldDeltaSourcePath,
    $pluginSourcePath,
    $configSourcePath,
    $trackedConfigPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required resource-state-v2 surface is missing: $path"
    }
}

Add-Type -Path $cecilPath
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)
$types = @{}
foreach ($type in $assembly.MainModule.Types) {
    $types[$type.FullName] = $type
}

function Require-Methods {
    param(
        [string]$TypeName,
        [string[]]$MethodNames
    )

    if (-not $types.ContainsKey($TypeName)) {
        throw "Native type is missing: $TypeName"
    }
    $available = @($types[$TypeName].Methods | ForEach-Object Name)
    foreach ($methodName in $MethodNames) {
        if ($available -notcontains $methodName) {
            throw "Native method is missing: $TypeName.$methodName"
        }
    }
}

Require-Methods "NSMedieval.Components.Storage" @(
    "Transfer",
    "Add",
    "Consume",
    "Take",
    "DeleteResource",
    "ClearResources",
    "ClearAll",
    "Dispose"
)
Require-Methods "NSMedieval.State.ResourcePileInstance" @(
    "OnResourceAdded",
    "OnResourceTaken",
    "SetPlacedOnStorage",
    "Dispose"
)
Require-Methods "NSMedieval.Manager.ResourcePileFactory" @("ProducePile")
Require-Methods "NSMedieval.StorageUniversal.UniversalStorage" @(
    "StoreResourcePile",
    "OnPileTaken",
    "DropResource",
    "DropStorage",
    "DisposeStorage"
)
Require-Methods "NSMedieval.View.WorkerView" @("Setup", "Dispose")
Require-Methods "NSMedieval.View.Animals.AnimalView" @("Setup", "Dispose")
Require-Methods "NSMedieval.View.NPCView" @("Setup", "Dispose")

$source = Get-Content -LiteralPath $sourcePath -Raw
$containerSource = Get-Content -LiteralPath $containerSourcePath -Raw
$runtimeSource = Get-Content -LiteralPath $runtimeSourcePath -Raw
$worldDeltaSource = Get-Content -LiteralPath $worldDeltaSourcePath -Raw
$pluginSource = Get-Content -LiteralPath $pluginSourcePath -Raw
$configSource = Get-Content -LiteralPath $configSourcePath -Raw
$trackedConfig = Get-Content -LiteralPath $trackedConfigPath -Raw

foreach ($field in @(
    "replicationConfigResourceStateV2",
    "replicationConfigAgentInventoryStateV2",
    "replicationConfigGroundPileStateV2",
    "replicationConfigShelfStorageStateV2",
    "replicationConfigResourceStateV2Diagnostics"
)) {
    if ($configSource -notmatch [regex]::Escape("private static bool $field;")) {
        throw "Config gate is missing or not default-off: $field"
    }
}

foreach ($line in @(
    "resourceStateV2=false",
    "agentInventoryStateV2=false",
    "groundPileStateV2=false",
    "shelfStorageStateV2=false",
    "resourceStateV2Diagnostics=false"
)) {
    if ($trackedConfig -notmatch "(?m)^$([regex]::Escape($line))\s*$") {
        throw "Tracked config does not preserve rollback-off default: $line"
    }
}

foreach ($required in @(
    "TryInstallReplicationResourceStateV2Hooks",
    "ReplicationResourceStateStorageMutationPostfixV2",
    "ReplicationResourceStatePileMutationPostfixV2",
    "ReplicationResourceStatePileDisposePrefixV2",
    "ReplicationResourceStateShelfMutationPostfixV2",
    "QueueReplicationResourceStateV2Baseline",
    "ScheduleReplicationResourceStateRecoveryV2",
    "ReplicationResourceStateV2RecoveryAuditIntervalSeconds",
    "ReplicationResourceStateV2DirtyBudget",
    "ReplicationResourceStateV2RecoveryBudget",
    "ReplicationResourceStateChangedScratchV2",
    "FormatReplicationResourceStateV2Capability"
)) {
    if ($source -notmatch [regex]::Escape($required)) {
        throw "Resource State V2 source contract is missing: $required"
    }
}

if ($pluginSource -notmatch "TryInstallReplicationResourceStateV2Hooks\(harmony\)") {
    throw "Resource State V2 Harmony installer is not wired."
}
if ($runtimeSource -notmatch "UpdateReplicationResourceStateV2\(\)") {
    throw "Resource State V2 host update is not wired."
}
if ($runtimeSource -notmatch "QueueReplicationResourceStateV2Baseline\(\)") {
    throw "Resource State V2 hello baseline is not wired."
}
if ($runtimeSource -notmatch "ClearReplicationResourceStateV2\(\)") {
    throw "Resource State V2 reset is not wired."
}
if ($runtimeSource -notmatch '\|resourcev2=') {
    throw "Resource State V2 hello capability is not advertised."
}
if ($runtimeSource -notmatch "resource-state-v2-capability-mismatch") {
    throw "Resource State V2 hello capability mismatch is not fail-closed."
}

foreach ($skip in @(
    "ReplicationAgentInventoryStateV2Enabled",
    "ReplicationGroundPileStateV2Enabled",
    "ReplicationShelfStorageStateV2Enabled",
    "HasLegacyReplicationResourceContainerPollingDomains"
)) {
    if ($containerSource -notmatch [regex]::Escape($skip)) {
        throw "Legacy domain isolation is missing: $skip"
    }
}

if ($source -match "FindObjectsOfType") {
    throw "Resource State V2 must use the shared bounded registry bootstrap, not a new scene scan."
}
if ($source -notmatch "FindReplicationAnimatedAgentViews\(\)") {
    throw "Agent bootstrap does not use the shared animated-view cache."
}
if ($source -notmatch "SpawnedPileInstances") {
    throw "Ground-pile bootstrap does not use the native live registry."
}
if ($source -notmatch "StorageCommonManager") {
    throw "Shelf bootstrap does not use the native live storage registry."
}
if ($source -notmatch "dirtySet\.Add\(key\)") {
    throw "Dirty mutation coalescing is missing."
}
if ($source -notmatch "mark\(key, false\)") {
    throw "Recovery audits must not force unchanged rows onto the wire."
}
if ($source -notmatch "previous\.Signature == signature && !force") {
    throw "Unchanged non-baseline Resource State V2 rows are not suppressed."
}
if ($worldDeltaSource -notmatch "authoritative-pile-state unchanged") {
    throw "Client pile apply does not short-circuit an already-matching exact amount."
}
if ($worldDeltaSource -notmatch "currentPileAmount == exactPileAmount") {
    throw "Client pile no-op guard does not compare the native amount."
}

"Resource State V2 game surfaces and rollback contracts passed."
