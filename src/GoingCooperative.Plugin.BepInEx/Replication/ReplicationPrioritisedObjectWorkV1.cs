using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationPrioritisedObjectWorkResultV1DeltaKind =
            "PrioritisedObjectWorkResultV1";
        private const int ReplicationPrioritisedObjectWorkResultRetention = 256;
        private static long replicationPrioritisedObjectWorkRequestSequence;
        private static int replicationPrioritisedObjectWorkApplyDepth;
        private static long replicationPrioritisedObjectWorkSent;
        private static long replicationPrioritisedObjectWorkApplied;
        private static long replicationPrioritisedObjectWorkRejected;
        private static readonly HashSet<string>
            ReplicationAppliedPrioritisedObjectWorkResultRequestIds =
                new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string>
            ReplicationAppliedPrioritisedObjectWorkResultRequestOrder =
                new Queue<string>();
        private static readonly List<PendingReplicationPrioritisedObjectWork>
            ReplicationPendingPrioritisedObjectWork =
                new List<PendingReplicationPrioritisedObjectWork>();

        private sealed class PendingReplicationPrioritisedObjectWork
        {
            public object Worker = null!;
            public string WorkerEntityId = string.Empty;
            public long TargetHostId;
            public string TargetEntityId = string.Empty;
            public string TargetFamily = string.Empty;
            public string TargetPolicy = string.Empty;
            public string GoalId = string.Empty;
            public string RequestId = string.Empty;
            public int TargetX;
            public int TargetY;
            public int TargetZ;
            public uint StartingJobVersion;
            public float ExpiresRealtime;
        }

        private int TryInstallReplicationPrioritisedObjectWorkV1Hooks(Harmony harmony)
        {
            if (!replicationConfigPrioritisedObjectWorkV1)
            {
                return 0;
            }

            var prefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationPrioritisedObjectWorkV1Prefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var postfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationPrioritisedObjectWorkV1Postfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var menuTypes = new[]
            {
                "NSMedieval.AdditionalMenuItems.PrioritiseHarvestMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseChopMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseMineMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseFishingMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseHaulingMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseBuildingConstructionMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseBuildingMaterialsDeliveryMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseBuildingDeConstructionMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseBuildingUninstallMenuItem",
                "NSMedieval.AdditionalMenuItems.PrioritiseStripMenuItem"
            };

            var count = 0;
            for (var i = 0; i < menuTypes.Length; i++)
            {
                var menuType = AccessTools.TypeByName(menuTypes[i]);
                var method = menuType == null
                    ? null
                    : AccessTools.Method(menuType, "OnClickCallback", Type.EmptyTypes);
                if (method == null)
                {
                    LogReplicationWarning("Going Cooperative prioritised-object-work-v1 hook missing type="
                        + menuTypes[i]
                        + " method=OnClickCallback");
                    continue;
                }

                try
                {
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    count++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning("Going Cooperative prioritised-object-work-v1 hook failed type="
                        + menuTypes[i]
                        + " error="
                        + FormatReflectionExceptionDetail(ex));
                }
            }

            LogReplicationInfo("Going Cooperative prioritised-object-work-v1 hooks="
                + count.ToString(CultureInfo.InvariantCulture)
                + "/"
                + menuTypes.Length.ToString(CultureInfo.InvariantCulture));
            return count;
        }

        private static bool ReplicationPrioritisedObjectWorkV1Prefix(
            object __instance,
            MethodBase __originalMethod)
        {
            if (!replicationConfigPrioritisedObjectWorkV1
                || replicationPrioritisedObjectWorkApplyDepth > 0
                || replicationConfigHostMode)
            {
                return true;
            }

            if (!ShouldSendReplicationLocalCommandIntent())
            {
                instance?.LogReplicationWarning(
                    "Going Cooperative prioritised-object-work-v1 rejected client-not-ready type="
                    + (__originalMethod.DeclaringType?.FullName ?? "<unknown>"));
                return false;
            }

            if (!TryBuildReplicationPrioritisedObjectWorkV1Payload(
                    __instance,
                    __originalMethod,
                    out var payload,
                    out var detail))
            {
                replicationPrioritisedObjectWorkRejected++;
                instance?.LogReplicationWarning(
                    "Going Cooperative prioritised-object-work-v1 capture failed " + detail);
                return false;
            }

            var command = new LockstepCommand(
                ReplicationClientPeerId,
                ++replicationIntentSequence,
                0L,
                CommandKind.Custom,
                payload);
            SendReplicationLocalCommandIntent(command, "prioritised-object-work-v1");
            replicationPrioritisedObjectWorkSent++;
            instance?.LogReplicationInfo(
                "Going Cooperative prioritised-object-work-v1 sent " + detail);
            return false;
        }

        private static void ReplicationPrioritisedObjectWorkV1Postfix(
            object __instance,
            MethodBase __originalMethod)
        {
            if (!replicationConfigPrioritisedObjectWorkV1
                || replicationPrioritisedObjectWorkApplyDepth > 0
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived)
            {
                return;
            }

            if (!TryBuildReplicationPrioritisedObjectWorkV1Payload(
                    __instance,
                    __originalMethod,
                    out var payload,
                    out var detail))
            {
                replicationPrioritisedObjectWorkRejected++;
                instance?.LogReplicationWarning(
                    "Going Cooperative prioritised-object-work-v1 host result capture failed "
                    + detail);
                return;
            }

            instance?.SendReplicationPrioritisedObjectWorkResultPayload(
                payload,
                "host-local " + detail);
        }

        private static bool TryBuildReplicationPrioritisedObjectWorkV1Payload(
            object menuItem,
            MethodBase originalMethod,
            out string payload,
            out string detail)
        {
            payload = string.Empty;
            detail = string.Empty;
            var family = originalMethod.DeclaringType?.Name ?? menuItem.GetType().Name;
            if (!TryResolveReplicationPrioritisedObjectWorkGoal(family, out var goalId))
            {
                detail = "unsupported-family family=" + family;
                return false;
            }
            var targetPolicy = ResolveReplicationPrioritisedObjectWorkPolicy(
                family,
                menuItem);

            var selectedWorker = AccessTools.Method(
                    menuItem.GetType(),
                    "GetSelectedWorker",
                    Type.EmptyTypes)
                ?.Invoke(menuItem, null);
            var owner = AccessTools.Property(menuItem.GetType(), "Owner")
                ?.GetValue(menuItem, null);
            var target = owner == null
                ? null
                : AccessTools.Method(owner.GetType(), "GetAsTarget", Type.EmptyTypes)
                    ?.Invoke(owner, null);
            if (selectedWorker == null
                || target == null
                || !TryGetReplicationAgentOwnerEntityId(
                    selectedWorker,
                    out var workerEntityId,
                    out var workerDetail))
            {
                detail = "context-missing family=" + family
                    + " worker=" + (selectedWorker == null ? "missing" : "unresolved")
                    + " target=" + (target == null ? "missing" : "present");
                return false;
            }

            var targetHostId = 0L;
            if (!TryGetReplicationHostIdForLocalObjectV2(target, out targetHostId)
                && owner != null)
            {
                TryGetReplicationHostIdForLocalObjectV2(owner, out targetHostId);
            }

            TryGetReplicationStableEntityId(target, out var targetEntityId);
            if (targetHostId <= 0L
                && targetEntityId.StartsWith("uid:", StringComparison.Ordinal)
                && long.TryParse(
                    targetEntityId.Substring(4),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var nativeTargetId)
                && nativeTargetId > 0L)
            {
                targetHostId = nativeTargetId;
            }

            if (!TryResolveReplicationContextualObjectGrid(
                    target,
                    out var targetX,
                    out var targetY,
                    out var targetZ,
                    out var targetPositionDetail)
                && (owner == null
                    || !TryResolveReplicationContextualObjectGrid(
                        owner,
                        out targetX,
                        out targetY,
                        out targetZ,
                        out targetPositionDetail)))
            {
                detail = "target-position-missing family=" + family
                    + " target=" + (target.GetType().FullName ?? target.GetType().Name);
                return false;
            }

            if (targetHostId <= 0L && string.IsNullOrWhiteSpace(targetEntityId))
            {
                detail = "target-identity-missing family=" + family
                    + " target=" + (target.GetType().FullName ?? target.GetType().Name)
                    + " "
                    + targetPositionDetail;
                return false;
            }

            var requestId = "priority-"
                + (replicationConfigHostMode ? "host-" : "client-")
                + (++replicationPrioritisedObjectWorkRequestSequence).ToString(
                    CultureInfo.InvariantCulture);
            payload = LockstepCommandPayloads.CreatePrioritisedObjectWorkV1Payload(
                workerEntityId,
                targetHostId,
                targetEntityId,
                family,
                targetPolicy,
                goalId,
                requestId,
                targetX,
                targetY,
                targetZ);
            detail = "requestId=" + requestId
                + " worker=" + workerEntityId
                + " targetHostId=" + targetHostId.ToString(CultureInfo.InvariantCulture)
                + " targetEntityId=" + targetEntityId
                + " family=" + family
                + " policy=" + targetPolicy
                + " goal=" + goalId
                + " grid=Vec3Int("
                + targetX.ToString(CultureInfo.InvariantCulture)
                + ","
                + targetY.ToString(CultureInfo.InvariantCulture)
                + ","
                + targetZ.ToString(CultureInfo.InvariantCulture)
                + ") "
                + workerDetail
                + " "
                + targetPositionDetail;
            return true;
        }

        private static bool TryResolveReplicationPrioritisedObjectWorkGoal(
            string family,
            out string goalId)
        {
            switch (family)
            {
                case "PrioritiseHarvestMenuItem":
                    goalId = "HarvestGoal";
                    return true;
                case "PrioritiseChopMenuItem":
                    goalId = "ChopTreeGoal";
                    return true;
                case "PrioritiseMineMenuItem":
                    goalId = "DigGoal";
                    return true;
                case "PrioritiseFishingMenuItem":
                    goalId = "FishingGoal";
                    return true;
                case "PrioritiseHaulingMenuItem":
                    goalId = "StockpileHaulingGoal";
                    return true;
                case "PrioritiseBuildingConstructionMenuItem":
                    goalId = "ConstructBuildingGoal";
                    return true;
                case "PrioritiseBuildingMaterialsDeliveryMenuItem":
                    goalId = "DeliverBuildingMaterialsGoal";
                    return true;
                case "PrioritiseBuildingDeConstructionMenuItem":
                    goalId = "DeconstructGoal";
                    return true;
                case "PrioritiseBuildingUninstallMenuItem":
                    goalId = "UninstallBuildingGoal";
                    return true;
                case "PrioritiseStripMenuItem":
                    goalId = "StripCarcassGoal";
                    return true;
                default:
                    goalId = string.Empty;
                    return false;
            }
        }

        private static string ResolveReplicationPrioritisedObjectWorkPolicy(
            string family,
            object menuItem)
        {
            if (string.Equals(
                    family,
                    "PrioritiseHarvestMenuItem",
                    StringComparison.Ordinal))
            {
                return "Harvesting";
            }
            if (string.Equals(
                    family,
                    "PrioritiseFishingMenuItem",
                    StringComparison.Ordinal))
            {
                return "Fishing";
            }
            if (string.Equals(
                    family,
                    "PrioritiseChopMenuItem",
                    StringComparison.Ordinal))
            {
                return TryReadInstanceMemberValue(
                        menuItem,
                        "orderTypeToGive",
                        out var orderType)
                    && orderType != null
                    ? orderType.ToString() ?? "Chopping"
                    : "Chopping";
            }
            return string.Empty;
        }

        private static bool IsReplicationPrioritisedObjectWorkV1MenuType(string typeName)
        {
            var lastSeparator = typeName.LastIndexOf('.');
            var family = lastSeparator >= 0
                ? typeName.Substring(lastSeparator + 1)
                : typeName;
            return TryResolveReplicationPrioritisedObjectWorkGoal(family, out _);
        }

        private static bool TryApplyReplicationPrioritisedObjectWorkV1(
            string workerEntityId,
            long targetHostId,
            string targetEntityId,
            string targetFamily,
            string targetPolicy,
            string goalId,
            string requestId,
            int targetX,
            int targetY,
            int targetZ,
            out string detail)
        {
            detail = "prioritised-object-work-v1-rejected";
            if (!replicationConfigPrioritisedObjectWorkV1 || !replicationConfigHostMode)
            {
                return false;
            }

            if (!TryResolveReplicationPrioritisedObjectWorkGoal(
                    targetFamily,
                    out var expectedGoalId)
                || !string.Equals(goalId, expectedGoalId, StringComparison.Ordinal)
                || !IsReplicationPrioritisedObjectWorkPolicyAllowed(
                    targetFamily,
                    targetPolicy))
            {
                detail = "prioritised-object-work-v1-contract-mismatch family="
                    + targetFamily
                    + " goal="
                    + goalId;
                replicationPrioritisedObjectWorkRejected++;
                return false;
            }

            if (!TryFindReplicationAgentOwnerByEntityId(
                    workerEntityId,
                    out var worker,
                    out var workerDetail)
                || worker == null)
            {
                detail = "prioritised-object-work-v1-worker-missing worker="
                    + workerEntityId
                    + " "
                    + workerDetail;
                replicationPrioritisedObjectWorkRejected++;
                return false;
            }

            if ((TryReadInstanceMemberValue(worker, "HasFainted", out var fainted)
                    && fainted != null
                    && Convert.ToBoolean(fainted, CultureInfo.InvariantCulture))
                || (TryReadInstanceMemberValue(worker, "HasDisposed", out var disposed)
                    && disposed != null
                    && Convert.ToBoolean(disposed, CultureInfo.InvariantCulture)))
            {
                detail = "prioritised-object-work-v1-worker-unavailable worker="
                    + workerEntityId;
                replicationPrioritisedObjectWorkRejected++;
                return false;
            }

            if (!TryResolveReplicationPrioritisedObjectWorkTarget(
                    targetHostId,
                    targetEntityId,
                    targetFamily,
                    targetX,
                    targetY,
                    targetZ,
                    out var target,
                    out var targetDetail)
                || target == null)
            {
                detail = "prioritised-object-work-v1-target-missing targetHostId="
                    + targetHostId.ToString(CultureInfo.InvariantCulture)
                    + " targetEntityId="
                    + targetEntityId
                    + " family="
                    + targetFamily
                    + " "
                    + targetDetail;
                replicationPrioritisedObjectWorkRejected++;
                return false;
            }

            if (!IsReplicationPrioritisedObjectWorkTargetCompatible(
                    targetFamily,
                    target))
            {
                detail = "prioritised-object-work-v1-target-type-mismatch family="
                    + targetFamily
                    + " targetType="
                    + (target.GetType().FullName ?? target.GetType().Name);
                replicationPrioritisedObjectWorkRejected++;
                return false;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseBuildingMaterialsDeliveryMenuItem",
                    StringComparison.Ordinal)
                && TryReadInstanceMemberValue(
                    target,
                    "IsMoveBlueprint",
                    out var moveBlueprint)
                && moveBlueprint != null
                && Convert.ToBoolean(moveBlueprint, CultureInfo.InvariantCulture))
            {
                detail = "prioritised-object-work-v1-move-blueprint-fail-closed";
                replicationPrioritisedObjectWorkRejected++;
                return false;
            }

            try
            {
                replicationPrioritisedObjectWorkApplyDepth++;
                var waitForDeconstructJob = string.Equals(
                        targetFamily,
                        "PrioritiseBuildingDeConstructionMenuItem",
                        StringComparison.Ordinal)
                    && (!TryReadInstanceMemberValue(
                            target,
                            "MarkedForDestruction",
                            out var markedForDestruction)
                        || markedForDestruction == null
                        || !Convert.ToBoolean(
                            markedForDestruction,
                            CultureInfo.InvariantCulture));
                var startingDestroyJobVersion = 0u;
                if (waitForDeconstructJob)
                {
                    TryReadReplicationPrioritisedDestroyJobVersion(
                        worker,
                        out startingDestroyJobVersion);
                }
                TryApplyReplicationPrioritisedObjectWorkPolicy(
                    targetFamily,
                    targetPolicy,
                    target,
                    out var policyDetail);
                if (waitForDeconstructJob)
                {
                    ReplicationPendingPrioritisedObjectWork.Add(
                        new PendingReplicationPrioritisedObjectWork
                        {
                            Worker = worker,
                            WorkerEntityId = workerEntityId,
                            TargetHostId = targetHostId,
                            TargetEntityId = targetEntityId,
                            TargetFamily = targetFamily,
                            TargetPolicy = targetPolicy,
                            GoalId = goalId,
                            RequestId = requestId,
                            TargetX = targetX,
                            TargetY = targetY,
                            TargetZ = targetZ,
                            StartingJobVersion = startingDestroyJobVersion,
                            ExpiresRealtime = Time.realtimeSinceStartup + 2f
                        });
                    replicationPrioritisedObjectWorkApplied++;
                    detail = "ok prioritised-object-work-v1 queued-for-destroy-job"
                        + " requestId=" + requestId
                        + " worker=" + workerEntityId
                        + " targetHostId="
                        + targetHostId.ToString(CultureInfo.InvariantCulture)
                        + " startingVersion="
                        + startingDestroyJobVersion.ToString(
                            CultureInfo.InvariantCulture)
                        + " "
                        + policyDetail;
                    return true;
                }

                if (!TryReadInstanceMemberValue(worker, "GoapAgent", out var goapAgent)
                    || goapAgent == null)
                {
                    detail = "prioritised-object-work-v1-goap-missing worker="
                        + workerEntityId;
                    replicationPrioritisedObjectWorkRejected++;
                    return false;
                }

                var workerGoapType =
                    AccessTools.TypeByName("NSMedieval.Goap.WorkerGoapAgent");
                if (workerGoapType == null
                    || !workerGoapType.IsInstanceOfType(goapAgent))
                {
                    detail = "prioritised-object-work-v1-goap-type-invalid worker="
                        + workerEntityId;
                    replicationPrioritisedObjectWorkRejected++;
                    return false;
                }

                var reservationType =
                    AccessTools.TypeByName("NSMedieval.Manager.ReservationManager");
                var reservation = reservationType == null
                    ? null
                    : ResolveReplicationUnityManagerInstance(reservationType);
                if (reservation == null)
                {
                    detail = "prioritised-object-work-v1-reservation-manager-missing";
                    replicationPrioritisedObjectWorkRejected++;
                    return false;
                }

                var setPreferred = FindReplicationMedicalCompatibleMethod(
                    reservation.GetType(),
                    "SetPreferredReservable",
                    worker,
                    target);
                var reserve = FindReplicationMedicalCompatibleMethod(
                    reservation.GetType(),
                    "TryToExclusiveReservation",
                    target,
                    worker,
                    1f);
                var force = AccessTools.Method(
                    workerGoapType,
                    "ForceNextGoalExclusive",
                    new[] { typeof(string) });
                if (setPreferred == null || reserve == null || force == null)
                {
                    detail = "prioritised-object-work-v1-native-surface-missing"
                        + " setPreferred=" + (setPreferred == null ? "0" : "1")
                        + " reserve=" + (reserve == null ? "0" : "1")
                        + " force=" + (force == null ? "0" : "1");
                    replicationPrioritisedObjectWorkRejected++;
                    return false;
                }

                var getSingleReserver = FindReplicationMedicalCompatibleMethod(
                    reservation.GetType(),
                    "GetSingleReserver",
                    target);
                var priorReserver = getSingleReserver?.Invoke(
                    reservation,
                    new[] { target });
                if (priorReserver != null && !ReferenceEquals(priorReserver, worker))
                {
                    if (TryReadInstanceMemberValue(
                            priorReserver,
                            "GoapAgent",
                            out var priorAgent)
                        && priorAgent != null)
                    {
                        AccessTools.Method(
                                priorAgent.GetType(),
                                "Abort",
                                Type.EmptyTypes)
                            ?.Invoke(priorAgent, null);
                    }
                    FindReplicationMedicalCompatibleMethod(
                            reservation.GetType(),
                            "ReleaseObject",
                            target,
                            priorReserver)
                        ?.Invoke(
                            reservation,
                            new[] { target, priorReserver });
                }

                setPreferred.Invoke(reservation, new[] { worker, target });
                var reserved = Convert.ToBoolean(
                    reserve.Invoke(
                        reservation,
                        new object[] { target, worker, 1f }),
                    CultureInfo.InvariantCulture);
                AccessTools.Method(goapAgent.GetType(), "Abort", Type.EmptyTypes)
                    ?.Invoke(goapAgent, null);
                var goal = force.Invoke(goapAgent, new object[] { goalId });
                if (goal == null)
                {
                    detail = "prioritised-object-work-v1-force-returned-null"
                        + " requestId=" + requestId
                        + " worker=" + workerEntityId
                        + " goal=" + goalId
                        + " reserved=" + reserved
                        + " "
                        + targetDetail
                        + " "
                        + policyDetail;
                    replicationPrioritisedObjectWorkRejected++;
                    return false;
                }

                replicationPrioritisedObjectWorkApplied++;
                detail = "ok prioritised-object-work-v1"
                    + " requestId=" + requestId
                    + " worker=" + workerEntityId
                    + " targetHostId=" + targetHostId.ToString(CultureInfo.InvariantCulture)
                    + " targetEntityId=" + targetEntityId
                    + " family=" + targetFamily
                    + " policy=" + targetPolicy
                    + " goal=" + goalId
                    + " reserved=" + reserved
                    + " "
                    + workerDetail
                    + " "
                    + targetDetail
                    + " "
                    + policyDetail;
                instance?.LogReplicationInfo(
                    "Going Cooperative prioritised-object-work-v1 applied " + detail);
                return true;
            }
            catch (Exception ex)
            {
                replicationPrioritisedObjectWorkRejected++;
                detail = "prioritised-object-work-v1-exception "
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                replicationPrioritisedObjectWorkApplyDepth--;
            }
        }

        private void SendReplicationPrioritisedObjectWorkResultIfSupported(
            LockstepCommand command,
            RuntimeCommandResult result)
        {
            if (!result.Invoked
                || !replicationConfigPrioritisedObjectWorkV1
                || command.Kind != CommandKind.Custom
                || !LockstepCommandPayloads.TryReadPrioritisedObjectWorkV1Payload(
                    command.PayloadJson,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _))
            {
                return;
            }

            SendReplicationPrioritisedObjectWorkResultPayload(
                command.PayloadJson,
                "host-command player="
                    + command.PlayerId
                    + " sequence="
                    + command.Sequence.ToString(CultureInfo.InvariantCulture));
        }

        private void SendReplicationPrioritisedObjectWorkResultPayload(
            string intentPayload,
            string source)
        {
            if (!replicationConfigPrioritisedObjectWorkV1
                || !replicationConfigHostMode
                || !LockstepCommandPayloads.TryReadPrioritisedObjectWorkV1Payload(
                    intentPayload,
                    out var workerEntityId,
                    out var targetHostId,
                    out var targetEntityId,
                    out var targetFamily,
                    out var targetPolicy,
                    out var goalId,
                    out var requestId,
                    out var targetX,
                    out var targetY,
                    out var targetZ))
            {
                return;
            }

            var resultPayload =
                LockstepCommandPayloads.CreatePrioritisedObjectWorkResultV1Payload(
                    workerEntityId,
                    targetHostId,
                    targetEntityId,
                    targetFamily,
                    targetPolicy,
                    goalId,
                    requestId,
                    targetX,
                    targetY,
                    targetZ);
            var sent = SendReplicationWorldObjectDelta(
                new ReplicationWorldObjectDelta(
                    ++replicationWorldObjectDeltaSequence,
                    Time.realtimeSinceStartup,
                    ReplicationPrioritisedObjectWorkResultV1DeltaKind,
                    targetHostId,
                    targetFamily,
                    targetX,
                    targetY,
                    targetZ,
                    resultPayload));
            if (sent)
            {
                LogReplicationInfo(
                    "Going Cooperative prioritised-object-work-v1 result sent requestId="
                    + requestId
                    + " worker="
                    + workerEntityId
                    + " targetHostId="
                    + targetHostId.ToString(CultureInfo.InvariantCulture)
                    + " family="
                    + targetFamily
                    + " policy="
                    + targetPolicy
                    + " source="
                    + source);
            }
            else
            {
                LogReplicationWarning(
                    "Going Cooperative prioritised-object-work-v1 result not sent requestId="
                    + requestId
                    + " source="
                    + source);
            }
        }

        private static bool TryApplyReplicationPrioritisedObjectWorkResultV1(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            detail = "prioritised-object-work-v1-result-rejected";
            if (!replicationConfigPrioritisedObjectWorkV1
                || replicationConfigHostMode
                || !LockstepCommandPayloads.TryReadPrioritisedObjectWorkResultV1Payload(
                    delta.Detail,
                    out var workerEntityId,
                    out var targetHostId,
                    out var targetEntityId,
                    out var targetFamily,
                    out var targetPolicy,
                    out var goalId,
                    out var requestId,
                    out var targetX,
                    out var targetY,
                    out var targetZ))
            {
                return false;
            }

            if (ReplicationAppliedPrioritisedObjectWorkResultRequestIds.Contains(
                    requestId))
            {
                detail = "prioritised-object-work-v1-result-duplicate requestId="
                    + requestId;
                return true;
            }

            if (!TryResolveReplicationPrioritisedObjectWorkGoal(
                    targetFamily,
                    out var expectedGoalId)
                || !string.Equals(goalId, expectedGoalId, StringComparison.Ordinal)
                || !IsReplicationPrioritisedObjectWorkPolicyAllowed(
                    targetFamily,
                    targetPolicy))
            {
                detail = "prioritised-object-work-v1-result-contract-mismatch requestId="
                    + requestId
                    + " family="
                    + targetFamily
                    + " goal="
                    + goalId;
                return false;
            }

            if (!TryResolveReplicationPrioritisedObjectWorkTarget(
                    targetHostId,
                    targetEntityId,
                    targetFamily,
                    targetX,
                    targetY,
                    targetZ,
                    out var target,
                    out var targetDetail)
                || target == null
                || !IsReplicationPrioritisedObjectWorkTargetCompatible(
                    targetFamily,
                    target))
            {
                detail = "prioritised-object-work-v1-result-target-missing requestId="
                    + requestId
                    + " targetHostId="
                    + targetHostId.ToString(CultureInfo.InvariantCulture)
                    + " family="
                    + targetFamily
                    + " "
                    + targetDetail;
                return false;
            }

            try
            {
                replicationPrioritisedObjectWorkApplyDepth++;
                BeginReplicationRegionOrderStateCaptureSuppression();
                TryApplyReplicationPrioritisedObjectWorkPresentationPolicy(
                    targetFamily,
                    targetPolicy,
                    target,
                    out var presentationDetail);
                RememberReplicationPrioritisedObjectWorkResultRequest(requestId);
                detail = "ok prioritised-object-work-v1-result requestId="
                    + requestId
                    + " worker="
                    + workerEntityId
                    + " targetHostId="
                    + targetHostId.ToString(CultureInfo.InvariantCulture)
                    + " family="
                    + targetFamily
                    + " policy="
                    + targetPolicy
                    + " "
                    + targetDetail
                    + " "
                    + presentationDetail;
                instance?.LogReplicationInfo(
                    "Going Cooperative prioritised-object-work-v1 result applied "
                    + detail);
                return true;
            }
            catch (Exception ex)
            {
                detail = "prioritised-object-work-v1-result-exception "
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                EndReplicationRegionOrderStateCaptureSuppression();
                replicationPrioritisedObjectWorkApplyDepth--;
            }
        }

        private static void TryApplyReplicationPrioritisedObjectWorkPresentationPolicy(
            string targetFamily,
            string targetPolicy,
            object target,
            out string detail)
        {
            if (string.Equals(
                    targetFamily,
                    "PrioritiseHarvestMenuItem",
                    StringComparison.Ordinal)
                || string.Equals(
                    targetFamily,
                    "PrioritiseChopMenuItem",
                    StringComparison.Ordinal)
                || string.Equals(
                    targetFamily,
                    "PrioritiseFishingMenuItem",
                    StringComparison.Ordinal))
            {
                var orderType =
                    AccessTools.TypeByName("NSMedieval.Types.OrderType");
                var setOrder = orderType == null
                    ? null
                    : AccessTools.Method(
                        target.GetType(),
                        "SetCurrentOrder",
                        new[] { orderType, typeof(bool) });
                if (orderType == null
                    || setOrder == null
                    || !Enum.IsDefined(orderType, targetPolicy))
                {
                    throw new MissingMethodException(
                        target.GetType().FullName,
                        "SetCurrentOrder");
                }

                setOrder.Invoke(
                    target,
                    new[] { Enum.Parse(orderType, targetPolicy), (object)true });
                var playerOrder =
                    AccessTools.Property(target.GetType(), "PlayerOrder");
                if (playerOrder != null && playerOrder.CanWrite)
                {
                    playerOrder.SetValue(target, true, null);
                }
                else
                {
                    AccessTools.Field(target.GetType(), "playerOrder")
                        ?.SetValue(target, true);
                }

                TryRefreshReplicationPrioritisedObjectWorkTargetView(target);
                detail = "presentation=order-icon order="
                    + targetPolicy
                    + " playerOrder=true";
                return;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseHaulingMenuItem",
                    StringComparison.Ordinal))
            {
                AccessTools.Property(target.GetType(), "IsForbidden")
                    ?.SetValue(target, false, null);
                TryRefreshReplicationPrioritisedObjectWorkTargetView(target);
                detail = "presentation=haul-unforbidden";
                return;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseStripMenuItem",
                    StringComparison.Ordinal))
            {
                AccessTools.Property(target.GetType(), "IsForbidden")
                    ?.SetValue(target, false, null);
                AccessTools.Method(
                        target.GetType(),
                        "MarkForStripping",
                        new[] { typeof(bool) })
                    ?.Invoke(target, new object[] { true });
                TryRefreshReplicationPrioritisedObjectWorkTargetView(target);
                detail = "presentation=strip-marked";
                return;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseBuildingDeConstructionMenuItem",
                    StringComparison.Ordinal))
            {
                AccessTools.Method(
                        target.GetType(),
                        "SetMarkedForDestruction",
                        new[] { typeof(bool) })
                    ?.Invoke(target, new object[] { true });
                TryRefreshReplicationPrioritisedObjectWorkTargetView(target);
                detail = "presentation=deconstruct-marked";
                return;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseBuildingUninstallMenuItem",
                    StringComparison.Ordinal))
            {
                var markedForMoving =
                    TryReadInstanceMemberValue(
                        target,
                        "MarkedForMoving",
                        out var moving)
                    && moving != null
                    && Convert.ToBoolean(moving, CultureInfo.InvariantCulture);
                AccessTools.Method(
                        target.GetType(),
                        "SetIsMarkedForUninstall",
                        new[] { typeof(bool) })
                    ?.Invoke(target, new object[] { !markedForMoving });
                TryRefreshReplicationPrioritisedObjectWorkTargetView(target);
                detail = "presentation=uninstall-marked value="
                    + (!markedForMoving ? "true" : "false");
                return;
            }

            TryRefreshReplicationPrioritisedObjectWorkTargetView(target);
            detail = "presentation=target-refreshed";
        }

        private static void TryRefreshReplicationPrioritisedObjectWorkTargetView(
            object target)
        {
            object? view = null;
            TryReadInstanceMemberValue(target, "View", out view);
            if (view == null)
            {
                TryInvokeReplicationObjectMethod(target, "GetView", out view);
            }
            if (view == null)
            {
                return;
            }

            TryInvokeReplicationObjectMethod(
                view,
                "OnOrderRefreshed",
                Array.Empty<object>(),
                out _);
            TryInvokeReplicationObjectMethod(
                view,
                "SetOrderIcon",
                Array.Empty<object>(),
                out _);
            TryInvokeReplicationObjectMethod(
                view,
                "RefreshSpecificView",
                Array.Empty<object>(),
                out _);
        }

        private static void RememberReplicationPrioritisedObjectWorkResultRequest(
            string requestId)
        {
            if (!ReplicationAppliedPrioritisedObjectWorkResultRequestIds.Add(
                    requestId))
            {
                return;
            }

            ReplicationAppliedPrioritisedObjectWorkResultRequestOrder.Enqueue(
                requestId);
            while (ReplicationAppliedPrioritisedObjectWorkResultRequestOrder.Count
                > ReplicationPrioritisedObjectWorkResultRetention)
            {
                ReplicationAppliedPrioritisedObjectWorkResultRequestIds.Remove(
                    ReplicationAppliedPrioritisedObjectWorkResultRequestOrder.Dequeue());
            }
        }

        private static bool IsReplicationPrioritisedObjectWorkPolicyAllowed(
            string family,
            string policy)
        {
            if (string.Equals(
                    family,
                    "PrioritiseHarvestMenuItem",
                    StringComparison.Ordinal))
            {
                return string.Equals(
                    policy,
                    "Harvesting",
                    StringComparison.Ordinal);
            }
            if (string.Equals(
                    family,
                    "PrioritiseChopMenuItem",
                    StringComparison.Ordinal))
            {
                return string.Equals(
                        policy,
                        "Chopping",
                        StringComparison.Ordinal)
                    || string.Equals(
                        policy,
                        "CutAllVegetation",
                        StringComparison.Ordinal);
            }
            if (string.Equals(
                    family,
                    "PrioritiseFishingMenuItem",
                    StringComparison.Ordinal))
            {
                return string.Equals(
                    policy,
                    "Fishing",
                    StringComparison.Ordinal);
            }
            return string.IsNullOrEmpty(policy);
        }

        private static bool IsReplicationPrioritisedObjectWorkTargetCompatible(
            string family,
            object target)
        {
            string expectedTypeName;
            switch (family)
            {
                case "PrioritiseHarvestMenuItem":
                case "PrioritiseChopMenuItem":
                    expectedTypeName =
                        "NSMedieval.State.PlantMapResourceInstance";
                    break;
                case "PrioritiseMineMenuItem":
                    expectedTypeName =
                        "NSMedieval.State.DigMarkerResourceInstance";
                    break;
                case "PrioritiseFishingMenuItem":
                    expectedTypeName =
                        "NSMedieval.State.FishMapResourceInstance";
                    break;
                case "PrioritiseHaulingMenuItem":
                    expectedTypeName =
                        "NSMedieval.State.ResourcePileInstance";
                    break;
                case "PrioritiseStripMenuItem":
                    expectedTypeName =
                        "NSMedieval.State.HumanCarcassPileInstance";
                    break;
                default:
                    expectedTypeName =
                        "NSMedieval.BuildingComponents.BaseBuildingInstance";
                    break;
            }
            var expectedType = AccessTools.TypeByName(expectedTypeName);
            return expectedType != null && expectedType.IsInstanceOfType(target);
        }

        private static bool TryResolveReplicationPrioritisedObjectWorkTarget(
            long targetHostId,
            string targetEntityId,
            string targetFamily,
            int targetX,
            int targetY,
            int targetZ,
            out object? target,
            out string detail)
        {
            target = null;
            if (targetHostId > 0L
                && TryGetReplicationLocalObjectByHostId(
                    targetHostId,
                    out var mapped,
                    out var mappedDetail)
                && mapped != null)
            {
                target = NormalizeReplicationPrioritisedObjectWorkTarget(mapped);
                detail = "source=host-id " + mappedDetail;
                return target != null;
            }

            if ((string.Equals(
                    targetFamily,
                    "PrioritiseHarvestMenuItem",
                    StringComparison.Ordinal)
                || string.Equals(
                    targetFamily,
                    "PrioritiseChopMenuItem",
                    StringComparison.Ordinal)
                || string.Equals(
                    targetFamily,
                    "PrioritiseMineMenuItem",
                    StringComparison.Ordinal)
                || string.Equals(
                    targetFamily,
                    "PrioritiseFishingMenuItem",
                    StringComparison.Ordinal))
                && TryFindReplicationMapResourceAt(
                    targetX,
                    targetY,
                    targetZ,
                    string.Empty,
                    targetHostId,
                    out var resource,
                    out var resourceDetail)
                && resource != null)
            {
                target = NormalizeReplicationPrioritisedObjectWorkTarget(resource);
                detail = "source=map-resource " + resourceDetail;
                return target != null;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseHaulingMenuItem",
                    StringComparison.Ordinal)
                || string.Equals(
                    targetFamily,
                    "PrioritiseStripMenuItem",
                    StringComparison.Ordinal))
            {
                var lookup = new ReplicationWorldObjectDelta(
                    0L,
                    0f,
                    "PrioritisedObjectWorkLookup",
                    targetHostId,
                    string.Empty,
                    targetX,
                    targetY,
                    targetZ,
                    string.Empty);
                if (TryFindReplicationResourcePile(
                        lookup,
                        out var pile,
                        out var pileDetail)
                    && pile != null)
                {
                    target = NormalizeReplicationPrioritisedObjectWorkTarget(pile);
                    detail = "source=resource-pile " + pileDetail;
                    return target != null;
                }
            }

            if (targetFamily.IndexOf(
                    "Building",
                    StringComparison.Ordinal) >= 0
                && TryFindReplicationBuildingBlueprintCandidate(
                    string.Empty,
                    targetX,
                    targetY,
                    targetZ,
                    out var building,
                    out var buildingDetail)
                && building != null)
            {
                target = NormalizeReplicationPrioritisedObjectWorkTarget(building);
                detail = "source=building " + buildingDetail;
                return target != null;
            }

            detail = "no-exact-target targetEntityId=" + targetEntityId
                + " grid=Vec3Int("
                + targetX.ToString(CultureInfo.InvariantCulture)
                + ","
                + targetY.ToString(CultureInfo.InvariantCulture)
                + ","
                + targetZ.ToString(CultureInfo.InvariantCulture)
                + ")";
            return false;
        }

        private static object? NormalizeReplicationPrioritisedObjectWorkTarget(
            object candidate)
        {
            try
            {
                var getAsTarget = candidate.GetType().GetMethod(
                    "GetAsTarget",
                    BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                return getAsTarget?.Invoke(candidate, null) ?? candidate;
            }
            catch
            {
                return candidate;
            }
        }

        private static void TryApplyReplicationPrioritisedObjectWorkPolicy(
            string targetFamily,
            string targetPolicy,
            object target,
            out string detail)
        {
            if (string.Equals(
                    targetFamily,
                    "PrioritiseHaulingMenuItem",
                    StringComparison.Ordinal))
            {
                AccessTools.Property(target.GetType(), "IsForbidden")
                    ?.SetValue(target, false, null);
                var reservationType =
                    AccessTools.TypeByName("NSMedieval.Manager.ReservationManager");
                var reservation = reservationType == null
                    ? null
                    : ResolveReplicationUnityManagerInstance(reservationType);
                FindReplicationMedicalCompatibleMethod(
                        reservation?.GetType() ?? typeof(object),
                        "ReleaseAll",
                        target)
                    ?.Invoke(reservation, new[] { target });
                detail = "policy=haul-unforbid-release";
                return;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseStripMenuItem",
                    StringComparison.Ordinal))
            {
                AccessTools.Property(target.GetType(), "IsForbidden")
                    ?.SetValue(target, false, null);
                AccessTools.Method(
                        target.GetType(),
                        "MarkForStripping",
                        new[] { typeof(bool) })
                    ?.Invoke(target, new object[] { true });
                detail = "policy=strip-marked-unforbid";
                return;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseBuildingDeConstructionMenuItem",
                    StringComparison.Ordinal))
            {
                AccessTools.Method(
                        target.GetType(),
                        "SetMarkedForDestruction",
                        new[] { typeof(bool) })
                    ?.Invoke(target, new object[] { true });
                detail = "policy=deconstruct-marked";
                return;
            }

            if (string.Equals(
                    targetFamily,
                    "PrioritiseBuildingUninstallMenuItem",
                    StringComparison.Ordinal))
            {
                var markedForMoving =
                    TryReadInstanceMemberValue(
                        target,
                        "MarkedForMoving",
                        out var moving)
                    && moving != null
                    && Convert.ToBoolean(moving, CultureInfo.InvariantCulture);
                AccessTools.Method(
                        target.GetType(),
                        "SetIsMarkedForUninstall",
                        new[] { typeof(bool) })
                    ?.Invoke(target, new object[] { !markedForMoving });
                var managerType = AccessTools.TypeByName(
                    "NSMedieval.Construction.ConstructablesGoapUninstallManager");
                var manager = managerType == null
                    ? null
                    : ResolveReplicationUnityManagerInstance(managerType);
                FindReplicationMedicalCompatibleMethod(
                        manager?.GetType() ?? typeof(object),
                        "AddToUninstallList",
                        target)
                    ?.Invoke(manager, new[] { target });
                detail = "policy=uninstall-marked";
                return;
            }

            var orderName = targetPolicy;
            if (string.IsNullOrEmpty(orderName))
            {
                detail = "policy=unchanged";
                return;
            }

            var orderType = AccessTools.TypeByName("NSMedieval.Types.OrderType");
            var setOrder = orderType == null
                ? null
                : AccessTools.Method(
                    target.GetType(),
                    "SetCurrentOrder",
                    new[] { orderType, typeof(bool) });
            if (orderType == null
                || setOrder == null
                || !Enum.IsDefined(orderType, orderName))
            {
                detail = "policy=order-surface-missing order=" + orderName;
                return;
            }

            setOrder.Invoke(
                target,
                new[] { Enum.Parse(orderType, orderName), (object)true });
            var playerOrder = AccessTools.Property(target.GetType(), "PlayerOrder");
            if (playerOrder != null && playerOrder.CanWrite)
            {
                playerOrder.SetValue(target, true, null);
            }
            else
            {
                AccessTools.Field(target.GetType(), "playerOrder")
                    ?.SetValue(target, true);
            }
            detail = "policy=set order=" + orderName + " playerOrder=true";
        }

        private static void UpdateReplicationPrioritisedObjectWorkV1()
        {
            if (!replicationConfigPrioritisedObjectWorkV1
                || !replicationConfigHostMode
                || ReplicationPendingPrioritisedObjectWork.Count == 0)
            {
                return;
            }

            for (var i = ReplicationPendingPrioritisedObjectWork.Count - 1;
                i >= 0;
                i--)
            {
                var pending = ReplicationPendingPrioritisedObjectWork[i];
                var versionChanged =
                    TryReadReplicationPrioritisedDestroyJobVersion(
                        pending.Worker,
                        out var currentVersion)
                    && currentVersion != pending.StartingJobVersion;
                if (!versionChanged
                    && Time.realtimeSinceStartup < pending.ExpiresRealtime)
                {
                    continue;
                }

                ReplicationPendingPrioritisedObjectWork.RemoveAt(i);
                if (!TryApplyReplicationPrioritisedObjectWorkV1(
                        pending.WorkerEntityId,
                        pending.TargetHostId,
                        pending.TargetEntityId,
                        pending.TargetFamily,
                        pending.TargetPolicy,
                        pending.GoalId,
                        pending.RequestId,
                        pending.TargetX,
                        pending.TargetY,
                        pending.TargetZ,
                        out var detail))
                {
                    instance?.LogReplicationWarning(
                        "Going Cooperative prioritised-object-work-v1 deferred apply failed "
                        + detail);
                }
            }
        }

        private static bool TryReadReplicationPrioritisedDestroyJobVersion(
            object worker,
            out uint version)
        {
            version = 0u;
            if (!TryReadInstanceMemberValue(worker, "Map", out var map)
                || map == null
                || !TryReadInstanceMemberValue(
                    map,
                    "BuildingsManagerMain",
                    out var buildings)
                || buildings == null
                || !TryReadInstanceMemberValue(
                    buildings,
                    "ConstructionJobManager",
                    out var construction)
                || construction == null
                || !TryReadInstanceMemberValue(
                    construction,
                    "DestroyVoxelManager",
                    out var destroy)
                || destroy == null
                || !TryReadInstanceMemberValue(
                    destroy,
                    "Version",
                    out var versionValue)
                || versionValue == null)
            {
                return false;
            }

            version = Convert.ToUInt32(
                versionValue,
                CultureInfo.InvariantCulture);
            return true;
        }

        private static void ResetReplicationPrioritisedObjectWorkV1State()
        {
            ReplicationPendingPrioritisedObjectWork.Clear();
            ReplicationAppliedPrioritisedObjectWorkResultRequestIds.Clear();
            ReplicationAppliedPrioritisedObjectWorkResultRequestOrder.Clear();
            replicationPrioritisedObjectWorkRequestSequence = 0L;
            replicationPrioritisedObjectWorkApplyDepth = 0;
            replicationPrioritisedObjectWorkSent = 0;
            replicationPrioritisedObjectWorkApplied = 0;
            replicationPrioritisedObjectWorkRejected = 0;
        }
    }
}
