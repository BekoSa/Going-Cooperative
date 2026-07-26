using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const float ReplicationResourceStateV2RecoverySeconds = 20f;
        private const float ReplicationResourceStateV2RecoveryAuditIntervalSeconds = 0.10f;
        private const float ReplicationResourceStateV2DiagnosticsSeconds = 10f;
        private const int ReplicationResourceStateV2BootstrapBudget = 8;
        private const int ReplicationResourceStateV2DirtyBudget = 8;
        private const int ReplicationResourceStateV2RecoveryBudget = 1;

        private sealed class ReplicationAgentContainerDescriptorV2
        {
            public string ContainerId = string.Empty;
            public string ContainerKind = string.Empty;
            public string EntityId = string.Empty;
            public object Storage = null!;
            public WeakReference Owner = null!;
        }

        private sealed class ReplicationGroundPileDescriptorV2
        {
            public string ContainerId = string.Empty;
            public string BlueprintId = string.Empty;
            public int GridX;
            public int GridY;
            public int GridZ;
            public WeakReference Pile = null!;
        }

        private sealed class ReplicationShelfSlotDescriptorV2
        {
            public string ContainerId = string.Empty;
            public object UniversalStorage = null!;
            public object Slot = null!;
            public int StorageIndex;
            public int SlotIndex;
            public long HostUid;
            public int ComponentOrdinal;
            public string BlueprintFingerprint = string.Empty;
            public int GridX;
            public int GridY;
            public int GridZ;
        }

        private static readonly Dictionary<object, string> ReplicationAgentContainerIdByStorageV2 =
            new Dictionary<object, string>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, ReplicationAgentContainerDescriptorV2> ReplicationAgentContainersV2 =
            new Dictionary<string, ReplicationAgentContainerDescriptorV2>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<string>> ReplicationAgentContainerIdsByEntityV2 =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly List<string> ReplicationAgentContainerKnownOrderV2 = new List<string>();
        private static readonly Queue<string> ReplicationAgentContainerDirtyQueueV2 = new Queue<string>();
        private static readonly HashSet<string> ReplicationAgentContainerDirtySetV2 =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationAgentContainerForcedSetV2 =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<object, string> ReplicationGroundPileIdByObjectV2 =
            new Dictionary<object, string>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, ReplicationGroundPileDescriptorV2> ReplicationGroundPilesV2 =
            new Dictionary<string, ReplicationGroundPileDescriptorV2>(StringComparer.Ordinal);
        private static readonly List<string> ReplicationGroundPileKnownOrderV2 = new List<string>();
        private static readonly Queue<string> ReplicationGroundPileDirtyQueueV2 = new Queue<string>();
        private static readonly HashSet<string> ReplicationGroundPileDirtySetV2 =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationGroundPileForcedSetV2 =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<object, List<string>> ReplicationShelfSlotIdsByUniversalStorageV2 =
            new Dictionary<object, List<string>>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, ReplicationShelfSlotDescriptorV2> ReplicationShelfSlotsV2 =
            new Dictionary<string, ReplicationShelfSlotDescriptorV2>(StringComparer.Ordinal);
        private static readonly List<string> ReplicationShelfSlotKnownOrderV2 = new List<string>();
        private static readonly Queue<string> ReplicationShelfSlotDirtyQueueV2 = new Queue<string>();
        private static readonly HashSet<string> ReplicationShelfSlotDirtySetV2 =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationShelfSlotForcedSetV2 =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<ReplicationResourceContainerState> ReplicationResourceStateChangedScratchV2 =
            new List<ReplicationResourceContainerState>(ReplicationResourceStateV2DirtyBudget * 3);
        private static readonly Action<string, bool> MarkReplicationAgentContainerDirtyActionV2 =
            MarkReplicationAgentContainerDirtyV2;
        private static readonly Action<string, bool> MarkReplicationGroundPileDirtyActionV2 =
            MarkReplicationGroundPileDirtyV2;
        private static readonly Action<string, bool> MarkReplicationShelfSlotDirtyActionV2 =
            MarkReplicationShelfSlotDirtyV2;

        private static UnityEngine.Object[] replicationResourceStateAgentBootstrapViewsV2 =
            Array.Empty<UnityEngine.Object>();
        private static int replicationResourceStateAgentBootstrapIndexV2;
        private static bool replicationResourceStateAgentBootstrapStartedV2;
        private static bool replicationResourceStateAgentBootstrapCompleteV2;
        private static IList? replicationResourceStatePileBootstrapListV2;
        private static int replicationResourceStatePileBootstrapIndexV2;
        private static bool replicationResourceStatePileBootstrapCompleteV2;
        private static List<object>? replicationResourceStateShelfBootstrapV2;
        private static int replicationResourceStateShelfBootstrapIndexV2;
        private static bool replicationResourceStateShelfBootstrapCompleteV2;

        private static float replicationResourceStateNextRecoveryRealtimeV2;
        private static float replicationResourceStateNextRecoveryAuditRealtimeV2;
        private static bool replicationResourceStateRecoveryCycleActiveV2;
        private static int replicationResourceStateRecoveryDomainCursorV2;
        private static int replicationResourceStateAgentRecoveryCursorV2;
        private static int replicationResourceStateAgentRecoveryRemainingV2;
        private static int replicationResourceStatePileRecoveryCursorV2;
        private static int replicationResourceStatePileRecoveryRemainingV2;
        private static int replicationResourceStateShelfRecoveryCursorV2;
        private static int replicationResourceStateShelfRecoveryRemainingV2;
        private static float replicationResourceStateNextDiagnosticsRealtimeV2;
        private static long replicationResourceStateDirtyMarksV2;
        private static long replicationResourceStateCoalescedMarksV2;
        private static long replicationResourceStateRowsReadV2;
        private static long replicationResourceStateRowsSentV2;
        private static long replicationResourceStateUnchangedRowsSuppressedV2;
        private static long replicationResourceStateBatchesSentV2;
        private static int replicationResourceStateDirtyQueueMaxV2;
        private static double replicationResourceStateDrainMillisecondsV2;

        private static bool ReplicationAgentInventoryStateV2Enabled()
        {
            return replicationConfigResourceStateV2
                && replicationConfigAgentInventoryStateV2;
        }

        private static bool ReplicationGroundPileStateV2Enabled()
        {
            return replicationConfigResourceStateV2
                && replicationConfigGroundPileStateV2;
        }

        private static bool ReplicationShelfStorageStateV2Enabled()
        {
            return replicationConfigResourceStateV2
                && replicationConfigShelfStorageStateV2;
        }

        private static bool AnyReplicationResourceStateV2Enabled()
        {
            return ReplicationAgentInventoryStateV2Enabled()
                || ReplicationGroundPileStateV2Enabled()
                || ReplicationShelfStorageStateV2Enabled();
        }

        private void TryInstallReplicationResourceStateV2Hooks(Harmony harmony)
        {
            if (!replicationConfigResourceStateV2)
            {
                return;
            }

            var patched = 0;
            if (ReplicationAgentInventoryStateV2Enabled())
            {
                var viewSetupPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                    nameof(ReplicationResourceStateAgentViewSetupPostfixV2),
                    BindingFlags.Static | BindingFlags.NonPublic));
                var viewDisposePrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                    nameof(ReplicationResourceStateAgentViewDisposePrefixV2),
                    BindingFlags.Static | BindingFlags.NonPublic));
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony, "NSMedieval.View.WorkerView", viewSetupPostfix, null, "Setup");
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony, "NSMedieval.View.Animals.AnimalView", viewSetupPostfix, null, "Setup");
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony, "NSMedieval.View.NPCView", viewSetupPostfix, null, "Setup");
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony, "NSMedieval.View.WorkerView", null, viewDisposePrefix, "Dispose", "OnDestroy");
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony, "NSMedieval.View.Animals.AnimalView", null, viewDisposePrefix, "Dispose", "OnDestroy");
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony, "NSMedieval.View.NPCView", null, viewDisposePrefix, "Dispose", "OnDestroy");

                var storagePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                    nameof(ReplicationResourceStateStorageMutationPostfixV2),
                    BindingFlags.Static | BindingFlags.NonPublic));
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony,
                    "NSMedieval.Components.Storage",
                    storagePostfix,
                    null,
                    "Transfer", "Add", "Consume", "Take", "TransferTo",
                    "DeleteResource", "ClearResources", "ClearAll",
                    "DisposeAllResources", "Dispose");
            }

            if (ReplicationGroundPileStateV2Enabled())
            {
                var pileMutationPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                    nameof(ReplicationResourceStatePileMutationPostfixV2),
                    BindingFlags.Static | BindingFlags.NonPublic));
                var pileDisposePrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                    nameof(ReplicationResourceStatePileDisposePrefixV2),
                    BindingFlags.Static | BindingFlags.NonPublic));
                var pileProducedPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                    nameof(ReplicationResourceStatePileProducedPostfixV2),
                    BindingFlags.Static | BindingFlags.NonPublic));
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony,
                    "NSMedieval.State.ResourcePileInstance",
                    pileMutationPostfix,
                    null,
                    "OnResourceAdded", "OnResourceTaken", "SetPlacedOnStorage");
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony,
                    "NSMedieval.State.ResourcePileInstance",
                    null,
                    pileDisposePrefix,
                    "Dispose");
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony,
                    "NSMedieval.Manager.ResourcePileFactory",
                    pileProducedPostfix,
                    null,
                    "ProducePile");
            }

            if (ReplicationShelfStorageStateV2Enabled())
            {
                var shelfPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                    nameof(ReplicationResourceStateShelfMutationPostfixV2),
                    BindingFlags.Static | BindingFlags.NonPublic));
                patched += TryPatchReplicationResourceStateMethodsV2(
                    harmony,
                    "NSMedieval.StorageUniversal.UniversalStorage",
                    shelfPostfix,
                    null,
                    "StoreResourcePile", "OnPileTaken", "OnPileDurabilityDepleted",
                    "DropResource", "DropStorage", "DisposeStorage", "Dispose");
            }

            LogReplicationInfo(
                "Going Cooperative resource-state-v2 hooks patched="
                + patched.ToString(CultureInfo.InvariantCulture)
                + " owners="
                + FormatReplicationResourceStateV2Capability());
        }

        private int TryPatchReplicationResourceStateMethodsV2(
            Harmony harmony,
            string typeName,
            HarmonyMethod? postfix,
            HarmonyMethod? prefix,
            params string[] names)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                LogReplicationWarning(
                    "Going Cooperative resource-state-v2 hook type missing type="
                    + typeName);
                return 0;
            }

            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            var count = 0;
            var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            for (var i = 0; i < methods.Length; i++)
            {
                if (!wanted.Contains(methods[i].Name)
                    || methods[i].ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    harmony.Patch(methods[i], prefix: prefix, postfix: postfix);
                    count++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning(
                        "Going Cooperative resource-state-v2 hook failed method="
                        + typeName + "." + methods[i].Name
                        + " error=" + FormatReflectionExceptionDetail(ex));
                }
            }

            return count;
        }

        private static void ReplicationResourceStateAgentViewSetupPostfixV2(object __instance)
        {
            if (ReplicationAgentInventoryStateV2Enabled() && __instance != null)
            {
                RegisterReplicationResourceStateAgentViewV2(
                    __instance, forceBaseline: replicationConfigHostMode);
            }
        }

        private static void ReplicationResourceStateAgentViewDisposePrefixV2(object __instance)
        {
            if (!ReplicationAgentInventoryStateV2Enabled() || __instance == null
                || !TryGetReplicationViewEntityId(__instance, out var entityId))
            {
                return;
            }

            UnregisterReplicationResourceStateAgentV2(entityId);
        }

        private static void ReplicationResourceStateStorageMutationPostfixV2(object __instance)
        {
            if (!ReplicationAgentInventoryStateV2Enabled()
                || !replicationConfigHostMode
                || __instance == null
                || !ReplicationAgentContainerIdByStorageV2.TryGetValue(
                    __instance, out var containerId))
            {
                return;
            }

            MarkReplicationAgentContainerDirtyV2(containerId, false);
        }

        private static void ReplicationResourceStatePileMutationPostfixV2(object __instance)
        {
            if (!ReplicationGroundPileStateV2Enabled()
                || !replicationConfigHostMode
                || __instance == null)
            {
                return;
            }

            RegisterReplicationResourceStateGroundPileV2(
                __instance, forceBaseline: false, markDirty: true);
        }

        private static void ReplicationResourceStatePileDisposePrefixV2(object __instance)
        {
            if (!ReplicationGroundPileStateV2Enabled()
                || !replicationConfigHostMode
                || __instance == null)
            {
                return;
            }

            RegisterReplicationResourceStateGroundPileV2(
                __instance, forceBaseline: false, markDirty: true);
        }

        private static void ReplicationResourceStatePileProducedPostfixV2(object? __result)
        {
            if (!ReplicationGroundPileStateV2Enabled()
                || !replicationConfigHostMode
                || __result == null
                || ShouldSuppressReplicationShelfStorePileSpawn())
            {
                return;
            }

            RegisterReplicationResourceStateGroundPileV2(
                __result, forceBaseline: false, markDirty: true);
        }

        private static void ReplicationResourceStateShelfMutationPostfixV2(object __instance)
        {
            if (!ReplicationShelfStorageStateV2Enabled()
                || !replicationConfigHostMode
                || __instance == null)
            {
                return;
            }

            RegisterReplicationResourceStateShelfStorageV2(
                __instance, forceBaseline: false, markDirty: true);
        }

        private static void RegisterReplicationResourceStateAgentViewV2(
            object view,
            bool forceBaseline)
        {
            if (!TryGetReplicationViewEntityId(view, out var entityId)
                || !TryClassifyReplicationView(view, out var entityKind)
                || !TryResolveReplicationAgentOwnerFromView(view, out var owner, out _)
                || owner == null)
            {
                return;
            }

            object? behaviourOwner = null;
            TryResolveReplicationBehaviourOwner(owner, out behaviourOwner);
            RegisterReplicationResourceStateAgentStorageV2(
                entityId, owner, behaviourOwner, "Storage", "agent-haul", forceBaseline);
            if (string.Equals(entityKind, "worker", StringComparison.OrdinalIgnoreCase))
            {
                RegisterReplicationResourceStateAgentStorageV2(
                    entityId, owner, behaviourOwner, "FoodStorage", "agent-food", forceBaseline);
                RegisterReplicationResourceStateAgentStorageV2(
                    entityId, owner, behaviourOwner, "MedicineStorage", "agent-medicine", forceBaseline);
            }
        }

        private static void RegisterReplicationResourceStateAgentStorageV2(
            string entityId,
            object owner,
            object? behaviourOwner,
            string memberName,
            string kind,
            bool forceBaseline)
        {
            object? storage = null;
            if ((!TryReadInstanceMemberValue(owner, memberName, out storage) || storage == null)
                && (behaviourOwner == null
                    || !TryReadInstanceMemberValue(behaviourOwner, memberName, out storage)
                    || storage == null))
            {
                return;
            }

            var suffix = kind.Substring("agent-".Length);
            var containerId = "agent:" + entityId + ":" + suffix;
            if (ReplicationAgentContainersV2.TryGetValue(containerId, out var existing))
            {
                if (!ReferenceEquals(existing.Storage, storage))
                {
                    ReplicationAgentContainerIdByStorageV2.Remove(existing.Storage);
                    existing.Storage = storage;
                    ReplicationAgentContainerIdByStorageV2[storage] = containerId;
                }
                existing.Owner = new WeakReference(owner);
                if (forceBaseline)
                {
                    MarkReplicationAgentContainerDirtyV2(containerId, true);
                }
                return;
            }

            var descriptor = new ReplicationAgentContainerDescriptorV2
            {
                ContainerId = containerId,
                ContainerKind = kind,
                EntityId = entityId,
                Storage = storage,
                Owner = new WeakReference(owner)
            };
            ReplicationAgentContainersV2[containerId] = descriptor;
            ReplicationAgentContainerIdByStorageV2[storage] = containerId;
            ReplicationAgentContainerKnownOrderV2.Add(containerId);
            if (!ReplicationAgentContainerIdsByEntityV2.TryGetValue(entityId, out var ids))
            {
                ids = new List<string>(3);
                ReplicationAgentContainerIdsByEntityV2[entityId] = ids;
            }
            ids.Add(containerId);
            MarkReplicationAgentContainerDirtyV2(containerId, forceBaseline);
        }

        private static void UnregisterReplicationResourceStateAgentV2(string entityId)
        {
            if (!ReplicationAgentContainerIdsByEntityV2.TryGetValue(entityId, out var ids))
            {
                return;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (ReplicationAgentContainersV2.TryGetValue(ids[i], out var descriptor))
                {
                    ReplicationAgentContainerIdByStorageV2.Remove(descriptor.Storage);
                    ReplicationAgentContainersV2.Remove(ids[i]);
                }
                ReplicationAgentContainerDirtySetV2.Remove(ids[i]);
                ReplicationAgentContainerForcedSetV2.Remove(ids[i]);
            }
            ReplicationAgentContainerIdsByEntityV2.Remove(entityId);
        }

        private static void MarkReplicationAgentContainerDirtyV2(
            string containerId,
            bool force)
        {
            if (force)
            {
                ReplicationAgentContainerForcedSetV2.Add(containerId);
            }
            MarkReplicationResourceStateDirtyKeyV2(
                containerId,
                ReplicationAgentContainerDirtySetV2,
                ReplicationAgentContainerDirtyQueueV2);
        }

        private static void RegisterReplicationResourceStateGroundPileV2(
            object pile,
            bool forceBaseline,
            bool markDirty)
        {
            if (ReplicationGroundPileIdByObjectV2.TryGetValue(pile, out var existingId))
            {
                if (markDirty || forceBaseline)
                {
                    MarkReplicationGroundPileDirtyV2(existingId, forceBaseline);
                }
                return;
            }

            if (IsReplicationPileStoredOnShelf(pile)
                || !TryReadReplicationWorldObjectGridPosition(pile, out var gridX, out var gridY, out var gridZ)
                || !TryGetReplicationPileStoredResource(pile, out var resourceInstance, out _)
                || resourceInstance == null
                || !TryReadReplicationResourcePileBlueprintId(
                    pile, resourceInstance, out var blueprintId, out _)
                || string.IsNullOrWhiteSpace(blueprintId))
            {
                return;
            }

            var containerId = "pile:"
                + gridX.ToString(CultureInfo.InvariantCulture) + ":"
                + gridY.ToString(CultureInfo.InvariantCulture) + ":"
                + gridZ.ToString(CultureInfo.InvariantCulture) + ":"
                + blueprintId;
            if (!ReplicationGroundPilesV2.TryGetValue(containerId, out var descriptor))
            {
                descriptor = new ReplicationGroundPileDescriptorV2
                {
                    ContainerId = containerId,
                    BlueprintId = blueprintId,
                    GridX = gridX,
                    GridY = gridY,
                    GridZ = gridZ,
                    Pile = new WeakReference(pile)
                };
                ReplicationGroundPilesV2[containerId] = descriptor;
                ReplicationGroundPileKnownOrderV2.Add(containerId);
            }
            else
            {
                descriptor.Pile = new WeakReference(pile);
            }
            ReplicationGroundPileIdByObjectV2[pile] = containerId;
            if (markDirty || forceBaseline)
            {
                MarkReplicationGroundPileDirtyV2(containerId, forceBaseline);
            }
        }

        private static void MarkReplicationGroundPileDirtyV2(
            string containerId,
            bool force)
        {
            if (force)
            {
                ReplicationGroundPileForcedSetV2.Add(containerId);
            }
            MarkReplicationResourceStateDirtyKeyV2(
                containerId,
                ReplicationGroundPileDirtySetV2,
                ReplicationGroundPileDirtyQueueV2);
        }

        private static void RegisterReplicationResourceStateShelfStorageV2(
            object universalStorage,
            bool forceBaseline,
            bool markDirty)
        {
            if (ReplicationShelfSlotIdsByUniversalStorageV2.TryGetValue(
                    universalStorage, out var existingIds))
            {
                if (markDirty || forceBaseline)
                {
                    for (var i = 0; i < existingIds.Count; i++)
                    {
                        MarkReplicationShelfSlotDirtyV2(existingIds[i], forceBaseline);
                    }
                }
                return;
            }

            if (!TryCreateReplicationStoragePolicyTargetReference(
                    universalStorage, out var target, out var owner, out _)
                || !string.Equals(
                    target.Kind, ReplicationStoragePolicyShelfKind, StringComparison.Ordinal)
                || !TryReadInstanceMemberValue(owner, "AllStorage", out var allStorageRaw)
                || !(allStorageRaw is IList universalStorages)
                || !TryReadInstanceMemberValue(universalStorage, "StorageSlots", out var slotsRaw)
                || !(slotsRaw is IList slots))
            {
                return;
            }

            var storageIndex = -1;
            for (var i = 0; i < universalStorages.Count; i++)
            {
                if (ReferenceEquals(universalStorages[i], universalStorage))
                {
                    storageIndex = i;
                    break;
                }
            }
            if (storageIndex < 0)
            {
                return;
            }

            var ids = new List<string>(slots.Count);
            for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
            {
                var slot = slots[slotIndex];
                if (slot == null)
                {
                    continue;
                }

                var containerId = "shelf-slot:"
                    + target.HostUid.ToString(CultureInfo.InvariantCulture) + ":"
                    + target.ComponentOrdinal.ToString(CultureInfo.InvariantCulture) + ":"
                    + storageIndex.ToString(CultureInfo.InvariantCulture) + ":"
                    + slotIndex.ToString(CultureInfo.InvariantCulture) + ":0";
                if (!ReplicationShelfSlotsV2.ContainsKey(containerId))
                {
                    ReplicationShelfSlotsV2[containerId] =
                        new ReplicationShelfSlotDescriptorV2
                        {
                            ContainerId = containerId,
                            UniversalStorage = universalStorage,
                            Slot = slot,
                            StorageIndex = storageIndex,
                            SlotIndex = slotIndex,
                            HostUid = target.HostUid,
                            ComponentOrdinal = target.ComponentOrdinal,
                            BlueprintFingerprint = target.BlueprintFingerprint,
                            GridX = target.AnchorX,
                            GridY = target.AnchorY,
                            GridZ = target.AnchorZ
                        };
                    ReplicationShelfSlotKnownOrderV2.Add(containerId);
                }
                ids.Add(containerId);
                if (markDirty || forceBaseline)
                {
                    MarkReplicationShelfSlotDirtyV2(containerId, forceBaseline);
                }
            }
            ReplicationShelfSlotIdsByUniversalStorageV2[universalStorage] = ids;
        }

        private static void MarkReplicationShelfSlotDirtyV2(
            string containerId,
            bool force)
        {
            if (force)
            {
                ReplicationShelfSlotForcedSetV2.Add(containerId);
            }
            MarkReplicationResourceStateDirtyKeyV2(
                containerId,
                ReplicationShelfSlotDirtySetV2,
                ReplicationShelfSlotDirtyQueueV2);
        }

        private static void MarkReplicationResourceStateDirtyKeyV2(
            string key,
            HashSet<string> dirtySet,
            Queue<string> dirtyQueue)
        {
            if (replicationConfigResourceStateV2Diagnostics)
            {
                replicationResourceStateDirtyMarksV2++;
            }
            if (dirtySet.Add(key))
            {
                dirtyQueue.Enqueue(key);
                if (replicationConfigResourceStateV2Diagnostics)
                {
                    var total = ReplicationAgentContainerDirtyQueueV2.Count
                        + ReplicationGroundPileDirtyQueueV2.Count
                        + ReplicationShelfSlotDirtyQueueV2.Count;
                    replicationResourceStateDirtyQueueMaxV2 =
                        Math.Max(replicationResourceStateDirtyQueueMaxV2, total);
                }
            }
            else if (replicationConfigResourceStateV2Diagnostics)
            {
                replicationResourceStateCoalescedMarksV2++;
            }
        }

        private static void QueueReplicationResourceStateV2Baseline()
        {
            if (!AnyReplicationResourceStateV2Enabled())
            {
                return;
            }

            replicationResourceStateAgentBootstrapStartedV2 = false;
            replicationResourceStateAgentBootstrapCompleteV2 = false;
            replicationResourceStatePileBootstrapListV2 = null;
            replicationResourceStatePileBootstrapIndexV2 = 0;
            replicationResourceStatePileBootstrapCompleteV2 = false;
            replicationResourceStateShelfBootstrapV2 = null;
            replicationResourceStateShelfBootstrapIndexV2 = 0;
            replicationResourceStateShelfBootstrapCompleteV2 = false;

            foreach (var key in ReplicationAgentContainersV2.Keys)
            {
                MarkReplicationAgentContainerDirtyV2(key, true);
            }
            foreach (var key in ReplicationGroundPilesV2.Keys)
            {
                MarkReplicationGroundPileDirtyV2(key, true);
            }
            foreach (var key in ReplicationShelfSlotsV2.Keys)
            {
                MarkReplicationShelfSlotDirtyV2(key, true);
            }
        }

        private static void UpdateReplicationResourceStateV2()
        {
            if (!AnyReplicationResourceStateV2Enabled()
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationConfigHostMode
                || !replicationRemoteHelloReceived
                || replicationTransport == null)
            {
                return;
            }

            var stopwatch = replicationConfigResourceStateV2Diagnostics
                ? Stopwatch.StartNew()
                : null;
            BootstrapReplicationResourceStateV2();
            ScheduleReplicationResourceStateRecoveryV2();
            var changed = ReplicationResourceStateChangedScratchV2;
            changed.Clear();
            DrainReplicationAgentContainersV2(changed);
            DrainReplicationGroundPilesV2(changed);
            DrainReplicationShelfSlotsV2(changed);
            SendReplicationResourceStateRowsV2(changed);
            if (stopwatch != null)
            {
                stopwatch.Stop();
                replicationResourceStateDrainMillisecondsV2 +=
                    stopwatch.Elapsed.TotalMilliseconds;
            }
            LogReplicationResourceStateDiagnosticsIfDueV2();
        }

        private static void BootstrapReplicationResourceStateV2()
        {
            if (ReplicationAgentInventoryStateV2Enabled()
                && !replicationResourceStateAgentBootstrapCompleteV2)
            {
                if (!replicationResourceStateAgentBootstrapStartedV2)
                {
                    replicationResourceStateAgentBootstrapViewsV2 =
                        FindReplicationAnimatedAgentViews();
                    replicationResourceStateAgentBootstrapStartedV2 = true;
                }
                var processed = 0;
                while (processed < ReplicationResourceStateV2BootstrapBudget
                    && replicationResourceStateAgentBootstrapIndexV2
                        < replicationResourceStateAgentBootstrapViewsV2.Length)
                {
                    var view = replicationResourceStateAgentBootstrapViewsV2[
                        replicationResourceStateAgentBootstrapIndexV2++];
                    processed++;
                    if (view != null)
                    {
                        RegisterReplicationResourceStateAgentViewV2(
                            view, forceBaseline: true);
                    }
                }
                replicationResourceStateAgentBootstrapCompleteV2 =
                    replicationResourceStateAgentBootstrapIndexV2
                    >= replicationResourceStateAgentBootstrapViewsV2.Length;
                if (replicationResourceStateAgentBootstrapCompleteV2)
                {
                    replicationResourceStateAgentBootstrapViewsV2 =
                        Array.Empty<UnityEngine.Object>();
                }
            }

            if (ReplicationGroundPileStateV2Enabled()
                && !replicationResourceStatePileBootstrapCompleteV2)
            {
                if (replicationResourceStatePileBootstrapListV2 == null)
                {
                    if (!TryGetReplicationResourcePileManager(out var manager, out _)
                        || manager == null
                        || !TryReadInstanceMemberValue(
                            manager, "SpawnedPileInstances", out var spawned)
                        || !(spawned is IList list))
                    {
                        return;
                    }
                    replicationResourceStatePileBootstrapListV2 = list;
                }
                var processed = 0;
                while (processed < ReplicationResourceStateV2BootstrapBudget
                    && replicationResourceStatePileBootstrapIndexV2
                        < replicationResourceStatePileBootstrapListV2.Count)
                {
                    var pile = replicationResourceStatePileBootstrapListV2[
                        replicationResourceStatePileBootstrapIndexV2++];
                    processed++;
                    if (pile != null)
                    {
                        RegisterReplicationResourceStateGroundPileV2(
                            pile, forceBaseline: true, markDirty: true);
                    }
                }
                replicationResourceStatePileBootstrapCompleteV2 =
                    replicationResourceStatePileBootstrapIndexV2
                    >= replicationResourceStatePileBootstrapListV2.Count;
                if (replicationResourceStatePileBootstrapCompleteV2)
                {
                    replicationResourceStatePileBootstrapListV2 = null;
                }
            }

            if (ReplicationShelfStorageStateV2Enabled()
                && !replicationResourceStateShelfBootstrapCompleteV2)
            {
                if (replicationResourceStateShelfBootstrapV2 == null)
                {
                    replicationResourceStateShelfBootstrapV2 = new List<object>();
                    var managerType = AccessTools.TypeByName(
                        "NSMedieval.StorageUniversal.StorageCommonManager");
                    var manager = managerType == null
                        ? null
                        : AccessTools.Property(managerType, "Instance")
                            ?.GetValue(null, null);
                    var allStorages = manager == null || managerType == null
                        ? null
                        : AccessTools.Property(managerType, "AllStorages")
                            ?.GetValue(manager, null) as IEnumerable;
                    if (allStorages != null)
                    {
                        foreach (var storage in allStorages)
                        {
                            if (storage != null)
                            {
                                replicationResourceStateShelfBootstrapV2.Add(storage);
                            }
                        }
                    }
                }
                var processed = 0;
                while (processed < ReplicationResourceStateV2BootstrapBudget
                    && replicationResourceStateShelfBootstrapIndexV2
                        < replicationResourceStateShelfBootstrapV2.Count)
                {
                    var storage = replicationResourceStateShelfBootstrapV2[
                        replicationResourceStateShelfBootstrapIndexV2++];
                    processed++;
                    RegisterReplicationResourceStateShelfStorageV2(
                        storage, forceBaseline: true, markDirty: true);
                }
                replicationResourceStateShelfBootstrapCompleteV2 =
                    replicationResourceStateShelfBootstrapIndexV2
                    >= replicationResourceStateShelfBootstrapV2.Count;
                if (replicationResourceStateShelfBootstrapCompleteV2)
                {
                    replicationResourceStateShelfBootstrapV2 = null;
                }
            }
        }

        private static void ScheduleReplicationResourceStateRecoveryV2()
        {
            var now = Time.realtimeSinceStartup;
            if (replicationResourceStateNextRecoveryRealtimeV2 <= 0f)
            {
                replicationResourceStateNextRecoveryRealtimeV2 =
                    now + ReplicationResourceStateV2RecoverySeconds;
            }
            else if (!replicationResourceStateRecoveryCycleActiveV2
                && now >= replicationResourceStateNextRecoveryRealtimeV2)
            {
                replicationResourceStateAgentRecoveryRemainingV2 =
                    ReplicationAgentContainerKnownOrderV2.Count;
                replicationResourceStatePileRecoveryRemainingV2 =
                    ReplicationGroundPileKnownOrderV2.Count;
                replicationResourceStateShelfRecoveryRemainingV2 =
                    ReplicationShelfSlotKnownOrderV2.Count;
                replicationResourceStateRecoveryCycleActiveV2 =
                    replicationResourceStateAgentRecoveryRemainingV2 > 0
                    || replicationResourceStatePileRecoveryRemainingV2 > 0
                    || replicationResourceStateShelfRecoveryRemainingV2 > 0;
                replicationResourceStateNextRecoveryAuditRealtimeV2 = now;
            }

            if (!replicationResourceStateRecoveryCycleActiveV2
                || now < replicationResourceStateNextRecoveryAuditRealtimeV2)
            {
                return;
            }

            replicationResourceStateNextRecoveryAuditRealtimeV2 =
                now + ReplicationResourceStateV2RecoveryAuditIntervalSeconds;
            var scheduled = 0;
            var attemptedDomains = 0;
            while (scheduled < ReplicationResourceStateV2RecoveryBudget
                && attemptedDomains < 3)
            {
                var domain = replicationResourceStateRecoveryDomainCursorV2++ % 3;
                attemptedDomains++;
                if (domain == 0 && ReplicationAgentInventoryStateV2Enabled())
                {
                    if (ScheduleReplicationResourceStateRecoveryKeyV2(
                        ReplicationAgentContainerKnownOrderV2,
                        ReplicationAgentContainersV2,
                        ref replicationResourceStateAgentRecoveryCursorV2,
                        ref replicationResourceStateAgentRecoveryRemainingV2,
                        MarkReplicationAgentContainerDirtyActionV2))
                    {
                        scheduled++;
                    }
                }
                else if (domain == 1 && ReplicationGroundPileStateV2Enabled())
                {
                    if (ScheduleReplicationResourceStateRecoveryKeyV2(
                        ReplicationGroundPileKnownOrderV2,
                        ReplicationGroundPilesV2,
                        ref replicationResourceStatePileRecoveryCursorV2,
                        ref replicationResourceStatePileRecoveryRemainingV2,
                        MarkReplicationGroundPileDirtyActionV2))
                    {
                        scheduled++;
                    }
                }
                else if (domain == 2 && ReplicationShelfStorageStateV2Enabled())
                {
                    if (ScheduleReplicationResourceStateRecoveryKeyV2(
                        ReplicationShelfSlotKnownOrderV2,
                        ReplicationShelfSlotsV2,
                        ref replicationResourceStateShelfRecoveryCursorV2,
                        ref replicationResourceStateShelfRecoveryRemainingV2,
                        MarkReplicationShelfSlotDirtyActionV2))
                    {
                        scheduled++;
                    }
                }
            }

            if (replicationResourceStateAgentRecoveryRemainingV2 <= 0
                && replicationResourceStatePileRecoveryRemainingV2 <= 0
                && replicationResourceStateShelfRecoveryRemainingV2 <= 0)
            {
                replicationResourceStateRecoveryCycleActiveV2 = false;
                replicationResourceStateNextRecoveryRealtimeV2 =
                    now + ReplicationResourceStateV2RecoverySeconds;
            }
        }

        private static bool ScheduleReplicationResourceStateRecoveryKeyV2<T>(
            List<string> order,
            Dictionary<string, T> known,
            ref int cursor,
            ref int remaining,
            Action<string, bool> mark)
        {
            while (remaining > 0 && order.Count > 0)
            {
                if (cursor >= order.Count)
                {
                    cursor = 0;
                }
                var key = order[cursor++];
                remaining--;
                if (known.ContainsKey(key))
                {
                    // Recovery is an audit. Only the initial hello/load
                    // baseline may force an unchanged row onto the wire.
                    mark(key, false);
                    return true;
                }
            }
            return false;
        }

        private static void DrainReplicationAgentContainersV2(
            List<ReplicationResourceContainerState> changed)
        {
            if (!ReplicationAgentInventoryStateV2Enabled())
            {
                return;
            }

            var processed = 0;
            while (processed < ReplicationResourceStateV2DirtyBudget
                && ReplicationAgentContainerDirtyQueueV2.Count > 0)
            {
                var key = ReplicationAgentContainerDirtyQueueV2.Dequeue();
                ReplicationAgentContainerDirtySetV2.Remove(key);
                var forced = ReplicationAgentContainerForcedSetV2.Remove(key);
                processed++;
                if (!ReplicationAgentContainersV2.TryGetValue(key, out var descriptor)
                    || !TryReadReplicationStorageEntries(
                        descriptor.Storage, out var entries, out _))
                {
                    continue;
                }
                if (replicationConfigResourceStateV2Diagnostics)
                {
                    replicationResourceStateRowsReadV2++;
                }
                AppendReplicationResourceStateRowV2(
                    new ReplicationResourceContainerState(
                        descriptor.ContainerId,
                        descriptor.ContainerKind,
                        descriptor.EntityId,
                        0L, 0, 0, 0, entries),
                    forced,
                    changed);
            }
        }

        private static void DrainReplicationGroundPilesV2(
            List<ReplicationResourceContainerState> changed)
        {
            if (!ReplicationGroundPileStateV2Enabled())
            {
                return;
            }

            var processed = 0;
            while (processed < ReplicationResourceStateV2DirtyBudget
                && ReplicationGroundPileDirtyQueueV2.Count > 0)
            {
                var key = ReplicationGroundPileDirtyQueueV2.Dequeue();
                ReplicationGroundPileDirtySetV2.Remove(key);
                var forced = ReplicationGroundPileForcedSetV2.Remove(key);
                processed++;
                if (!ReplicationGroundPilesV2.TryGetValue(key, out var descriptor))
                {
                    continue;
                }

                IReadOnlyList<ReplicationResourceContainerEntry> entries =
                    Array.Empty<ReplicationResourceContainerEntry>();
                var pile = descriptor.Pile.Target;
                if (pile != null
                    && (!TryReadInstanceMemberValue(pile, "HasDisposed", out var disposedRaw)
                        || disposedRaw is not bool disposed
                        || !disposed)
                    && (!TryReadInstanceMemberValue(pile, "HasDied", out var diedRaw)
                        || diedRaw is not bool died
                        || !died)
                    && !IsReplicationPileStoredOnShelf(pile)
                    && TryGetReplicationPileStoredResource(
                        pile, out var resourceInstance, out _)
                    && resourceInstance != null
                    && TryReadReplicationWorldObjectIntMember(
                        resourceInstance, "Amount", "amount", out var amount)
                    && amount > 0)
                {
                    entries = new[]
                    {
                        new ReplicationResourceContainerEntry(
                            descriptor.BlueprintId, amount)
                    };
                }

                if (replicationConfigResourceStateV2Diagnostics)
                {
                    replicationResourceStateRowsReadV2++;
                }
                AppendReplicationResourceStateRowV2(
                    new ReplicationResourceContainerState(
                        descriptor.ContainerId,
                        "pile",
                        descriptor.BlueprintId,
                        0L,
                        descriptor.GridX,
                        descriptor.GridY,
                        descriptor.GridZ,
                        entries),
                    forced,
                    changed);
                if (entries.Count == 0)
                {
                    var retiredPile = descriptor.Pile.Target;
                    if (retiredPile != null)
                    {
                        ReplicationGroundPileIdByObjectV2.Remove(retiredPile);
                    }
                    ReplicationGroundPilesV2.Remove(key);
                    ReplicationGroundPileKnownOrderV2.Remove(key);
                    ReplicationGroundPileForcedSetV2.Remove(key);
                }
            }
        }

        private static void DrainReplicationShelfSlotsV2(
            List<ReplicationResourceContainerState> changed)
        {
            if (!ReplicationShelfStorageStateV2Enabled())
            {
                return;
            }

            var processed = 0;
            while (processed < ReplicationResourceStateV2DirtyBudget
                && ReplicationShelfSlotDirtyQueueV2.Count > 0)
            {
                var key = ReplicationShelfSlotDirtyQueueV2.Dequeue();
                ReplicationShelfSlotDirtySetV2.Remove(key);
                var forced = ReplicationShelfSlotForcedSetV2.Remove(key);
                processed++;
                if (!ReplicationShelfSlotsV2.TryGetValue(key, out var descriptor))
                {
                    continue;
                }

                var entries = new List<ReplicationResourceContainerEntry>(1);
                var storageDisposed =
                    TryReadInstanceMemberValue(
                        descriptor.UniversalStorage, "HasDisposed", out var disposedRaw)
                    && disposedRaw is bool disposed
                    && disposed;
                if (!storageDisposed
                    && TryReadInstanceMemberValue(
                        descriptor.Slot, "Pile", out var pile)
                    && pile != null
                    && TryGetReplicationPileStoredResource(
                        pile, out var resourceInstance, out _)
                    && resourceInstance != null
                    && TryReadReplicationResourcePileBlueprintId(
                        pile, resourceInstance, out var blueprintId, out _)
                    && TryReadReplicationWorldObjectIntMember(
                        resourceInstance, "Amount", "amount", out var amount)
                    && amount > 0)
                {
                    entries.Add(new ReplicationResourceContainerEntry(
                        blueprintId, amount));
                }

                if (replicationConfigResourceStateV2Diagnostics)
                {
                    replicationResourceStateRowsReadV2++;
                }
                AppendReplicationResourceStateRowV2(
                    new ReplicationResourceContainerState(
                        descriptor.ContainerId,
                        ReplicationShelfStorageSlotKindV1,
                        descriptor.BlueprintFingerprint,
                        0L,
                        descriptor.GridX,
                        descriptor.GridY,
                        descriptor.GridZ,
                        entries),
                    forced,
                    changed);
                if (storageDisposed)
                {
                    ReplicationShelfSlotIdsByUniversalStorageV2.Remove(
                        descriptor.UniversalStorage);
                    ReplicationShelfSlotsV2.Remove(key);
                    ReplicationShelfSlotKnownOrderV2.Remove(key);
                    ReplicationShelfSlotForcedSetV2.Remove(key);
                }
            }
        }

        private static void AppendReplicationResourceStateRowV2(
            ReplicationResourceContainerState collected,
            bool force,
            List<ReplicationResourceContainerState> changed)
        {
            var signature = ComputeReplicationResourceContainerSignature(collected);
            var revision = 1L;
            if (ReplicationHostResourceContainers.TryGetValue(
                    collected.ContainerId, out var previous))
            {
                if (previous.Signature == signature && !force)
                {
                    if (replicationConfigResourceStateV2Diagnostics)
                    {
                        replicationResourceStateUnchangedRowsSuppressedV2++;
                    }
                    return;
                }
                revision = previous.Revision + 1L;
            }

            var state = CopyReplicationResourceContainerWithRevision(
                collected, revision);
            ReplicationHostResourceContainers[collected.ContainerId] =
                new ReplicationHostResourceContainerState(
                    state, signature, revision);
            changed.Add(state);
        }

        private static void SendReplicationResourceStateRowsV2(
            List<ReplicationResourceContainerState> changed)
        {
            if (changed.Count == 0 || replicationTransport == null)
            {
                return;
            }

            for (var offset = 0;
                offset < changed.Count;
                offset += ReplicationResourceContainerBatchMaxContainers)
            {
                var count = Math.Min(
                    ReplicationResourceContainerBatchMaxContainers,
                    changed.Count - offset);
                var chunk = new List<ReplicationResourceContainerState>(count);
                for (var i = 0; i < count; i++)
                {
                    chunk.Add(changed[offset + i]);
                }
                var batch = new ReplicationResourceContainerBatch(
                    ++replicationResourceContainerBatchSequence,
                    Time.realtimeSinceStartup,
                    false,
                    chunk);
                replicationTransport.Send(
                    ReplicationPayloadCodec.ForResourceContainerBatch(
                        ReplicationHostPeerId, batch));
                replicationResourceContainerBatchesSent++;
                if (replicationConfigResourceStateV2Diagnostics)
                {
                    replicationResourceStateBatchesSentV2++;
                }
            }
            if (replicationConfigResourceStateV2Diagnostics)
            {
                replicationResourceStateRowsSentV2 += changed.Count;
            }
        }

        private static void LogReplicationResourceStateDiagnosticsIfDueV2()
        {
            if (!replicationConfigResourceStateV2Diagnostics
                || Time.realtimeSinceStartup
                    < replicationResourceStateNextDiagnosticsRealtimeV2)
            {
                return;
            }

            replicationResourceStateNextDiagnosticsRealtimeV2 =
                Time.realtimeSinceStartup
                + ReplicationResourceStateV2DiagnosticsSeconds;
            instance?.LogReplicationInfo(
                "Going Cooperative resource-state-v2 window owners="
                + FormatReplicationResourceStateV2Capability()
                + " known="
                + ReplicationAgentContainersV2.Count.ToString(CultureInfo.InvariantCulture)
                + "/"
                + ReplicationGroundPilesV2.Count.ToString(CultureInfo.InvariantCulture)
                + "/"
                + ReplicationShelfSlotsV2.Count.ToString(CultureInfo.InvariantCulture)
                + " dirtyMarks="
                + replicationResourceStateDirtyMarksV2.ToString(CultureInfo.InvariantCulture)
                + " coalesced="
                + replicationResourceStateCoalescedMarksV2.ToString(CultureInfo.InvariantCulture)
                + " rowsRead="
                + replicationResourceStateRowsReadV2.ToString(CultureInfo.InvariantCulture)
                + " rowsSent="
                + replicationResourceStateRowsSentV2.ToString(CultureInfo.InvariantCulture)
                + " unchangedSuppressed="
                + replicationResourceStateUnchangedRowsSuppressedV2.ToString(
                    CultureInfo.InvariantCulture)
                + " batches="
                + replicationResourceStateBatchesSentV2.ToString(CultureInfo.InvariantCulture)
                + " queueMax="
                + replicationResourceStateDirtyQueueMaxV2.ToString(CultureInfo.InvariantCulture)
                + " drainMs="
                + replicationResourceStateDrainMillisecondsV2.ToString(
                    "F3", CultureInfo.InvariantCulture)
                + " bootstrap="
                + replicationResourceStateAgentBootstrapCompleteV2 + "/"
                + replicationResourceStatePileBootstrapCompleteV2 + "/"
                + replicationResourceStateShelfBootstrapCompleteV2);
            replicationResourceStateDirtyMarksV2 = 0L;
            replicationResourceStateCoalescedMarksV2 = 0L;
            replicationResourceStateRowsReadV2 = 0L;
            replicationResourceStateRowsSentV2 = 0L;
            replicationResourceStateUnchangedRowsSuppressedV2 = 0L;
            replicationResourceStateBatchesSentV2 = 0L;
            replicationResourceStateDirtyQueueMaxV2 = 0;
            replicationResourceStateDrainMillisecondsV2 = 0d;
        }

        private static string FormatReplicationResourceStateV2Capability()
        {
            return (replicationConfigResourceStateV2 ? "1" : "0")
                + (replicationConfigAgentInventoryStateV2 ? "1" : "0")
                + (replicationConfigGroundPileStateV2 ? "1" : "0")
                + (replicationConfigShelfStorageStateV2 ? "1" : "0")
                + ":1";
        }

        private static void ClearReplicationResourceStateV2()
        {
            ReplicationAgentContainerIdByStorageV2.Clear();
            ReplicationAgentContainersV2.Clear();
            ReplicationAgentContainerIdsByEntityV2.Clear();
            ReplicationAgentContainerKnownOrderV2.Clear();
            ReplicationAgentContainerDirtyQueueV2.Clear();
            ReplicationAgentContainerDirtySetV2.Clear();
            ReplicationAgentContainerForcedSetV2.Clear();
            ReplicationGroundPileIdByObjectV2.Clear();
            ReplicationGroundPilesV2.Clear();
            ReplicationGroundPileKnownOrderV2.Clear();
            ReplicationGroundPileDirtyQueueV2.Clear();
            ReplicationGroundPileDirtySetV2.Clear();
            ReplicationGroundPileForcedSetV2.Clear();
            ReplicationShelfSlotIdsByUniversalStorageV2.Clear();
            ReplicationShelfSlotsV2.Clear();
            ReplicationShelfSlotKnownOrderV2.Clear();
            ReplicationShelfSlotDirtyQueueV2.Clear();
            ReplicationShelfSlotDirtySetV2.Clear();
            ReplicationShelfSlotForcedSetV2.Clear();
            ReplicationResourceStateChangedScratchV2.Clear();
            replicationResourceStateAgentBootstrapViewsV2 =
                Array.Empty<UnityEngine.Object>();
            replicationResourceStateAgentBootstrapIndexV2 = 0;
            replicationResourceStateAgentBootstrapStartedV2 = false;
            replicationResourceStateAgentBootstrapCompleteV2 = false;
            replicationResourceStatePileBootstrapListV2 = null;
            replicationResourceStatePileBootstrapIndexV2 = 0;
            replicationResourceStatePileBootstrapCompleteV2 = false;
            replicationResourceStateShelfBootstrapV2 = null;
            replicationResourceStateShelfBootstrapIndexV2 = 0;
            replicationResourceStateShelfBootstrapCompleteV2 = false;
            replicationResourceStateNextRecoveryRealtimeV2 = 0f;
            replicationResourceStateNextRecoveryAuditRealtimeV2 = 0f;
            replicationResourceStateRecoveryCycleActiveV2 = false;
            replicationResourceStateRecoveryDomainCursorV2 = 0;
            replicationResourceStateAgentRecoveryCursorV2 = 0;
            replicationResourceStateAgentRecoveryRemainingV2 = 0;
            replicationResourceStatePileRecoveryCursorV2 = 0;
            replicationResourceStatePileRecoveryRemainingV2 = 0;
            replicationResourceStateShelfRecoveryCursorV2 = 0;
            replicationResourceStateShelfRecoveryRemainingV2 = 0;
            replicationResourceStateNextDiagnosticsRealtimeV2 = 0f;
            replicationResourceStateDirtyMarksV2 = 0L;
            replicationResourceStateCoalescedMarksV2 = 0L;
            replicationResourceStateRowsReadV2 = 0L;
            replicationResourceStateRowsSentV2 = 0L;
            replicationResourceStateUnchangedRowsSuppressedV2 = 0L;
            replicationResourceStateBatchesSentV2 = 0L;
            replicationResourceStateDirtyQueueMaxV2 = 0;
            replicationResourceStateDrainMillisecondsV2 = 0d;
        }
    }
}
