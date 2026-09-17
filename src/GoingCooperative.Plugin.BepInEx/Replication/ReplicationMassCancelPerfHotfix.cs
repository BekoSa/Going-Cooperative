using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        // This patch is intentionally self-installing. It is isolated from the main
        // replication bootstrap so it can protect the hot native Cancel path without
        // changing the ordering of the existing region/building Harmony patches.
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
                // through BuildingLifecycleV2 and are collected into the exact-ID
                // terminal safety net. Only intermediate state churn is suppressed.
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
                for (var nameIndex = 0; nameIndex < methodNames.Length; nameIndex++)
                {
                    var methods = buildingType.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

                var scheduleMethod = typeof(GoingCooperativePlugin).GetMethod(
                    "ScheduleReplicationClientMassBuildingRegionReplay",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (scheduleMethod != null)
                {
                    var schedulePrefix = new HarmonyMethod(
                        typeof(GoingCooperativePlugin).GetMethod(
                            nameof(ReplicationMassCancelScheduleHotfixPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic))
                    {
                        priority = Priority.First
                    };
                    harmony.Patch(scheduleMethod, prefix: schedulePrefix);
                }

                return true;
            }
            catch
            {
                // The normal replication implementation remains functional if a
                // future game/Harmony version changes one of these private surfaces.
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

            // BuildingLifecycleV2 uses an active build capture as a capture gate.
            // Hold a private sentinel only across this one intermediate native call.
            // Terminal BuildingCanceled/DestroyBuilding methods are not patched and
            // therefore still generate the exact removal safety net.
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

        private static bool ReplicationMassCancelScheduleHotfixPrefix(
            ReplicationRegionOrderState state,
            ref bool __result,
            ref string detail)
        {
            // Let the original scheduler handle Chopping and any future region type.
            // Cancel/Deconstruct are the expensive building-selection paths where a
            // vanilla 6x6 call measured 76-115 ms on the client.
            if (replicationConfigHostMode
                || (!string.Equals(state.OrderType, "Cancel", StringComparison.Ordinal)
                    && !string.Equals(
                        state.OrderType,
                        "Deconstruct",
                        StringComparison.Ordinal)))
            {
                return true;
            }

            var minX = Math.Min(state.StartX, state.EndX);
            var maxX = Math.Max(state.StartX, state.EndX);
            var minZ = Math.Min(state.StartZ, state.EndZ);
            var maxZ = Math.Max(state.StartZ, state.EndZ);
            var width = checked(maxX - minX + 1);
            var depth = checked(maxZ - minZ + 1);

            // 2x2 keeps each native SelectionManager call small enough to avoid the
            // 80-115 ms single-frame stalls seen with the old 6x6 tiles. For very
            // large selections grow only as much as required to respect the existing
            // bounded queue limit.
            var tileSpan = 2;
            while ((long)((width + tileSpan - 1) / tileSpan)
                    * ((depth + tileSpan - 1) / tileSpan)
                > ReplicationClientMassBuildingRegionMaxInitialTiles)
            {
                tileSpan = checked(tileSpan * 2);
            }

            var tilesX = (width + tileSpan - 1) / tileSpan;
            var tilesZ = (depth + tileSpan - 1) / tileSpan;
            var initialTileCount = checked(tilesX * tilesZ);
            var replay = new ReplicationPendingMassBuildingRegionReplay(
                state.Sequence,
                state.OrderType,
                initialTileCount);

            for (var z = minZ; z <= maxZ; z = checked(z + tileSpan))
            {
                var tileEndZ = Math.Min(maxZ, checked(z + tileSpan - 1));
                for (var x = minX; x <= maxX; x = checked(x + tileSpan))
                {
                    var tileEndX = Math.Min(maxX, checked(x + tileSpan - 1));
                    ReplicationPendingClientMassBuildingRegionTiles.Enqueue(
                        new ReplicationPendingMassBuildingRegionTile(
                            replay,
                            x,
                            Math.Min(state.StartY, state.EndY),
                            z,
                            tileEndX,
                            Math.Max(state.StartY, state.EndY),
                            tileEndZ));
                }
            }

            detail = "scheduled-hotfix tiles="
                + initialTileCount.ToString(CultureInfo.InvariantCulture)
                + " tileSpan="
                + tileSpan.ToString(CultureInfo.InvariantCulture)
                + " region="
                + width.ToString(CultureInfo.InvariantCulture)
                + "x"
                + depth.ToString(CultureInfo.InvariantCulture);
            __result = true;
            return false;
        }
    }
}
