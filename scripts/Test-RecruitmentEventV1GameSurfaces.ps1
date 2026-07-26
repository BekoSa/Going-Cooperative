$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$eventPath = Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationEvents.cs'
$lanePath = Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRecruitmentEventV1.cs'
$configPath = Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs'
$runtimePath = Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs'
$transformPath = Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationTransformCollector.cs'
$deltaPath = Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs'
$cfgPath = Join-Path $root 'config\replication.cfg'

$event = Get-Content $eventPath -Raw
$lane = Get-Content $lanePath -Raw
$config = Get-Content $configPath -Raw
$runtime = Get-Content $runtimePath -Raw
$transform = Get-Content $transformPath -Raw
$delta = Get-Content $deltaPath -Raw
$cfg = Get-Content $cfgPath -Raw

if ($config -notmatch 'replicationConfigEventRecruitmentAuthorityV1' -or $config -notmatch 'case "eventrecruitmentauthorityv1"') { throw 'Recruitment V1 config gate is not declared and parsed.' }
if ($cfg -notmatch '(?m)^eventRecruitmentAuthorityV1=true$') { throw 'Recruitment V1 is not enabled in the test config.' }
if ($lane -notmatch '(?s)RecruitmentEventAuthorityV1Enabled\(\).*?replicationConfigEventReplication.*?replicationConfigEventLifecycleReplication.*?replicationConfigEventRecruitmentAuthorityV1.*?replicationConfigEventDialogReplication.*?replicationConfigEventChoiceCommands.*?replicationRecruitmentEventHooksReady.*?worldObjectDeltaMode.*?commandCaptureMode') { throw 'Recruitment V1 does not fail closed on every required dependency.' }
if ($lane -notmatch 'NSMedieval\.GameEventSystem\.Events\.NewWorkerEvent' -or $lane -notmatch 'ClassName.*NewWorkerEvent') { throw 'Recruitment V1 is not exact-type and exact-blueprint allowlisted.' }
if ($event -notmatch 'ShouldSuppressReplicationClientRecruitmentEventStart' -or $event -notmatch 'QuarantineReplicationClientNativeRecruitmentEvents') { throw 'Recruitment V1 does not suppress both new and already-running client-native recruitment events.' }
if ($lane -notmatch 'AddWorkerPhase' -or $lane -notmatch 'ReplicationRecruitmentAddWorkerPostfix') { throw 'Recruitment V1 does not capture the native accepted-worker boundary.' }
foreach ($kind in @('RecruitmentWorkerBegin', 'RecruitmentWorkerChunk', 'RecruitmentWorkerAdopt')) { if ($lane -notmatch $kind) { throw "Recruitment transfer kind missing: $kind" } }
if ($lane -notmatch 'FVSerializer' -or $lane -notmatch 'FVDeserializer' -or $lane -notmatch 'gameAssemblyMvid' -or $lane -notmatch 'SHA256') { throw 'Recruitment transfer lacks native serialization, assembly compatibility, or integrity validation.' }
if ($lane -notmatch 'WorkerController\.Instance\.CreateWorker' -or $lane -notmatch 'AllWorkers\.ContainsKey') { throw 'Recruitment transfer does not use native worker adoption with postconditions.' }
if ($transform -notmatch 'TryGetReplicationRecruitmentWorkerNetworkId' -or $delta -notmatch 'TryGetReplicationRecruitmentWorker') { throw 'Recruited worker does not have a shared event-scoped replication identity and resolver.' }
if ($runtime -notmatch '(?s)replicationConfigEventRecruitmentAuthorityV1.*?RecruitmentEventAuthorityV1Enabled\(\).*?:9' -or $runtime -notmatch 'fingerprint\.Length != 25') { throw 'Recruitment V1 is not handshake capability-versioned.' }
if ($event -notmatch '(?s)!FullEventGraphAuthorityEnabled\(\).*?RecruitmentEventAuthorityV1Enabled\(\).*?Family, "Recruitment"') { throw 'Recruitment UI projection is not isolated from other event families.' }

Write-Host 'PASS RecruitmentEventV1GameSurfaces'
