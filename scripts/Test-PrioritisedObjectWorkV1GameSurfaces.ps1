[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$implementationPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationPrioritisedObjectWorkV1.cs"
$commandPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandApplication.cs"
$runtimePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$worldDeltaPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
$regionPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.RegionOrders.cs"
$configSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$configPath = Join-Path $repositoryRoot "config\replication.cfg"

foreach ($path in @(
    $cecilPath,
    $gameAssemblyPath,
    $implementationPath,
    $commandPath,
    $runtimePath,
    $worldDeltaPath,
    $regionPath,
    $configSourcePath,
    $configPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Prioritised-object-work-v1 contract input missing: $path"
    }
}

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)

function Get-GameType([string] $fullName) {
    $type = $assembly.MainModule.Types |
        Where-Object FullName -eq $fullName |
        Select-Object -First 1
    if ($null -eq $type) { throw "Missing game type: $fullName" }
    return $type
}

function Get-Method($type, [string] $name, [string[]] $parameterTypes) {
    $signature = $parameterTypes -join "|"
    $method = $type.Methods | Where-Object {
        $_.Name -eq $name -and
        (@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join "|") -eq $signature
    } | Select-Object -First 1
    if ($null -eq $method) {
        throw "Missing $($type.FullName).$name($($parameterTypes -join ','))"
    }
    return $method
}

$prioritiseBase = Get-GameType "NSMedieval.AdditionalMenuItems.AdditionalMenuPrioritiseItem"
[void](Get-Method $prioritiseBase "ForceGoal" @(
    "System.String",
    "NSMedieval.State.IReservable",
    "NSMedieval.State.HumanoidInstance"
))

$contracts = @{
    "PrioritiseHarvestMenuItem" = @("HarvestGoal", "GiveOrder")
    "PrioritiseChopMenuItem" = @("ChopTreeGoal", "GiveOrder", "orderTypeToGive")
    "PrioritiseMineMenuItem" = @("DigGoal", "GetAsTarget")
    "PrioritiseFishingMenuItem" = @("FishingGoal", "GiveOrder")
    "PrioritiseHaulingMenuItem" = @("StockpileHaulingGoal", "ReleaseAll", "set_IsForbidden")
    "PrioritiseBuildingConstructionMenuItem" = @("ConstructBuildingGoal", "GetAsTarget")
    "PrioritiseBuildingMaterialsDeliveryMenuItem" = @("DeliverBuildingMaterialsGoal", "GetAsTarget")
    "PrioritiseBuildingDeConstructionMenuItem" = @("DeconstructGoal", "SetMarkedForDestruction", "get_Version")
    "PrioritiseBuildingUninstallMenuItem" = @("UninstallBuildingGoal", "SetIsMarkedForUninstall", "AddToUninstallList")
    "PrioritiseStripMenuItem" = @("StripCarcassGoal", "MarkForStripping", "set_IsForbidden")
}

foreach ($entry in $contracts.GetEnumerator()) {
    $type = Get-GameType "NSMedieval.AdditionalMenuItems.$($entry.Key)"
    $method = Get-Method $type "OnClickCallback" @()
    $operands = @($method.Body.Instructions | ForEach-Object { [string]$_.Operand })
    foreach ($required in $entry.Value) {
        if (-not ($operands | Where-Object { $_.Contains($required) })) {
            throw "$($entry.Key).OnClickCallback native contract no longer contains $required"
        }
    }
    if (-not ($operands | Where-Object {
        $_.Contains("AdditionalMenuPrioritiseItem::ForceGoal")
    })) {
        throw "$($entry.Key).OnClickCallback no longer commits through ForceGoal."
    }
}

$plantView = Get-GameType "NSMedieval.Views.Resources.PlantMapResourceView"
[void](Get-Method $plantView "OnOrderRefreshed" @())
$mapResourceView = Get-GameType 'NSMedieval.Views.Resources.MapResourceView`1'
[void](Get-Method $mapResourceView "SetOrderIcon" @())

$implementation = Get-Content -LiteralPath $implementationPath -Raw
$command = Get-Content -LiteralPath $commandPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$worldDelta = Get-Content -LiteralPath $worldDeltaPath -Raw
$region = Get-Content -LiteralPath $regionPath -Raw
$configSource = Get-Content -LiteralPath $configSourcePath -Raw
$config = Get-Content -LiteralPath $configPath -Raw

foreach ($marker in @(
    "TryInstallReplicationPrioritisedObjectWorkV1Hooks",
    "ReplicationPrioritisedObjectWorkV1Prefix",
    "TryApplyReplicationPrioritisedObjectWorkV1",
    "ReplicationPrioritisedObjectWorkV1Postfix",
    "SendReplicationPrioritisedObjectWorkResultIfSupported",
    "TryApplyReplicationPrioritisedObjectWorkResultV1",
    "PrioritisedObjectWorkResultV1",
    "OnOrderRefreshed",
    "SetOrderIcon",
    "SetPreferredReservable",
    "TryToExclusiveReservation",
    "ForceNextGoalExclusive",
    "ReleaseObject",
    "SetMarkedForDestruction",
    "AddToUninstallList",
    "MarkForStripping",
    "UpdateReplicationPrioritisedObjectWorkV1",
    "ResetReplicationPrioritisedObjectWorkV1State")) {
    if (-not $implementation.Contains($marker)) {
        throw "Prioritised-object-work-v1 implementation marker missing: $marker"
    }
}
if ($implementation.Contains("FindObjectsOfTypeAll")) {
    throw "Prioritised-object-work-v1 must not add a full-heap polling path."
}
if (-not $command.Contains("TryReadPrioritisedObjectWorkV1Payload")) {
    throw "Prioritised-object-work-v1 custom command is not routed."
}
if (-not $runtime.Contains('"|orders="')) {
    throw "Prioritised-object-work-v1 capability is missing from hello fingerprint."
}
if (-not $runtime.Contains("UpdateReplicationPrioritisedObjectWorkV1") -or
    -not $runtime.Contains("ResetReplicationPrioritisedObjectWorkV1State")) {
    throw "Prioritised-object-work-v1 deferred work/reset is not wired into runtime."
}
if (-not $runtime.Contains("SendReplicationPrioritisedObjectWorkResultIfSupported")) {
    throw "Prioritised-object-work-v1 authoritative result is not wired into host command handling."
}
if (-not $worldDelta.Contains("TryApplyReplicationPrioritisedObjectWorkResultV1") -or
    -not $worldDelta.Contains("ReplicationPrioritisedObjectWorkResultV1DeltaKind")) {
    throw "Prioritised-object-work-v1 authoritative result is not wired into client world-delta handling."
}
if (-not $region.Contains("IsReplicationPrioritisedObjectWorkV1MenuType")) {
    throw "Legacy RegionOrder capture is not suppressed for typed prioritize work."
}
if (-not $configSource.Contains("replicationConfigPrioritisedObjectWorkV1")) {
    throw "Prioritised-object-work-v1 source gate is missing."
}
if (-not $config.Contains("prioritisedObjectWorkV1=false")) {
    throw "Prioritised-object-work-v1 release gate must default false."
}

Write-Host "PASS PrioritisedObjectWorkV1GameSurfaces"
