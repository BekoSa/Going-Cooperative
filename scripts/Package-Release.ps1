[CmdletBinding()]
param(
    [string]$Version = "",

    [Parameter(Mandatory)]
    [string]$GameRoot,

    [string]$BepInExRoot,

    [string]$BepInExArchive,

    [string]$UnityDoorstopSourceArchive
)

$ErrorActionPreference = "Stop"
$BepInExVersion = "5.4.23.5"
$BepInExAssetName = "BepInEx_win_x64_$BepInExVersion.zip"
$BepInExAssetUrl =
    "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$BepInExAssetName"
$BepInExAssetSha256 =
    "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
$UnityDoorstopVersion = "4.5.0"
$UnityDoorstopSourceName =
    "UnityDoorstop-v$UnityDoorstopVersion-source.zip"
$UnityDoorstopSourceUrl =
    "https://github.com/NeighTools/UnityDoorstop/archive/refs/tags/v$UnityDoorstopVersion.zip"
$UnityDoorstopSourceSha256 =
    "7f0c963104aa08bf5fefef8ff85e7fecd8306838f5af3101487d9db4e9188d63"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$sourceVersionPath = Join-Path $repositoryRoot `
    "src\GoingCooperative.Core\GoingCooperativeConstants.cs"
$sourceVersionText = Get-Content -LiteralPath $sourceVersionPath -Raw
if ($sourceVersionText -notmatch 'public const string Version = "([^"]+)";') {
    throw "Could not read the Going Cooperative version from $sourceVersionPath"
}
$pluginVersion = $Matches[1]
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $pluginVersion
}
elseif (-not [string]::Equals($Version, $pluginVersion, [StringComparison]::Ordinal)) {
    throw "Package version $Version does not match plugin version $pluginVersion."
}

function Assert-ArtifactChildPath([string]$Path) {
    $artifactPrefix = [IO.Path]::GetFullPath($artifactRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith(
            $artifactPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifact directory: $resolved"
    }
}

function Get-ReleaseConfigMap([string]$Path) {
    $values = @{}
    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith("#")) {
            continue
        }

        $separator = $line.IndexOf("=")
        if ($separator -le 0) {
            throw "Invalid release config line: $rawLine"
        }

        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        $values[$key] = $value
    }
    return $values
}

function Assert-ReleaseConfig([string]$Path) {
    $values = Get-ReleaseConfigMap $Path
    if ($values["directTransportSecurityV1"] -ne "true") {
        throw "Release config must enable directTransportSecurityV1."
    }

    $optionalDiagnosticKeys = @(
        "logSnapshots",
        "cropfieldPolicyDiagnostics",
        "beamReplicationDiagnostics",
        "worldObjectDeltaDiagnostics",
        "verboseReplicationLogging",
        "perfFpsProbe",
        "pathingPerfDiagnostics",
        "validateSnapshots",
        "resultLifecycleProbes",
        "animationDiagnostics",
        "characterStateDiagnostics",
        "carryDiagnostics",
        "goapActionProbe",
        "resourceStateV2Diagnostics",
        "combatDiagnostics",
        "medicalDiagnostics",
        "eventDiagnostics",
        "traderTransferDiagnostics"
    )
    foreach ($key in $optionalDiagnosticKeys) {
        if (-not $values.ContainsKey($key)) {
            throw "Release config is missing diagnostic key $key."
        }
        if ($values[$key] -ne "false") {
            throw "Release config must disable $key."
        }
    }

    $requiredPerfSettings = @{
        "snapshotHz" = "10"
        "worldObjectDeltaApplyBudgetPerFrame" = "8"
        "worldObjectDeltaApplyBudgetMsPerFrame" = "2"
        "runtimeMainThreadBudgetMsPerFrame" = "4"
        "presentationApplyBudgetMsPerFrame" = "1.25"
        "presentationApplyMaxEntitiesPerFrame" = "48"
        "snapshotViewCacheSafetyRefreshSeconds" = "60"
    }
    foreach ($key in $requiredPerfSettings.Keys) {
        if (-not $values.ContainsKey($key)) {
            throw "Release config is missing performance key $key."
        }
        if ($values[$key] -ne $requiredPerfSettings[$key]) {
            throw "Release config performance key $key must be $($requiredPerfSettings[$key]), got $($values[$key])."
        }
    }
}

function Get-VerifiedBepInExArchive {
    if ([string]::IsNullOrWhiteSpace($BepInExArchive)) {
        $dependencyDirectory = Join-Path $artifactRoot "dependencies"
        New-Item -ItemType Directory -Force -Path $dependencyDirectory |
            Out-Null
        $script:BepInExArchive = Join-Path $dependencyDirectory `
            $BepInExAssetName
        if (-not (Test-Path -LiteralPath $script:BepInExArchive)) {
            Write-Host "Downloading official $BepInExAssetName"
            Invoke-WebRequest -UseBasicParsing -Uri $BepInExAssetUrl `
                -OutFile $script:BepInExArchive
        }
    }

    if (-not (Test-Path -LiteralPath $script:BepInExArchive -PathType Leaf)) {
        throw "BepInEx archive not found: $script:BepInExArchive"
    }

    $actualHash = (Get-FileHash -LiteralPath $script:BepInExArchive `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $BepInExAssetSha256) {
        throw "BepInEx archive hash mismatch. Expected $BepInExAssetSha256, got $actualHash."
    }
    return [IO.Path]::GetFullPath($script:BepInExArchive)
}

function Get-VerifiedUnityDoorstopSourceArchive {
    if ([string]::IsNullOrWhiteSpace($UnityDoorstopSourceArchive)) {
        $dependencyDirectory = Join-Path $artifactRoot "dependencies"
        New-Item -ItemType Directory -Force -Path $dependencyDirectory |
            Out-Null
        $script:UnityDoorstopSourceArchive = Join-Path $dependencyDirectory `
            $UnityDoorstopSourceName
        if (-not (Test-Path -LiteralPath `
                $script:UnityDoorstopSourceArchive)) {
            Write-Host "Downloading official $UnityDoorstopSourceName"
            Invoke-WebRequest -UseBasicParsing -Uri $UnityDoorstopSourceUrl `
                -OutFile $script:UnityDoorstopSourceArchive
        }
    }

    if (-not (Test-Path -LiteralPath `
            $script:UnityDoorstopSourceArchive -PathType Leaf)) {
        throw "Unity Doorstop source archive not found: $script:UnityDoorstopSourceArchive"
    }

    $actualHash = (Get-FileHash -LiteralPath `
        $script:UnityDoorstopSourceArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $UnityDoorstopSourceSha256) {
        throw "Unity Doorstop source archive hash mismatch. Expected $UnityDoorstopSourceSha256, got $actualHash."
    }
    return [IO.Path]::GetFullPath($script:UnityDoorstopSourceArchive)
}

$configPath = Join-Path $repositoryRoot "config\replication.cfg"
Assert-ReleaseConfig $configPath

& (Join-Path $PSScriptRoot "Build.ps1") -Configuration Release `
    -GameRoot $GameRoot -BepInExRoot $BepInExRoot
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

$verifiedBepInExArchive = Get-VerifiedBepInExArchive
$verifiedUnityDoorstopSourceArchive =
    Get-VerifiedUnityDoorstopSourceArchive
$packageName = "Going-Cooperative-v$Version-win-x64"
$stage = Join-Path $artifactRoot $packageName
$zip = Join-Path $artifactRoot "$packageName.zip"
$checksum = "$zip.sha256"
foreach ($path in @($stage, $zip, $checksum)) {
    Assert-ArtifactChildPath $path
}
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
if (Test-Path -LiteralPath $checksum) {
    Remove-Item -LiteralPath $checksum -Force
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null
Expand-Archive -LiteralPath $verifiedBepInExArchive -DestinationPath $stage

$requiredBepInExFiles = @(
    ".doorstop_version",
    "doorstop_config.ini",
    "winhttp.dll",
    "BepInEx\core\BepInEx.dll",
    "BepInEx\core\BepInEx.Preloader.dll",
    "BepInEx\core\0Harmony.dll",
    "BepInEx\core\MonoMod.RuntimeDetour.dll",
    "BepInEx\core\Mono.Cecil.dll"
)
foreach ($relativePath in $requiredBepInExFiles) {
    $path = Join-Path $stage $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Official BepInEx archive is missing $relativePath."
    }
}
$bundledBepInExVersion = [Reflection.AssemblyName]::GetAssemblyName(
    (Join-Path $stage "BepInEx\core\BepInEx.dll")).Version.ToString()
if ($bundledBepInExVersion -ne $BepInExVersion) {
    throw "Bundled BepInEx version mismatch: $bundledBepInExVersion"
}

$pluginDirectory = Join-Path $stage "BepInEx\plugins\GoingCooperative"
$configDirectory = Join-Path $stage "GoingCooperative"
$licensesDirectory = Join-Path $stage "Licenses"
$licenseSourceDirectory = Join-Path $licensesDirectory "Source"
New-Item -ItemType Directory -Force -Path `
    $pluginDirectory, $configDirectory, $licensesDirectory, `
    $licenseSourceDirectory | Out-Null

$pluginArtifact = Join-Path $artifactRoot `
    "bin\Release\GoingCooperative.dll"
Copy-Item -LiteralPath $pluginArtifact -Destination $pluginDirectory
Copy-Item -LiteralPath $configPath -Destination $configDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") `
    -Destination $stage
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") `
    -Destination (Join-Path $licensesDirectory `
        "Going-Cooperative-MIT.txt")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") `
    -Destination $stage
Copy-Item -LiteralPath (Join-Path $repositoryRoot `
    "THIRD-PARTY-NOTICES.md") -Destination $stage
Copy-Item -Path (Join-Path $repositoryRoot `
    "third-party\licenses\*") -Destination $licensesDirectory
Copy-Item -LiteralPath $verifiedUnityDoorstopSourceArchive `
    -Destination (Join-Path $licenseSourceDirectory `
        $UnityDoorstopSourceName)

$manifestLines = New-Object Collections.Generic.List[string]
$manifestLines.Add("Going Cooperative $Version Windows x64 release")
$manifestLines.Add("Going Cooperative source: https://github.com/reality-comes/Going-Cooperative")
$manifestLines.Add("BepInEx version: $BepInExVersion Windows x64 (unmodified official distribution)")
$manifestLines.Add("BepInEx asset: $BepInExAssetName")
$manifestLines.Add("BepInEx asset SHA-256: $BepInExAssetSha256")
$manifestLines.Add("BepInEx source: https://github.com/BepInEx/BepInEx/tree/v$BepInExVersion")
$manifestLines.Add("Unity Doorstop source archive: $UnityDoorstopSourceName")
$manifestLines.Add("Unity Doorstop source archive SHA-256: $UnityDoorstopSourceSha256")
$manifestLines.Add("Unity Doorstop source: https://github.com/NeighTools/UnityDoorstop/tree/v$UnityDoorstopVersion")
$manifestLines.Add("")
$manifestLines.Add("Packaged files (SHA-256, bytes, path):")
$files = Get-ChildItem -LiteralPath $stage -Recurse -File |
    Sort-Object FullName
foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($stage.Length + 1).Replace(
        [IO.Path]::DirectorySeparatorChar,
        [char]"/")
    $fileHash = (Get-FileHash -LiteralPath $file.FullName `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestLines.Add(
        "$fileHash  $($file.Length)  $relativePath")
}
$manifestPath = Join-Path $stage "RELEASE-MANIFEST.txt"
[IO.File]::WriteAllLines(
    $manifestPath,
    $manifestLines,
    (New-Object Text.UTF8Encoding($false)))

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipStream = [IO.File]::Open(
    $zip,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None)
$zipArchive = New-Object IO.Compression.ZipArchive(
    $zipStream,
    [IO.Compression.ZipArchiveMode]::Create,
    $false)
try {
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $entryName = $file.FullName.Substring($stage.Length + 1).Replace(
            [IO.Path]::DirectorySeparatorChar,
            [char]"/")
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zipArchive,
            $file.FullName,
            $entryName,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zipArchive.Dispose()
    $zipStream.Dispose()
}

$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    $checksum,
    "$($zipHash.ToLowerInvariant())  $packageName.zip`r`n",
    [Text.Encoding]::ASCII)

$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    foreach ($requiredEntry in @(
        ".doorstop_version",
        "doorstop_config.ini",
        "winhttp.dll",
        "BepInEx/core/BepInEx.dll",
        "BepInEx/plugins/GoingCooperative/GoingCooperative.dll",
        "GoingCooperative/replication.cfg",
        "THIRD-PARTY-NOTICES.md",
        "Licenses/UnityDoorstop-4.5.0-LGPL-2.1.txt",
        "Licenses/Source/UnityDoorstop-v4.5.0-source.zip",
        "RELEASE-MANIFEST.txt")) {
        if ($entryNames -notcontains $requiredEntry) {
            throw "Release ZIP is missing $requiredEntry."
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Packaged $zip"
Write-Host "SHA-256 $($zipHash.ToLowerInvariant())"
Write-Host "Bundled BepInEx $BepInExVersion Windows x64"
