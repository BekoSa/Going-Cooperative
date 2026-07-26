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
    $type = $assembly.MainModule.Types | Where-Object FullName -eq $fullName | Select-Object -First 1
    if ($null -eq $type) { throw "Missing game type: $fullName" }
    return $type
}

function Assert-Property($type, [string] $name, [string] $propertyType) {
    $property = $type.Properties | Where-Object {
        $_.Name -eq $name -and $_.PropertyType.FullName -eq $propertyType
    } | Select-Object -First 1
    if ($null -eq $property) { throw "Missing $($type.FullName).$name : $propertyType" }
}

function Assert-Method($type, [string] $name, [string[]] $parameterTypes) {
    $signature = $parameterTypes -join "|"
    $method = $type.Methods | Where-Object {
        $_.Name -eq $name -and
        (@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join "|") -eq $signature
    } | Select-Object -First 1
    if ($null -eq $method) { throw "Missing $($type.FullName).$name($($parameterTypes -join ','))" }
}

$shelf = Get-GameType "NSMedieval.BuildingComponents.ShelfComponentInstance"
$universal = Get-GameType "NSMedieval.StorageUniversal.UniversalStorage"
$slot = Get-GameType "NSMedieval.StorageUniversal.StorageSlot"
$pile = Get-GameType "NSMedieval.State.ResourcePileInstance"
$manager = Get-GameType "NSMedieval.StorageUniversal.StorageCommonManager"

Assert-Property $manager "AllStorages" 'FoxyVoxel.Collections.HashSetIterationOptimized`1<NSMedieval.IStorage>'
Assert-Property $shelf "AllStorage" 'System.Collections.Generic.List`1<NSMedieval.StorageUniversal.UniversalStorage>'
Assert-Property $universal "GetOwner" "NSMedieval.IStorage"
Assert-Property $universal "StorageSlots" "NSMedieval.StorageUniversal.StorageSlot[]"
Assert-Property $slot "Pile" "NSMedieval.State.ResourcePileInstance"
Assert-Property $pile "InstanceStorage" "NSMedieval.StorageUniversal.UniversalStorage"
Assert-Method $slot "SetStoredPile" @("NSMedieval.State.ResourcePileInstance")
Assert-Method $universal "StoreResourcePile" @(
    "NSMedieval.State.ResourceInstance",
    "NSMedieval.StorageUniversal.StorageSlot"
)
Assert-Method $pile "SetPlacedOnStorage" @(
    "NSMedieval.IStorage",
    "NSMedieval.StorageUniversal.UniversalStorage"
)

$pluginSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Plugin.cs"
) -Raw
$manifestSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationShelfStorageManifest.cs"
) -Raw
$deltaSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
) -Raw

if ($pluginSource.IndexOf(
        "TryInstallReplicationShelfStorageManifestHooks(harmony)",
        [StringComparison]::Ordinal) -lt 0) {
    throw "Shelf native-store authority hook is not installed."
}
if ($manifestSource.IndexOf(
        '"NSMedieval.BuildingComponents.ShelfComponentInstance"',
        [StringComparison]::Ordinal) -lt 0 -or
    $manifestSource.IndexOf(
        "replicationShelfStorageHostStoreDepth++",
        [StringComparison]::Ordinal) -lt 0 -or
    $manifestSource.IndexOf(
        "ReplicationShelfStoreResourcePileFinalizer",
        [StringComparison]::Ordinal) -lt 0) {
    throw "Shelf native-store scope is not fail-safe and shelf-specific."
}
if ($deltaSource.IndexOf(
        "ShouldSuppressReplicationShelfStorePileSpawn()",
        [StringComparison]::Ordinal) -lt 0) {
    throw "Generic coordinate pile replication is not suppressed during shelf storage."
}
if ($manifestSource.IndexOf(
        "TryCleanupReplicationShelfCoordinatePile(",
        [StringComparison]::Ordinal) -lt 0 -or
    $deltaSource.IndexOf(
        "ReplicationClientGenericSpawnedResourcePiles.Add",
        [StringComparison]::Ordinal) -lt 0) {
    throw "Shelf receiver recovery is not restricted to known replicated coordinate piles."
}
if ($manifestSource.IndexOf(
        '"BlueprintId",',
        [StringComparison]::Ordinal) -lt 0 -or
    $manifestSource.IndexOf(
        '"blueprintId",',
        [StringComparison]::Ordinal) -lt 0) {
    throw "Shelf manifest does not read the live ResourceInstance.BlueprintId contract."
}

Write-Host "PASS ShelfStorageGameSurfaces"
