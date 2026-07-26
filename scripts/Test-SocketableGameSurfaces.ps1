[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"

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

$manager = Get-GameType "NSMedieval.BuildingComponents.BuildingPlacementManager"
$vec3Int = Get-GameType "NSMedieval.Vec3Int"
$objectSide = Get-GameType "NSMedieval.Construction.ObjectSide"
$shelfComponent = Get-GameType "NSMedieval.BuildingComponents.ShelfComponent"

$tryPlace = Get-Method $manager "TryPlaceSocketable" @(
    "NSMedieval.Vec3Int",
    "NSMedieval.Vec3Int",
    "NSMedieval.Construction.ObjectSide",
    "System.Int32"
)
$mouseUp = Get-Method $manager "MouseUpSocketable" @()
$objectPlaced = Get-Method $manager "ObjectPlacedOnMap" @(
    "NSMedieval.BuildingComponents.BaseBuildingViewComponent"
)
$shelfFinished = Get-Method $shelfComponent "OnBaseBuildingEnterFinishedState" @(
    "System.Boolean"
)

foreach ($fieldName in @("x", "y", "z")) {
    $field = $vec3Int.Fields |
        Where-Object { $_.Name -eq $fieldName -and $_.FieldType.FullName -eq "System.Int32" } |
        Select-Object -First 1
    if ($null -eq $field) { throw "Vec3Int lowercase field missing: $fieldName" }
}

$tryPlaceOperands = @($tryPlace.Body.Instructions | ForEach-Object { [string]$_.Operand })
if (-not ($tryPlaceOperands | Where-Object {
    $_ -eq "System.Void NSMedieval.BuildingComponents.BuildingPlacementManager::ObjectPlacedOnMap(NSMedieval.BuildingComponents.BaseBuildingViewComponent)"
})) {
    throw "TryPlaceSocketable no longer commits through ObjectPlacedOnMap."
}
if (-not ($tryPlaceOperands | Where-Object {
    $_ -eq "System.Void NSMedieval.BuildingComponents.SocketComponentInstance::AttachToSocket(NSMedieval.BuildingComponents.BaseBuildingInstance,NSMedieval.Construction.ObjectSide,System.Boolean)"
})) {
    throw "TryPlaceSocketable no longer attaches through SocketComponentInstance.AttachToSocket."
}
$contractChanged =
    $tryPlace.ReturnType.FullName -ne "System.Void" -or
    $mouseUp.ReturnType.FullName -ne "System.Void" -or
    $objectSide.IsEnum -ne $true -or
    $objectPlaced.ReturnType.FullName -ne "System.Void"
if ($contractChanged) {
    throw "Socketable native contract return/type shape changed."
}

$shelfFinishedOperands = @($shelfFinished.Body.Instructions | ForEach-Object { [string]$_.Operand })
if (-not ($shelfFinishedOperands | Where-Object {
    $_ -eq "NSMedieval.BuildingComponents.ShelfComponentInstance NSMedieval.BuildingComponents.ComponentFactory::CreateComponentInstance(NSMedieval.BuildingComponents.BaseBuildingInstance,NSMedieval.BuildingComponents.ShelfComponentBlueprint)"
})) {
    throw "Shelf finish callback no longer creates ShelfComponentInstance through ComponentFactory."
}
if (-not ($shelfFinishedOperands | Where-Object {
    ($_.Contains("NSMedieval.BuildingComponents.ShelfComponent,NSMedieval.BuildingComponents.ShelfComponentInstance>") -and
        $_.Contains("::AddToCache(TComponent,TComponentInstance)"))
})) {
    throw "Shelf finish callback no longer registers the component through ShelfComponentManager."
}

$captureSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.Building.cs"
) -Raw
$batchSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildBatch.cs"
) -Raw
if ($captureSource.IndexOf(
        "TryReadReplicationVec3Int(value, out x, out y, out z)",
        [StringComparison]::Ordinal) -lt 0) {
    throw "Socketable semantic endpoint capture is not using the lowercase-aware Vec3Int reader."
}
if ($batchSource.IndexOf(
        'AccessTools.TypeByName("NSMedieval.Construction.ObjectSide")',
        [StringComparison]::Ordinal) -lt 0) {
    throw "Socketable replay is not resolving the exact live ObjectSide namespace."
}
if ($batchSource.IndexOf(
        'AccessTools.TypeByName("NSMedieval.ObjectSide")',
        [StringComparison]::Ordinal) -ge 0) {
    throw "Socketable replay still contains the obsolete ObjectSide namespace."
}
if ($batchSource.IndexOf(
        '"kind=Socketable origin="',
        [StringComparison]::Ordinal) -lt 0 -or
    $batchSource.IndexOf(
        '" outcome=preinvoke gate="',
        [StringComparison]::Ordinal) -lt 0) {
    throw "Socketable replay is missing fail-closed pre-invoke diagnostics."
}

$lifecycleSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildingLifecycleV2.cs"
) -Raw
foreach ($required in @(
    "TryRepairReplicationFinishedSocketableShelfV2",
    '"AttachedToSocketComponent"',
    '"NSMedieval.BuildingComponents.ShelfComponent"',
    "GetComponentInChildren(",
    '"OnBaseBuildingEnterFinishedState"',
    "enterFinished.Invoke(shelfComponent, new object[] { false })",
    '"initialized-via-native-finish"'
)) {
    if ($lifecycleSource.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Socketable shelf lifecycle repair is missing required contract: $required"
    }
}

$identitySource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationHostIdentityMap.cs"
) -Raw
foreach ($required in @(
    "RegisterReplicationBuildingHostIdentity",
    "TryResolveReplicationBuildingCandidateInstance(",
    'source + "-instance"'
)) {
    if ($identitySource.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Socketable shelf placement identity pairing is missing required contract: $required"
    }
}

$shelfManifestSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationShelfStorageManifest.cs"
) -Raw
if ($shelfManifestSource.IndexOf(
        "TryRepairReplicationShelfManifestTargetV1",
        [StringComparison]::Ordinal) -ge 0 -or
    $shelfManifestSource.IndexOf(
        "TryFindReplicationBuildingBlueprintCandidate(",
        [StringComparison]::Ordinal) -ge 0) {
    throw "Shelf manifest apply regressed to a retry-time scene scan."
}

Write-Host "PASS SocketableGameSurfaces"
