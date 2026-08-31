[CmdletBinding()]
param(
    [string]$GameRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnet
$compiler = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot "sdk") -Recurse -Filter "csc.dll" |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $legacyManaged = Join-Path (Split-Path -Parent $repositoryRoot) "Going Medieval_Data\Managed"
    if (-not (Test-Path -LiteralPath $legacyManaged -PathType Container)) {
        throw "Going Medieval managed assemblies were not found. Pass -GameRoot, for example: -GameRoot \"C:\GOG Games\Going Medieval\""
    }

    $managed = $legacyManaged
}
else {
    $gameRootPath = (Resolve-Path -LiteralPath $GameRoot).Path
    $managed = Join-Path $gameRootPath "Going Medieval_Data\Managed"
    if (-not (Test-Path -LiteralPath $managed -PathType Container)) {
        throw "Going Medieval managed directory not found: $managed"
    }
}

$outputDirectory = Join-Path $repositoryRoot "artifacts\tests"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory "CorePolicyTests.exe"
$sources = @(
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\BuildingReplicationV2Policy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\CoordinateResolverPolicy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\CombatPresentationOrderingPolicy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\ReplicationOrderingPolicy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\TransportContracts.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\TransportEnvelopeCodec.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\TransportChunkCodec.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\DirectTransportSecurity.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\DeterminismHash.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\MultiplayerActionRegistry.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\StoragePolicyContracts.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\MedicalReplicationPayloads.cs"),
    (Join-Path $repositoryRoot "tests\BuildingReplicationV2PolicyTests.cs"),
    (Join-Path $repositoryRoot "tests\DirectTransportSecurityTests.cs"),
    (Join-Path $repositoryRoot "tests\CorePolicyTests.cs")
)

& $dotnet $compiler -noconfig -nostdlib+ -target:exe -langversion:10.0 -nullable:enable "-out:$output" `
    "-r:$(Join-Path $managed 'mscorlib.dll')" `
    "-r:$(Join-Path $managed 'System.dll')" `
    "-r:$(Join-Path $managed 'System.Core.dll')" `
    "-r:$(Join-Path $managed 'netstandard.dll')" @sources
if ($LASTEXITCODE -ne 0) { throw "Core policy test compilation failed." }
& $output
if ($LASTEXITCODE -ne 0) { throw "Core policy tests failed." }

$releaseConfig = Join-Path $repositoryRoot "config\replication.cfg"
$settings = @{}
foreach ($line in Get-Content -LiteralPath $releaseConfig) {
    $trimmed = $line.Trim()
    if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#") -or $trimmed.StartsWith(";")) { continue }
    $separator = $trimmed.IndexOf("=")
    if ($separator -le 0) { continue }
    $settings[$trimmed.Substring(0, $separator).Trim().ToLowerInvariant()] = $trimmed.Substring($separator + 1).Trim()
}
if ($settings["enabled"] -ne "false") { throw "Release config must leave replication disabled until the Multiplayer UI starts a session." }
if ($settings["multiplayermenu"] -ne "true") { throw "Release config must enable the Multiplayer UI." }
foreach ($testedGate in @(
    "semanticagentpresentation",
    "semanticanimalpresentationv2",
    "semanticworkcycledriver",
    "hostsleeppresentationv2",
    "disableautomaticsleepspeed",
    "uiv3",
    "buildingreplicationv2",
    "eventreplication",
    "eventtraderauthority",
    "eventrecruitmentauthorityv1",
    "eventrunawayauthorityv1",
    "synchronizedtrading",
    "eventlifecyclereplication",
    "eventdialogreplication",
    "eventchoicecommands",
    "eventspeedreplication",
    "weatherreplication",
    "weathertemperaturereplication",
    "productionstatev2",
    "productionticketordersv2",
    "workstationruntimepresentation",
    "resourcecontainerreplication",
    "storagepolicyv4",
    "shelfstoragemanifestv1")) {
    if ($settings[$testedGate] -ne "true") { throw "Release config must enable $testedGate." }
}
if ($settings["directtransportsecurityv1"] -ne "true") { throw "Release config must enable directTransportSecurityV1." }
foreach ($diagnosticGate in @(
    "logsnapshots",
    "cropfieldpolicydiagnostics",
    "beamreplicationdiagnostics",
    "worldobjectdeltadiagnostics",
    "verbosereplicationlogging",
    "perffpsprobe",
    "pathingperfdiagnostics",
    "validatesnapshots",
    "resultlifecycleprobes",
    "animationdiagnostics",
    "characterstatediagnostics",
    "carrydiagnostics",
    "goapactionprobe",
    "resourcestatev2diagnostics",
    "combatdiagnostics",
    "medicaldiagnostics",
    "eventdiagnostics",
    "tradertransferdiagnostics")) {
    if ($settings[$diagnosticGate] -ne "false") {
        throw "Release config must disable $diagnosticGate."
    }
}
foreach ($disabledGate in @(
    "eventschedulerauthority",
    "eventwarningreplication",
    "eventnoticereplication",
    "eventexternalagentlifecycle",
    "eventenvironmentmutationreplication",
    "playertriggeredeventreplication",
    "weatherschedulerauthority")) {
    if ($settings.ContainsKey($disabledGate) -and $settings[$disabledGate] -ne "false") {
        throw "Release config must leave $disabledGate disabled."
    }
}
if ($settings.ContainsKey("mode") -or $settings.ContainsKey("host")) { throw "Release config must not hard-code a session role or host address." }
Write-Host "PASS TestedConfigPolicy"
