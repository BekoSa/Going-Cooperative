using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        // Host-local mass Cancel invokes several intermediate lifecycle methods per
        // building before the terminal BuildingCanceled/DestroyBuilding edge. Those
        // intermediate rows are redundant with the authoritative region operation and
        // used to flood the client with hundreds of reliable BuildingLifecycleV2 rows.
        // Install a narrow guard without changing the existing terminal safety net.
        private static readonly bool ReplicationMassCancelPerfHotfixInstalled =
            InstallReplicationMassCancelPerfHotfix();

        [ThreadStatic]
        private static int replicationMassCancelLifecycleSuppressionDepth;

        private static bool InstallReplicationMassCancelPerfHotfix()
        {
            try
            {
                var harmony = new Harmony("GoingCooperative.ReplicationMassCancelPerfHotfix");
                var buildingType = AccessTools.TypeByName(
                    "NSMedieval.BuildingComponents.BaseBuildingInstance");
                if (buildingType == null)
                {
                    return false;
                }

                var prefix = new HarmonyMethod(
                    typeof(GoingCooperativePlugin).GetMethod(
                        nameof(ReplicationMassCancelLifecycleHotfixPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic))
                {
                    priority = Priority.First
                };
                var finalizer = new HarmonyMethod(
                    typeof(GoingCooperativePlugin).GetMethod(
                        nameof(ReplicationMassCancelLifecycleHotfixFinalizer),
                        BindingFlags.Static | BindingFlags.NonPublic))
                {
                    priority = Priority.Last
                };

                // Terminal methods deliberately remain untouched. They still flow
                // through BuildingLifecycleV2, so exact IDs are queued and collapsed
                // into BuildingTerminalBatchV2 by the existing semantic-removal path.
                var methodNames = new[]
                {
                    "ConstructionStarted",
                    "ConstructionPaused",
                    "ConstructionFailed",
                    "EnterFoundationState",
                    "ConstructionCompleted",
                    "EnterFinishedState",
                    "SetConstructionPhase",
                    "SetMarkedForDestruction"
                };
                var methods = buildingType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (var nameIndex = 0; nameIndex < methodNames.Length; nameIndex++)
                {
                    for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                    {
                        if (!string.Equals(
                                methods[methodIndex].Name,
                                methodNames[nameIndex],
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        harmony.Patch(
                            methods[methodIndex],
                            prefix: prefix,
                            finalizer: finalizer);
                    }
                }

                return true;
            }
            catch
            {
                // If a future game/Harmony version changes one of these surfaces,
                // keep the existing replication path rather than fail plugin startup.
                return false;
            }
        }

        private static void ReplicationMassCancelLifecycleHotfixPrefix(
            out ReplicationBuildCaptureTransaction? __state)
        {
            __state = null;
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationRegionSelectionActionDepth <= 0
                || replicationRegionSelectionActionFrame != Time.frameCount
                || !replicationHostLocalSemanticRegionArmed
                || replicationHostLocalSemanticRegionFrame != Time.frameCount
                || !string.Equals(
                    replicationHostLocalSemanticRegionOrderType,
                    "Cancel",
                    StringComparison.Ordinal)
                || replicationActiveBuildCaptureTransaction != null
                || replicationActiveAuthoritativeBuildApplyCapture != null)
            {
                return;
            }

            // BuildingLifecycleV2 treats an active build transaction as a capture
            // gate. Hold a private sentinel only around the intermediate native call.
            // BuildingCanceled/DestroyBuilding are intentionally not patched, so the
            // terminal exact-ID safety net remains fully durable.
            var sentinel = new ReplicationBuildCaptureTransaction(
                ++replicationBuildCaptureTransactionSequence);
            replicationActiveBuildCaptureTransaction = sentinel;
            replicationMassCancelLifecycleSuppressionDepth++;
            __state = sentinel;
        }

        private static Exception? ReplicationMassCancelLifecycleHotfixFinalizer(
            Exception? __exception,
            ReplicationBuildCaptureTransaction? __state)
        {
            if (__state != null
                && ReferenceEquals(replicationActiveBuildCaptureTransaction, __state))
            {
                replicationActiveBuildCaptureTransaction = null;
                replicationMassCancelLifecycleSuppressionDepth = Math.Max(
                    0,
                    replicationMassCancelLifecycleSuppressionDepth - 1);
            }

            return __exception;
        }
    }
}
