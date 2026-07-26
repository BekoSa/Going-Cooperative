using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core.Replication;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationShelfStorageSlotKindV1 = "shelf-slot-v1";
        private static int replicationShelfStorageHostStoreDepth;
        private static long replicationShelfStorageSuppressedPileSpawns;

        private void TryInstallReplicationShelfStorageManifestHooks(Harmony harmonyInstance)
        {
            if ((!replicationConfigEnabled && !replicationConfigMultiplayerMenuEnabled)
                || (!replicationConfigShelfStorageManifestV1
                    && !ReplicationShelfStorageStateV2Enabled()))
            {
                return;
            }

            try
            {
                var universalStorageType = AccessTools.TypeByName(
                    "NSMedieval.StorageUniversal.UniversalStorage");
                var resourceInstanceType = AccessTools.TypeByName("NSMedieval.State.ResourceInstance");
                var storageSlotType = AccessTools.TypeByName(
                    "NSMedieval.StorageUniversal.StorageSlot");
                var storeResourcePile = universalStorageType == null
                    || resourceInstanceType == null
                    || storageSlotType == null
                    ? null
                    : AccessTools.Method(
                        universalStorageType,
                        "StoreResourcePile",
                        new[] { resourceInstanceType, storageSlotType });
                if (storeResourcePile == null)
                {
                    LogReplicationWarning(
                        "Going Cooperative shelf-storage manifest hook missing "
                        + "universalStorage=" + (universalStorageType != null)
                        + " resourceInstance=" + (resourceInstanceType != null)
                        + " storageSlot=" + (storageSlotType != null)
                        + " storeResourcePile=" + (storeResourcePile != null));
                    return;
                }

                harmonyInstance.Patch(
                    storeResourcePile,
                    prefix: new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                        nameof(ReplicationShelfStoreResourcePilePrefix),
                        BindingFlags.Static | BindingFlags.NonPublic)),
                    finalizer: new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                        nameof(ReplicationShelfStoreResourcePileFinalizer),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                LogReplicationInfo(
                    "Going Cooperative shelf-storage manifest native store hook patched");
            }
            catch (Exception ex)
            {
                LogReplicationWarning(
                    "Going Cooperative shelf-storage manifest hook failed "
                    + FormatReflectionExceptionDetail(ex));
            }
        }

        private static void ReplicationShelfStoreResourcePilePrefix(
            object __instance,
            out bool __state)
        {
            __state = false;
            if ((!replicationConfigShelfStorageManifestV1
                    && !ReplicationShelfStorageStateV2Enabled())
                || !replicationConfigEnabled
                || !replicationConfigHostMode
                || __instance == null
                || !TryReadInstanceMemberValue(__instance, "GetOwner", out var owner)
                || owner == null
                || !string.Equals(
                    owner.GetType().FullName,
                    "NSMedieval.BuildingComponents.ShelfComponentInstance",
                    StringComparison.Ordinal))
            {
                return;
            }

            replicationShelfStorageHostStoreDepth++;
            __state = true;
        }

        private static Exception? ReplicationShelfStoreResourcePileFinalizer(
            Exception? __exception,
            bool __state)
        {
            if (__state)
            {
                replicationShelfStorageHostStoreDepth =
                    Math.Max(0, replicationShelfStorageHostStoreDepth - 1);
            }

            return __exception;
        }

        private static bool ShouldSuppressReplicationShelfStorePileSpawn()
        {
            return (replicationConfigShelfStorageManifestV1
                    || ReplicationShelfStorageStateV2Enabled())
                && replicationConfigEnabled
                && replicationConfigHostMode
                && replicationShelfStorageHostStoreDepth > 0;
        }

        // This lane intentionally walks StorageCommonManager's live registry. It does
        // not scan Unity's object heap, and the shared resource-container cadence
        // bounds it to twice per second. Every row is one physical shelf slot so the
        // client can use the game's own slot-aware placement transaction.
        private static void CollectReplicationShelfStorageManifestV1(
            List<ReplicationResourceContainerState> states,
            ref int count)
        {
            if (!replicationConfigShelfStorageManifestV1
                && !ReplicationShelfStorageStateV2Enabled())
            {
                return;
            }

            var managerType = AccessTools.TypeByName("NSMedieval.StorageUniversal.StorageCommonManager");
            var manager = managerType == null
                ? null
                : AccessTools.Property(managerType, "Instance")?.GetValue(null, null);
            var allStorages = manager == null || managerType == null
                ? null
                : AccessTools.Property(managerType, "AllStorages")?.GetValue(manager, null) as IEnumerable;
            if (allStorages == null)
            {
                return;
            }

            var visitedOwners = new HashSet<object>(ReferenceObjectComparer.Instance);
            foreach (var candidate in allStorages)
            {
                if (candidate == null
                    || !TryCreateReplicationStoragePolicyTargetReference(
                        candidate, out var target, out var owner, out _)
                    || !string.Equals(target.Kind, ReplicationStoragePolicyShelfKind, StringComparison.Ordinal)
                    || !visitedOwners.Add(owner)
                    || !TryReadInstanceMemberValue(owner, "AllStorage", out var allStorageRaw)
                    || !(allStorageRaw is IList universalStorages))
                {
                    continue;
                }

                for (var storageIndex = 0; storageIndex < universalStorages.Count; storageIndex++)
                {
                    var universalStorage = universalStorages[storageIndex];
                    if (universalStorage == null
                        || !TryReadInstanceMemberValue(universalStorage, "StorageSlots", out var slotsRaw)
                        || !(slotsRaw is IList slots))
                    {
                        continue;
                    }

                    for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
                    {
                        var entries = new List<ReplicationResourceContainerEntry>(1);
                        var slot = slots[slotIndex];
                        if (slot != null
                            && TryReadInstanceMemberValue(slot, "Pile", out var pile)
                            && pile != null
                            && TryGetReplicationPileStoredResource(pile, out var resourceInstance, out _)
                            && resourceInstance != null
                            && ((TryReadReplicationWorldObjectStringMember(
                                        resourceInstance,
                                        "BlueprintId",
                                        "blueprintId",
                                        out var blueprintId)
                                    && !string.IsNullOrWhiteSpace(blueprintId))
                                || (TryExtractReplicationResourceId(resourceInstance, out blueprintId)
                                    && !string.IsNullOrWhiteSpace(blueprintId)))
                            && TryReadReplicationWorldObjectIntMember(resourceInstance, "Amount", "amount", out var amount)
                            && amount > 0)
                        {
                            entries.Add(new ReplicationResourceContainerEntry(blueprintId, amount));
                        }

                        states.Add(new ReplicationResourceContainerState(
                            "shelf-slot:"
                                + target.HostUid.ToString(CultureInfo.InvariantCulture) + ":"
                                + target.ComponentOrdinal.ToString(CultureInfo.InvariantCulture) + ":"
                                + storageIndex.ToString(CultureInfo.InvariantCulture) + ":"
                                + slotIndex.ToString(CultureInfo.InvariantCulture) + ":0",
                            ReplicationShelfStorageSlotKindV1,
                            target.BlueprintFingerprint,
                            0L,
                            target.AnchorX,
                            target.AnchorY,
                            target.AnchorZ,
                            entries));
                        count++;
                    }
                }
            }
        }

        private static bool TryApplyReplicationShelfStorageSlotV1(
            ReplicationResourceContainerState state,
            out string detail)
        {
            if (!replicationConfigShelfStorageManifestV1
                && !ReplicationShelfStorageStateV2Enabled())
            {
                detail = "shelf-storage-manifest-gated-off";
                return true;
            }

            var parts = state.ContainerId.Split(':');
            if (parts.Length != 6
                || !string.Equals(parts[0], "shelf-slot", StringComparison.Ordinal)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostUid)
                || hostUid <= 0L
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var componentOrdinal)
                || componentOrdinal < 0
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var storageIndex)
                || storageIndex < 0
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotIndex)
                || slotIndex < 0)
            {
                detail = "shelf-storage-manifest-id-invalid id=" + state.ContainerId;
                return false;
            }

            // The final token is reserved for a future per-slot topology generation.
            // V1 emits zero; accepting only that value makes later schema evolution
            // fail closed instead of silently addressing the wrong slot.
            if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var topologyGeneration)
                || topologyGeneration != 0)
            {
                detail = "shelf-storage-manifest-topology-unsupported id=" + state.ContainerId;
                return false;
            }

            var requested = new ReplicationStoragePolicyTargetReference
            {
                Kind = ReplicationStoragePolicyShelfKind,
                HostUid = hostUid,
                Canonical = true,
                ComponentOrdinal = componentOrdinal,
                BlueprintFingerprint = state.OwnerId,
                AnchorX = state.GridX,
                AnchorY = state.GridY,
                AnchorZ = state.GridZ
            };
            if (!TryResolveReplicationStoragePolicyTarget(
                    requested,
                    out var owner,
                    out _,
                    out var resolveDetail)
                || owner == null
                || !TryReadInstanceMemberValue(owner, "AllStorage", out var allStorageRaw)
                || !(allStorageRaw is IList universalStorages)
                || storageIndex >= universalStorages.Count
                || universalStorages[storageIndex] == null)
            {
                detail = "shelf-storage-manifest-target-pending " + resolveDetail;
                return false;
            }

            var universalStorage = universalStorages[storageIndex]!;
            if (!TryReadInstanceMemberValue(universalStorage, "StorageSlots", out var slotsRaw)
                || !(slotsRaw is IList slots)
                || slotIndex >= slots.Count
                || slots[slotIndex] == null)
            {
                detail = "shelf-storage-manifest-slot-pending storage="
                    + storageIndex.ToString(CultureInfo.InvariantCulture)
                    + " slot=" + slotIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (state.Entries.Count > 1)
            {
                detail = "shelf-storage-manifest-multiple-resources-in-slot";
                return false;
            }

            var slot = slots[slotIndex]!;
            TryReadInstanceMemberValue(slot, "Pile", out var currentPile);
            var desiredBlueprint = state.Entries.Count == 1 ? state.Entries[0].BlueprintId : string.Empty;
            var desiredAmount = state.Entries.Count == 1 ? state.Entries[0].Amount : 0;
            var coordinateCleanupDetail = "coordinate-copy=not-applicable";
            if (desiredAmount > 0
                && !string.IsNullOrWhiteSpace(desiredBlueprint)
                && !TryCleanupReplicationShelfCoordinatePile(
                    desiredBlueprint,
                    desiredAmount,
                    state.GridX,
                    state.GridY,
                    state.GridZ,
                    out coordinateCleanupDetail))
            {
                detail = "shelf-storage-manifest-coordinate-cleanup-failed "
                    + coordinateCleanupDetail;
                return false;
            }

            if (currentPile != null
                && TryGetReplicationPileStoredResource(currentPile, out var currentResource, out _)
                && currentResource != null
                && TryExtractReplicationResourceId(currentResource, out var currentBlueprint)
                && TryReadReplicationWorldObjectIntMember(currentResource, "Amount", "amount", out var currentAmount)
                && string.Equals(currentBlueprint, desiredBlueprint, StringComparison.Ordinal)
                && currentAmount == desiredAmount)
            {
                detail = "ok shelf-slot-unchanged " + coordinateCleanupDetail;
                return true;
            }

            if (currentPile != null)
            {
                TryInvokeReplicationObjectVoidMethod(currentPile, "Dispose", out _);
                var clearMethod = AccessTools.Method(slot.GetType(), "SetStoredPile");
                if (clearMethod != null)
                {
                    clearMethod.Invoke(slot, new object?[] { null });
                }
            }

            if (desiredAmount <= 0 || string.IsNullOrWhiteSpace(desiredBlueprint))
            {
                detail = "ok shelf-slot-cleared";
                return true;
            }

            if (!TryResolveReplicationResourceModel(desiredBlueprint, out var resource, out var resourceDetail)
                || resource == null)
            {
                detail = "shelf-storage-manifest-resource-failed " + resourceDetail;
                return false;
            }
            if (!TryCreateReplicationResourceInstance(resource, desiredAmount, out var resourceInstance, out var instanceDetail)
                || resourceInstance == null)
            {
                detail = "shelf-storage-manifest-resource-instance-failed " + instanceDetail;
                return false;
            }

            MethodInfo? storeMethod = null;
            var methods = universalStorage.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < methods.Length; i++)
            {
                var parameters = methods[i].GetParameters();
                if (string.Equals(methods[i].Name, "StoreResourcePile", StringComparison.Ordinal)
                    && parameters.Length == 2
                    && parameters[0].ParameterType.IsInstanceOfType(resourceInstance)
                    && parameters[1].ParameterType.IsInstanceOfType(slot))
                {
                    storeMethod = methods[i];
                    break;
                }
            }
            if (storeMethod == null)
            {
                detail = "shelf-storage-manifest-native-store-missing";
                return false;
            }

            try
            {
                var stored = Convert.ToInt32(
                    storeMethod.Invoke(universalStorage, new[] { resourceInstance, slot }),
                    CultureInfo.InvariantCulture);
                if (stored != desiredAmount)
                {
                    detail = "shelf-storage-manifest-native-store-partial desired="
                        + desiredAmount.ToString(CultureInfo.InvariantCulture)
                        + " stored=" + stored.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                detail = "ok shelf-slot-stored blueprint=" + desiredBlueprint
                    + " amount=" + desiredAmount.ToString(CultureInfo.InvariantCulture)
                    + " " + coordinateCleanupDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "shelf-storage-manifest-native-store-threw " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryCleanupReplicationShelfCoordinatePile(
            string blueprintId,
            int amount,
            int gridX,
            int gridY,
            int gridZ,
            out string detail)
        {
            object? match = null;
            var matching = 0;
            List<object>? stale = null;
            foreach (var candidate in ReplicationClientGenericSpawnedResourcePiles)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (!TryReadReplicationWorldObjectGridPosition(
                        candidate, out var candidateX, out var candidateY, out var candidateZ)
                    || !TryGetReplicationPileStoredResource(
                        candidate, out var candidateResource, out _)
                    || candidateResource == null)
                {
                    stale ??= new List<object>();
                    stale.Add(candidate);
                    continue;
                }

                if (candidateX != gridX
                    || candidateY != gridY
                    || candidateZ != gridZ
                    || IsReplicationPileStoredOnShelf(candidate)
                    || !TryExtractReplicationResourceId(candidateResource, out var candidateBlueprint)
                    || !string.Equals(candidateBlueprint, blueprintId, StringComparison.Ordinal)
                    || !TryReadReplicationWorldObjectIntMember(
                        candidateResource, "Amount", "amount", out var candidateAmount)
                    || candidateAmount != amount)
                {
                    continue;
                }

                match = candidate;
                matching++;
            }

            if (stale != null)
            {
                for (var i = 0; i < stale.Count; i++)
                {
                    ReplicationClientGenericSpawnedResourcePiles.Remove(stale[i]);
                }
            }

            if (matching == 0 || match == null)
            {
                detail = "coordinate-copy=none";
                return true;
            }

            if (matching != 1)
            {
                detail = "coordinate-copy=ambiguous matches="
                    + matching.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (!TryDisposeReplicationResourcePile(
                    match,
                    "shelf-slot-recovery",
                    out var disposeDetail))
            {
                detail = "coordinate-copy=dispose-failed "
                    + FormatReplicationWorldObjectDetailToken(disposeDetail);
                return false;
            }

            ReplicationClientGenericSpawnedResourcePiles.Remove(match);
            detail = "coordinate-copy=removed "
                + FormatReplicationWorldObjectDetailToken(disposeDetail);
            return true;
        }

        private static bool IsReplicationPileStoredOnShelf(object pile)
        {
            if ((!replicationConfigShelfStorageManifestV1
                    && !ReplicationShelfStorageStateV2Enabled())
                || !TryReadInstanceMemberValue(pile, "InstanceStorage", out var universalStorage)
                || universalStorage == null
                || !TryReadInstanceMemberValue(universalStorage, "GetOwner", out var owner)
                || owner == null)
            {
                return false;
            }

            return string.Equals(
                owner.GetType().FullName,
                "NSMedieval.BuildingComponents.ShelfComponentInstance",
                StringComparison.Ordinal);
        }
    }
}
