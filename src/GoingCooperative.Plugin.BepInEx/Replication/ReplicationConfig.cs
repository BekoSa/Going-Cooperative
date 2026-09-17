using System;
using System.Globalization;
using System.IO;
using BepInEx;
using GoingCooperative.Core;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationConfigRelativePath = @"GoingCooperative\replication.cfg";
        private const string DiagnosticsConfigRelativePath = @"GoingCooperative\diagnostics.cfg";
        private const string ReplicationHostPeerId = "host";
        private const string ReplicationClientPeerId = "client";
        private static string replicationAssignedClientPeerId = ReplicationClientPeerId;

        private static string GetReplicationLocalPeerId()
        {
            if (replicationConfigHostMode)
            {
                return ReplicationHostPeerId;
            }

            return MultiplayerPeerIds.TryParseClientSlot(
                    replicationAssignedClientPeerId,
                    out _)
                ? replicationAssignedClientPeerId
                : ReplicationClientPeerId;
        }

        private static void SetReplicationLocalPeerId(string peerId)
        {
            replicationAssignedClientPeerId =
                MultiplayerPeerIds.TryParseClientSlot(peerId, out _)
                    ? peerId
                    : ReplicationClientPeerId;
        }

        private static bool replicationConfigLoadAttempted;
        private static bool replicationConfigEnabled;
        private static bool replicationConfigHostMode = true;
        private static bool replicationConfigApplySnapshots;
        private static bool replicationConfigSuppressClientSimulation = true;
        private static bool replicationConfigBasicDeterminismAssist;
        private static bool replicationConfigBasicDeterminismAssistCreatureManager;
        private static bool replicationConfigBasicDeterminismAssistPathing;
        private static bool replicationConfigBasicDeterminismAssistCancelPathMovement;
        private static bool replicationConfigHostDrivenGoapPlayback;
        private static bool replicationConfigHostDrivenGoapPlaybackTick;
        private static bool replicationConfigPuppetVisualTick;
        private static bool replicationConfigActionPhaseReplication;
        private static bool replicationConfigActionPhaseVisualReplay;
        private static bool replicationConfigActionParameterReplication;
        private static bool replicationConfigNativeAnimationControllerReplay;
        private static bool replicationConfigActionAnimatorStateSampling;
        private static bool replicationConfigAnimateReplicatedMovement = true;
        private static bool replicationConfigSmoothReplicatedMovement = true;
        private static bool replicationConfigSemanticAgentPresentation;
        // Client-only rollback gate for the debounced animal locomotion/facing
        // presentation state machine. It does not change transform authority or wire data.
        private static bool replicationConfigSemanticAnimalPresentationV2;
        // Narrow rollback gate for the Chop/Dig/Harvest client animation-cycle
        // controller. False preserves the previous semantic work presentation.
        private static bool replicationConfigSemanticWorkCycleDriver;
        private static bool replicationConfigNeedsReplication = true;
        // Client-only rollback gate for host-authored sleep presentation through
        // CreatureBase.IsSleeping instead of replaying LifeController global events.
        private static bool replicationConfigHostSleepPresentationV2;
        // Local gameplay preference applied identically on both peers. Suppresses
        // only the game's all-workers-sleeping speed override and its wake restore.
        private static bool replicationConfigDisableAutomaticSleepSpeed;
        // Master rollback gate for the event-driven production registry and
        // scheduler. Sub-gates below remain independently reversible.
        private static bool replicationConfigProductionStateV2;
        private static bool replicationConfigProductionTicketOrdersV2;
        // Optional, presentation-only workstation runtime lane. It never owns
        // queue policy, resources, ticket completion, or simulation.
        private static bool replicationConfigWorkstationRuntimePresentation;
        private static bool replicationConfigForceHostMovement;
        private static bool replicationConfigSendSnapshots = true;
        private static bool replicationConfigLogSnapshots = true;
        // Full world-delta payload logging is intentionally opt-in. Building and roof
        // records can be very large and are replicated in bursts during construction.
        private static bool replicationConfigWorldObjectDeltaDiagnostics;
        // High-volume operational transport traces (send, queue, apply, ACK, retry).
        // This does not affect replication, warnings/errors, or the FPS summary.
        private static bool replicationConfigVerboseReplicationLogging;
        // Transactional placement plus sparse lifecycle replication. False restores
        // the legacy periodic full-building snapshot lane for rollback testing.
        private static bool replicationConfigBuildingReplicationV2 = true;
        // Authoritative BaseBuildingInstance.Storage replication for unfinished
        // construction. This remains independent from workstation containers.
        private static bool replicationConfigBuildingConstructionMaterialsV2;
        // Exact native semantic placement lanes. Each remains independently
        // reversible; disabled categories continue through the fail-closed rollback.
        private static bool replicationConfigBeamPlacementReplication;
        private static bool replicationConfigBeamLifecycleReplication;
        private static bool replicationConfigSocketablePlacementReplication;
        private static bool replicationConfigCropfieldSpatialReplicationV1;
        // Host-authoritative cropfield menu policies: crop type, sowing, priority,
        // sow range, harvest/cut phases, and pasted policy state.
        private static bool replicationConfigCropfieldPolicyV1;
        // Focused cropfield setter/capture diagnostics. Functional replication and
        // reconcile remain active when this diagnostic-only gate is disabled.
        private static bool replicationConfigCropfieldPolicyDiagnostics;
        private static bool replicationConfigPlantLifecycleReplication;
        // Exact selected-worker to exact-world-target right-click work requests.
        // False preserves the legacy coordinate-only RegionOrder behavior.
        private static bool replicationConfigPrioritisedObjectWorkV1;
        // Focused beam capture/replay tracing. This is intentionally independent
        // from the high-volume world-delta and transport diagnostic gates.
        private static bool replicationConfigBeamReplicationDiagnostics;
        private static bool replicationConfigValidateSnapshots = true;
        private static bool replicationConfigSendProofIntent;
        private static bool replicationConfigResultLifecycleProbes = true;
        private static bool replicationConfigAnimationDiagnostics;
        private static bool replicationConfigCharacterStateDiagnostics;
        private static bool replicationConfigCarryDiagnostics;
        private static bool replicationConfigGoapActionProbe;
        private static bool replicationConfigMultiplayerMenuEnabled = true;
        // Multiplayer menu implementation. True renders the redesigned v2 menu;
        // false restores the original Canvas menu unchanged (rollback path).
        private static bool replicationConfigUiV2 = true;
        // Gated (v3) single-flow multiplayer menu: HOST/JOIN choice first, then only
        // that path's options, then the session screen; no tabs and no settings page.
        // uiV3=false rolls back to the v2 tab menu unchanged (ui=classic still
        // restores the original menu below that).
        private static bool replicationConfigUiV3 = true;
        // Steam networking master gate. Off by default; when on, the multiplayer
        // menu offers a Steam connection mode (lobby, invites, relay tunnel).
        private static bool replicationConfigSteamNetworking;
        // Mutual authentication, endpoint pinning, replay protection, and authenticated
        // save/control records for Direct (IP) sessions. Steam keeps its own trust lane.
        private static bool replicationConfigDirectTransportSecurityV1;
        private static bool replicationConfigPerfFpsProbe = true;
        // Aggregate-only attribution for host FPS under movement/path churn.
        // Disabled by default; it does no scene discovery or per-entity logging.
        private static bool replicationConfigPathingPerfDiagnostics;
        private static bool replicationConfigResourcePileStateSnapshots;
        private static bool replicationConfigResourceContainerReplication;
        // Event-dirty replacement for the legacy 0.5 second resource-container
        // rebuild. The master and each authority domain are independently
        // rollback-gated; all default off until two-machine certification.
        private static bool replicationConfigResourceStateV2;
        private static bool replicationConfigAgentInventoryStateV2;
        private static bool replicationConfigGroundPileStateV2;
        private static bool replicationConfigShelfStorageStateV2;
        private static bool replicationConfigResourceStateV2Diagnostics;
        // Complete ground-stockpile and shelf policy authority (filters, ranges,
        // priority, name, and production eligibility).
        private static bool replicationConfigStoragePolicyV4;
        // Canonical per-slot shelf contents/presentation manifest.
        private static bool replicationConfigShelfStorageManifestV1;
        // Combat is intentionally split into independently reversible layers. The
        // master gate must be on before any future authoritative combat mutation
        // path is allowed to run; diagnostics remain separately controllable.
        private static bool replicationConfigCombatReplication;
        private static bool replicationConfigCombatDraftCommands;
        private static bool replicationConfigCombatAttackCommands;
        private static bool replicationConfigCombatStateReplication;
        private static bool replicationConfigCombatHealthReplication;
        private static bool replicationConfigCombatHealthDetailReplication;
        private static bool replicationConfigCombatDeathReplication;
        private static bool replicationConfigCombatPresentationReplication;
        private static bool replicationConfigCombatProjectileReplication;
        private static bool replicationConfigCombatExternalAgentLifecycle;
        private static bool replicationConfigCombatDiagnostics;
        // Medical V1 owns live wound effectors and medical intents. Combat keeps
        // sole ownership of health/blood/pain/consciousness scalar outcomes.
        private static bool replicationConfigMedicalReplicationV1;
        private static bool replicationConfigMedicalWoundStateV1;
        private static bool replicationConfigMedicalTreatmentOrdersV1;
        private static bool replicationConfigMedicalTreatmentPresentationV1;
        private static bool replicationConfigMedicalPanelRefreshV1;
        private static bool replicationConfigMedicalClientWoundTickSuppressionV1;
        private static bool replicationConfigMedicalDiagnostics;
        // Scripted events and ambient weather are split into independently reversible
        // layers. Trader starts have a narrow authority gate; replacing the complete
        // scheduler or loaded/running event graph remains separately fail-closed.
        private static bool replicationConfigEventReplication;
        private static bool replicationConfigEventSchedulerAuthority;
        private static bool replicationConfigEventTraderAuthority;
        // Exact NewWorkerEvent authority pilot. This does not enable scheduler-wide
        // authority or any other visitor, raid, weather, or environment event family.
        private static bool replicationConfigEventRecruitmentAuthorityV1;
        // Exact RunawayEvent opening/recruitment authority. Kept independent from
        // NewWorkerEvent because RunawayEvent is a derived threat graph.
        private static bool replicationConfigEventRunawayAuthorityV1;
        private static bool replicationConfigSynchronizedTrading;
        private static bool replicationConfigEventLifecycleReplication;
        private static bool replicationConfigEventDialogReplication;
        private static bool replicationConfigEventChoiceCommands;
        private static bool replicationConfigEventSpeedReplication;
        private static bool replicationConfigEventWarningReplication;
        private static bool replicationConfigEventNoticeReplication;
        private static bool replicationConfigEventExternalAgentLifecycle;
        private static bool replicationConfigEventEnvironmentMutationReplication;
        private static bool replicationConfigPlayerTriggeredEventReplication;
        private static bool replicationConfigWeatherReplication;
        private static bool replicationConfigWeatherSchedulerAuthority;
        private static bool replicationConfigWeatherTemperatureReplication;
        private static bool replicationConfigEventDiagnostics;
        // Focused bootstrap/ACK tracing for merchant party transfers. This is
        // intentionally independent of the high-volume transport log gate.
        private static bool replicationConfigTraderTransferDiagnostics;
        private static string replicationConfigHost = "127.0.0.1";
        private static string replicationConfigProofIntent = "speed-normal";
        private static string replicationConfigCommandCaptureMode = "send";
        private static string replicationConfigRegionCommandMode = "shadow";
        private static string replicationConfigGoapActionProbeEntityId = string.Empty;
        private static string replicationConfigWorldObjectDeltaMode = "shadow";
        private static UnityEngine.KeyCode replicationConfigPingKey = UnityEngine.KeyCode.F9;
        private static int replicationConfigPort = 47692;
        private static int replicationConfigSnapshotHz = 10;
        private static int replicationConfigFullResyncSeconds = 45;
        private static int replicationConfigInterpolationMs = 150;
        private static int replicationConfigMaxSnapshotEntities = 128;
        private static int replicationConfigSnapshotValidationSeconds = 10;
        private static int replicationConfigProofIntentDelaySeconds = 5;
        private static int replicationConfigWorldObjectDeltaApplyBudgetPerFrame = 8;
        private static int replicationConfigWorldObjectDeltaApplyQueueMax = 2048;
        private static float replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame = 2f;
        private static float replicationConfigRuntimeMainThreadBudgetMsPerFrame = 4f;
        private static float replicationConfigPresentationApplyBudgetMsPerFrame = 1.25f;
        private static int replicationConfigPresentationApplyMaxEntitiesPerFrame = 48;
        private static float replicationConfigSnapshotViewCacheSafetyRefreshSeconds = 0f;

        private static void TryLoadReplicationConfig(GoingCooperativePlugin current)
        {
            if (replicationConfigLoadAttempted)
            {
                return;
            }

            replicationConfigLoadAttempted = true;
            var configPath = Path.Combine(Paths.GameRootPath, ReplicationConfigRelativePath);
            if (!File.Exists(configPath))
            {
                current.Logger.LogInfo("Going Cooperative replication config missing; replication disabled path=" + configPath);
                return;
            }

            try
            {
                var lines = File.ReadAllLines(configPath);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        current.Logger.LogWarning("Going Cooperative replication config ignored malformed line=" + (i + 1).ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    var value = line.Substring(separator + 1).Trim();
                    ApplyReplicationConfigValue(current, key, value, i + 1);
                }

                ApplyReplicationPerformanceSafetyLimits(current);

                current.Logger.LogInfo("Going Cooperative replication config loaded enabled="
                    + replicationConfigEnabled
                    + " mode="
                    + (replicationConfigHostMode ? "host" : "client")
                    + " host="
                    + replicationConfigHost
                    + " port="
                    + replicationConfigPort.ToString(CultureInfo.InvariantCulture)
                    + " snapshotHz="
                    + replicationConfigSnapshotHz.ToString(CultureInfo.InvariantCulture)
                    + " applySnapshots="
                    + replicationConfigApplySnapshots
                    + " suppressClientSimulation="
                    + replicationConfigSuppressClientSimulation
                    + " basicDeterminismAssist="
                    + replicationConfigBasicDeterminismAssist
                    + " basicDeterminismAssistCreatureManager="
                    + replicationConfigBasicDeterminismAssistCreatureManager
                    + " basicDeterminismAssistPathing="
                    + replicationConfigBasicDeterminismAssistPathing
                    + " basicDeterminismAssistCancelPathMovement="
                    + replicationConfigBasicDeterminismAssistCancelPathMovement
                    + " hostDrivenGoapPlayback="
                    + replicationConfigHostDrivenGoapPlayback
                    + " hostDrivenGoapPlaybackTick="
                    + replicationConfigHostDrivenGoapPlaybackTick
                    + " puppetVisualTick="
                    + replicationConfigPuppetVisualTick
                    + " actionPhaseReplication="
                    + replicationConfigActionPhaseReplication
                    + " actionPhaseVisualReplay="
                    + replicationConfigActionPhaseVisualReplay
                    + " actionParameterReplication="
                    + replicationConfigActionParameterReplication
                    + " nativeAnimationControllerReplay="
                    + replicationConfigNativeAnimationControllerReplay
                    + " actionAnimatorStateSampling="
                    + replicationConfigActionAnimatorStateSampling
                    + " animateReplicatedMovement="
                    + replicationConfigAnimateReplicatedMovement
                    + " smoothReplicatedMovement="
                    + replicationConfigSmoothReplicatedMovement
                    + " semanticAgentPresentation="
                    + replicationConfigSemanticAgentPresentation
                    + " semanticAnimalPresentationV2="
                    + replicationConfigSemanticAnimalPresentationV2
                    + " semanticWorkCycleDriver="
                    + replicationConfigSemanticWorkCycleDriver
                    + " needsReplication="
                    + replicationConfigNeedsReplication
                    + " hostSleepPresentationV2="
                    + replicationConfigHostSleepPresentationV2
                    + " disableAutomaticSleepSpeed="
                    + replicationConfigDisableAutomaticSleepSpeed
                    + " productionStateV2="
                    + replicationConfigProductionStateV2
                    + " productionTicketOrdersV2="
                    + replicationConfigProductionTicketOrdersV2
                    + " workstationRuntimePresentation="
                    + replicationConfigWorkstationRuntimePresentation
                    + " interpolationMs="
                    + replicationConfigInterpolationMs.ToString(CultureInfo.InvariantCulture)
                    + " forceHostMovement="
                    + replicationConfigForceHostMovement
                    + " validateSnapshots="
                    + replicationConfigValidateSnapshots
                    + " sendProofIntent="
                    + replicationConfigSendProofIntent
                    + " proofIntent="
                    + replicationConfigProofIntent
                    + " commandCaptureMode="
                    + replicationConfigCommandCaptureMode
                    + " regionCommandMode="
                    + replicationConfigRegionCommandMode
                    + " worldObjectDeltaMode="
                    + replicationConfigWorldObjectDeltaMode
                    + " resultLifecycleProbes="
                    + replicationConfigResultLifecycleProbes
                    + " animationDiagnostics="
                    + replicationConfigAnimationDiagnostics
                    + " characterStateDiagnostics="
                    + replicationConfigCharacterStateDiagnostics
                    + " carryDiagnostics="
                    + replicationConfigCarryDiagnostics
                    + " goapActionProbe="
                    + replicationConfigGoapActionProbe
                    + " goapActionProbeEntityId="
                    + replicationConfigGoapActionProbeEntityId
                    + " multiplayerMenu="
                    + replicationConfigMultiplayerMenuEnabled
                    + " perfFpsProbe="
                    + replicationConfigPerfFpsProbe
                    + " pathingPerfDiagnostics="
                    + replicationConfigPathingPerfDiagnostics
                    + " worldObjectDeltaDiagnostics="
                    + replicationConfigWorldObjectDeltaDiagnostics
                    + " verboseReplicationLogging="
                    + replicationConfigVerboseReplicationLogging
                    + " buildingReplicationV2="
                    + replicationConfigBuildingReplicationV2
                    + " buildingConstructionMaterialsV2="
                    + replicationConfigBuildingConstructionMaterialsV2
                    + " beamPlacementReplication="
                    + replicationConfigBeamPlacementReplication
                    + " beamLifecycleReplication="
                    + replicationConfigBeamLifecycleReplication
                    + " socketablePlacementReplication="
                    + replicationConfigSocketablePlacementReplication
                    + " cropfieldSpatialReplicationV1="
                    + replicationConfigCropfieldSpatialReplicationV1
                    + " cropfieldPolicyV1="
                    + replicationConfigCropfieldPolicyV1
                    + " cropfieldPolicyDiagnostics="
                    + replicationConfigCropfieldPolicyDiagnostics
                    + " plantLifecycleReplication="
                    + replicationConfigPlantLifecycleReplication
                    + " prioritisedObjectWorkV1="
                    + replicationConfigPrioritisedObjectWorkV1
                    + " beamReplicationDiagnostics="
                    + replicationConfigBeamReplicationDiagnostics
                    + " resourcePileStateSnapshots="
                    + replicationConfigResourcePileStateSnapshots
                    + " resourceContainerReplication="
                    + replicationConfigResourceContainerReplication
                    + " resourceStateV2="
                    + replicationConfigResourceStateV2
                    + " agentInventoryStateV2="
                    + replicationConfigAgentInventoryStateV2
                    + " groundPileStateV2="
                    + replicationConfigGroundPileStateV2
                    + " shelfStorageStateV2="
                    + replicationConfigShelfStorageStateV2
                    + " resourceStateV2Diagnostics="
                    + replicationConfigResourceStateV2Diagnostics
                    + " storagePolicyV4="
                    + replicationConfigStoragePolicyV4
                    + " shelfStorageManifestV1="
                    + replicationConfigShelfStorageManifestV1
                    + " combatReplication="
                    + replicationConfigCombatReplication
                    + " combatDraftCommands="
                    + replicationConfigCombatDraftCommands
                    + " combatAttackCommands="
                    + replicationConfigCombatAttackCommands
                    + " combatStateReplication="
                    + replicationConfigCombatStateReplication
                    + " combatHealthReplication="
                    + replicationConfigCombatHealthReplication
                    + " combatHealthDetailReplication="
                    + replicationConfigCombatHealthDetailReplication
                    + " combatDeathReplication="
                    + replicationConfigCombatDeathReplication
                    + " combatPresentationReplication="
                    + replicationConfigCombatPresentationReplication
                    + " combatProjectileReplication="
                    + replicationConfigCombatProjectileReplication
                    + " combatExternalAgentLifecycle="
                    + replicationConfigCombatExternalAgentLifecycle
                    + " combatDiagnostics="
                    + replicationConfigCombatDiagnostics
                    + " medicalReplicationV1="
                    + replicationConfigMedicalReplicationV1
                    + " medicalWoundStateV1="
                    + replicationConfigMedicalWoundStateV1
                    + " medicalTreatmentOrdersV1="
                    + replicationConfigMedicalTreatmentOrdersV1
                    + " medicalTreatmentPresentationV1="
                    + replicationConfigMedicalTreatmentPresentationV1
                    + " medicalPanelRefreshV1="
                    + replicationConfigMedicalPanelRefreshV1
                    + " medicalClientWoundTickSuppressionV1="
                    + replicationConfigMedicalClientWoundTickSuppressionV1
                    + " medicalDiagnostics="
                    + replicationConfigMedicalDiagnostics
                    + " eventReplication="
                    + replicationConfigEventReplication
                    + " eventSchedulerAuthority="
                    + replicationConfigEventSchedulerAuthority
                    + " eventTraderAuthority="
                    + replicationConfigEventTraderAuthority
                    + " eventRecruitmentAuthorityV1="
                    + replicationConfigEventRecruitmentAuthorityV1
                    + " eventRunawayAuthorityV1="
                    + replicationConfigEventRunawayAuthorityV1
                    + " synchronizedTrading="
                    + replicationConfigSynchronizedTrading
                    + " eventLifecycleReplication="
                    + replicationConfigEventLifecycleReplication
                    + " eventDialogReplication="
                    + replicationConfigEventDialogReplication
                    + " eventChoiceCommands="
                    + replicationConfigEventChoiceCommands
                    + " eventSpeedReplication="
                    + replicationConfigEventSpeedReplication
                    + " eventWarningReplication="
                    + replicationConfigEventWarningReplication
                    + " eventNoticeReplication="
                    + replicationConfigEventNoticeReplication
                    + " eventExternalAgentLifecycle="
                    + replicationConfigEventExternalAgentLifecycle
                    + " eventEnvironmentMutationReplication="
                    + replicationConfigEventEnvironmentMutationReplication
                    + " playerTriggeredEventReplication="
                    + replicationConfigPlayerTriggeredEventReplication
                    + " weatherReplication="
                    + replicationConfigWeatherReplication
                    + " weatherSchedulerAuthority="
                    + replicationConfigWeatherSchedulerAuthority
                    + " weatherTemperatureReplication="
                    + replicationConfigWeatherTemperatureReplication
                    + " eventDiagnostics="
                    + replicationConfigEventDiagnostics
                    + " traderTransferDiagnostics="
                    + replicationConfigTraderTransferDiagnostics
                    + " worldObjectDeltaApplyBudgetPerFrame="
                    + replicationConfigWorldObjectDeltaApplyBudgetPerFrame.ToString(CultureInfo.InvariantCulture)
                    + " worldObjectDeltaApplyQueueMax="
                    + replicationConfigWorldObjectDeltaApplyQueueMax.ToString(CultureInfo.InvariantCulture)
                    + " worldObjectDeltaApplyBudgetMsPerFrame="
                    + replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                    + " runtimeMainThreadBudgetMsPerFrame="
                    + replicationConfigRuntimeMainThreadBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                    + " presentationApplyBudgetMsPerFrame="
                    + replicationConfigPresentationApplyBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                    + " presentationApplyMaxEntitiesPerFrame="
                    + replicationConfigPresentationApplyMaxEntitiesPerFrame.ToString(CultureInfo.InvariantCulture)
                    + " snapshotViewCacheSafetyRefreshSeconds="
                    + replicationConfigSnapshotViewCacheSafetyRefreshSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                    + " maxSnapshotEntities="
                    + replicationConfigMaxSnapshotEntities.ToString(CultureInfo.InvariantCulture));
                LogReplicationInventoryAuthority(current);
                LogReplicationMedicalAuthority(current);

                current.Logger.LogInfo("Going Cooperative replication config ui="
                    + (replicationConfigUiV3 ? "v3" : replicationConfigUiV2 ? "v2" : "classic")
                    + " steamNetworking="
                    + replicationConfigSteamNetworking
                    + " directTransportSecurityV1="
                    + replicationConfigDirectTransportSecurityV1);

                if (replicationConfigEventReplication
                    && replicationConfigEventSchedulerAuthority
                    && (!replicationConfigEventLifecycleReplication
                        || !string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase)))
                {
                    current.Logger.LogWarning("Going Cooperative event scheduler authority is dependency-gated off: eventLifecycleReplication=true and worldObjectDeltaMode=apply are required.");
                }

                if (replicationConfigEventReplication
                    && replicationConfigEventTraderAuthority
                    && (!replicationConfigEventLifecycleReplication
                        || !string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase)))
                {
                    current.Logger.LogWarning("Going Cooperative trader event authority is dependency-gated off: eventLifecycleReplication=true and worldObjectDeltaMode=apply are required. Native trader events remain enabled.");
                }

                if (replicationConfigEventRecruitmentAuthorityV1
                    && !RecruitmentEventAuthorityV1Enabled())
                {
                    current.Logger.LogWarning("Going Cooperative recruitment event authority V1 is configured but failed closed; native recruitment events remain enabled on the client.");
                }

                if (replicationConfigEventRunawayAuthorityV1
                    && !RunawayEventAuthorityV1Enabled())
                {
                    current.Logger.LogWarning("Going Cooperative runaway event authority V1 is configured but failed closed; native runaway events remain enabled on the client.");
                }

                if (replicationConfigEventReplication
                    && replicationConfigEventTraderAuthority
                    && !ReplicationTraderPartySurfacesReady())
                {
                    current.Logger.LogWarning("Going Cooperative trader event authority remains source/surface-gated off because the exact trader-party snapshot, identity, spawn, removal, or trade-UI contract is incomplete. Native trader events remain enabled.");
                }

                if (replicationConfigEventReplication
                    && replicationConfigEventChoiceCommands
                    && !IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode))
                {
                    current.Logger.LogWarning("Going Cooperative event choice commands are dependency-gated off: commandCaptureMode=send or authoritative is required.");
                }

                if (replicationConfigEventReplication
                    && replicationConfigEventSchedulerAuthority
                    && (!replicationConfigEventDialogReplication
                        || !replicationConfigEventChoiceCommands
                        || !replicationConfigEventSpeedReplication
                        || !replicationConfigEventWarningReplication
                        || !replicationConfigEventNoticeReplication
                        || !replicationConfigMultiplayerMenuEnabled
                        || !replicationConfigEventExternalAgentLifecycle
                        || !replicationConfigCombatExternalAgentLifecycle
                        || !replicationConfigEventEnvironmentMutationReplication))
                {
                    current.Logger.LogWarning("Going Cooperative full scripted-event graph authority is dependency-gated off: dialog, choice, speed, warning, notice, multiplayer UI, external-agent lifecycle, combat external-agent lifecycle, and environment-mutation lanes are required before the client scheduler or loaded/running event graphs can be parked. Trader authority is controlled independently by eventTraderAuthority.");
                }

                if (replicationConfigEventReplication && replicationConfigEventSchedulerAuthority
                    && (!ReplicationEventExternalAgentLifecycleImplemented()
                        || !ReplicationCombatExternalAgentLifecycleImplemented()
                        || !ReplicationEventEnvironmentMutationImplemented()
                        || !ReplicationEventWarningReplicationImplemented()
                        || !ReplicationEventNoticeReplicationImplemented()))
                {
                    current.Logger.LogWarning("Going Cooperative full scripted-event graph authority remains source-gated off: external roster, environment mutation, warning, or notice implementations are not present in this build. Trader authority is controlled independently by eventTraderAuthority.");
                }

                if (!replicationConfigMedicalReplicationV1
                    && (replicationConfigMedicalWoundStateV1
                        || replicationConfigMedicalTreatmentOrdersV1
                        || replicationConfigMedicalTreatmentPresentationV1
                        || replicationConfigMedicalPanelRefreshV1
                        || replicationConfigMedicalClientWoundTickSuppressionV1))
                {
                    current.Logger.LogWarning("Going Cooperative medical sub-gates are inert because medicalReplicationV1=false.");
                }

                if (replicationConfigMedicalReplicationV1
                    && replicationConfigMedicalWoundStateV1
                    && !string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase))
                {
                    current.Logger.LogWarning("Going Cooperative medical wound state requires worldObjectDeltaMode=apply on both peers; current mode will not provide live client reconciliation.");
                }

                if (replicationConfigWeatherReplication
                    && replicationConfigWeatherSchedulerAuthority
                    && (!replicationConfigEventEnvironmentMutationReplication
                        || !ReplicationEventEnvironmentMutationImplemented()))
                {
                    current.Logger.LogWarning("Going Cooperative weather scheduler authority is dependency-gated off: an implemented environment-mutation lane is required before native weather effectors can be suppressed safely. Config booleans cannot bypass this safety gate.");
                }

                TryLoadDiagnosticsConfig(current);
            }
            catch (Exception ex)
            {
                current.Logger.LogWarning("Going Cooperative replication config load failed path="
                    + configPath
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
                replicationConfigEnabled = false;
            }
        }

        private static void LogReplicationInventoryAuthority(GoingCooperativePlugin current)
        {
            var carrySnapshotAvailable = !string.Equals(
                replicationConfigWorldObjectDeltaMode,
                "off",
                StringComparison.OrdinalIgnoreCase);
            var agentInventoryOwner = replicationConfigResourceContainerReplication
                ? "resource-containers"
                : carrySnapshotAvailable ? "carry-snapshot" : "none";
            var productionContainerOwner = replicationConfigResourceContainerReplication
                ? (replicationConfigProductionStateV2 ? "production-v2" : "legacy-resource-containers")
                : "none";
            current.Logger.LogInfo("Going Cooperative replication authority agent-inventory="
                + agentInventoryOwner
                + " production-containers="
                + productionContainerOwner);

            if (string.Equals(agentInventoryOwner, "none", StringComparison.Ordinal))
            {
                current.Logger.LogWarning(
                    "Going Cooperative replication authority invalid agent-inventory=none; pawn inventory will not converge.");
            }
        }

        private static void LogReplicationMedicalAuthority(GoingCooperativePlugin current)
        {
            var woundOwner = replicationConfigMedicalReplicationV1 && replicationConfigMedicalWoundStateV1
                ? "medical-v1"
                : "local-simulation";
            var scalarOwner = replicationConfigCombatReplication && replicationConfigCombatHealthReplication
                ? "combat-health"
                : "local-simulation";
            var orderOwner = replicationConfigMedicalReplicationV1 && replicationConfigMedicalTreatmentOrdersV1
                ? "medical-v1"
                : "local-only";
            current.Logger.LogInfo("Going Cooperative replication authority wounds=" + woundOwner
                + " health-scalars=" + scalarOwner
                + " treatment-orders=" + orderOwner);
            if (replicationConfigMedicalClientWoundTickSuppressionV1
                && (!replicationConfigMedicalReplicationV1 || !replicationConfigMedicalWoundStateV1))
            {
                current.Logger.LogWarning("Going Cooperative medical client wound tick suppression is dependency-gated off: medicalReplicationV1=true and medicalWoundStateV1=true are required.");
            }
        }

        private static void ApplyReplicationPerformanceSafetyLimits(GoingCooperativePlugin current)
        {
            var originalSnapshotHz = replicationConfigSnapshotHz;
            var originalWorldBudgetPerFrame = replicationConfigWorldObjectDeltaApplyBudgetPerFrame;
            var originalWorldBudgetMs = replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame;
            var originalRuntimeBudgetMs = replicationConfigRuntimeMainThreadBudgetMsPerFrame;
            var originalPresentationBudgetMs = replicationConfigPresentationApplyBudgetMsPerFrame;
            var originalPresentationMaxEntities = replicationConfigPresentationApplyMaxEntitiesPerFrame;

            // Treat zero/negative time budgets as "unsafe unlimited", not as an
            // escape hatch. These limits protect old hand-edited configs too, so
            // installing a new DLL is enough to get the current frame safeguards.
            replicationConfigSnapshotHz = Math.Max(1, Math.Min(replicationConfigSnapshotHz, 10));
            replicationConfigWorldObjectDeltaApplyBudgetPerFrame =
                replicationConfigWorldObjectDeltaApplyBudgetPerFrame <= 0
                    ? 8
                    : Math.Min(replicationConfigWorldObjectDeltaApplyBudgetPerFrame, 8);
            replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame =
                replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame <= 0f
                    ? 2f
                    : Math.Min(replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame, 2f);
            replicationConfigRuntimeMainThreadBudgetMsPerFrame =
                replicationConfigRuntimeMainThreadBudgetMsPerFrame <= 0f
                    ? 4f
                    : Math.Min(replicationConfigRuntimeMainThreadBudgetMsPerFrame, 4f);
            replicationConfigPresentationApplyBudgetMsPerFrame =
                replicationConfigPresentationApplyBudgetMsPerFrame <= 0f
                    ? 1.25f
                    : Math.Min(replicationConfigPresentationApplyBudgetMsPerFrame, 1.25f);
            replicationConfigPresentationApplyMaxEntitiesPerFrame =
                Math.Max(1, Math.Min(replicationConfigPresentationApplyMaxEntitiesPerFrame, 48));

            if (originalSnapshotHz != replicationConfigSnapshotHz
                || originalWorldBudgetPerFrame != replicationConfigWorldObjectDeltaApplyBudgetPerFrame
                || Math.Abs(originalWorldBudgetMs - replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame) > 0.0001f
                || Math.Abs(originalRuntimeBudgetMs - replicationConfigRuntimeMainThreadBudgetMsPerFrame) > 0.0001f
                || Math.Abs(originalPresentationBudgetMs - replicationConfigPresentationApplyBudgetMsPerFrame) > 0.0001f
                || originalPresentationMaxEntities != replicationConfigPresentationApplyMaxEntitiesPerFrame)
            {
                current.Logger.LogWarning("Going Cooperative replication performance safety limits applied"
                    + " snapshotHz=" + originalSnapshotHz.ToString(CultureInfo.InvariantCulture)
                    + "->" + replicationConfigSnapshotHz.ToString(CultureInfo.InvariantCulture)
                    + " worldCount=" + originalWorldBudgetPerFrame.ToString(CultureInfo.InvariantCulture)
                    + "->" + replicationConfigWorldObjectDeltaApplyBudgetPerFrame.ToString(CultureInfo.InvariantCulture)
                    + " worldMs=" + originalWorldBudgetMs.ToString("0.###", CultureInfo.InvariantCulture)
                    + "->" + replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                    + " runtimeMs=" + originalRuntimeBudgetMs.ToString("0.###", CultureInfo.InvariantCulture)
                    + "->" + replicationConfigRuntimeMainThreadBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                    + " presentationMs=" + originalPresentationBudgetMs.ToString("0.###", CultureInfo.InvariantCulture)
                    + "->" + replicationConfigPresentationApplyBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                    + " presentationMax=" + originalPresentationMaxEntities.ToString(CultureInfo.InvariantCulture)
                    + "->" + replicationConfigPresentationApplyMaxEntitiesPerFrame.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void ApplyReplicationConfigValue(GoingCooperativePlugin current, string key, string value, int lineNumber)
        {
            switch (key)
            {
                case "enabled":
                case "enable":
                    if (TryParseConfigBool(value, out var enabled))
                    {
                        replicationConfigEnabled = enabled;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "mode":
                    if (string.Equals(value, "host", StringComparison.OrdinalIgnoreCase))
                    {
                        replicationConfigHostMode = true;
                    }
                    else if (string.Equals(value, "client", StringComparison.OrdinalIgnoreCase))
                    {
                        replicationConfigHostMode = false;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "host":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        replicationConfigHost = value;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port <= 65535)
                    {
                        replicationConfigPort = port;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "pingkey":
                case "playerpingkey":
                    if (TryParseReplicationKeyCode(value, out var pingKey))
                    {
                        replicationConfigPingKey = pingKey;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "snapshothz":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snapshotHz) && snapshotHz > 0 && snapshotHz <= 60)
                    {
                        replicationConfigSnapshotHz = snapshotHz;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "fullresyncseconds":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds <= 3600)
                    {
                        replicationConfigFullResyncSeconds = seconds;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "interpolationms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interpolationMs) && interpolationMs >= 0 && interpolationMs <= 5000)
                    {
                        replicationConfigInterpolationMs = interpolationMs;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "smoothreplicatedmovement":
                case "smoothmovement":
                case "snapshotinterpolation":
                    if (TryParseConfigBool(value, out var smoothReplicatedMovement))
                    {
                        replicationConfigSmoothReplicatedMovement = smoothReplicatedMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "semanticagentpresentation":
                case "agentactivitypresentation":
                    if (TryParseConfigBool(value, out var semanticAgentPresentation))
                    {
                        replicationConfigSemanticAgentPresentation = semanticAgentPresentation;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "semanticanimalpresentationv2":
                case "animalpresentationv2":
                    if (TryParseConfigBool(value, out var semanticAnimalPresentationV2))
                    {
                        replicationConfigSemanticAnimalPresentationV2 = semanticAnimalPresentationV2;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "semanticworkcycledriver":
                case "workcycledriver":
                    if (TryParseConfigBool(value, out var semanticWorkCycleDriver))
                    {
                        replicationConfigSemanticWorkCycleDriver = semanticWorkCycleDriver;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "needsreplication":
                case "hungersleepreplication":
                case "needsync":
                    if (TryParseConfigBool(value, out var needsReplication))
                    {
                        replicationConfigNeedsReplication = needsReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "combatreplication":
                case "combatenabled":
                    if (TryParseConfigBool(value, out var combatReplication)) replicationConfigCombatReplication = combatReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatdraftcommands":
                case "combatdrafting":
                    if (TryParseConfigBool(value, out var combatDraftCommands)) replicationConfigCombatDraftCommands = combatDraftCommands;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatattackcommands":
                case "combatorders":
                    if (TryParseConfigBool(value, out var combatAttackCommands)) replicationConfigCombatAttackCommands = combatAttackCommands;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatstatereplication":
                case "combatstate":
                    if (TryParseConfigBool(value, out var combatStateReplication)) replicationConfigCombatStateReplication = combatStateReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combathealthreplication":
                case "combathealth":
                    if (TryParseConfigBool(value, out var combatHealthReplication)) replicationConfigCombatHealthReplication = combatHealthReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combathealthdetailreplication":
                case "combatwounds":
                    if (TryParseConfigBool(value, out var combatHealthDetailReplication)) replicationConfigCombatHealthDetailReplication = combatHealthDetailReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatdeathreplication":
                case "combatdeath":
                    if (TryParseConfigBool(value, out var combatDeathReplication)) replicationConfigCombatDeathReplication = combatDeathReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatpresentationreplication":
                case "combatpresentation":
                    if (TryParseConfigBool(value, out var combatPresentationReplication)) replicationConfigCombatPresentationReplication = combatPresentationReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatprojectilereplication":
                case "combatprojectiles":
                    if (TryParseConfigBool(value, out var combatProjectileReplication)) replicationConfigCombatProjectileReplication = combatProjectileReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatexternalagentlifecycle":
                case "combathostiles":
                    if (TryParseConfigBool(value, out var combatExternalAgentLifecycle)) replicationConfigCombatExternalAgentLifecycle = combatExternalAgentLifecycle;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "combatdiagnostics":
                case "combatprobes":
                    if (TryParseConfigBool(value, out var combatDiagnostics)) replicationConfigCombatDiagnostics = combatDiagnostics;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "medicalreplicationv1":
                case "medicalenabled":
                    if (TryParseConfigBool(value, out var medicalReplicationV1)) replicationConfigMedicalReplicationV1 = medicalReplicationV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "medicalwoundstatev1":
                case "medicalwoundsv1":
                    if (TryParseConfigBool(value, out var medicalWoundStateV1)) replicationConfigMedicalWoundStateV1 = medicalWoundStateV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "medicaltreatmentordersv1":
                case "medicalordersv1":
                    if (TryParseConfigBool(value, out var medicalTreatmentOrdersV1)) replicationConfigMedicalTreatmentOrdersV1 = medicalTreatmentOrdersV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "medicaltreatmentpresentationv1":
                case "medicalpresentationv1":
                    if (TryParseConfigBool(value, out var medicalTreatmentPresentationV1)) replicationConfigMedicalTreatmentPresentationV1 = medicalTreatmentPresentationV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "medicalpanelrefreshv1":
                case "medicaluiv1":
                    if (TryParseConfigBool(value, out var medicalPanelRefreshV1)) replicationConfigMedicalPanelRefreshV1 = medicalPanelRefreshV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "medicalclientwoundticksuppressionv1":
                case "medicalwoundticksuppressionv1":
                    if (TryParseConfigBool(value, out var medicalClientWoundTickSuppressionV1)) replicationConfigMedicalClientWoundTickSuppressionV1 = medicalClientWoundTickSuppressionV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "medicaldiagnostics":
                case "medicalprobes":
                    if (TryParseConfigBool(value, out var medicalDiagnostics)) replicationConfigMedicalDiagnostics = medicalDiagnostics;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventreplication":
                case "eventenabled":
                    if (TryParseConfigBool(value, out var eventReplication)) replicationConfigEventReplication = eventReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventschedulerauthority":
                case "eventauthority":
                    if (TryParseConfigBool(value, out var eventSchedulerAuthority)) replicationConfigEventSchedulerAuthority = eventSchedulerAuthority;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventtraderauthority":
                case "traderauthority":
                    if (TryParseConfigBool(value, out var eventTraderAuthority)) replicationConfigEventTraderAuthority = eventTraderAuthority;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventrecruitmentauthorityv1":
                case "recruitmenteventauthorityv1":
                    if (TryParseConfigBool(value, out var eventRecruitmentAuthorityV1)) replicationConfigEventRecruitmentAuthorityV1 = eventRecruitmentAuthorityV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventrunawayauthorityv1":
                case "runawayeventauthorityv1":
                    if (TryParseConfigBool(value, out var eventRunawayAuthorityV1)) replicationConfigEventRunawayAuthorityV1 = eventRunawayAuthorityV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "synchronizedtrading":
                case "tradinginteraction":
                    if (TryParseConfigBool(value, out var synchronizedTrading)) replicationConfigSynchronizedTrading = synchronizedTrading;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventlifecyclereplication":
                case "eventlifecycle":
                    if (TryParseConfigBool(value, out var eventLifecycleReplication)) replicationConfigEventLifecycleReplication = eventLifecycleReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventdialogreplication":
                case "eventdialogs":
                    if (TryParseConfigBool(value, out var eventDialogReplication)) replicationConfigEventDialogReplication = eventDialogReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventchoicecommands":
                case "eventchoices":
                    if (TryParseConfigBool(value, out var eventChoiceCommands)) replicationConfigEventChoiceCommands = eventChoiceCommands;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventspeedreplication":
                case "eventspeed":
                    if (TryParseConfigBool(value, out var eventSpeedReplication)) replicationConfigEventSpeedReplication = eventSpeedReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventwarningreplication":
                case "eventwarnings":
                    if (TryParseConfigBool(value, out var eventWarningReplication)) replicationConfigEventWarningReplication = eventWarningReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventnoticereplication":
                case "eventnotices":
                    if (TryParseConfigBool(value, out var eventNoticeReplication)) replicationConfigEventNoticeReplication = eventNoticeReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventexternalagentlifecycle":
                case "eventexternalagents":
                    if (TryParseConfigBool(value, out var eventExternalAgentLifecycle)) replicationConfigEventExternalAgentLifecycle = eventExternalAgentLifecycle;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventenvironmentmutationreplication":
                case "eventenvironmentmutations":
                    if (TryParseConfigBool(value, out var eventEnvironmentMutationReplication)) replicationConfigEventEnvironmentMutationReplication = eventEnvironmentMutationReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "playertriggeredeventreplication":
                case "playertriggeredevents":
                    if (TryParseConfigBool(value, out var playerTriggeredEventReplication)) replicationConfigPlayerTriggeredEventReplication = playerTriggeredEventReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "weatherreplication":
                case "weatherenabled":
                    if (TryParseConfigBool(value, out var weatherReplication)) replicationConfigWeatherReplication = weatherReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "weatherschedulerauthority":
                case "weatherauthority":
                    if (TryParseConfigBool(value, out var weatherSchedulerAuthority)) replicationConfigWeatherSchedulerAuthority = weatherSchedulerAuthority;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "weathertemperaturereplication":
                case "weathertemperature":
                    if (TryParseConfigBool(value, out var weatherTemperatureReplication)) replicationConfigWeatherTemperatureReplication = weatherTemperatureReplication;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "eventdiagnostics":
                case "eventprobes":
                    if (TryParseConfigBool(value, out var eventDiagnostics)) replicationConfigEventDiagnostics = eventDiagnostics;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "tradertransferdiagnostics":
                case "traderbootstrapdiagnostics":
                    if (TryParseConfigBool(value, out var traderTransferDiagnostics)) replicationConfigTraderTransferDiagnostics = traderTransferDiagnostics;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "workstationruntimepresentation":
                case "productionruntimepresentation":
                    if (TryParseConfigBool(value, out var workstationRuntimePresentation)) replicationConfigWorkstationRuntimePresentation = workstationRuntimePresentation;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "productionstatev2":
                case "productionregistryv2":
                    if (TryParseConfigBool(value, out var productionStateV2)) replicationConfigProductionStateV2 = productionStateV2;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "productionticketordersv2":
                case "productionordersv2":
                    if (TryParseConfigBool(value, out var productionTicketOrdersV2)) replicationConfigProductionTicketOrdersV2 = productionTicketOrdersV2;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "applysnapshots":
                    if (TryParseConfigBool(value, out var applySnapshots))
                    {
                        replicationConfigApplySnapshots = applySnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "suppressclientsimulation":
                    if (TryParseConfigBool(value, out var suppressClientSimulation))
                    {
                        replicationConfigSuppressClientSimulation = suppressClientSimulation;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassist":
                case "basicdeterminism":
                case "simassist":
                    if (TryParseConfigBool(value, out var basicDeterminismAssist))
                    {
                        replicationConfigBasicDeterminismAssist = basicDeterminismAssist;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassistcreaturemanager":
                case "basicdeterminismassistcreatures":
                case "simassistcreaturemanager":
                    if (TryParseConfigBool(value, out var basicDeterminismAssistCreatureManager))
                    {
                        replicationConfigBasicDeterminismAssistCreatureManager = basicDeterminismAssistCreatureManager;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassistpathing":
                case "basicdeterminismassistpathfinder":
                case "simassistpathing":
                    if (TryParseConfigBool(value, out var basicDeterminismAssistPathing))
                    {
                        replicationConfigBasicDeterminismAssistPathing = basicDeterminismAssistPathing;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassistcancelpathmovement":
                case "basicdeterminismassistcancelwalking":
                case "simassistcancelpathmovement":
                    if (TryParseConfigBool(value, out var basicDeterminismAssistCancelPathMovement))
                    {
                        replicationConfigBasicDeterminismAssistCancelPathMovement = basicDeterminismAssistCancelPathMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "hostdrivengoapplayback":
                case "goapplayback":
                    if (TryParseConfigBool(value, out var hostDrivenGoapPlayback))
                    {
                        replicationConfigHostDrivenGoapPlayback = hostDrivenGoapPlayback;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "hostdrivengoapplaybacktick":
                case "goapplaybacktick":
                    if (TryParseConfigBool(value, out var hostDrivenGoapPlaybackTick))
                    {
                        replicationConfigHostDrivenGoapPlaybackTick = hostDrivenGoapPlaybackTick;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "puppetvisualtick":
                case "replicationpuppetvisualtick":
                    if (TryParseConfigBool(value, out var puppetVisualTick))
                    {
                        replicationConfigPuppetVisualTick = puppetVisualTick;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionphasereplication":
                case "goapactionphases":
                    if (TryParseConfigBool(value, out var actionPhaseReplication))
                    {
                        replicationConfigActionPhaseReplication = actionPhaseReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionphasevisualreplay":
                case "goapphasevisualreplay":
                    if (TryParseConfigBool(value, out var actionPhaseVisualReplay))
                    {
                        replicationConfigActionPhaseVisualReplay = actionPhaseVisualReplay;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionparameterreplication":
                case "goapactionparameters":
                    if (TryParseConfigBool(value, out var actionParameterReplication))
                    {
                        replicationConfigActionParameterReplication = actionParameterReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "nativeanimationcontrollerreplay":
                case "animationcontrollerreplay":
                    if (TryParseConfigBool(value, out var nativeAnimationControllerReplay))
                    {
                        replicationConfigNativeAnimationControllerReplay = nativeAnimationControllerReplay;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionanimatorstatesampling":
                case "actionanimatorstream":
                    if (TryParseConfigBool(value, out var actionAnimatorStateSampling))
                    {
                        replicationConfigActionAnimatorStateSampling = actionAnimatorStateSampling;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "forcehostmovement":
                case "hostmovementauthority":
                    if (TryParseConfigBool(value, out var forceHostMovement))
                    {
                        replicationConfigForceHostMovement = forceHostMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "animatereplicatedmovement":
                    if (TryParseConfigBool(value, out var animateReplicatedMovement))
                    {
                        replicationConfigAnimateReplicatedMovement = animateReplicatedMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "sendsnapshots":
                    if (TryParseConfigBool(value, out var sendSnapshots))
                    {
                        replicationConfigSendSnapshots = sendSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "logsnapshots":
                    if (TryParseConfigBool(value, out var logSnapshots))
                    {
                        replicationConfigLogSnapshots = logSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "worldobjectdeltadiagnostics":
                case "worlddeltadiagnostics":
                case "verboseworlddeltas":
                    if (TryParseConfigBool(value, out var worldObjectDeltaDiagnostics))
                    {
                        replicationConfigWorldObjectDeltaDiagnostics = worldObjectDeltaDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "hostsleeppresentationv2":
                case "sleeppresentationv2":
                    if (TryParseConfigBool(value, out var hostSleepPresentationV2))
                    {
                        replicationConfigHostSleepPresentationV2 = hostSleepPresentationV2;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "disableautomaticsleepspeed":
                case "disableallsleepingspeed":
                    if (TryParseConfigBool(value, out var disableAutomaticSleepSpeed))
                    {
                        replicationConfigDisableAutomaticSleepSpeed = disableAutomaticSleepSpeed;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "verbosereplicationlogging":
                case "verbosereplicationlogs":
                case "verbosetransportlogging":
                    if (TryParseConfigBool(value, out var verboseReplicationLogging))
                    {
                        replicationConfigVerboseReplicationLogging = verboseReplicationLogging;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "buildingreplicationv2":
                case "transactionalbuildingreplication":
                    if (TryParseConfigBool(value, out var buildingReplicationV2))
                    {
                        replicationConfigBuildingReplicationV2 = buildingReplicationV2;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "buildingconstructionmaterialsv2":
                case "constructionmaterialsv2":
                    if (TryParseConfigBool(value, out var buildingConstructionMaterialsV2))
                    {
                        replicationConfigBuildingConstructionMaterialsV2 = buildingConstructionMaterialsV2;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "beamplacementreplication":
                    if (TryParseConfigBool(value, out var beamPlacementReplication))
                    {
                        replicationConfigBeamPlacementReplication = beamPlacementReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "cropfieldspatialreplicationv1":
                    if (TryParseConfigBool(value, out var cropfieldSpatialReplicationV1))
                    {
                        replicationConfigCropfieldSpatialReplicationV1 = cropfieldSpatialReplicationV1;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "cropfieldpolicyv1":
                    if (TryParseConfigBool(value, out var cropfieldPolicyV1))
                    {
                        replicationConfigCropfieldPolicyV1 = cropfieldPolicyV1;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "cropfieldpolicydiagnostics":
                case "cropfielddiagnostics":
                    if (TryParseConfigBool(value, out var cropfieldPolicyDiagnostics))
                    {
                        replicationConfigCropfieldPolicyDiagnostics = cropfieldPolicyDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "plantlifecyclereplication":
                    if (TryParseConfigBool(value, out var plantLifecycleReplication))
                    {
                        replicationConfigPlantLifecycleReplication = plantLifecycleReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "prioritisedobjectworkv1":
                case "prioritizedobjectworkv1":
                    if (TryParseConfigBool(value, out var prioritisedObjectWorkV1))
                    {
                        replicationConfigPrioritisedObjectWorkV1 = prioritisedObjectWorkV1;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "beamlifecyclereplication":
                    if (TryParseConfigBool(value, out var beamLifecycleReplication))
                    {
                        replicationConfigBeamLifecycleReplication = beamLifecycleReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "socketableplacementreplication":
                    if (TryParseConfigBool(value, out var socketablePlacementReplication))
                    {
                        replicationConfigSocketablePlacementReplication = socketablePlacementReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "beamreplicationdiagnostics":
                case "beamdiagnostics":
                    if (TryParseConfigBool(value, out var beamReplicationDiagnostics))
                    {
                        replicationConfigBeamReplicationDiagnostics = beamReplicationDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "validatesnapshots":
                    if (TryParseConfigBool(value, out var validateSnapshots))
                    {
                        replicationConfigValidateSnapshots = validateSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "sendproofintent":
                    if (TryParseConfigBool(value, out var sendProofIntent))
                    {
                        replicationConfigSendProofIntent = sendProofIntent;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "resultlifecycleprobes":
                    if (TryParseConfigBool(value, out var resultLifecycleProbes))
                    {
                        replicationConfigResultLifecycleProbes = resultLifecycleProbes;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "animationdiagnostics":
                    if (TryParseConfigBool(value, out var animationDiagnostics))
                    {
                        replicationConfigAnimationDiagnostics = animationDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "characterstatediagnostics":
                    if (TryParseConfigBool(value, out var characterStateDiagnostics))
                    {
                        replicationConfigCharacterStateDiagnostics = characterStateDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "carrydiagnostics":
                case "carryprobe":
                    if (TryParseConfigBool(value, out var carryDiagnostics))
                    {
                        replicationConfigCarryDiagnostics = carryDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "goapactionprobe":
                case "goapprobe":
                    if (TryParseConfigBool(value, out var goapActionProbe))
                    {
                        replicationConfigGoapActionProbe = goapActionProbe;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "goapactionprobeentityid":
                case "goapprobeentityid":
                    replicationConfigGoapActionProbeEntityId = value.Trim();
                    break;
                case "multiplayermenu":
                case "multiplayermenuenabled":
                case "enablemultiplayermenu":
                    if (TryParseConfigBool(value, out var multiplayerMenuEnabled))
                    {
                        replicationConfigMultiplayerMenuEnabled = multiplayerMenuEnabled;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "ui":
                case "menuui":
                case "multiplayerui":
                    if (string.Equals(value, "v2", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "modern", StringComparison.OrdinalIgnoreCase))
                    {
                        replicationConfigUiV2 = true;
                    }
                    else if (string.Equals(value, "classic", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "v1", StringComparison.OrdinalIgnoreCase))
                    {
                        replicationConfigUiV2 = false;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "uiv3":
                case "menuv3":
                case "multiplayeruiv3":
                    if (TryParseConfigBool(value, out var uiV3))
                    {
                        replicationConfigUiV3 = uiV3;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "steamnetworking":
                case "steam":
                case "steamtransport":
                    if (TryParseConfigBool(value, out var steamNetworking))
                    {
                        replicationConfigSteamNetworking = steamNetworking;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "directtransportsecurityv1":
                case "authenticateddirecttransport":
                case "directsecurity":
                    if (TryParseConfigBool(value, out var directTransportSecurityV1))
                    {
                        replicationConfigDirectTransportSecurityV1 = directTransportSecurityV1;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "perffpsprobe":
                    if (TryParseConfigBool(value, out var perfFpsProbe))
                    {
                        replicationConfigPerfFpsProbe = perfFpsProbe;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "pathingperfdiagnostics":
                case "pathingperformanceprobe":
                    if (TryParseConfigBool(value, out var pathingPerfDiagnostics))
                    {
                        replicationConfigPathingPerfDiagnostics = pathingPerfDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "resourcepilestatesnapshots":
                case "resourcepilesnapshots":
                case "sendresourcepilestatesnapshots":
                    if (TryParseConfigBool(value, out var resourcePileStateSnapshots))
                    {
                        replicationConfigResourcePileStateSnapshots = resourcePileStateSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "resourcecontainerreplication":
                case "resourcecontainers":
                case "resourceownershipreplication":
                    if (TryParseConfigBool(value, out var resourceContainerReplication))
                    {
                        replicationConfigResourceContainerReplication = resourceContainerReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "resourcestatev2":
                    if (TryParseConfigBool(value, out var resourceStateV2)) replicationConfigResourceStateV2 = resourceStateV2;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "agentinventorystatev2":
                    if (TryParseConfigBool(value, out var agentInventoryStateV2)) replicationConfigAgentInventoryStateV2 = agentInventoryStateV2;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "groundpilestatev2":
                    if (TryParseConfigBool(value, out var groundPileStateV2)) replicationConfigGroundPileStateV2 = groundPileStateV2;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "shelfstoragestatev2":
                    if (TryParseConfigBool(value, out var shelfStorageStateV2)) replicationConfigShelfStorageStateV2 = shelfStorageStateV2;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "resourcestatev2diagnostics":
                    if (TryParseConfigBool(value, out var resourceStateV2Diagnostics)) replicationConfigResourceStateV2Diagnostics = resourceStateV2Diagnostics;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "storagepolicyv4":
                    if (TryParseConfigBool(value, out var storagePolicyV4)) replicationConfigStoragePolicyV4 = storagePolicyV4;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "shelfstoragemanifestv1":
                case "shelfcontainerreplicationv1":
                    if (TryParseConfigBool(value, out var shelfStorageManifestV1)) replicationConfigShelfStorageManifestV1 = shelfStorageManifestV1;
                    else LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    break;
                case "worldobjectdeltaapplybudgetperframe":
                case "worlddeltaapplybudgetperframe":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var applyBudget)
                        && applyBudget >= 0
                        && applyBudget <= 256)
                    {
                        replicationConfigWorldObjectDeltaApplyBudgetPerFrame = applyBudget;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "worldobjectdeltaapplyqueuemax":
                case "worlddeltaapplyqueuemax":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var queueMax)
                        && queueMax >= 64
                        && queueMax <= 65536)
                    {
                        replicationConfigWorldObjectDeltaApplyQueueMax = queueMax;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "worldobjectdeltaapplybudgetmsperframe":
                case "worlddeltaapplybudgetmsperframe":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var applyBudgetMs)
                        && applyBudgetMs >= 0f
                        && applyBudgetMs <= 100f)
                    {
                        replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame = applyBudgetMs;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "runtimemainthreadbudgetmsperframe":
                case "multiplayermainthreadbudgetmsperframe":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var runtimeBudgetMs)
                        && runtimeBudgetMs >= 0f
                        && runtimeBudgetMs <= 16f)
                    {
                        replicationConfigRuntimeMainThreadBudgetMsPerFrame = runtimeBudgetMs;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "presentationapplybudgetmsperframe":
                case "presentationbudgetmsperframe":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var presentationBudgetMs)
                        && presentationBudgetMs >= 0f
                        && presentationBudgetMs <= 8f)
                    {
                        replicationConfigPresentationApplyBudgetMsPerFrame = presentationBudgetMs;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "presentationapplymaxentitiesperframe":
                case "presentationmaxentitiesperframe":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var presentationMaxEntities)
                        && presentationMaxEntities >= 1
                        && presentationMaxEntities <= 512)
                    {
                        replicationConfigPresentationApplyMaxEntitiesPerFrame = presentationMaxEntities;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "snapshotviewcachesafetyrefreshseconds":
                case "snapshotviewcacherefreshseconds":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cacheRefreshSeconds)
                        && cacheRefreshSeconds >= 0f
                        && cacheRefreshSeconds <= 600f)
                    {
                        replicationConfigSnapshotViewCacheSafetyRefreshSeconds = cacheRefreshSeconds;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "proofintent":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        replicationConfigProofIntent = value.Trim();
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "commandcapturemode":
                    if (TryParseReplicationCaptureMode(value, out var commandCaptureMode))
                    {
                        replicationConfigCommandCaptureMode = commandCaptureMode;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "regioncommandmode":
                    if (TryParseReplicationCaptureMode(value, out var regionCommandMode))
                    {
                        replicationConfigRegionCommandMode = regionCommandMode;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "worldobjectdeltamode":
                    if (TryParseReplicationWorldObjectDeltaMode(value, out var worldObjectDeltaMode))
                    {
                        replicationConfigWorldObjectDeltaMode = worldObjectDeltaMode;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "proofintentdelayseconds":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var proofIntentDelaySeconds)
                        && proofIntentDelaySeconds >= 0
                        && proofIntentDelaySeconds <= 300)
                    {
                        replicationConfigProofIntentDelaySeconds = proofIntentDelaySeconds;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "snapshotvalidationseconds":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var validationSeconds)
                        && validationSeconds >= 1
                        && validationSeconds <= 300)
                    {
                        replicationConfigSnapshotValidationSeconds = validationSeconds;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "maxsnapshotentities":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxEntities) && maxEntities >= 0 && maxEntities <= 2048)
                    {
                        replicationConfigMaxSnapshotEntities = maxEntities;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                default:
                    current.Logger.LogWarning("Going Cooperative replication config ignored unknown key line="
                        + lineNumber.ToString(CultureInfo.InvariantCulture)
                        + " key="
                        + key);
                    break;
            }
        }

        private static void TryLoadDiagnosticsConfig(GoingCooperativePlugin current)
        {
            var configPath = Path.Combine(Paths.GameRootPath, DiagnosticsConfigRelativePath);
            if (!File.Exists(configPath))
            {
                return;
            }

            try
            {
                var lines = File.ReadAllLines(configPath);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    var value = line.Substring(separator + 1).Trim();
                    if (!string.Equals(key, "perffpsprobe", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (TryParseConfigBool(value, out var perfFpsProbe))
                    {
                        replicationConfigPerfFpsProbe = perfFpsProbe;
                    }
                    else
                    {
                        current.Logger.LogWarning("Going Cooperative diagnostics config ignored invalid value line="
                            + (i + 1).ToString(CultureInfo.InvariantCulture)
                            + " key="
                            + key
                            + " value="
                            + value);
                    }
                }

                current.Logger.LogInfo("Going Cooperative diagnostics config loaded perfFpsProbe=" + replicationConfigPerfFpsProbe);
            }
            catch (Exception ex)
            {
                current.Logger.LogWarning("Going Cooperative diagnostics config load failed path="
                    + configPath
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private static void LogReplicationConfigInvalidValue(GoingCooperativePlugin current, int lineNumber, string key, string value)
        {
            current.Logger.LogWarning("Going Cooperative replication config ignored invalid value line="
                + lineNumber.ToString(CultureInfo.InvariantCulture)
                + " key="
                + key
                + " value="
                + value);
        }

        private static bool TryParseReplicationKeyCode(string value, out UnityEngine.KeyCode keyCode)
        {
            keyCode = UnityEngine.KeyCode.None;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                var parsed = (UnityEngine.KeyCode)Enum.Parse(
                    typeof(UnityEngine.KeyCode),
                    value.Trim(),
                    true);
                if (!Enum.IsDefined(typeof(UnityEngine.KeyCode), parsed))
                {
                    return false;
                }

                keyCode = parsed;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryParseReplicationCaptureMode(string value, out string mode)
        {
            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "send", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "authoritative", StringComparison.OrdinalIgnoreCase))
            {
                mode = value.Trim().ToLowerInvariant();
                return true;
            }

            mode = string.Empty;
            return false;
        }

        private static bool TryParseReplicationWorldObjectDeltaMode(string value, out string mode)
        {
            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "apply", StringComparison.OrdinalIgnoreCase))
            {
                mode = value.Trim().ToLowerInvariant();
                return true;
            }

            mode = string.Empty;
            return false;
        }
    }
}
