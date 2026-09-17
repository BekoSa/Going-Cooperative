using System;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private int TryInstallReplicationClientRegionReplayOptimization(
            Harmony harmonyInstance)
        {
            var pluginType = typeof(GoingCooperativePlugin);
            var predicate = pluginType.GetMethod(
                "IsReplicationClientTiledRegionOrder",
                BindingFlags.Static | BindingFlags.NonPublic);
            var scheduler = pluginType.GetMethod(
                "ScheduleReplicationClientMassBuildingRegionReplay",
                BindingFlags.Static | BindingFlags.NonPublic);
            var predicatePrefix = pluginType.GetMethod(
                nameof(ReplicationClientDeferredRegionPredicatePrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            var schedulerPrefix = pluginType.GetMethod(
                nameof(ReplicationClientSingleRegionReplaySchedulePrefix),
                BindingFlags.Static | BindingFlags.NonPublic);

            if (predicate == null
                || scheduler == null
                || predicatePrefix == null
                || schedulerPrefix == null)
            {
                LogReplicationWarning(
                    "Going Cooperative client region replay optimization unavailable predicate="
                    + (predicate != null)
                    + " scheduler="
                    + (scheduler != null)
                    + " predicatePrefix="
                    + (predicatePrefix != null)
                    + " schedulerPrefix="
                    + (schedulerPrefix != null));
                return 0;
            }

            harmonyInstance.Patch(
                predicate,
                prefix: new HarmonyMethod(predicatePrefix));
            harmonyInstance.Patch(
                scheduler,
                prefix: new HarmonyMethod(schedulerPrefix));
            LogReplicationInfo(
                "Going Cooperative client region replay optimization installed mode=single-native-first fallback=split-on-failure");
            return 2;
        }

        private static bool ReplicationClientDeferredRegionPredicatePrefix(
            ReplicationRegionOrderState state,
            ref bool __result)
        {
            if (state != null
                && ReplicationOrderingPolicy.ShouldStartRegionReplayWithSingleNativeAction(
                    state.OrderType))
            {
                // Region replay must not execute a potentially expensive native
                // SelectionManager action inside the transport pump. Route even a
                // 1x1 chop through the existing deferred replay queue.
                __result = true;
                return false;
            }

            return true;
        }

        private static bool ReplicationClientSingleRegionReplaySchedulePrefix(
            ReplicationRegionOrderState state,
            ref string detail,
            ref bool __result)
        {
            if (state == null
                || replicationConfigHostMode
                || !ReplicationOrderingPolicy.ShouldStartRegionReplayWithSingleNativeAction(
                    state.OrderType))
            {
                return true;
            }

            var minX = Math.Min(state.StartX, state.EndX);
            var maxX = Math.Max(state.StartX, state.EndX);
            var minY = Math.Min(state.StartY, state.EndY);
            var maxY = Math.Max(state.StartY, state.EndY);
            var minZ = Math.Min(state.StartZ, state.EndZ);
            var maxZ = Math.Max(state.StartZ, state.EndZ);
            var width = checked(maxX - minX + 1);
            var depth = checked(maxZ - minZ + 1);

            // SelectionManager.OnOrderCancel/OnOrderDeconstruction/OnChopOrder has
            // a large fixed cost per invocation. The previous 6x6 tiling multiplied
            // that cost (for example, an 18x32 cancel became 18 native calls). Try
            // the exact authoritative region once. The existing replay processor
            // still bisects this tile if the native call actually fails, preserving
            // the fail-closed safety net without paying the split cost on success.
            var replay = new ReplicationPendingMassBuildingRegionReplay(
                state.Sequence,
                state.OrderType,
                1);
            ReplicationPendingClientMassBuildingRegionTiles.Enqueue(
                new ReplicationPendingMassBuildingRegionTile(
                    replay,
                    minX,
                    minY,
                    minZ,
                    maxX,
                    maxY,
                    maxZ));

            detail = "scheduled tiles=1 mode=single-native-fallback-split region="
                + width.ToString(CultureInfo.InvariantCulture)
                + "x"
                + depth.ToString(CultureInfo.InvariantCulture);
            __result = true;
            return false;
        }
    }
}
