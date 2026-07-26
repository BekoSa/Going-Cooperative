[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$medicalPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationMedicalV1.cs"
$runtimePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$pluginPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Plugin.cs"
$commandPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandApplication.cs"
$worldDeltaPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
$resourceContainersPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationResourceContainers.cs"
$configSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$configPath = Join-Path $repositoryRoot "config\replication.cfg"

foreach ($path in @($cecilPath, $gameAssemblyPath, $medicalPath, $runtimePath, $pluginPath, $commandPath, $worldDeltaPath, $resourceContainersPath, $configSourcePath, $configPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Medical-v1 contract input missing: $path" }
}

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)

function Get-TypeTree {
    param([Parameter(Mandatory)] $Type)
    $Type
    foreach ($nested in $Type.NestedTypes) { Get-TypeTree -Type $nested }
}

$types = @($assembly.MainModule.Types | ForEach-Object { Get-TypeTree -Type $_ })
function Get-GameType {
    param([Parameter(Mandatory)][string] $FullName)
    $type = $types | Where-Object FullName -eq $FullName | Select-Object -First 1
    if ($null -eq $type) { throw "Game type is missing: $FullName" }
    return $type
}

function Assert-Method {
    param(
        [Parameter(Mandatory)] $Type,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ParameterTypes
    )
    $method = $Type.Methods | Where-Object {
        $_.Name -eq $Name -and
        $_.Parameters.Count -eq $ParameterTypes.Count -and
        (Compare-Object @($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) $ParameterTypes).Count -eq 0
    } | Select-Object -First 1
    if ($null -eq $method) { throw "$($Type.FullName).$Name($($ParameterTypes -join ',')) is missing." }
    return $method
}

function Assert-Field {
    param([Parameter(Mandatory)] $Type, [Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $FieldType)
    $field = $Type.Fields | Where-Object { $_.Name -eq $Name -and $_.FieldType.FullName -eq $FieldType } | Select-Object -First 1
    if ($null -eq $field) { throw "$($Type.FullName).$Name field is missing or changed type." }
}

function Assert-Property {
    param([Parameter(Mandatory)] $Type, [Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $PropertyType)
    $property = $Type.Properties | Where-Object { $_.Name -eq $Name -and $_.PropertyType.FullName -eq $PropertyType } | Select-Object -First 1
    if ($null -eq $property) { throw "$($Type.FullName).$Name property is missing or changed type." }
}

$stats = Get-GameType "NSMedieval.StatsSystem.StatsInstance"
foreach ($eventName in @("OnEffectorStartEvent", "OnEffectorStackEvent", "OnEffectorEndEvent")) {
    $event = $stats.Events | Where-Object Name -eq $eventName | Select-Object -First 1
    if ($null -eq $event -or $event.EventType.FullName -ne 'System.Action`1<NSMedieval.StatsSystem.StatEffector>') {
        throw "StatsInstance.$eventName native event contract changed."
    }
}
[void](Assert-Method $stats "StartEffector" @("System.String", "System.Single", "System.Boolean", "System.Int32", "System.String"))
[void](Assert-Method $stats "GetActiveEffectors" @())
[void](Assert-Method $stats "HandleEffectorTimers" @("System.Int64", "System.Int32"))

$active = Get-GameType "NSMedieval.StatsSystem.ActiveEffectorInfo"
Assert-Field $active "name" "System.String"
Assert-Field $active "startTime" "System.Int64"
Assert-Field $active "stackCount" "System.Int32"
Assert-Field $active "duration" "System.Single"
Assert-Field $active "durationModifier" "System.Single"
Assert-Field $active "woundInfo" "NSMedieval.StatsSystem.WoundEffectorInfo"
Assert-Field $active "causeCreatureName" "System.String"
Assert-Field $active "causeCreatureBodyType" "NSMedieval.BodyType"
Assert-Field $active "causeHumanoidPerkId" "System.String"

$wound = Get-GameType "NSMedieval.StatsSystem.WoundEffectorInfo"
foreach ($field in @(
    @("minSeverity", "System.Single"),
    @("currentSeverity", "System.Single"),
    @("needTend", "System.Boolean"),
    @("needRest", "System.Boolean"),
    @("lastTendTime", "System.Int64"),
    @("lastTendQuality", "System.Single"),
    @("lastTickTime", "System.Int64"))) {
    Assert-Field $wound $field[0] $field[1]
}

$creature = Get-GameType "NSMedieval.State.CreatureBase"
[void](Assert-Method $creature "TendWounds" @("System.Single"))
[void](Assert-Method $creature "HandleBloodLoss" @())
Assert-Property $creature "Stats" "NSMedieval.StatsSystem.StatsInstance"
Assert-Property $creature "IsReceivingWoundTreatman" "System.Boolean"
Assert-Property $creature "CanReceiveWoundTreatment" "System.Boolean"

$prioritiseBase = Get-GameType "NSMedieval.AdditionalMenuItems.AdditionalMenuPrioritiseItem"
[void](Assert-Method $prioritiseBase "ForceGoal" @("System.String", "NSMedieval.State.IReservable", "NSMedieval.State.HumanoidInstance"))
foreach ($typeName in @(
    "NSMedieval.AdditionalMenuItems.PrioritiseTendWoundsMenuItem",
    "NSMedieval.AdditionalMenuItems.PrioritiseSelfTendWoundsMenuItem",
    "NSMedieval.AdditionalMenuItems.PrioritiseAnimalTendWoundsMenuItem")) {
    [void](Assert-Method (Get-GameType $typeName) "OnClickCallback" @())
}

foreach ($typeName in @(
    "NSMedieval.UI.WorkerHealthExtraPanel",
    "NSMedieval.UI.AnimalHealthExtraPanel",
    "NSMedieval.UI.EnemyHealthExtraPanel")) {
    $panel = Get-GameType $typeName
    [void](Assert-Method $panel "SetupTabPanel" @())
    [void](Assert-Method $panel "UpdateTabPanel" @())
}

$medical = Get-Content -LiteralPath $medicalPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$plugin = Get-Content -LiteralPath $pluginPath -Raw
$command = Get-Content -LiteralPath $commandPath -Raw
$worldDelta = Get-Content -LiteralPath $worldDeltaPath -Raw
$resourceContainers = Get-Content -LiteralPath $resourceContainersPath -Raw
$configSource = Get-Content -LiteralPath $configSourcePath -Raw
$config = Get-Content -LiteralPath $configPath -Raw

foreach ($marker in @(
    "ReplicationMedicalWoundStateDeltaKind",
    "OnEffectorStartEvent",
    "OnEffectorStackEvent",
    "OnEffectorEndEvent",
    "ReplicationMedicalOrderPrefix",
    "TryApplyReplicationMedicalTreatmentOrder",
    "TryReconcileReplicationMedicalWounds",
    "RefreshReplicationMedicalHealthPanels",
    "ResetReplicationMedicalV1State")) {
    if (-not $medical.Contains($marker)) { throw "Medical-v1 implementation marker missing: $marker" }
}
if ($medical.Contains("FindObjectsOfTypeAll")) { throw "Medical-v1 must not introduce full-heap scans." }
if (-not $plugin.Contains("TryInstallReplicationMedicalV1Hooks")) { throw "Medical-v1 hooks are not installed from Plugin.Awake." }
if (-not $runtime.Contains("UpdateReplicationMedicalV1")) { throw "Medical-v1 update is not wired into the runtime pump." }
if (-not $runtime.Contains("ResetReplicationMedicalV1State")) { throw "Medical-v1 reset is not wired into runtime stop/reload." }
if (-not $runtime.Contains('"|medical="')) { throw "Medical-v1 capability is missing from hello fingerprint." }
if (-not $command.Contains("TryReadTreatmentOrder") -or -not $command.Contains("TryReadStateRequest")) { throw "Medical-v1 commands are not routed." }
if (-not $worldDelta.Contains("TryApplyReplicationMedicalWorldDelta")) { throw "Medical-v1 state delta is not routed." }
if (-not $worldDelta.Contains('"TreatWounds"') -or -not $worldDelta.Contains('"TreatSelf"')) { throw "Generic treatment animation dependencies are missing." }
if (-not $resourceContainers.Contains('"MedicineStorage"') -or -not $resourceContainers.Contains('"agent-medicine"')) { throw "Agent medicine-container dependency is missing." }

foreach ($gate in @(
    "medicalReplicationV1",
    "medicalWoundStateV1",
    "medicalTreatmentOrdersV1",
    "medicalTreatmentPresentationV1",
    "medicalPanelRefreshV1",
    "medicalClientWoundTickSuppressionV1",
    "medicalDiagnostics")) {
    if ($configSource.IndexOf("replicationConfig$gate", [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "Medical-v1 source gate missing: $gate" }
    if (-not $config.Contains("$gate=false")) { throw "Medical-v1 release gate must default false: $gate" }
}

Write-Host "PASS MedicalV1GameSurfaces"
