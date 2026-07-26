$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$config = Get-Content (Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs') -Raw
$events = Get-Content (Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationEvents.cs') -Raw
$lane = Get-Content (Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRecruitmentEventV1.cs') -Raw
$runtime = Get-Content (Join-Path $root 'src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs') -Raw
$cfg = Get-Content (Join-Path $root 'config\replication.cfg') -Raw

if ($config -notmatch 'replicationConfigEventRunawayAuthorityV1' -or $config -notmatch 'case "eventrunawayauthorityv1"') { throw 'Runaway V1 config gate is not declared and parsed.' }
if ($cfg -notmatch '(?m)^eventRunawayAuthorityV1=true$') { throw 'Runaway V1 is not enabled in the test config.' }
if ($lane -notmatch '(?s)RunawayEventAuthorityV1Enabled\(\).*?replicationConfigEventReplication.*?replicationConfigEventLifecycleReplication.*?replicationConfigEventRunawayAuthorityV1.*?replicationConfigEventDialogReplication.*?replicationConfigEventChoiceCommands.*?replicationRecruitmentEventHooksReady.*?ValidateReplicationRunawayNativeSurfaces.*?worldObjectDeltaMode.*?commandCaptureMode') { throw 'Runaway V1 does not fail closed on every required dependency.' }
if ($lane -notmatch 'NSMedieval\.GameEventSystem\.Events\.RunawayEvent' -or $lane -notmatch 'className, "RunawayEvent"') { throw 'Runaway V1 does not use exact runtime and blueprint classification.' }
if ($events -notmatch 'ShouldSuppressReplicationClientRunawayEventStart' -or $events -notmatch 'IsReplicationRunawayEventBlueprintId' -or $events -notmatch 'IsReplicationRunawayEventInstance') { throw 'Runaway V1 is not wired into both native start suppression paths.' }
if ($lane -notmatch 'IsReplicationAuthoritativeWorkerOfferEvent' -or $lane -notmatch 'SendHostReplicationRecruitmentWorker') { throw 'Runaway V1 does not reuse the accepted-worker transfer contract.' }
if ($events -notmatch '(?s)CanSendReplicationEventChoice.*?RunawayEventAuthorityV1Enabled.*?IsReplicationRunawayEventBlueprintId') { throw 'Runaway client choices are not enabled through the exact gated family.' }
if ($runtime -notmatch '(?s)replicationConfigEventRunawayAuthorityV1.*?RunawayEventAuthorityV1Enabled\(\).*?:9' -or $runtime -notmatch 'fingerprint\.Length != 25') { throw 'Runaway V1 is not handshake capability-versioned.' }

Write-Host 'Runaway event V1 surface checks passed.'
