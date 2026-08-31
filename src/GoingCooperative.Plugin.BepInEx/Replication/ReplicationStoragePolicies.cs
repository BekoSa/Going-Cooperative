using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using GoingCooperative.Core;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationStoragePolicyGroundKind = "Stockpile";
        private const string ReplicationStoragePolicyShelfKind = "Shelf";
        private const int ReplicationStoragePolicyMaximumSlots = 64;
        private const int ReplicationStoragePolicyMaximumCatalogResources = 4096;
        private const int ReplicationStoragePolicyMaximumPendingTargets = 4096;
        private const int ReplicationStoragePolicyHostFlushBudget = 16;
        private const string ReplicationStoragePolicyDisposedTopologySignature = "disposed";
        private const float ReplicationStoragePolicyRetainedRetrySeconds = 0.5f;
        private const int ReplicationStoragePolicyMinimumPriority = 1;
        private const int ReplicationStoragePolicyMaximumPriority = 4;
        private const int ReplicationStoragePolicyMinimumQuality = 1;
        private const int ReplicationStoragePolicyMaximumQuality = 6;

        // Client policy interaction is UI-only until the host's complete state proof
        // arrives. In particular, an unproved resource-filter edit must never release
        // reservations, update storage membership, or drop feeder resources locally.
        private static int replicationStoragePolicyAuthoritativeApplyDepth;
        private static int replicationStoragePolicyUiRefreshDepth;
        private static int replicationStoragePolicyRegisterStorageDepth;
        private static int replicationStoragePolicyFilterNotificationSuppressionDepth;
        private static readonly HashSet<object> ReplicationStoragePolicyDeferredFilterNotifications =
            new HashSet<object>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, ReplicationStoragePolicyPendingTarget>
            ReplicationStoragePolicyPendingByTarget =
                new Dictionary<string, ReplicationStoragePolicyPendingTarget>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationStoragePolicyDirtyTarget>
            ReplicationStoragePolicyHostDirtyByTarget =
                new Dictionary<string, ReplicationStoragePolicyDirtyTarget>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationStoragePolicyHostHighWaterByCell =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationStoragePolicyHostRevisionByTarget =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationStoragePolicyHostProofThroughByTarget =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationStoragePolicyTargetReference>
            ReplicationStoragePolicyHostKnownTargets =
                new Dictionary<string, ReplicationStoragePolicyTargetReference>(
                    StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationStoragePolicyClientRevisionByTarget =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationStoragePolicyClientTombstones =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationStoragePolicyClientQuarantinedTargets =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationStoragePolicyHostQuarantinedTargets =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationStoragePolicyRetainedLogAtByKey =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationStoragePolicyRetryAtByKey =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationStoragePolicyMissingSinceByKey =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static ReplicationStoragePolicyCatalog? replicationStoragePolicyCatalog;
        private static int replicationStoragePolicyCaptureSnapshotFrame = -1;
        private static readonly Dictionary<int, Tuple<object, ReplicationStoragePolicySnapshot>>
            ReplicationStoragePolicyCaptureSnapshotsByStorage =
                new Dictionary<int, Tuple<object, ReplicationStoragePolicySnapshot>>();
        private static ReplicationStoragePolicyStateCompletionContext?
            replicationStoragePolicyStateCompletionContext;
        private static bool replicationStoragePolicyCriticalPatchesReady;
        private static long replicationStoragePolicyLastBaselineEpoch = -1L;
        private static long replicationStoragePolicyRuntimeEpoch;
        private static float replicationStoragePolicyNextFailClosedLogRealtime;
        private static string replicationStoragePolicyLastFailClosedDetail = string.Empty;

        private enum ReplicationStoragePolicyCellKind
        {
            Name,
            Priority,
            UseInProduction,
            HitPoints,
            Quality,
            Resource
        }

        private static bool StoragePolicyV4Enabled()
        {
            return replicationConfigStoragePolicyV4
                && replicationConfigEnabled
                && replicationStoragePolicyCriticalPatchesReady;
        }

        private sealed class ReplicationStoragePolicyTargetReference
        {
            public string Kind = string.Empty;
            public long HostUid;
            public bool Canonical;
            public int ComponentOrdinal;
            public string BlueprintFingerprint = string.Empty;
            public int AnchorX;
            public int AnchorY;
            public int AnchorZ;

            public ReplicationStoragePolicyTargetReference Clone()
            {
                return (ReplicationStoragePolicyTargetReference)MemberwiseClone();
            }
        }

        private readonly struct ReplicationStoragePolicyCellValue
        {
            public ReplicationStoragePolicyCellValue(
                ReplicationStoragePolicyCellKind kind,
                int slot,
                int resource,
                int minimum,
                int maximum,
                bool enabled,
                string text)
            {
                Kind = kind;
                Slot = slot;
                Resource = resource;
                Minimum = minimum;
                Maximum = maximum;
                Enabled = enabled;
                Text = text ?? string.Empty;
            }

            public ReplicationStoragePolicyCellKind Kind { get; }
            public int Slot { get; }
            public int Resource { get; }
            public int Minimum { get; }
            public int Maximum { get; }
            public bool Enabled { get; }
            public string Text { get; }
        }

        private sealed class ReplicationStoragePolicyPendingTarget
        {
            public ReplicationStoragePolicyPendingTarget(
                ReplicationStoragePolicyTargetReference target,
                object storage)
            {
                Target = target;
                Storage = storage;
            }

            public ReplicationStoragePolicyTargetReference Target;
            public object Storage;
            public long InFlightSequence;
            public readonly SortedDictionary<string, ReplicationStoragePolicyCellValue> Cells =
                new SortedDictionary<string, ReplicationStoragePolicyCellValue>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> SequenceByCell =
                new Dictionary<string, long>(StringComparer.Ordinal);
        }

        private sealed class ReplicationStoragePolicyDirtyTarget
        {
            public ReplicationStoragePolicyDirtyTarget(
                ReplicationStoragePolicyTargetReference target,
                object storage)
            {
                Target = target;
                Storage = storage;
            }

            public ReplicationStoragePolicyTargetReference Target;
            public object Storage;
        }

        private sealed class ReplicationStoragePolicyMutationCapture
        {
            public ReplicationStoragePolicyMutationCapture(
                ReplicationStoragePolicyTargetReference target,
                object storage,
                bool hostCapture)
            {
                Target = target;
                Storage = storage;
                HostCapture = hostCapture;
            }

            public ReplicationStoragePolicyTargetReference Target { get; }
            public object Storage { get; }
            public bool HostCapture { get; }
            public ReplicationStoragePolicySnapshot? BeforePaste { get; set; }
            public bool PasteNotificationScopeEntered { get; set; }
        }

        private sealed class ReplicationStoragePolicyCatalog
        {
            public string Signature = string.Empty;
            public string[] ResourceIds = Array.Empty<string>();
            public object[] Resources = Array.Empty<object>();
            public Dictionary<string, int> IndexByResourceId =
                new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<int, int> IndexByLocalObjectKey = new Dictionary<int, int>();
        }

        private sealed class ReplicationStoragePolicyStateCompletionContext
        {
            public string TargetKey = string.Empty;
            public long Revision;
            public bool Exists;
        }

        private sealed class ReplicationStoragePolicySlotSnapshot
        {
            public int Ordinal;
            public string UniversalStorageId = string.Empty;
            public string DefaultAllowedFingerprint = string.Empty;
            public object? UniversalStorage;
            public object? Filter;
            public bool[] DefaultAllowed = Array.Empty<bool>();
            public bool[] Allowed = Array.Empty<bool>();
            public int HitPointsMinimum;
            public int HitPointsMaximum;
            public int QualityMinimum;
            public int QualityMaximum;

            public ReplicationStoragePolicySlotSnapshot Clone()
            {
                return new ReplicationStoragePolicySlotSnapshot
                {
                    Ordinal = Ordinal,
                    UniversalStorageId = UniversalStorageId,
                    DefaultAllowedFingerprint = DefaultAllowedFingerprint,
                    UniversalStorage = UniversalStorage,
                    Filter = Filter,
                    DefaultAllowed = (bool[])DefaultAllowed.Clone(),
                    Allowed = (bool[])Allowed.Clone(),
                    HitPointsMinimum = HitPointsMinimum,
                    HitPointsMaximum = HitPointsMaximum,
                    QualityMinimum = QualityMinimum,
                    QualityMaximum = QualityMaximum
                };
            }
        }

        private sealed class ReplicationStoragePolicySnapshot
        {
            public ReplicationStoragePolicyTargetReference Target = new ReplicationStoragePolicyTargetReference();
            public object? Storage;
            public int Priority;
            public bool UseInProduction;
            public string Name = string.Empty;
            public string TopologySignature = string.Empty;
            public List<ReplicationStoragePolicySlotSnapshot> Slots =
                new List<ReplicationStoragePolicySlotSnapshot>();

            public ReplicationStoragePolicySnapshot Clone()
            {
                var clone = new ReplicationStoragePolicySnapshot
                {
                    Target = Target.Clone(),
                    Storage = Storage,
                    Priority = Priority,
                    UseInProduction = UseInProduction,
                    Name = Name,
                    TopologySignature = TopologySignature
                };
                for (var i = 0; i < Slots.Count; i++)
                {
                    clone.Slots.Add(Slots[i].Clone());
                }
                return clone;
            }
        }

        private int TryInstallReplicationStoragePolicyCapture(Harmony harmony)
        {
            replicationStoragePolicyCriticalPatchesReady = false;
            var resourceType = AccessTools.TypeByName("NSMedieval.Model.Resource");
            var rangeType = AccessTools.TypeByName("NSEipix.Model.IntRange");
            var priorityType = AccessTools.TypeByName("NSMedieval.State.ZonePriority");
            var storageType = AccessTools.TypeByName("NSMedieval.IStorage");
            if (resourceType == null || rangeType == null || priorityType == null || storageType == null)
            {
                LogReplicationWarning("Going Cooperative storage-policy native argument surface missing");
                return 0;
            }

            var count = 0;
            var storageImplementations = new[]
            {
                "NSMedieval.Stockpiles.StockpileInstance",
                "NSMedieval.BuildingComponents.ShelfComponentInstance"
            };
            for (var i = 0; i < storageImplementations.Length; i++)
            {
                var typeName = storageImplementations[i];
                count += PatchReplicationStoragePolicyMethod(
                    harmony, typeName, "AllowResource", new[] { resourceType, typeof(bool) },
                    nameof(ReplicationStoragePolicyAllowResourcePrefix),
                    nameof(ReplicationStoragePolicyMutationPostfix));
                count += PatchReplicationStoragePolicyMethod(
                    harmony, typeName, "SetPriority", new[] { priorityType },
                    nameof(ReplicationStoragePolicyPriorityPrefix),
                    nameof(ReplicationStoragePolicyMutationPostfix));
                count += PatchReplicationStoragePolicyMethod(
                    harmony, typeName, "SetHitPointsPercent", new[] { rangeType },
                    nameof(ReplicationStoragePolicyHitPointsPrefix),
                    nameof(ReplicationStoragePolicyMutationPostfix));
                count += PatchReplicationStoragePolicyMethod(
                    harmony, typeName, "SetQuality", new[] { rangeType },
                    nameof(ReplicationStoragePolicyQualityPrefix),
                    nameof(ReplicationStoragePolicyMutationPostfix));
                count += PatchReplicationStoragePolicyMethod(
                    harmony, typeName, "SetCanBeUsedInProduction", new[] { typeof(bool) },
                    nameof(ReplicationStoragePolicyProductionPrefix),
                    nameof(ReplicationStoragePolicyMutationPostfix));
                count += PatchReplicationStoragePolicyMethod(
                    harmony, typeName, "SetName", new[] { typeof(string) },
                    nameof(ReplicationStoragePolicyNamePrefix),
                    nameof(ReplicationStoragePolicyMutationPostfix));
                count += PatchReplicationStoragePolicyPaste(
                    harmony,
                    typeName,
                    storageType);
            }

            // Bulk/group/preset shelf operations call UniversalStorage directly and
            // never cross ShelfComponentInstance.AllowResource.
            count += PatchReplicationStoragePolicyMethod(
                harmony,
                "NSMedieval.StorageUniversal.UniversalStorage",
                "AllowResource",
                new[] { resourceType, typeof(bool) },
                nameof(ReplicationStorageUniversalAllowResourcePrefix),
                nameof(ReplicationStoragePolicyMutationPostfix));

            count += PatchReplicationStoragePolicyMethod(
                harmony,
                "NSMedieval.Stockpiles.ResourcesFilter",
                "ParametersChanged",
                Type.EmptyTypes,
                nameof(ReplicationStoragePolicyParametersChangedPrefix),
                string.Empty);

            count += PatchReplicationStoragePolicyRegisterStorage(
                harmony,
                storageType);
            count += PatchReplicationStoragePolicyUpdatePanel(harmony);
            replicationStoragePolicyCriticalPatchesReady = count == 18;
            LogReplicationInfo(
                "Going Cooperative storage-policy model/UI patches="
                + count.ToString(CultureInfo.InvariantCulture)
                + " criticalReady="
                + replicationStoragePolicyCriticalPatchesReady.ToString().ToLowerInvariant());
            return count;
        }

        private int PatchReplicationStoragePolicyMethod(
            Harmony harmony,
            string typeName,
            string methodName,
            Type[] arguments,
            string prefixName,
            string postfixName)
        {
            var type = AccessTools.TypeByName(typeName);
            var original = type == null ? null : AccessTools.Method(type, methodName, arguments);
            var prefix = string.IsNullOrEmpty(prefixName)
                ? null
                : AccessTools.Method(typeof(GoingCooperativePlugin), prefixName);
            var postfix = string.IsNullOrEmpty(postfixName)
                ? null
                : AccessTools.Method(typeof(GoingCooperativePlugin), postfixName);
            if (original == null || (prefix == null && postfix == null))
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy patch surface missing "
                    + typeName + "." + methodName);
                return 0;
            }

            try
            {
                harmony.Patch(
                    original,
                    prefix == null ? null : new HarmonyMethod(prefix),
                    postfix == null ? null : new HarmonyMethod(postfix));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy patch failed "
                    + typeName + "." + methodName + " "
                    + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private int PatchReplicationStoragePolicyUpdatePanel(Harmony harmony)
        {
            var panelType = AccessTools.TypeByName("NSMedieval.UI.InfoPanelStockpile");
            var selectionType = AccessTools.TypeByName("NSMedieval.UI.SelectionExtraStockpile");
            var original = panelType == null || selectionType == null
                ? null
                : AccessTools.Method(selectionType, "UpdatePanel", new[] { panelType });
            var prefix = AccessTools.Method(
                typeof(GoingCooperativePlugin),
                nameof(ReplicationStoragePolicyUpdatePanelPrefix));
            var finalizer = AccessTools.Method(
                typeof(GoingCooperativePlugin),
                nameof(ReplicationStoragePolicyUpdatePanelPostfix));
            if (original == null || prefix == null || finalizer == null)
            {
                LogReplicationWarning("Going Cooperative storage-policy UpdatePanel wrapper missing");
                return 0;
            }

            try
            {
                // A finalizer, rather than an ordinary postfix, guarantees the depth
                // is balanced if a third-party UI patch or the native panel throws.
                harmony.Patch(
                    original,
                    prefix: new HarmonyMethod(prefix),
                    finalizer: new HarmonyMethod(finalizer));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy UpdatePanel wrapper failed "
                    + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private int PatchReplicationStoragePolicyPaste(
            Harmony harmony,
            string typeName,
            Type storageType)
        {
            var type = AccessTools.TypeByName(typeName);
            var original = type == null
                ? null
                : AccessTools.Method(
                    type,
                    "PasteStorageSettings",
                    new[] { storageType });
            var prefix = AccessTools.Method(
                typeof(GoingCooperativePlugin),
                nameof(ReplicationStoragePolicyPastePrefix));
            var finalizer = AccessTools.Method(
                typeof(GoingCooperativePlugin),
                nameof(ReplicationStoragePolicyPasteFinalizer));
            if (original == null || prefix == null || finalizer == null)
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy paste wrapper missing "
                    + typeName);
                return 0;
            }

            try
            {
                harmony.Patch(
                    original,
                    prefix: new HarmonyMethod(prefix),
                    finalizer: new HarmonyMethod(finalizer));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy paste wrapper failed "
                    + typeName + " " + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private int PatchReplicationStoragePolicyRegisterStorage(
            Harmony harmony,
            Type storageType)
        {
            var managerType = AccessTools.TypeByName(
                "NSMedieval.StorageUniversal.StorageCommonManager");
            var original = managerType == null
                ? null
                : AccessTools.Method(
                    managerType,
                    "RegisterStorage",
                    new[] { storageType, typeof(bool) });
            var prefix = AccessTools.Method(
                typeof(GoingCooperativePlugin),
                nameof(ReplicationStoragePolicyRegisterStoragePrefix));
            var finalizer = AccessTools.Method(
                typeof(GoingCooperativePlugin),
                nameof(ReplicationStoragePolicyRegisterStorageFinalizer));
            if (original == null || prefix == null || finalizer == null)
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy RegisterStorage wrapper missing");
                return 0;
            }

            try
            {
                harmony.Patch(
                    original,
                    prefix: new HarmonyMethod(prefix),
                    finalizer: new HarmonyMethod(finalizer));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy RegisterStorage wrapper failed "
                    + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private static bool ReplicationStoragePolicyShouldSuppressClientModelMutation()
        {
            return StoragePolicyV4Enabled()
                && !replicationConfigHostMode
                && replicationRuntimeStarted
                && !multiplayerLoadingInProgress
                && replicationStoragePolicyAuthoritativeApplyDepth == 0;
        }

        private static bool ReplicationStoragePolicyAllowResourcePrefix(
            object __instance,
            object __0,
            bool __1,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            __state = null;
            if (replicationStoragePolicyUiRefreshDepth > 0)
            {
                return false;
            }
            var suppressClient = ReplicationStoragePolicyShouldSuppressClientModelMutation();
            if (!TryPrepareReplicationStoragePolicyMutation(__instance, out var target, out var storage))
            {
                if (suppressClient)
                {
                    RecordReplicationStoragePolicyFailClosed(
                        "allow-resource-target-resolution-failed");
                    return false;
                }
                return true;
            }

            if (suppressClient)
            {
                if (replicationStoragePolicyUiRefreshDepth == 0)
                {
                    if (!RecordReplicationStoragePolicyResourceOverlay(
                            storage, target, __0, __1, null))
                    {
                        RecordReplicationStoragePolicyFailClosed(
                            "allow-resource-overlay-capture-failed");
                    }
                }
                return false;
            }

            __state = new ReplicationStoragePolicyMutationCapture(
                target,
                storage,
                replicationConfigHostMode);
            return true;
        }

        private static bool ReplicationStorageUniversalAllowResourcePrefix(
            object __instance,
            object __0,
            bool __1,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            __state = null;
            if (replicationStoragePolicyUiRefreshDepth > 0)
            {
                return false;
            }
            var suppressClient = ReplicationStoragePolicyShouldSuppressClientModelMutation();
            var owner = AccessTools.Property(__instance.GetType(), "GetOwner")?.GetValue(__instance, null);
            if (owner == null
                || !TryPrepareReplicationStoragePolicyMutation(owner, out var target, out var storage))
            {
                if (suppressClient)
                {
                    RecordReplicationStoragePolicyFailClosed(
                        "universal-allow-resource-owner-resolution-failed");
                    return false;
                }
                return true;
            }

            if (suppressClient)
            {
                if (replicationStoragePolicyUiRefreshDepth == 0)
                {
                    if (!RecordReplicationStoragePolicyResourceOverlay(
                            storage, target, __0, __1, __instance))
                    {
                        RecordReplicationStoragePolicyFailClosed(
                            "universal-allow-resource-overlay-capture-failed");
                    }
                }
                return false;
            }

            __state = new ReplicationStoragePolicyMutationCapture(
                target,
                storage,
                replicationConfigHostMode);
            return true;
        }

        private static bool ReplicationStoragePolicyPriorityPrefix(
            object __instance,
            object __0,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            if (!replicationConfigHostMode
                && replicationStoragePolicyRegisterStorageDepth > 0)
            {
                // RegisterStorage repairs the native invalid sentinel (0 or 5) and
                // immediately indexes its priority dictionary with the repaired
                // value. Suppressing that internal repair leaves the sentinel in the
                // model and makes registration throw. This wrapper is the only
                // client-side exception to UI-only policy mutation suppression.
                __state = null;
                return true;
            }
            if (!TryConvertReplicationStoragePolicyInt(__0, out var value))
            {
                return ReplicationStoragePolicyUnreadableScalarPrefix(
                    __instance, "priority-value-unreadable", ref __state);
            }
            return ReplicationStoragePolicyScalarPrefix(
                __instance,
                new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.Priority, -1, -1, value, value, false, string.Empty),
                ref __state);
        }

        private static bool ReplicationStoragePolicyProductionPrefix(
            object __instance,
            bool __0,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            return ReplicationStoragePolicyScalarPrefix(
                __instance,
                new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.UseInProduction, -1, -1, 0, 0, __0, string.Empty),
                ref __state);
        }

        private static bool ReplicationStoragePolicyNamePrefix(
            object __instance,
            string __0,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            return ReplicationStoragePolicyScalarPrefix(
                __instance,
                new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.Name, -1, -1, 0, 0, false, __0 ?? string.Empty),
                ref __state);
        }

        private static bool ReplicationStoragePolicyHitPointsPrefix(
            object __instance,
            object __0,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            if (!TryReadReplicationStoragePolicyRange(
                    __0, out var minimum, out var maximum))
            {
                return ReplicationStoragePolicyUnreadableScalarPrefix(
                    __instance, "hit-points-range-unreadable", ref __state);
            }
            return ReplicationStoragePolicyScalarPrefix(
                __instance,
                new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.HitPoints, -1, -1,
                    minimum, maximum, false, string.Empty),
                ref __state);
        }

        private static bool ReplicationStoragePolicyQualityPrefix(
            object __instance,
            object __0,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            if (!TryReadReplicationStoragePolicyRange(
                    __0, out var minimum, out var maximum))
            {
                return ReplicationStoragePolicyUnreadableScalarPrefix(
                    __instance, "quality-range-unreadable", ref __state);
            }
            return ReplicationStoragePolicyScalarPrefix(
                __instance,
                new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.Quality, -1, -1,
                    minimum, maximum, false, string.Empty),
                ref __state);
        }

        private static bool ReplicationStoragePolicyUnreadableScalarPrefix(
            object storageCandidate,
            string failureDetail,
            ref ReplicationStoragePolicyMutationCapture? state)
        {
            state = null;
            if (replicationStoragePolicyUiRefreshDepth > 0)
            {
                return false;
            }

            var suppressClient = ReplicationStoragePolicyShouldSuppressClientModelMutation();
            if (!TryPrepareReplicationStoragePolicyMutation(
                    storageCandidate, out var target, out var storage))
            {
                if (suppressClient)
                {
                    RecordReplicationStoragePolicyFailClosed(
                        failureDetail + ":target-resolution-failed");
                    return false;
                }
                return true;
            }

            if (suppressClient)
            {
                RecordReplicationStoragePolicyFailClosed(failureDetail);
                return false;
            }

            // On the host, preserve native behavior and use the postfix's complete
            // state readback. A client must not invent a value for an unreadable
            // control argument because HP 0..0 is otherwise a valid policy.
            state = new ReplicationStoragePolicyMutationCapture(
                target, storage, replicationConfigHostMode);
            return true;
        }

        private static bool ReplicationStoragePolicyScalarPrefix(
            object storageCandidate,
            ReplicationStoragePolicyCellValue cell,
            ref ReplicationStoragePolicyMutationCapture? state)
        {
            state = null;
            if (replicationStoragePolicyUiRefreshDepth > 0)
            {
                return false;
            }
            var suppressClient = ReplicationStoragePolicyShouldSuppressClientModelMutation();
            if (!TryPrepareReplicationStoragePolicyMutation(
                    storageCandidate,
                    out var target,
                    out var storage))
            {
                if (suppressClient)
                {
                    RecordReplicationStoragePolicyFailClosed(
                        "scalar-target-resolution-failed kind=" + cell.Kind);
                    return false;
                }
                return true;
            }

            if (suppressClient)
            {
                if (replicationStoragePolicyUiRefreshDepth == 0)
                {
                    if (!RecordReplicationStoragePolicyScalarOverlay(storage, target, cell))
                    {
                        RecordReplicationStoragePolicyFailClosed(
                            "scalar-overlay-capture-failed kind=" + cell.Kind);
                    }
                }
                return false;
            }

            state = new ReplicationStoragePolicyMutationCapture(target, storage, replicationConfigHostMode);
            return true;
        }

        private static bool ReplicationStoragePolicyPastePrefix(
            object __instance,
            object __0,
            ref ReplicationStoragePolicyMutationCapture? __state)
        {
            __state = null;
            if (replicationStoragePolicyUiRefreshDepth > 0)
            {
                return false;
            }
            var suppressClient = ReplicationStoragePolicyShouldSuppressClientModelMutation();
            if (!TryPrepareReplicationStoragePolicyMutation(__instance, out var target, out var storage))
            {
                if (suppressClient)
                {
                    RecordReplicationStoragePolicyFailClosed(
                        "paste-target-resolution-failed");
                    return false;
                }
                return true;
            }

            if (suppressClient)
            {
                if (replicationStoragePolicyUiRefreshDepth == 0)
                {
                    if (!RecordReplicationStoragePolicyPasteOverlay(
                            storage, target, __0))
                    {
                        RecordReplicationStoragePolicyFailClosed(
                            "paste-overlay-capture-failed");
                    }
                }
                return false;
            }

            __state = new ReplicationStoragePolicyMutationCapture(
                target,
                storage,
                replicationConfigHostMode);
            ReplicationStoragePolicySnapshot? beforePaste = null;
            if (replicationConfigHostMode
                && !TryReadReplicationStoragePolicySnapshot(
                    storage,
                    target,
                    out beforePaste,
                    out var beforeDetail))
            {
                RequestReplicationStoragePolicyRecovery(
                    "storage-policy-host-paste-prestate-unreadable target="
                    + FormatReplicationStoragePolicyTargetKey(target)
                    + " detail=" + beforeDetail);
                __state = null;
                return false;
            }
            if (replicationConfigHostMode)
            {
                __state.BeforePaste = beforePaste!;
                if (replicationStoragePolicyFilterNotificationSuppressionDepth == 0)
                {
                    ReplicationStoragePolicyDeferredFilterNotifications.Clear();
                }
                replicationStoragePolicyFilterNotificationSuppressionDepth++;
                __state.PasteNotificationScopeEntered = true;
            }
            return true;
        }

        private static void RecordReplicationStoragePolicyFailClosed(string detail)
        {
            replicationStoragePolicyLastFailClosedDetail = detail;
            if (Time.realtimeSinceStartup >= replicationStoragePolicyNextFailClosedLogRealtime)
            {
                replicationStoragePolicyNextFailClosedLogRealtime = Time.realtimeSinceStartup + 2f;
                instance?.LogReplicationWarning(
                    "Going Cooperative storage-policy client-ui-only mutation suppressed without capture detail="
                    + detail);
            }

            // The next native panel tick restores the authoritative model value. Mark
            // the infrequently-refreshed controls dirty as well so a failed identity
            // lookup never leaves a changed widget masquerading as accepted state.
            RequestReplicationStoragePolicyPanelCorrection();
        }

        private static void ReplicationStoragePolicyMutationPostfix(
            ReplicationStoragePolicyMutationCapture? __state)
        {
            if (__state == null
                || !__state.HostCapture
                || replicationStoragePolicyAuthoritativeApplyDepth > 0
                || replicationStoragePolicyUiRefreshDepth > 0
                || !replicationConfigHostMode
                || !replicationRuntimeStarted)
            {
                return;
            }

            MarkReplicationStoragePolicyHostDirty(
                __state.Storage,
                __state.Target);
        }

        private static Exception? ReplicationStoragePolicyPasteFinalizer(
            ReplicationStoragePolicyMutationCapture? __state,
            Exception? __exception)
        {
            if (__state == null)
            {
                return __exception;
            }

            var targetKey = FormatReplicationStoragePolicyTargetKey(__state.Target);
            try
            {
                if (!__state.HostCapture
                    || __state.BeforePaste == null
                    || replicationStoragePolicyUiRefreshDepth > 0
                    || !replicationConfigHostMode
                    || !replicationRuntimeStarted)
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-host-paste-finalizer-state-invalid target="
                        + targetKey);
                    return __exception;
                }

                if (__exception != null)
                {
                    var rollbackSucceeded = false;
                    var rollbackSideEffectsStarted = false;
                    var rollbackDetail = "not-attempted";
                    replicationStoragePolicyAuthoritativeApplyDepth++;
                    try
                    {
                        rollbackSucceeded = TryRollbackReplicationStoragePolicySnapshot(
                            __state.Storage,
                            __state.BeforePaste,
                            out rollbackSideEffectsStarted,
                            out rollbackDetail);
                    }
                    finally
                    {
                        replicationStoragePolicyAuthoritativeApplyDepth--;
                    }
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-host-paste-native-exception target="
                        + targetKey + " failure="
                        + FormatReflectionExceptionDetail(__exception)
                        + " rollback=" + rollbackDetail
                        + " rollbackSucceeded="
                        + rollbackSucceeded.ToString().ToLowerInvariant()
                        + " rollbackSideEffects="
                        + rollbackSideEffectsStarted.ToString().ToLowerInvariant());
                    return __exception;
                }

                if (!TryReadReplicationStoragePolicySnapshot(
                        __state.Storage,
                        __state.Target,
                        out var nativePasteResult,
                        out var readDetail))
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-host-paste-poststate-unreadable target="
                        + targetKey + " detail=" + readDetail);
                    return __exception;
                }

                // Ground paste assigns its priority field directly. Restore only
                // that field to the captured value so the following native setter
                // can tell StorageCommonManager the true old bucket and reindex it.
                if (!TryRestoreReplicationStoragePolicyGroundPastePriority(
                        __state.Storage,
                        __state.BeforePaste,
                        nativePasteResult,
                        out var priorityDetail))
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-host-paste-priority-reindex-unavailable target="
                        + targetKey + " detail=" + priorityDetail);
                    return __exception;
                }

                // Native paste writes ResourcesFilter directly, unlike leaf/group
                // toggles. Reconcile the before/after difference through the same
                // native AllowResource boundary used for client-issued absolute cells.
                var normalized = false;
                var irreversibleResourceSideEffectsStarted = false;
                var normalizeDetail = "not-applied";
                replicationStoragePolicyAuthoritativeApplyDepth++;
                try
                {
                    normalized = TryApplyReplicationStoragePolicySnapshot(
                        __state.Storage,
                        __state.BeforePaste,
                        nativePasteResult,
                        out irreversibleResourceSideEffectsStarted,
                        out normalizeDetail);
                    if (normalized)
                    {
                        normalized = TryReadReplicationStoragePolicySnapshot(
                                __state.Storage,
                                __state.Target,
                                out var readback,
                                out readDetail)
                            && ReplicationStoragePolicySnapshotsEqual(
                                readback,
                                nativePasteResult);
                    }
                }
                catch (Exception ex)
                {
                    normalizeDetail = "exception="
                        + FormatReflectionExceptionDetail(ex);
                    normalized = false;
                }
                finally
                {
                    replicationStoragePolicyAuthoritativeApplyDepth--;
                }

                if (!normalized)
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-host-paste-normalization-unproven target="
                        + targetKey + " normalize=" + normalizeDetail
                        + " read=" + readDetail
                        + " priority=" + priorityDetail
                        + " nativeSideEffects="
                        + irreversibleResourceSideEffectsStarted.ToString().ToLowerInvariant());
                    return __exception;
                }

                MarkReplicationStoragePolicyHostDirty(
                    __state.Storage,
                    __state.Target);
                return __exception;
            }
            finally
            {
                if (__state.PasteNotificationScopeEntered)
                {
                    if (replicationStoragePolicyFilterNotificationSuppressionDepth > 0)
                    {
                        replicationStoragePolicyFilterNotificationSuppressionDepth--;
                    }
                    __state.PasteNotificationScopeEntered = false;
                    if (replicationStoragePolicyFilterNotificationSuppressionDepth == 0
                        && !FlushReplicationStoragePolicyDeferredFilterNotifications(
                            out var notificationDetail))
                    {
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-host-paste-notification-failed target="
                            + targetKey + " detail=" + notificationDetail);
                    }
                }
            }
        }

        private static bool TryRestoreReplicationStoragePolicyGroundPastePriority(
            object storage,
            ReplicationStoragePolicySnapshot before,
            ReplicationStoragePolicySnapshot after,
            out string detail)
        {
            if (before.Priority == after.Priority
                || !string.Equals(
                    storage.GetType().FullName,
                    "NSMedieval.Stockpiles.StockpileInstance",
                    StringComparison.Ordinal))
            {
                detail = "not-required";
                return true;
            }

            var priorityField = AccessTools.Field(storage.GetType(), "priority");
            if (priorityField == null || !priorityField.FieldType.IsEnum)
            {
                detail = "ground-priority-field-missing";
                return false;
            }
            try
            {
                priorityField.SetValue(
                    storage,
                    Enum.ToObject(priorityField.FieldType, before.Priority));
                detail = "restored-before="
                    + before.Priority.ToString(CultureInfo.InvariantCulture)
                    + " desired="
                    + after.Priority.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool ReplicationStoragePolicyParametersChangedPrefix(object __instance)
        {
            if (replicationStoragePolicyFilterNotificationSuppressionDepth <= 0)
            {
                return true;
            }

            ReplicationStoragePolicyDeferredFilterNotifications.Add(__instance);
            return false;
        }

        private static void ReplicationStoragePolicyRegisterStoragePrefix()
        {
            replicationStoragePolicyRegisterStorageDepth++;
        }

        private static Exception? ReplicationStoragePolicyRegisterStorageFinalizer(
            Exception? __exception)
        {
            if (replicationStoragePolicyRegisterStorageDepth > 0)
            {
                replicationStoragePolicyRegisterStorageDepth--;
            }
            return __exception;
        }

        private static void ReplicationStoragePolicyUpdatePanelPrefix(
            object __instance,
            object __0)
        {
            if (TryGetListMember(__instance, "storageObjects", out var current)
                && TryGetListMember(__0, "StorageObjects", out var incoming)
                && !ReplicationStoragePolicyPanelBindingsMatch(current, incoming))
            {
                // Native UpdatePanel dirties these controls only when the selection
                // count changes. Rebinding one selected storage to another otherwise
                // leaves the old name/ranges visible and can turn the next edit into
                // a value copied from the previous target.
                TrySetInstanceMemberValue(__instance, "refreshSliders", true);
                TrySetInstanceMemberValue(__instance, "refreshInput", true);
            }
            replicationStoragePolicyUiRefreshDepth++;
        }

        private static bool ReplicationStoragePolicyPanelBindingsMatch(
            IList current,
            IList incoming)
        {
            if (current.Count != incoming.Count)
            {
                return false;
            }
            for (var i = 0; i < current.Count; i++)
            {
                if (!ReferenceEquals(current[i], incoming[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static Exception? ReplicationStoragePolicyUpdatePanelPostfix(
            object __instance,
            Exception? __exception)
        {
            if (replicationStoragePolicyUiRefreshDepth > 0)
            {
                replicationStoragePolicyUiRefreshDepth--;
            }

            if (__exception == null)
            {
                RepaintReplicationStoragePolicyPendingOverlay(__instance);
            }
            return __exception;
        }

        private static bool TryPrepareReplicationStoragePolicyMutation(
            object candidate,
            out ReplicationStoragePolicyTargetReference target,
            out object storage)
        {
            target = new ReplicationStoragePolicyTargetReference();
            storage = candidate;
            if (replicationStoragePolicyAuthoritativeApplyDepth > 0)
            {
                return TryCreateReplicationStoragePolicyTargetReference(
                    candidate, out target, out storage, out _);
            }

            if (!StoragePolicyV4Enabled()
                || !replicationRuntimeStarted
                || multiplayerLoadingInProgress)
            {
                return false;
            }

            return TryCreateReplicationStoragePolicyTargetReference(
                candidate, out target, out storage, out _);
        }

        private static void MarkReplicationStoragePolicyHostDirty(
            object storage,
            ReplicationStoragePolicyTargetReference target)
        {
            if (!StoragePolicyV4Enabled() || replicationStoragePolicyFailStopped)
            {
                return;
            }

            var key = FormatReplicationStoragePolicyTargetKey(target);
            ReplicationStoragePolicyRetryAtByKey.Remove("host-dirty|" + key);
            ReplicationStoragePolicyHostDirtyByTarget[key] =
                new ReplicationStoragePolicyDirtyTarget(target.Clone(), storage);
        }

        private static void QueueReplicationStoragePolicyBaseline()
        {
            if (!replicationConfigHostMode
                || !StoragePolicyV4Enabled()
                || !replicationRuntimeStarted)
            {
                return;
            }
            if (!EnsureReplicationStoragePolicyRuntimeEpoch()
                || replicationStoragePolicyFailStopped)
            {
                return;
            }

            var managerType = AccessTools.TypeByName(
                "NSMedieval.StorageUniversal.StorageCommonManager");
            var manager = managerType == null
                ? null
                : AccessTools.Property(managerType, "Instance")?.GetValue(null, null);
            var allStorages = manager == null || managerType == null
                ? null
                : AccessTools.Property(managerType, "AllStorages")
                    ?.GetValue(manager, null) as IEnumerable;
            if (allStorages == null)
            {
                RequestReplicationStoragePolicyRecovery(
                    "storage-policy-baseline-manager-missing");
                return;
            }

            var queued = 0;
            var currentKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in allStorages)
            {
                if (candidate == null
                    || IsReplicationStoragePolicyStorageDisposed(candidate))
                {
                    continue;
                }
                if (!TryCreateReplicationStoragePolicyTargetReference(
                        candidate,
                        out var target,
                        out var storage,
                        out var targetDetail))
                {
                    // There is no baseline manifest whose receiver could use to
                    // discover an omitted row. A live storage that cannot be named
                    // must therefore fail closed instead of producing a silently
                    // incomplete recovery set.
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-baseline-live-target-unnameable detail="
                        + targetDetail);
                    return;
                }
                MarkReplicationStoragePolicyHostDirty(storage, target);
                currentKeys.Add(FormatReplicationStoragePolicyTargetKey(target));
                queued++;
            }

            var knownKeys = new List<string>(ReplicationStoragePolicyHostKnownTargets.Keys);
            for (var i = 0; i < knownKeys.Count; i++)
            {
                var knownKey = knownKeys[i];
                if (currentKeys.Contains(knownKey)
                    || !ReplicationStoragePolicyHostKnownTargets.TryGetValue(
                        knownKey, out var known))
                {
                    continue;
                }

                if (TryGetReplicationLocalObjectByHostId(
                        known.HostUid, out var mapped, out _)
                    && mapped != null
                    && IsReplicationStoragePolicyStorageDisposed(mapped))
                {
                    ReplicationStoragePolicyHostDirtyByTarget[knownKey] =
                        new ReplicationStoragePolicyDirtyTarget(
                            known.Clone(), mapped);
                    queued++;
                    continue;
                }

                // Absence from a manager enumeration is not deletion proof. A
                // reconnect baseline that cannot account for a previously published
                // canonical target must recover rather than silently omit it.
                RequestReplicationStoragePolicyRecovery(
                    "storage-policy-baseline-known-target-unaccounted target="
                    + knownKey);
                return;
            }

            instance?.LogReplicationInfo(
                "Going Cooperative storage-policy baseline queued targets="
                + queued.ToString(CultureInfo.InvariantCulture)
                + " budgetPerFrame="
                + ReplicationStoragePolicyHostFlushBudget.ToString(
                    CultureInfo.InvariantCulture));
            replicationStoragePolicyLastBaselineEpoch =
                GetReplicationStoragePolicyEpoch();
        }

        private static bool EnsureReplicationStoragePolicyRuntimeEpoch()
        {
            var currentEpoch = GetReplicationStoragePolicyEpoch();
            if (currentEpoch <= 0L)
            {
                return false;
            }
            if (replicationStoragePolicyRuntimeEpoch == currentEpoch)
            {
                return true;
            }
            if (replicationStoragePolicyRuntimeEpoch == 0L)
            {
                replicationStoragePolicyRuntimeEpoch = currentEpoch;
                return true;
            }

            var previousEpoch = replicationStoragePolicyRuntimeEpoch;
            PurgeReplicationStoragePolicyWorldObjectDeltas();
            PurgeReplicationStoragePolicyCommandsForPriorEpoch();
            ResetReplicationStoragePolicyRuntimeState();
            replicationStoragePolicyRuntimeEpoch = currentEpoch;
            instance?.LogReplicationInfo(
                "Going Cooperative storage-policy epoch transitioned previous="
                + previousEpoch.ToString(CultureInfo.InvariantCulture)
                + " current=" + currentEpoch.ToString(CultureInfo.InvariantCulture)
                + " staleCommandsAndStates=purged");
            return true;
        }

        private static void PurgeReplicationStoragePolicyCommandsForPriorEpoch()
        {
            var pendingKeys = new List<string>();
            foreach (var pair in ReplicationPendingCommandIntents)
            {
                if (LockstepCommandPayloads.TryReadStoragePolicyUpdatePayload(
                        pair.Value.Command.PayloadJson, out _))
                {
                    pendingKeys.Add(pair.Key);
                }
            }
            for (var i = 0; i < pendingKeys.Count; i++)
            {
                ReplicationPendingCommandIntents.Remove(pendingKeys[i]);
            }

            var resultKeys = new List<string>();
            foreach (var pair in replicationHostCommandIntentResults)
            {
                if (pair.Key.IndexOf(":storage:", StringComparison.Ordinal) >= 0)
                {
                    resultKeys.Add(pair.Key);
                }
            }
            for (var i = 0; i < resultKeys.Count; i++)
            {
                replicationHostCommandIntentResults.Remove(resultKeys[i]);
            }
        }

        private static bool RecordReplicationStoragePolicyScalarOverlay(
            object storage,
            ReplicationStoragePolicyTargetReference target,
            ReplicationStoragePolicyCellValue cell)
        {
            if (cell.Kind == ReplicationStoragePolicyCellKind.Priority
                && (cell.Minimum < ReplicationStoragePolicyMinimumPriority
                    || cell.Minimum > ReplicationStoragePolicyMaximumPriority))
            {
                return false;
            }
            if (cell.Kind == ReplicationStoragePolicyCellKind.HitPoints
                && (cell.Minimum < 0 || cell.Maximum > 100 || cell.Minimum > cell.Maximum))
            {
                return false;
            }
            if (cell.Kind == ReplicationStoragePolicyCellKind.Quality
                && (cell.Minimum < ReplicationStoragePolicyMinimumQuality
                    || cell.Maximum > ReplicationStoragePolicyMaximumQuality
                    || cell.Minimum > cell.Maximum))
            {
                return false;
            }
            if (cell.Kind == ReplicationStoragePolicyCellKind.Name
                && !IsValidReplicationStoragePolicyName(cell.Text))
            {
                return false;
            }

            ReplicationStoragePolicySnapshot? rangeSnapshot = null;
            if ((cell.Kind == ReplicationStoragePolicyCellKind.HitPoints
                    || cell.Kind == ReplicationStoragePolicyCellKind.Quality)
                && !TryReadReplicationStoragePolicySnapshot(
                    storage, target, out rangeSnapshot, out _))
            {
                return false;
            }

            var pending = GetOrCreateReplicationStoragePolicyPendingTarget(target, storage);
            if (pending == null)
            {
                return false;
            }

            if (cell.Kind == ReplicationStoragePolicyCellKind.HitPoints
                || cell.Kind == ReplicationStoragePolicyCellKind.Quality)
            {
                for (var slot = 0; slot < rangeSnapshot!.Slots.Count; slot++)
                {
                    var slotCell = new ReplicationStoragePolicyCellValue(
                        cell.Kind, slot, -1, cell.Minimum, cell.Maximum,
                        false, string.Empty);
                    SetReplicationStoragePolicyPendingCell(pending, slotCell);
                }
            }
            else
            {
                SetReplicationStoragePolicyPendingCell(pending, cell);
            }
            return true;
        }

        private static bool IsValidReplicationStoragePolicyName(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0)
            {
                return false;
            }

            var scalarCount = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsLowSurrogate(value[i]))
                {
                    return false;
                }
                if (char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    {
                        return false;
                    }
                    i++;
                }
                scalarCount++;
            }

            return scalarCount <= StoragePolicyPayloadCodec.MaximumNameCharacters
                && Encoding.UTF8.GetByteCount(value)
                    <= StoragePolicyPayloadCodec.MaximumNameUtf8Bytes;
        }

        private static bool RecordReplicationStoragePolicyResourceOverlay(
            object storage,
            ReplicationStoragePolicyTargetReference target,
            object resource,
            bool allowed,
            object? universalStorage)
        {
            if (!TryGetReplicationStoragePolicyCatalog(out var catalog, out _)
                || !TryGetReplicationStoragePolicyCatalogIndex(catalog, resource, out var resourceIndex)
                || !TryReadReplicationStoragePolicyCaptureSnapshot(
                    storage, target, out var snapshot, out _))
            {
                return false;
            }

            var pending = GetOrCreateReplicationStoragePolicyPendingTarget(target, storage);
            if (pending == null)
            {
                return false;
            }

            var recorded = 0;
            for (var slot = 0; slot < snapshot.Slots.Count; slot++)
            {
                var slotSnapshot = snapshot.Slots[slot];
                if (universalStorage != null
                    && !ReferenceEquals(slotSnapshot.UniversalStorage, universalStorage))
                {
                    continue;
                }

                // UniversalStorage ignores resources outside the slot's blueprint
                // capability. Mirror that no-op rather than sending an invalid cell.
                if (!slotSnapshot.DefaultAllowed[resourceIndex])
                {
                    continue;
                }

                var cell = new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.Resource,
                    slot,
                    resourceIndex,
                    0,
                    0,
                    allowed,
                    string.Empty);
                SetReplicationStoragePolicyPendingCell(pending, cell);
                recorded++;
            }
            return recorded > 0;
        }

        private static bool TryReadReplicationStoragePolicyCaptureSnapshot(
            object storage,
            ReplicationStoragePolicyTargetReference target,
            out ReplicationStoragePolicySnapshot snapshot,
            out string detail)
        {
            var frame = Time.frameCount;
            if (replicationStoragePolicyCaptureSnapshotFrame != frame)
            {
                replicationStoragePolicyCaptureSnapshotFrame = frame;
                ReplicationStoragePolicyCaptureSnapshotsByStorage.Clear();
            }

            var localKey = GetReplicationLocalObjectKey(storage);
            if (ReplicationStoragePolicyCaptureSnapshotsByStorage.TryGetValue(
                    localKey, out var cached)
                && ReferenceEquals(cached.Item1, storage))
            {
                snapshot = cached.Item2;
                detail = "ok capture-cache";
                return true;
            }

            if (!TryReadReplicationStoragePolicySnapshot(
                    storage, target, out snapshot, out detail))
            {
                return false;
            }

            ReplicationStoragePolicyCaptureSnapshotsByStorage[localKey] =
                Tuple.Create(storage, snapshot);
            return true;
        }

        private static ReplicationStoragePolicyPendingTarget?
            GetOrCreateReplicationStoragePolicyPendingTarget(
                ReplicationStoragePolicyTargetReference target,
                object storage)
        {
            if (replicationStoragePolicyFailStopped)
            {
                return null;
            }

            var key = FormatReplicationStoragePolicyTargetKey(target);
            if (ReplicationStoragePolicyPendingByTarget.TryGetValue(key, out var existing))
            {
                existing.Target = target.Clone();
                existing.Storage = storage;
                MergeReplicationStoragePolicyPendingTargetsForStorage(
                    key, existing, storage);
                return existing;
            }

            var referenceKey = string.Empty;
            ReplicationStoragePolicyPendingTarget? byReference = null;
            foreach (var pair in ReplicationStoragePolicyPendingByTarget)
            {
                if (!ReferenceEquals(pair.Value.Storage, storage))
                {
                    continue;
                }
                referenceKey = pair.Key;
                byReference = pair.Value;
                break;
            }
            if (byReference != null)
            {
                byReference.Target = target.Clone();
                byReference.Storage = storage;
                if (!string.Equals(referenceKey, key, StringComparison.Ordinal))
                {
                    ReplicationStoragePolicyPendingByTarget.Remove(referenceKey);
                    if (ReplicationStoragePolicyPendingByTarget.TryGetValue(
                            key, out var canonicalPending))
                    {
                        MergeReplicationStoragePolicyPendingTarget(
                            canonicalPending, byReference);
                        byReference = canonicalPending;
                    }
                    else
                    {
                        ReplicationStoragePolicyPendingByTarget[key] = byReference;
                    }
                }
                return byReference;
            }

            // A disconnected client can retain unproved UI-only edits until a fresh
            // hello/state proof arrives. Bound distinct targets without eviction:
            // evicting an accepted-looking edit would be silent data loss, while
            // rejecting this gesture lets the caller restore the authoritative UI.
            if (ReplicationStoragePolicyPendingByTarget.Count
                >= ReplicationStoragePolicyMaximumPendingTargets)
            {
                return null;
            }

            var pending = new ReplicationStoragePolicyPendingTarget(target.Clone(), storage);
            ReplicationStoragePolicyPendingByTarget.Add(key, pending);
            return pending;
        }

        private static void MergeReplicationStoragePolicyPendingTargetsForStorage(
            string destinationKey,
            ReplicationStoragePolicyPendingTarget destination,
            object storage)
        {
            var duplicateKeys = new List<string>();
            foreach (var pair in ReplicationStoragePolicyPendingByTarget)
            {
                if (string.Equals(pair.Key, destinationKey, StringComparison.Ordinal)
                    || !ReferenceEquals(pair.Value.Storage, storage))
                {
                    continue;
                }
                MergeReplicationStoragePolicyPendingTarget(destination, pair.Value);
                duplicateKeys.Add(pair.Key);
            }
            for (var i = 0; i < duplicateKeys.Count; i++)
            {
                ReplicationStoragePolicyPendingByTarget.Remove(duplicateKeys[i]);
            }
        }

        private static void MergeReplicationStoragePolicyPendingTarget(
            ReplicationStoragePolicyPendingTarget destination,
            ReplicationStoragePolicyPendingTarget source)
        {
            if (destination.InFlightSequence <= 0L)
            {
                destination.InFlightSequence = source.InFlightSequence;
            }
            else if (source.InFlightSequence > 0L
                && source.InFlightSequence != destination.InFlightSequence)
            {
                RequestReplicationStoragePolicyRecovery(
                    "storage-policy-pending-merge-multiple-in-flight first="
                    + destination.InFlightSequence.ToString(CultureInfo.InvariantCulture)
                    + " second="
                    + source.InFlightSequence.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var pair in source.Cells)
            {
                source.SequenceByCell.TryGetValue(pair.Key, out var sourceSequence);
                var destinationHasCell = destination.Cells.ContainsKey(pair.Key);
                destination.SequenceByCell.TryGetValue(pair.Key, out var destinationSequence);
                var sourceIsNewer = !destinationHasCell
                    || (sourceSequence <= 0L && destinationSequence > 0L)
                    || (sourceSequence > 0L
                        && destinationSequence > 0L
                        && sourceSequence > destinationSequence);
                if (!sourceIsNewer)
                {
                    continue;
                }

                destination.Cells[pair.Key] = pair.Value;
                if (sourceSequence > 0L)
                {
                    destination.SequenceByCell[pair.Key] = sourceSequence;
                }
                else
                {
                    destination.SequenceByCell.Remove(pair.Key);
                }
            }
        }

        private static bool RecordReplicationStoragePolicyPasteOverlay(
            object destination,
            ReplicationStoragePolicyTargetReference destinationTarget,
            object source)
        {
            if (!TryCreateReplicationStoragePolicyTargetReference(
                    source,
                    out var sourceTarget,
                    out var sourceStorage,
                    out _)
                || !TryReadEffectiveReplicationStoragePolicySnapshot(
                    sourceStorage,
                    sourceTarget,
                    out var sourceState,
                    out _)
                || !TryReadEffectiveReplicationStoragePolicySnapshot(
                    destination,
                    destinationTarget,
                    out var destinationState,
                    out _))
            {
                return false;
            }

            var pending = GetOrCreateReplicationStoragePolicyPendingTarget(
                destinationTarget, destination);
            if (pending == null || sourceState.Slots.Count == 0)
            {
                return false;
            }

            var production = new ReplicationStoragePolicyCellValue(
                ReplicationStoragePolicyCellKind.UseInProduction,
                -1, -1, 0, 0, sourceState.UseInProduction, string.Empty);
            SetReplicationStoragePolicyPendingCell(pending, production);

            // StorageCommonManager performs SetPriority immediately after the paste;
            // recording it here as well makes direct IStorage paste calls complete.
            var priority = new ReplicationStoragePolicyCellValue(
                ReplicationStoragePolicyCellKind.Priority,
                -1, -1, sourceState.Priority, sourceState.Priority,
                false, string.Empty);
            SetReplicationStoragePolicyPendingCell(pending, priority);

            var sourceAllowedUnion = new bool[sourceState.Slots[0].Allowed.Length];
            for (var sourceSlot = 0; sourceSlot < sourceState.Slots.Count; sourceSlot++)
            {
                for (var resource = 0; resource < sourceAllowedUnion.Length; resource++)
                {
                    sourceAllowedUnion[resource] |= sourceState.Slots[sourceSlot].Allowed[resource];
                }
            }

            // Native cross-type paste takes HP/quality from IStorage.ResourcesFilter,
            // which is the first shelf slot, while resource membership is the union
            // of every source shelf slot intersected with each destination capability.
            var sourceFilter = sourceState.Slots[0];
            for (var slot = 0; slot < destinationState.Slots.Count; slot++)
            {
                var destinationSlot = destinationState.Slots[slot];
                var hp = new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.HitPoints,
                    slot, -1,
                    sourceFilter.HitPointsMinimum,
                    sourceFilter.HitPointsMaximum,
                    false,
                    string.Empty);
                var quality = new ReplicationStoragePolicyCellValue(
                    ReplicationStoragePolicyCellKind.Quality,
                    slot, -1,
                    sourceFilter.QualityMinimum,
                    sourceFilter.QualityMaximum,
                    false,
                    string.Empty);
                SetReplicationStoragePolicyPendingCell(pending, hp);
                SetReplicationStoragePolicyPendingCell(pending, quality);

                for (var resource = 0; resource < sourceAllowedUnion.Length; resource++)
                {
                    var desired = sourceAllowedUnion[resource]
                        && destinationSlot.DefaultAllowed[resource];
                    if (desired == destinationSlot.Allowed[resource])
                    {
                        continue;
                    }

                    var cell = new ReplicationStoragePolicyCellValue(
                        ReplicationStoragePolicyCellKind.Resource,
                        slot,
                        resource,
                        0,
                        0,
                        desired,
                        string.Empty);
                    SetReplicationStoragePolicyPendingCell(pending, cell);
                }
            }
            return true;
        }

        private static bool TryReadEffectiveReplicationStoragePolicySnapshot(
            object storage,
            ReplicationStoragePolicyTargetReference target,
            out ReplicationStoragePolicySnapshot snapshot,
            out string detail)
        {
            if (!TryReadReplicationStoragePolicySnapshot(storage, target, out snapshot, out detail))
            {
                return false;
            }

            var key = FormatReplicationStoragePolicyTargetKey(target);
            if (TryGetReplicationStoragePolicyPendingTarget(
                    key, storage, out var pending))
            {
                foreach (var pair in pending.Cells)
                {
                    ApplyReplicationStoragePolicyCellToSnapshot(snapshot, pair.Value);
                }
            }
            return true;
        }

        private static bool TryGetReplicationStoragePolicyPendingTarget(
            string targetKey,
            object storage,
            out ReplicationStoragePolicyPendingTarget pending)
        {
            if (ReplicationStoragePolicyPendingByTarget.TryGetValue(
                    targetKey, out pending!))
            {
                return true;
            }
            foreach (var pair in ReplicationStoragePolicyPendingByTarget)
            {
                if (ReferenceEquals(pair.Value.Storage, storage))
                {
                    pending = pair.Value;
                    return true;
                }
            }
            pending = null!;
            return false;
        }

        private static void ApplyReplicationStoragePolicyCellToSnapshot(
            ReplicationStoragePolicySnapshot snapshot,
            ReplicationStoragePolicyCellValue cell)
        {
            switch (cell.Kind)
            {
                case ReplicationStoragePolicyCellKind.Name:
                    snapshot.Name = cell.Text;
                    break;
                case ReplicationStoragePolicyCellKind.Priority:
                    snapshot.Priority = cell.Minimum;
                    break;
                case ReplicationStoragePolicyCellKind.UseInProduction:
                    snapshot.UseInProduction = cell.Enabled;
                    break;
                case ReplicationStoragePolicyCellKind.HitPoints:
                    if (cell.Slot >= 0 && cell.Slot < snapshot.Slots.Count)
                    {
                        snapshot.Slots[cell.Slot].HitPointsMinimum = cell.Minimum;
                        snapshot.Slots[cell.Slot].HitPointsMaximum = cell.Maximum;
                    }
                    break;
                case ReplicationStoragePolicyCellKind.Quality:
                    if (cell.Slot >= 0 && cell.Slot < snapshot.Slots.Count)
                    {
                        snapshot.Slots[cell.Slot].QualityMinimum = cell.Minimum;
                        snapshot.Slots[cell.Slot].QualityMaximum = cell.Maximum;
                    }
                    break;
                case ReplicationStoragePolicyCellKind.Resource:
                    if (cell.Slot >= 0
                        && cell.Slot < snapshot.Slots.Count
                        && cell.Resource >= 0
                        && cell.Resource < snapshot.Slots[cell.Slot].Allowed.Length)
                    {
                        snapshot.Slots[cell.Slot].Allowed[cell.Resource] = cell.Enabled;
                    }
                    break;
            }
        }

        private static string FormatReplicationStoragePolicyCellKey(
            ReplicationStoragePolicyCellValue cell)
        {
            return cell.Kind switch
            {
                ReplicationStoragePolicyCellKind.Name => "common|name",
                ReplicationStoragePolicyCellKind.Priority => "common|priority",
                ReplicationStoragePolicyCellKind.UseInProduction => "common|production",
                ReplicationStoragePolicyCellKind.HitPoints => "slot|"
                    + cell.Slot.ToString(CultureInfo.InvariantCulture) + "|hp",
                ReplicationStoragePolicyCellKind.Quality => "slot|"
                    + cell.Slot.ToString(CultureInfo.InvariantCulture) + "|quality",
                ReplicationStoragePolicyCellKind.Resource => "slot|"
                    + cell.Slot.ToString(CultureInfo.InvariantCulture) + "|resource|"
                    + cell.Resource.ToString(CultureInfo.InvariantCulture),
                _ => string.Empty
            };
        }

        private static void SetReplicationStoragePolicyPendingCell(
            ReplicationStoragePolicyPendingTarget pending,
            ReplicationStoragePolicyCellValue cell)
        {
            var key = FormatReplicationStoragePolicyCellKey(cell);
            pending.Cells[key] = cell;
            // A new local edit supersedes the previously-sent value for this cell.
            // Its sequence is assigned only after the new sparse batch is queued.
            pending.SequenceByCell.Remove(key);
            ReplicationStoragePolicyRetryAtByKey.Remove(
                "client-pending|" + FormatReplicationStoragePolicyTargetKey(pending.Target));
        }

        private static string FormatReplicationStoragePolicyTargetKey(
            ReplicationStoragePolicyTargetReference target)
        {
            var epochPrefix = "epoch="
                + GetReplicationStoragePolicyEpoch().ToString(
                    CultureInfo.InvariantCulture) + "|";
            if (target.Canonical && target.HostUid > 0L)
            {
                return epochPrefix + target.Kind + "|host="
                    + target.HostUid.ToString(CultureInfo.InvariantCulture)
                    + "|component="
                    + target.ComponentOrdinal.ToString(CultureInfo.InvariantCulture);
            }

            return epochPrefix + target.Kind + "|candidate="
                + target.HostUid.ToString(CultureInfo.InvariantCulture)
                + "|component="
                + target.ComponentOrdinal.ToString(CultureInfo.InvariantCulture)
                + "|blueprint=" + target.BlueprintFingerprint
                + "|anchor="
                + target.AnchorX.ToString(CultureInfo.InvariantCulture) + ","
                + target.AnchorY.ToString(CultureInfo.InvariantCulture) + ","
                + target.AnchorZ.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryConvertReplicationStoragePolicyInt(object? value, out int parsed)
        {
            parsed = 0;
            if (value == null)
            {
                return false;
            }
            try
            {
                parsed = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadReplicationStoragePolicyRange(
            object? range,
            out int minimum,
            out int maximum)
        {
            minimum = 0;
            maximum = 0;
            if (range == null
                || !TryReadInstanceMemberValue(range, "Min", out var minValue)
                || !TryReadInstanceMemberValue(range, "Max", out var maxValue)
                || !TryConvertReplicationStoragePolicyInt(minValue, out minimum)
                || !TryConvertReplicationStoragePolicyInt(maxValue, out maximum))
            {
                return false;
            }
            return true;
        }

        private static StoragePolicyTarget ToReplicationStoragePolicyWireTarget(
            ReplicationStoragePolicyTargetReference target,
            bool forceCanonical = false)
        {
            return new StoragePolicyTarget(
                string.Equals(target.Kind, ReplicationStoragePolicyGroundKind, StringComparison.Ordinal)
                    ? StoragePolicyTargetKind.GroundStockpile
                    : StoragePolicyTargetKind.Shelf,
                target.HostUid,
                forceCanonical || target.Canonical,
                target.BlueprintFingerprint,
                target.ComponentOrdinal,
                new StoragePolicyAnchor(target.AnchorX, target.AnchorY, target.AnchorZ));
        }

        private static ReplicationStoragePolicyTargetReference
            FromReplicationStoragePolicyWireTarget(StoragePolicyTarget target)
        {
            return new ReplicationStoragePolicyTargetReference
            {
                Kind = target.Kind == StoragePolicyTargetKind.GroundStockpile
                    ? ReplicationStoragePolicyGroundKind
                    : ReplicationStoragePolicyShelfKind,
                HostUid = target.HostUidCandidate,
                Canonical = target.IsCanonicalHostUid,
                ComponentOrdinal = target.ComponentOrdinal,
                BlueprintFingerprint = target.BlueprintFingerprint,
                AnchorX = target.Anchor.X,
                AnchorY = target.Anchor.Y,
                AnchorZ = target.Anchor.Z
            };
        }

        private static bool TryCreateReplicationStoragePolicyTargetReference(
            object candidate,
            out ReplicationStoragePolicyTargetReference target,
            out object storage,
            out string detail)
        {
            target = new ReplicationStoragePolicyTargetReference();
            storage = candidate;
            var typeName = candidate.GetType().FullName ?? candidate.GetType().Name;
            if (string.Equals(
                    typeName,
                    "NSMedieval.StorageUniversal.UniversalStorage",
                    StringComparison.Ordinal))
            {
                storage = AccessTools.Property(candidate.GetType(), "GetOwner")
                    ?.GetValue(candidate, null) ?? candidate;
                typeName = storage.GetType().FullName ?? storage.GetType().Name;
            }

            var ground = string.Equals(
                typeName,
                "NSMedieval.Stockpiles.StockpileInstance",
                StringComparison.Ordinal);
            var shelf = string.Equals(
                typeName,
                "NSMedieval.BuildingComponents.ShelfComponentInstance",
                StringComparison.Ordinal);
            if (!ground && !shelf)
            {
                detail = "storage-policy-target-type-unsupported type=" + typeName;
                return false;
            }

            object identityObject = storage;
            if (shelf)
            {
                identityObject = AccessTools.Property(storage.GetType(), "OwnerBuilding")
                    ?.GetValue(storage, null) ?? storage;
            }

            var nativeId = ReadReplicationStoragePolicyPositiveId(
                identityObject,
                ground ? "UniqueId" : "UniqueId");
            long hostUid = 0L;
            var canonical = false;
            lock (ReplicationWorldObjectDeltaLock)
            {
                canonical = ReplicationHostIdByLocalObject.TryGetValue(
                        GetReplicationLocalObjectKey(identityObject),
                        out hostUid)
                    && hostUid > 0L;
            }

            if (replicationConfigHostMode)
            {
                hostUid = nativeId;
                canonical = hostUid > 0L;
                if (canonical)
                {
                    RegisterReplicationHostIdentity(
                        hostUid,
                        identityObject,
                        "storage-policy-native-identity");
                }
            }
            else if (!canonical)
            {
                hostUid = nativeId;
            }

            var blueprint = Convert.ToString(
                AccessTools.Property(storage.GetType(), "ObjectId")?.GetValue(storage, null),
                CultureInfo.InvariantCulture) ?? string.Empty;
            object? anchor = ground
                ? AccessTools.Property(storage.GetType(), "Start")?.GetValue(storage, null)
                : AccessTools.Property(storage.GetType(), "GridPosition")?.GetValue(storage, null);
            TryReadReplicationVec3Int(anchor, out var x, out var y, out var z);
            if (hostUid <= 0L || string.IsNullOrWhiteSpace(blueprint))
            {
                detail = "storage-policy-target-identity-incomplete kind="
                    + (ground ? ReplicationStoragePolicyGroundKind : ReplicationStoragePolicyShelfKind)
                    + " uid=" + hostUid.ToString(CultureInfo.InvariantCulture)
                    + " blueprint=" + blueprint;
                return false;
            }

            target.Kind = ground
                ? ReplicationStoragePolicyGroundKind
                : ReplicationStoragePolicyShelfKind;
            target.HostUid = hostUid;
            target.Canonical = canonical;
            target.ComponentOrdinal = 0;
            target.BlueprintFingerprint = blueprint;
            target.AnchorX = x;
            target.AnchorY = y;
            target.AnchorZ = z;
            detail = "ok target=" + FormatReplicationStoragePolicyTargetKey(target);
            return true;
        }

        private static long ReadReplicationStoragePolicyPositiveId(
            object candidate,
            string memberName)
        {
            if (!TryReadInstanceMemberValue(candidate, memberName, out var raw)
                || raw == null)
            {
                return 0L;
            }

            try
            {
                var value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return value > 0L ? value : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private static bool TryGetReplicationStoragePolicyCatalog(
            out ReplicationStoragePolicyCatalog catalog,
            out string detail)
        {
            if (replicationStoragePolicyCatalog != null)
            {
                catalog = replicationStoragePolicyCatalog;
                detail = "ok cached count="
                    + catalog.ResourceIds.Length.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            catalog = new ReplicationStoragePolicyCatalog();
            var marker = AccessTools.TypeByName("NSMedieval.Repository.ResourceRepository");
            var model = AccessTools.TypeByName("NSMedieval.Model.Resource");
            var repositoryDefinition = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
            if (marker == null || model == null || repositoryDefinition == null)
            {
                detail = "storage-policy-resource-repository-types-missing";
                return false;
            }

            try
            {
                var repositoryType = repositoryDefinition.MakeGenericType(marker, model);
                var repository = AccessTools.Property(repositoryType, "Instance")
                    ?.GetValue(null, null);
                var getAllItems = AccessTools.Method(marker, "GetAllItems", Type.EmptyTypes);
                var resources = repository == null || getAllItems == null
                    ? null
                    : getAllItems.Invoke(repository, null) as IEnumerable;
                if (resources == null)
                {
                    detail = "storage-policy-resource-catalog-missing";
                    return false;
                }

                var byId = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var resource in resources)
                {
                    if (resource == null
                        || !TryResolveReplicationModelId(resource, out var resourceId)
                        || string.IsNullOrWhiteSpace(resourceId))
                    {
                        continue;
                    }

                    if (byId.TryGetValue(resourceId, out var duplicate)
                        && !ReferenceEquals(duplicate, resource))
                    {
                        detail = "storage-policy-resource-catalog-duplicate id=" + resourceId;
                        return false;
                    }
                    byId[resourceId] = resource;
                }

                if (byId.Count <= 0
                    || byId.Count > ReplicationStoragePolicyMaximumCatalogResources)
                {
                    detail = "storage-policy-resource-catalog-count-invalid count="
                        + byId.Count.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                var ids = new string[byId.Count];
                var values = new object[byId.Count];
                var indexById = new Dictionary<string, int>(byId.Count, StringComparer.Ordinal);
                var indexByObject = new Dictionary<int, int>(byId.Count);
                var hash = new DeterminismHash();
                var index = 0;
                foreach (var pair in byId)
                {
                    ids[index] = pair.Key;
                    values[index] = pair.Value;
                    indexById.Add(pair.Key, index);
                    indexByObject[GetReplicationLocalObjectKey(pair.Value)] = index;
                    hash.Add(index);
                    hash.Add(pair.Key);
                    index++;
                }

                catalog = new ReplicationStoragePolicyCatalog
                {
                    Signature = DeterminismHash.Format(hash.Value),
                    ResourceIds = ids,
                    Resources = values,
                    IndexByResourceId = indexById,
                    IndexByLocalObjectKey = indexByObject
                };
                replicationStoragePolicyCatalog = catalog;
                detail = "ok count=" + ids.Length.ToString(CultureInfo.InvariantCulture)
                    + " signature=" + catalog.Signature;
                return true;
            }
            catch (Exception ex)
            {
                detail = "storage-policy-resource-catalog-read-failed "
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryGetReplicationStoragePolicyCatalogIndex(
            ReplicationStoragePolicyCatalog catalog,
            object resource,
            out int index)
        {
            if (catalog.IndexByLocalObjectKey.TryGetValue(
                    GetReplicationLocalObjectKey(resource),
                    out index))
            {
                return true;
            }

            return TryResolveReplicationModelId(resource, out var id)
                && catalog.IndexByResourceId.TryGetValue(id, out index);
        }

        private static bool TryReadReplicationStoragePolicySnapshot(
            object storage,
            ReplicationStoragePolicyTargetReference target,
            out ReplicationStoragePolicySnapshot snapshot,
            out string detail)
        {
            snapshot = new ReplicationStoragePolicySnapshot();
            if (!TryGetReplicationStoragePolicyCatalog(out var catalog, out detail))
            {
                return false;
            }

            try
            {
                var priorityValue = AccessTools.Property(storage.GetType(), "Priority")
                    ?.GetValue(storage, null);
                var productionValue = AccessTools.Property(
                    storage.GetType(), "CanBeUsedInProduction")?.GetValue(storage, null);
                var nameValue = AccessTools.Property(storage.GetType(), "StorageName")
                    ?.GetValue(storage, null);
                if (!TryConvertReplicationStoragePolicyInt(priorityValue, out var priority)
                    || priority < ReplicationStoragePolicyMinimumPriority
                    || priority > ReplicationStoragePolicyMaximumPriority
                    || productionValue == null)
                {
                    detail = "storage-policy-common-state-invalid target="
                        + FormatReplicationStoragePolicyTargetKey(target);
                    return false;
                }

                snapshot.Target = target.Clone();
                snapshot.Storage = storage;
                snapshot.Priority = priority;
                snapshot.UseInProduction = Convert.ToBoolean(
                    productionValue, CultureInfo.InvariantCulture);
                snapshot.Name = Convert.ToString(nameValue, CultureInfo.InvariantCulture)
                    ?? string.Empty;

                var typeName = storage.GetType().FullName ?? storage.GetType().Name;
                if (string.Equals(
                        typeName,
                        "NSMedieval.Stockpiles.StockpileInstance",
                        StringComparison.Ordinal))
                {
                    var filter = AccessTools.Property(storage.GetType(), "ResourcesFilter")
                        ?.GetValue(storage, null);
                    if (filter == null
                        || !TryReadReplicationStoragePolicySlot(
                            0, "ground", null, filter, catalog,
                            out var groundSlot, out detail))
                    {
                        return false;
                    }
                    snapshot.Slots.Add(groundSlot);
                }
                else
                {
                    var allStorage = AccessTools.Property(storage.GetType(), "AllStorage")
                        ?.GetValue(storage, null) as IEnumerable;
                    if (allStorage == null)
                    {
                        detail = "storage-policy-shelf-slot-list-missing";
                        return false;
                    }

                    var ordinal = 0;
                    foreach (var universal in allStorage)
                    {
                        if (universal == null || ordinal >= ReplicationStoragePolicyMaximumSlots)
                        {
                            detail = "storage-policy-shelf-slot-invalid ordinal="
                                + ordinal.ToString(CultureInfo.InvariantCulture);
                            return false;
                        }

                        var slotId = Convert.ToString(
                            AccessTools.Property(universal.GetType(), "UniversalStorageID")
                                ?.GetValue(universal, null),
                            CultureInfo.InvariantCulture) ?? string.Empty;
                        var filter = AccessTools.Property(universal.GetType(), "ResourcesFilter")
                            ?.GetValue(universal, null);
                        if (filter == null
                            || string.IsNullOrWhiteSpace(slotId)
                            || !TryReadReplicationStoragePolicySlot(
                                ordinal, slotId, universal, filter, catalog,
                                out var shelfSlot, out detail))
                        {
                            return false;
                        }
                        snapshot.Slots.Add(shelfSlot);
                        ordinal++;
                    }
                }

                if (snapshot.Slots.Count == 0
                    || snapshot.Slots.Count > ReplicationStoragePolicyMaximumSlots)
                {
                    detail = "storage-policy-slot-count-invalid count="
                        + snapshot.Slots.Count.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                snapshot.TopologySignature = CalculateReplicationStoragePolicyTopologySignature(
                    snapshot.Slots);
                detail = "ok target=" + FormatReplicationStoragePolicyTargetKey(target)
                    + " slots=" + snapshot.Slots.Count.ToString(CultureInfo.InvariantCulture)
                    + " topology=" + snapshot.TopologySignature;
                return true;
            }
            catch (Exception ex)
            {
                detail = "storage-policy-state-read-failed "
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryReadReplicationStoragePolicySlot(
            int ordinal,
            string slotId,
            object? universalStorage,
            object filter,
            ReplicationStoragePolicyCatalog catalog,
            out ReplicationStoragePolicySlotSnapshot slot,
            out string detail)
        {
            slot = new ReplicationStoragePolicySlotSnapshot();
            var allowedValue = AccessTools.Property(filter.GetType(), "AllowedResourceTypes")
                ?.GetValue(filter, null) as IEnumerable;
            var defaultValue = AccessTools.Property(filter.GetType(), "DefaultAllowedResources")
                ?.GetValue(filter, null) as IEnumerable;
            var hpValue = AccessTools.Property(filter.GetType(), "HitPointsPercent")
                ?.GetValue(filter, null);
            var qualityValue = AccessTools.Property(filter.GetType(), "Quality")
                ?.GetValue(filter, null);
            if (allowedValue == null
                || defaultValue == null
                || !TryReadReplicationStoragePolicyRange(
                    hpValue, out var hpMinimum, out var hpMaximum)
                || !TryReadReplicationStoragePolicyRange(
                    qualityValue, out var qualityMinimum, out var qualityMaximum)
                || hpMinimum < 0 || hpMaximum > 100 || hpMinimum > hpMaximum
                || qualityMinimum < ReplicationStoragePolicyMinimumQuality
                || qualityMaximum > ReplicationStoragePolicyMaximumQuality
                || qualityMinimum > qualityMaximum)
            {
                detail = "storage-policy-slot-values-invalid ordinal="
                    + ordinal.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            var allowed = new bool[catalog.ResourceIds.Length];
            var defaultAllowed = new bool[catalog.ResourceIds.Length];
            foreach (var resource in allowedValue)
            {
                if (resource == null
                    || !TryGetReplicationStoragePolicyCatalogIndex(
                        catalog, resource, out var index))
                {
                    detail = "storage-policy-slot-allowed-resource-unknown ordinal="
                        + ordinal.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                allowed[index] = true;
            }
            foreach (var resource in defaultValue)
            {
                if (resource == null
                    || !TryGetReplicationStoragePolicyCatalogIndex(
                        catalog, resource, out var index))
                {
                    detail = "storage-policy-slot-default-resource-unknown ordinal="
                        + ordinal.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                defaultAllowed[index] = true;
            }
            for (var resource = 0; resource < allowed.Length; resource++)
            {
                if (allowed[resource] && !defaultAllowed[resource])
                {
                    detail = "storage-policy-slot-allowed-outside-capability ordinal="
                        + ordinal.ToString(CultureInfo.InvariantCulture)
                        + " resource=" + resource.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            slot.Ordinal = ordinal;
            slot.UniversalStorageId = slotId;
            slot.UniversalStorage = universalStorage;
            slot.Filter = filter;
            slot.Allowed = allowed;
            slot.DefaultAllowed = defaultAllowed;
            slot.HitPointsMinimum = hpMinimum;
            slot.HitPointsMaximum = hpMaximum;
            slot.QualityMinimum = qualityMinimum;
            slot.QualityMaximum = qualityMaximum;
            slot.DefaultAllowedFingerprint = CalculateReplicationStoragePolicyBooleanFingerprint(
                "default", defaultAllowed);
            detail = "ok";
            return true;
        }

        private static string CalculateReplicationStoragePolicyTopologySignature(
            List<ReplicationStoragePolicySlotSnapshot> slots)
        {
            var hash = new DeterminismHash();
            hash.Add(slots.Count);
            for (var i = 0; i < slots.Count; i++)
            {
                hash.Add(slots[i].Ordinal);
                hash.Add(slots[i].UniversalStorageId);
                hash.Add(slots[i].DefaultAllowedFingerprint);
            }
            return DeterminismHash.Format(hash.Value);
        }

        private static string CalculateReplicationStoragePolicyBooleanFingerprint(
            string scope,
            bool[] values)
        {
            var hash = new DeterminismHash();
            hash.Add(scope);
            hash.Add(values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i])
                {
                    hash.Add(i);
                }
            }
            return DeterminismHash.Format(hash.Value);
        }

        private static byte[] CreateReplicationStoragePolicyAllowedMask(bool[] allowed)
        {
            var length = StoragePolicyPayloadCodec.GetAllowedMaskByteLength(allowed.Length);
            if (length <= 0)
            {
                return Array.Empty<byte>();
            }

            var mask = new byte[length];
            for (var i = 0; i < allowed.Length; i++)
            {
                if (allowed[i])
                {
                    mask[i >> 3] |= (byte)(1 << (i & 7));
                }
            }
            return mask;
        }

        private static string FormatReplicationStoragePolicyCapabilityFingerprint()
        {
            return replicationStoragePolicyCriticalPatchesReady
                && TryGetReplicationStoragePolicyCatalog(out var catalog, out _)
                ? StoragePolicyPayloadCodec.SchemaVersion.ToString(CultureInfo.InvariantCulture)
                    + ":" + catalog.ResourceIds.Length.ToString(CultureInfo.InvariantCulture)
                    + ":" + catalog.Signature
                : StoragePolicyPayloadCodec.SchemaVersion.ToString(CultureInfo.InvariantCulture)
                    + ":unavailable";
        }

        private static bool IsReplicationStoragePolicyUpdateCommand(LockstepCommand command)
        {
            return command.Kind == CommandKind.Custom
                && LockstepCommandPayloads.TryReadStoragePolicyUpdatePayload(
                    command.PayloadJson, out _);
        }

        private static bool HasReplicationStoragePolicyInFlightUpdate(
            ReplicationStoragePolicyPendingTarget pending)
        {
            // This is deliberately stored on the logical pending record rather
            // than rediscovered through mutable target metadata. Canonical adoption,
            // an anchor change, or a rekey must not let a second sparse chunk launch
            // while scalar proofThrough still owns the first one.
            return pending.InFlightSequence > 0L;
        }

        private static LockstepCommand CoalesceReplicationPendingStoragePolicyIntent(
            LockstepCommand newest)
        {
            if (!LockstepCommandPayloads.TryReadStoragePolicyUpdatePayload(
                    newest.PayloadJson, out var newestUpdate))
            {
                return newest;
            }

            var prior = new List<KeyValuePair<string, PendingReplicationCommandIntent>>();
            foreach (var pair in ReplicationPendingCommandIntents)
            {
                if (!LockstepCommandPayloads.TryReadStoragePolicyUpdatePayload(
                        pair.Value.Command.PayloadJson, out var pendingUpdate))
                {
                    continue;
                }

                if (ReplicationStoragePolicyTargetsMatch(
                        newestUpdate.Target,
                        pendingUpdate.Target)
                    && pendingUpdate.Epoch == newestUpdate.Epoch
                    && string.Equals(
                        pendingUpdate.CatalogSignature,
                        newestUpdate.CatalogSignature,
                        StringComparison.Ordinal)
                    && pendingUpdate.CatalogCount == newestUpdate.CatalogCount
                    && string.Equals(
                        pendingUpdate.TopologySignature,
                        newestUpdate.TopologySignature,
                        StringComparison.Ordinal))
                {
                    prior.Add(pair);
                }
            }

            prior.Sort((left, right) =>
                left.Value.Command.Sequence.CompareTo(right.Value.Command.Sequence));
            var changesByCell = new SortedDictionary<string, StoragePolicyChange>(
                StringComparer.Ordinal);
            for (var i = 0; i < prior.Count; i++)
            {
                if (!LockstepCommandPayloads.TryReadStoragePolicyUpdatePayload(
                        prior[i].Value.Command.PayloadJson, out var pendingUpdate)
                    )
                {
                    continue;
                }

                for (var cell = 0; cell < pendingUpdate.Changes.Length; cell++)
                {
                    changesByCell[pendingUpdate.Changes[cell].CellKey] =
                        pendingUpdate.Changes[cell];
                }
            }
            for (var cell = 0; cell < newestUpdate.Changes.Length; cell++)
            {
                changesByCell[newestUpdate.Changes[cell].CellKey] =
                    newestUpdate.Changes[cell];
            }

            var mergedUpdate = new StoragePolicyUpdate(
                newestUpdate.Target,
                newestUpdate.Epoch,
                newestUpdate.CatalogSignature,
                newestUpdate.CatalogCount,
                newestUpdate.TopologySignature,
                new List<StoragePolicyChange>(changesByCell.Values).ToArray());
            if (!LockstepCommandPayloads.TryCreateStoragePolicyUpdatePayload(
                    mergedUpdate, out var mergedPayload))
            {
                return newest;
            }

            for (var i = 0; i < prior.Count; i++)
            {
                ReplicationPendingCommandIntents.Remove(prior[i].Key);
            }

            return new LockstepCommand(
                newest.PlayerId,
                newest.Sequence,
                newest.TargetTick,
                newest.Kind,
                mergedPayload,
                newest.TargetStableId,
                newest.MapX,
                newest.MapY,
                newest.MapZ);
        }

        private static bool ReplicationStoragePolicyTargetsMatch(
            StoragePolicyTarget left,
            StoragePolicyTarget right)
        {
            if (left.Kind != right.Kind
                || left.ComponentOrdinal != right.ComponentOrdinal)
            {
                return false;
            }
            if (left.IsCanonicalHostUid && right.IsCanonicalHostUid)
            {
                return left.HostUidCandidate == right.HostUidCandidate;
            }
            if (left.IsCanonicalHostUid != right.IsCanonicalHostUid)
            {
                // A provisional client target is associated with a canonical result
                // by object reference and exact in-flight sequence, never by a fuzzy
                // blueprint/anchor comparison that a replacement can inherit.
                return false;
            }
            if (left.HostUidCandidate > 0L
                && right.HostUidCandidate > 0L
                && left.HostUidCandidate == right.HostUidCandidate
                && string.Equals(
                    left.BlueprintFingerprint,
                    right.BlueprintFingerprint,
                    StringComparison.Ordinal)
                && left.Anchor.X == right.Anchor.X
                && left.Anchor.Y == right.Anchor.Y
                && left.Anchor.Z == right.Anchor.Z)
            {
                return true;
            }
            return string.Equals(
                    left.BlueprintFingerprint,
                    right.BlueprintFingerprint,
                    StringComparison.Ordinal)
                && left.Anchor.X == right.Anchor.X
                && left.Anchor.Y == right.Anchor.Y
                && left.Anchor.Z == right.Anchor.Z;
        }

        private static bool IsReplicationStoragePolicyStorageDisposed(object storage)
        {
            return TryReadInstanceMemberValue(storage, "HasDisposed", out var raw)
                && raw is bool disposed
                && disposed;
        }

        private static bool TryProveReplicationStoragePolicyCanonicalTargetDisposed(
            ReplicationStoragePolicyTargetReference target,
            out string detail)
        {
            if (!target.Canonical || target.HostUid <= 0L)
            {
                detail = "target-not-canonical";
                return false;
            }
            if (!TryGetReplicationLocalObjectByHostId(
                    target.HostUid, out var mapped, out var mapDetail)
                || mapped == null)
            {
                detail = "canonical-map-missing " + mapDetail;
                return false;
            }
            if (!IsReplicationStoragePolicyStorageDisposed(mapped))
            {
                detail = "canonical-object-not-disposed " + mapDetail;
                return false;
            }
            detail = "ok canonical-disposed " + mapDetail;
            return true;
        }

        private static bool IsReplicationStoragePolicyPositiveMissingDetail(string detail)
        {
            return detail.StartsWith(
                "storage-policy-target-missing ", StringComparison.Ordinal);
        }

        private static void LogReplicationStoragePolicyRetainedThrottled(
            string channel,
            string targetKey,
            string detail)
        {
            var throttleKey = channel + "|" + targetKey;
            var now = Time.realtimeSinceStartup;
            if (ReplicationStoragePolicyRetainedLogAtByKey.TryGetValue(
                    throttleKey, out var lastAt)
                && now - lastAt < 5f)
            {
                return;
            }

            ReplicationStoragePolicyRetainedLogAtByKey[throttleKey] = now;
            instance?.LogReplicationWarning(
                "Going Cooperative storage-policy " + channel
                + " retained target=" + targetKey + " detail=" + detail);
        }

        private static bool ShouldAttemptReplicationStoragePolicyRetainedTarget(
            string channel,
            string targetKey)
        {
            return !ReplicationStoragePolicyRetryAtByKey.TryGetValue(
                    channel + "|" + targetKey, out var retryAt)
                || Time.realtimeSinceStartup >= retryAt;
        }

        private static void DeferReplicationStoragePolicyRetainedTarget(
            string channel,
            string targetKey)
        {
            ReplicationStoragePolicyRetryAtByKey[channel + "|" + targetKey] =
                Time.realtimeSinceStartup + ReplicationStoragePolicyRetainedRetrySeconds;
        }

        private static void ClearReplicationStoragePolicyRetainedThrottle(
            string channel,
            string targetKey)
        {
            ReplicationStoragePolicyRetainedLogAtByKey.Remove(channel + "|" + targetKey);
            ReplicationStoragePolicyRetryAtByKey.Remove(channel + "|" + targetKey);
            ReplicationStoragePolicyMissingSinceByKey.Remove(channel + "|" + targetKey);
        }

        private static bool ShouldEscalateReplicationStoragePolicyMissingTarget(
            string channel,
            string targetKey)
        {
            var key = channel + "|" + targetKey;
            var now = Time.realtimeSinceStartup;
            if (!ReplicationStoragePolicyMissingSinceByKey.TryGetValue(key, out var since))
            {
                ReplicationStoragePolicyMissingSinceByKey[key] = now;
                return false;
            }
            return now - since >= 2f;
        }

        private static void RequestReplicationStoragePolicyRecovery(string reason)
        {
            if (!replicationStoragePolicyRecoveryRequested)
            {
                replicationStoragePolicyRecoveryReason = reason;
            }
            replicationStoragePolicyFailStopped = true;
            replicationStoragePolicyRecoveryRequested = true;
        }

        private static void HandleReplicationStoragePolicyRollbackResult(
            bool rollbackSucceeded,
            string scope,
            string targetKey,
            string failureDetail,
            string rollbackDetail)
        {
            if (rollbackSucceeded)
            {
                return;
            }

            RequestReplicationStoragePolicyRecovery(
                "storage-policy-" + scope + "-rollback-unproven target="
                + targetKey + " failure=" + failureDetail
                + " rollback=" + rollbackDetail);
        }

        private static void RemoveReplicationStoragePolicyHostOrdering(string targetKey)
        {
            var prefix = targetKey + "|";
            var keys = new List<string>(ReplicationStoragePolicyHostHighWaterByCell.Keys);
            for (var i = 0; i < keys.Count; i++)
            {
                if (keys[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    ReplicationStoragePolicyHostHighWaterByCell.Remove(keys[i]);
                }
            }
        }

        private static bool TrySendReplicationStoragePolicyDisposedTombstone(
            string dirtyKey,
            ReplicationStoragePolicyDirtyTarget dirty,
            string reason,
            out string detail)
        {
            var canonical = dirty.Target.Clone();
            canonical.Canonical = true;
            var canonicalKey = FormatReplicationStoragePolicyTargetKey(canonical);
            var empty = new ReplicationStoragePolicySnapshot
            {
                Target = canonical.Clone(),
                TopologySignature = ReplicationStoragePolicyDisposedTopologySignature
            };
            if (!TryCreateReplicationStoragePolicyState(
                    empty,
                    canonical,
                    exists: false,
                    proofThrough: GetReplicationStoragePolicyHostProofThrough(canonicalKey),
                    advanceRevision: true,
                    out var tombstone,
                    out var stateDetail)
                || !LockstepCommandPayloads.TryCreateStoragePolicyStatePayload(
                    tombstone, out var payload))
            {
                detail = "disposed-tombstone-encode-failed state=" + stateDetail;
                return false;
            }

            if (!instance!.SendReplicationManagementDelta(
                    payload,
                    "storage-policy-host-disposed:" + reason))
            {
                detail = "disposed-tombstone-not-queued";
                return false;
            }
            CommitReplicationStoragePolicyStateOrdering(tombstone);
            ReplicationStoragePolicyHostDirtyByTarget.Remove(dirtyKey);
            RemoveReplicationStoragePolicyHostOrdering(canonicalKey);
            ClearReplicationStoragePolicyRetainedThrottle("host-dirty", dirtyKey);
            detail = "ok target=" + canonicalKey + " reason=" + reason;
            return true;
        }

        private static void FlushReplicationStoragePolicyChanges()
        {
            if (!StoragePolicyV4Enabled()) return;
            if (!EnsureReplicationStoragePolicyRuntimeEpoch())
            {
                return;
            }
            if (replicationStoragePolicyFailStopped)
            {
                ProcessReplicationStoragePolicyRecoveryIfRequested();
                return;
            }

            FlushReplicationStoragePolicyHostDirtyChanges();
            FlushReplicationStoragePolicyClientPendingChanges();
        }

        private static void FlushReplicationStoragePolicyHostDirtyChanges()
        {
            if (!replicationConfigHostMode
                || ReplicationStoragePolicyHostDirtyByTarget.Count == 0
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || instance == null)
            {
                return;
            }

            var keys = new List<string>(ReplicationStoragePolicyHostDirtyByTarget.Keys);
            var processed = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                if (processed >= ReplicationStoragePolicyHostFlushBudget)
                {
                    break;
                }
                var key = keys[i];
                if (ReplicationStoragePolicyHostQuarantinedTargets.Contains(key)
                    || !ReplicationStoragePolicyHostDirtyByTarget.TryGetValue(key, out var dirty)
                    || !ShouldAttemptReplicationStoragePolicyRetainedTarget(
                        "host-dirty", key))
                {
                    continue;
                }
                processed++;

                var disposed = IsReplicationStoragePolicyStorageDisposed(dirty.Storage);
                var readDetail = disposed ? "storage-has-disposed" : "not-read";
                var resolveDetail = "not-attempted";
                ReplicationStoragePolicySnapshot snapshot = null!;
                var snapshotReady = !disposed
                    && TryReadReplicationStoragePolicySnapshot(
                        dirty.Storage, dirty.Target, out snapshot, out readDetail);
                if (!snapshotReady
                    && TryResolveReplicationStoragePolicyTarget(
                        dirty.Target,
                        out var recoveredStorage,
                        out var recoveredTarget,
                        out resolveDetail)
                    && recoveredStorage != null)
                {
                    dirty.Storage = recoveredStorage;
                    dirty.Target = recoveredTarget;
                    disposed = IsReplicationStoragePolicyStorageDisposed(recoveredStorage);
                    snapshotReady = !disposed
                        && TryReadReplicationStoragePolicySnapshot(
                            recoveredStorage,
                            recoveredTarget,
                            out snapshot,
                            out readDetail);
                }

                if (!snapshotReady)
                {
                    if (disposed)
                    {
                        if (!TrySendReplicationStoragePolicyDisposedTombstone(
                                key,
                                dirty,
                                disposed ? "native-disposed" : "resolver-missing",
                                out var tombstoneDetail))
                        {
                            LogReplicationStoragePolicyRetainedThrottled(
                                "host-dirty", key, tombstoneDetail);
                            DeferReplicationStoragePolicyRetainedTarget(
                                "host-dirty", key);
                            if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                                    "host-dirty-tombstone", key))
                            {
                                ReplicationStoragePolicyHostQuarantinedTargets.Add(key);
                                RequestReplicationStoragePolicyRecovery(
                                    "storage-policy-host-tombstone-nonconvergent target="
                                    + key + " detail=" + tombstoneDetail);
                                return;
                            }
                        }
                    }
                    else if (IsReplicationStoragePolicyPositiveMissingDetail(resolveDetail))
                    {
                        LogReplicationStoragePolicyRetainedThrottled(
                            "host-dirty",
                            key,
                            "positive-missing-but-not-disposed read=" + readDetail
                                + " resolve=" + resolveDetail);
                        if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                                "host-dirty", key))
                        {
                            // HasDisposed=false cannot authorize a tombstone: manager
                            // membership can be transient while native lifecycle work is
                            // in flight. Preserve the dirty state, stop retry churn, and
                            // recover the whole session from the host's authority.
                            ReplicationStoragePolicyHostQuarantinedTargets.Add(key);
                            RequestReplicationStoragePolicyRecovery(
                                "storage-policy-host-target-missing-not-disposed target="
                                + key + " read=" + readDetail
                                + " resolve=" + resolveDetail);
                            return;
                        }
                        DeferReplicationStoragePolicyRetainedTarget(
                            "host-dirty", key);
                    }
                    else
                    {
                        LogReplicationStoragePolicyRetainedThrottled(
                            "host-dirty",
                            key,
                            "read=" + readDetail + " resolve=" + resolveDetail);
                        DeferReplicationStoragePolicyRetainedTarget(
                            "host-dirty", key);
                        if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                                "host-dirty-read", key))
                        {
                            ReplicationStoragePolicyHostQuarantinedTargets.Add(key);
                            RequestReplicationStoragePolicyRecovery(
                                "storage-policy-host-read-nonconvergent target="
                                + key + " read=" + readDetail
                                + " resolve=" + resolveDetail);
                            return;
                        }
                    }
                    continue;
                }

                ClearReplicationStoragePolicyRetainedThrottle("host-dirty", key);
                ClearReplicationStoragePolicyRetainedThrottle("host-dirty-read", key);
                ClearReplicationStoragePolicyRetainedThrottle("host-dirty-tombstone", key);

                var canonical = snapshot.Target.Clone();
                canonical.Canonical = true;
                var canonicalKey = FormatReplicationStoragePolicyTargetKey(canonical);
                if (!TryCreateReplicationStoragePolicyState(
                        snapshot,
                        canonical,
                        exists: true,
                        proofThrough: GetReplicationStoragePolicyHostProofThrough(canonicalKey),
                        advanceRevision: true,
                        out var state,
                        out var stateDetail)
                    || !LockstepCommandPayloads.TryCreateStoragePolicyStatePayload(
                        state, out var payload))
                {
                    LogReplicationStoragePolicyRetainedThrottled(
                        "host-dirty", key, "encode=" + stateDetail);
                    DeferReplicationStoragePolicyRetainedTarget(
                        "host-dirty", key);
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "host-dirty-encode", key))
                    {
                        ReplicationStoragePolicyHostQuarantinedTargets.Add(key);
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-host-encode-nonconvergent target="
                            + key + " detail=" + stateDetail);
                        return;
                    }
                    continue;
                }
                ClearReplicationStoragePolicyRetainedThrottle("host-dirty-encode", key);

                if (!instance.SendReplicationManagementDelta(
                        payload,
                        "storage-policy-host-model-batch"))
                {
                    LogReplicationStoragePolicyRetainedThrottled(
                        "host-dirty", key, "state-not-queued");
                    DeferReplicationStoragePolicyRetainedTarget(
                        "host-dirty", key);
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "host-dirty-queue", key))
                    {
                        ReplicationStoragePolicyHostQuarantinedTargets.Add(key);
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-host-state-queue-nonconvergent target="
                            + key);
                        return;
                    }
                    continue;
                }
                CommitReplicationStoragePolicyStateOrdering(state);
                ReplicationStoragePolicyHostDirtyByTarget.Remove(key);
                ClearReplicationStoragePolicyRetainedThrottle("host-dirty", key);
                ClearReplicationStoragePolicyRetainedThrottle("host-dirty-queue", key);
            }
        }

        private static void FlushReplicationStoragePolicyClientPendingChanges()
        {
            if (replicationConfigHostMode
                || ReplicationStoragePolicyPendingByTarget.Count == 0
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || !ShouldSendReplicationLocalCommandIntent())
            {
                return;
            }

            var keys = new List<string>(ReplicationStoragePolicyPendingByTarget.Keys);
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (ReplicationStoragePolicyClientQuarantinedTargets.Contains(key)
                    || !ReplicationStoragePolicyPendingByTarget.TryGetValue(key, out var pending)
                    || !ShouldAttemptReplicationStoragePolicyRetainedTarget(
                        "client-pending", key))
                {
                    continue;
                }

                // proofThrough is scalar per target. Permit exactly one command for
                // that target to be in flight so a later chunk can never acknowledge
                // and retire an earlier chunk that the host did not receive.
                if (HasReplicationStoragePolicyInFlightUpdate(pending))
                {
                    continue;
                }

                var disposed = IsReplicationStoragePolicyStorageDisposed(pending.Storage);
                var readDetail = disposed ? "storage-has-disposed" : "not-read";
                var resolveDetail = "not-attempted";
                ReplicationStoragePolicySnapshot snapshot = null!;
                var snapshotReady = !disposed
                    && TryReadReplicationStoragePolicySnapshot(
                        pending.Storage, pending.Target, out snapshot, out readDetail);
                if (!snapshotReady
                    && TryResolveReplicationStoragePolicyTarget(
                        pending.Target,
                        out var recoveredStorage,
                        out var recoveredTarget,
                        out resolveDetail)
                    && recoveredStorage != null)
                {
                    pending.Storage = recoveredStorage;
                    pending.Target = recoveredTarget;
                    disposed = IsReplicationStoragePolicyStorageDisposed(recoveredStorage);
                    snapshotReady = !disposed
                        && TryReadReplicationStoragePolicySnapshot(
                            recoveredStorage,
                            recoveredTarget,
                            out snapshot,
                            out readDetail);
                }

                if (!snapshotReady)
                {
                    if (disposed
                        || IsReplicationStoragePolicyPositiveMissingDetail(resolveDetail))
                    {
                        // Do not drop accepted-looking edits or their durable command
                        // sequences. Quarantine the target and force an authoritative
                        // full reload; reset/proof will then dispose of the retained
                        // intent without a permanent per-frame retry loop.
                        ReplicationStoragePolicyClientQuarantinedTargets.Add(key);
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-client-target-gone target=" + key
                            + " read=" + readDetail
                            + " resolve=" + resolveDetail);
                        LogReplicationStoragePolicyRetainedThrottled(
                            "client-pending-quarantined",
                            key,
                            "read=" + readDetail + " resolve=" + resolveDetail);
                        return;
                    }
                    else
                    {
                        LogReplicationStoragePolicyRetainedThrottled(
                            "client-pending",
                            key,
                            "read=" + readDetail + " resolve=" + resolveDetail);
                        DeferReplicationStoragePolicyRetainedTarget(
                            "client-pending", key);
                        if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                                "client-pending-read", key))
                        {
                            ReplicationStoragePolicyClientQuarantinedTargets.Add(key);
                            RequestReplicationStoragePolicyRecovery(
                                "storage-policy-client-read-nonconvergent target="
                                + key + " read=" + readDetail
                                + " resolve=" + resolveDetail);
                            return;
                        }
                    }
                    continue;
                }
                ClearReplicationStoragePolicyRetainedThrottle(
                    "client-pending-read", key);

                var catalogDetail = "not-read";
                if (!TryGetReplicationStoragePolicyCatalog(out var catalog, out catalogDetail))
                {
                    LogReplicationStoragePolicyRetainedThrottled(
                        "client-pending", key, "catalog=" + catalogDetail);
                    DeferReplicationStoragePolicyRetainedTarget(
                        "client-pending", key);
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "client-pending-catalog", key))
                    {
                        ReplicationStoragePolicyClientQuarantinedTargets.Add(key);
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-client-catalog-nonconvergent target="
                            + key + " detail=" + catalogDetail);
                        return;
                    }
                    continue;
                }
                ClearReplicationStoragePolicyRetainedThrottle(
                    "client-pending-catalog", key);

                ReplicationStoragePolicyClientQuarantinedTargets.Remove(key);
                ClearReplicationStoragePolicyRetainedThrottle("client-pending", key);

                var unsentKeys = new List<string>();
                var changes = new List<StoragePolicyChange>();
                foreach (var pair in pending.Cells)
                {
                    if (pending.SequenceByCell.TryGetValue(pair.Key, out var sequence)
                        && sequence > 0L)
                    {
                        continue;
                    }
                    if (TryCreateReplicationStoragePolicyWireChange(
                            pair.Value, out var change))
                    {
                        unsentKeys.Add(pair.Key);
                        changes.Add(change);
                        if (changes.Count >= StoragePolicyPayloadCodec.MaximumCells)
                        {
                            break;
                        }
                    }
                }
                if (changes.Count == 0)
                {
                    DeferReplicationStoragePolicyRetainedTarget(
                        "client-pending", key);
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "client-pending-empty", key))
                    {
                        ReplicationStoragePolicyClientQuarantinedTargets.Add(key);
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-client-pending-cells-unencodable target="
                            + key + " cells="
                            + pending.Cells.Count.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    continue;
                }
                ClearReplicationStoragePolicyRetainedThrottle(
                    "client-pending-empty", key);

                var update = new StoragePolicyUpdate(
                    ToReplicationStoragePolicyWireTarget(pending.Target),
                    GetReplicationStoragePolicyEpoch(),
                    catalog.Signature,
                    catalog.ResourceIds.Length,
                    snapshot.TopologySignature,
                    changes.ToArray());
                if (!LockstepCommandPayloads.TryCreateStoragePolicyUpdatePayload(
                        update, out var payload))
                {
                    LogReplicationStoragePolicyRetainedThrottled(
                        "client-pending", key, "encode-failed");
                    DeferReplicationStoragePolicyRetainedTarget(
                        "client-pending", key);
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "client-pending-encode", key))
                    {
                        ReplicationStoragePolicyClientQuarantinedTargets.Add(key);
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-client-encode-nonconvergent target="
                            + key);
                        return;
                    }
                    continue;
                }
                ClearReplicationStoragePolicyRetainedThrottle(
                    "client-pending-encode", key);

                var commandSequence = ++replicationIntentSequence;
                var command = new LockstepCommand(
                    GetReplicationLocalPeerId(),
                    commandSequence,
                    0L,
                    CommandKind.Custom,
                    payload);
                SendReplicationLocalCommandIntent(command, "storage-policy-model-batch");
                pending.InFlightSequence = commandSequence;
                for (var cell = 0; cell < unsentKeys.Count; cell++)
                {
                    pending.SequenceByCell[unsentKeys[cell]] = commandSequence;
                }
                ClearReplicationStoragePolicyRetainedThrottle("client-pending", key);
            }
        }

        private static bool TryCreateReplicationStoragePolicyWireChange(
            ReplicationStoragePolicyCellValue value,
            out StoragePolicyChange change)
        {
            switch (value.Kind)
            {
                case ReplicationStoragePolicyCellKind.Name:
                    change = StoragePolicyChange.ForName(value.Text);
                    return true;
                case ReplicationStoragePolicyCellKind.Priority:
                    change = StoragePolicyChange.ForPriority(value.Minimum);
                    return true;
                case ReplicationStoragePolicyCellKind.UseInProduction:
                    change = StoragePolicyChange.ForProductionUse(value.Enabled);
                    return true;
                case ReplicationStoragePolicyCellKind.HitPoints:
                    change = StoragePolicyChange.ForHitPointsRange(
                        value.Slot, value.Minimum, value.Maximum);
                    return true;
                case ReplicationStoragePolicyCellKind.Quality:
                    change = StoragePolicyChange.ForQualityRange(
                        value.Slot, value.Minimum, value.Maximum);
                    return true;
                case ReplicationStoragePolicyCellKind.Resource:
                    change = StoragePolicyChange.ForResourceAllowed(
                        value.Slot, value.Resource, value.Enabled);
                    return true;
                default:
                    change = null!;
                    return false;
            }
        }

        private static bool TryResolveReplicationStoragePolicyTarget(
            ReplicationStoragePolicyTargetReference requested,
            out object? storage,
            out ReplicationStoragePolicyTargetReference canonical,
            out string detail)
        {
            storage = null;
            canonical = requested.Clone();
            var managerType = AccessTools.TypeByName(
                "NSMedieval.StorageUniversal.StorageCommonManager");
            var manager = managerType == null
                ? null
                : AccessTools.Property(managerType, "Instance")?.GetValue(null, null);
            var allStorages = manager == null || managerType == null
                ? null
                : AccessTools.Property(managerType, "AllStorages")?.GetValue(manager, null)
                    as IEnumerable;
            if (allStorages == null)
            {
                detail = "storage-policy-resolver-manager-missing";
                return false;
            }

            var exact = new List<Tuple<object, ReplicationStoragePolicyTargetReference>>();
            var bootstrap = new List<Tuple<object, ReplicationStoragePolicyTargetReference>>();
            foreach (var candidate in allStorages)
            {
                if (candidate == null
                    || IsReplicationStoragePolicyStorageDisposed(candidate)
                    || !TryCreateReplicationStoragePolicyTargetReference(
                        candidate, out var candidateTarget, out var candidateStorage, out _)
                    || !string.Equals(
                        candidateTarget.Kind, requested.Kind, StringComparison.Ordinal)
                    || candidateTarget.ComponentOrdinal != requested.ComponentOrdinal)
                {
                    continue;
                }

                if (requested.Canonical
                    && requested.HostUid == candidateTarget.HostUid
                    && string.Equals(
                        requested.BlueprintFingerprint,
                        candidateTarget.BlueprintFingerprint,
                        StringComparison.Ordinal))
                {
                    // A transferred save preserves native UniqueId values before the
                    // generic host-identity index has necessarily observed this
                    // stockpile/shelf. Equality with that native UID is still an exact
                    // identity match; requiring an already-registered map entry here
                    // creates a chicken-and-egg failure because state application is
                    // what registers the canonical identity. This never authorizes the
                    // blueprint/anchor bootstrap below for canonical requests.
                    exact.Add(Tuple.Create(candidateStorage, candidateTarget));
                    continue;
                }

                if (!requested.Canonical
                    && requested.HostUid > 0L
                    && requested.HostUid == candidateTarget.HostUid
                    && string.Equals(
                        requested.BlueprintFingerprint,
                        candidateTarget.BlueprintFingerprint,
                        StringComparison.Ordinal)
                    && requested.AnchorX == candidateTarget.AnchorX
                    && requested.AnchorY == candidateTarget.AnchorY
                    && requested.AnchorZ == candidateTarget.AnchorZ)
                {
                    exact.Add(Tuple.Create(candidateStorage, candidateTarget));
                    continue;
                }

                if (!requested.Canonical
                    && string.Equals(
                        requested.BlueprintFingerprint,
                        candidateTarget.BlueprintFingerprint,
                        StringComparison.Ordinal)
                    && requested.AnchorX == candidateTarget.AnchorX
                    && requested.AnchorY == candidateTarget.AnchorY
                    && requested.AnchorZ == candidateTarget.AnchorZ)
                {
                    bootstrap.Add(Tuple.Create(candidateStorage, candidateTarget));
                }
            }

            var matches = exact.Count > 0 ? exact : bootstrap;
            if (matches.Count != 1)
            {
                detail = "storage-policy-target-"
                    + (matches.Count == 0 ? "missing" : "ambiguous")
                    + " exact=" + exact.Count.ToString(CultureInfo.InvariantCulture)
                    + " bootstrap=" + bootstrap.Count.ToString(CultureInfo.InvariantCulture)
                    + " requested=" + FormatReplicationStoragePolicyTargetKey(requested);
                return false;
            }

            storage = matches[0].Item1;
            canonical = matches[0].Item2.Clone();
            if (replicationConfigHostMode)
            {
                canonical.Canonical = true;
            }
            detail = "ok requested=" + FormatReplicationStoragePolicyTargetKey(requested)
                + " canonical=" + FormatReplicationStoragePolicyTargetKey(canonical);
            return true;
        }

        private static bool TryResolveReplicationStoragePolicyStateProofTarget(
            StoragePolicyState state,
            ReplicationStoragePolicyTargetReference requested,
            out object? storage,
            out ReplicationStoragePolicyTargetReference canonical,
            out string detail)
        {
            storage = null;
            canonical = requested.Clone();
            if (state.ProofThroughClientSequence <= 0L)
            {
                detail = "storage-policy-state-proof-bootstrap-unavailable";
                return false;
            }

            foreach (var pair in ReplicationStoragePolicyPendingByTarget)
            {
                var pending = pair.Value;
                if (pending.InFlightSequence != state.ProofThroughClientSequence
                    || IsReplicationStoragePolicyStorageDisposed(pending.Storage)
                    || !TryCreateReplicationStoragePolicyTargetReference(
                        pending.Storage,
                        out var provisional,
                        out var provisionalStorage,
                        out _)
                    || provisional.Canonical
                    || !string.Equals(
                        provisional.Kind,
                        requested.Kind,
                        StringComparison.Ordinal)
                    || provisional.ComponentOrdinal != requested.ComponentOrdinal
                    || !string.Equals(
                        provisional.BlueprintFingerprint,
                        requested.BlueprintFingerprint,
                        StringComparison.Ordinal)
                    || provisional.AnchorX != requested.AnchorX
                    || provisional.AnchorY != requested.AnchorY
                    || provisional.AnchorZ != requested.AnchorZ)
                {
                    continue;
                }

                // The host emitted this canonical row as the exact result of the
                // client's one in-flight request for this retained object. That
                // sequence proof is the missing identity edge for independently
                // allocated post-connect stockpiles; unlike an unsolicited baseline,
                // it cannot bind a replacement merely because an anchor matches.
                storage = provisionalStorage;
                canonical = requested.Clone();
                RegisterReplicationStoragePolicyCanonicalIdentity(
                    storage,
                    canonical);
                detail = "ok sequence-bound-canonical-adoption sequence="
                    + state.ProofThroughClientSequence.ToString(
                        CultureInfo.InvariantCulture);
                return true;
            }

            detail = "storage-policy-state-proof-bootstrap-not-matched sequence="
                + state.ProofThroughClientSequence.ToString(
                    CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryCreateReplicationStoragePolicyState(
            ReplicationStoragePolicySnapshot snapshot,
            ReplicationStoragePolicyTargetReference canonical,
            bool exists,
            long proofThrough,
            bool advanceRevision,
            out StoragePolicyState state,
            out string detail)
        {
            state = null!;
            if (!TryGetReplicationStoragePolicyCatalog(out var catalog, out detail))
            {
                return false;
            }

            canonical.Canonical = true;
            var key = FormatReplicationStoragePolicyTargetKey(canonical);
            ReplicationStoragePolicyHostRevisionByTarget.TryGetValue(key, out var revision);
            if (advanceRevision || revision <= 0L)
            {
                revision = Math.Max(1L, revision + 1L);
            }
            ReplicationStoragePolicyHostProofThroughByTarget.TryGetValue(
                key, out var priorProof);
            proofThrough = Math.Max(priorProof, proofThrough);

            if (!exists)
            {
                state = new StoragePolicyState(
                    ToReplicationStoragePolicyWireTarget(canonical, forceCanonical: true),
                    false,
                    GetReplicationStoragePolicyEpoch(),
                    revision,
                    proofThrough,
                    catalog.Signature,
                    catalog.ResourceIds.Length,
                    snapshot.TopologySignature,
                    0,
                    false,
                    string.Empty,
                    Array.Empty<StoragePolicyFilterState>());
                if (!StoragePolicyPayloadCodec.TryValidateState(state, out detail))
                {
                    return false;
                }
                detail = "ok tombstone revision="
                    + revision.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            var filters = new StoragePolicyFilterState[snapshot.Slots.Count];
            for (var i = 0; i < snapshot.Slots.Count; i++)
            {
                var slot = snapshot.Slots[i];
                filters[i] = new StoragePolicyFilterState(
                    slot.Ordinal,
                    slot.UniversalStorageId,
                    slot.DefaultAllowedFingerprint,
                    slot.HitPointsMinimum,
                    slot.HitPointsMaximum,
                    slot.QualityMinimum,
                    slot.QualityMaximum,
                    CreateReplicationStoragePolicyAllowedMask(slot.Allowed));
            }

            state = new StoragePolicyState(
                ToReplicationStoragePolicyWireTarget(canonical, forceCanonical: true),
                true,
                GetReplicationStoragePolicyEpoch(),
                revision,
                proofThrough,
                catalog.Signature,
                catalog.ResourceIds.Length,
                snapshot.TopologySignature,
                snapshot.Priority,
                snapshot.UseInProduction,
                snapshot.Name,
                filters);
            if (!StoragePolicyPayloadCodec.TryValidateState(state, out detail))
            {
                return false;
            }
            detail = "ok revision=" + revision.ToString(CultureInfo.InvariantCulture)
                + " proofThrough=" + proofThrough.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static long GetReplicationStoragePolicyEpoch()
        {
            var current = instance;
            if (ReferenceEquals(current, null)
                || current.multiplayerSaveTransfer.SessionId <= 0L)
            {
                return 0L;
            }

            var hash = new DeterminismHash();
            hash.Add("storage-policy-session-world-v1");
            hash.Add(current.multiplayerSaveTransfer.SessionId);
            hash.Add(Math.Max(0, current.multiplayerSaveTransfer.Epoch));
            var epoch = (long)(hash.Value & 0x7fffffffffffffffUL);
            return epoch == 0L ? 1L : epoch;
        }

        private static void CommitReplicationStoragePolicyStateOrdering(
            StoragePolicyState state)
        {
            var key = FormatReplicationStoragePolicyTargetKey(
                FromReplicationStoragePolicyWireTarget(state.Target));
            if (!ReplicationStoragePolicyHostRevisionByTarget.TryGetValue(
                    key, out var revision)
                || state.Revision > revision)
            {
                ReplicationStoragePolicyHostRevisionByTarget[key] = state.Revision;
            }
            if (!ReplicationStoragePolicyHostProofThroughByTarget.TryGetValue(
                    key, out var proof)
                || state.ProofThroughClientSequence > proof)
            {
                ReplicationStoragePolicyHostProofThroughByTarget[key] =
                    state.ProofThroughClientSequence;
            }
            if (state.Exists)
            {
                ReplicationStoragePolicyHostKnownTargets[key] =
                    FromReplicationStoragePolicyWireTarget(state.Target);
            }
            else
            {
                ReplicationStoragePolicyHostKnownTargets.Remove(key);
            }
        }

        private static long GetReplicationStoragePolicyHostProofThrough(string targetKey)
        {
            return ReplicationStoragePolicyHostProofThroughByTarget.TryGetValue(
                targetKey, out var value) ? value : 0L;
        }

        private void SendReplicationStoragePolicyState(
            StoragePolicyUpdate request,
            string source,
            long proofThroughClientSequence)
        {
            if (!StoragePolicyV4Enabled()) return;
            if (request.Epoch != GetReplicationStoragePolicyEpoch())
            {
                LogReplicationWarning(
                    "Going Cooperative storage-policy state request epoch mismatch expected="
                    + GetReplicationStoragePolicyEpoch().ToString(CultureInfo.InvariantCulture)
                    + " actual=" + request.Epoch.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (replicationStoragePolicyFailStopped)
            {
                LogReplicationStoragePolicyRetainedThrottled(
                    "state-send-fail-stopped",
                    FormatReplicationStoragePolicyTargetKey(
                        FromReplicationStoragePolicyWireTarget(request.Target)),
                    replicationStoragePolicyRecoveryReason);
                return;
            }

            var requested = FromReplicationStoragePolicyWireTarget(request.Target);
            if (!TryResolveReplicationStoragePolicyTarget(
                    requested, out var storage, out var canonical, out var resolveDetail)
                || storage == null)
            {
                var requestedKey = FormatReplicationStoragePolicyTargetKey(requested);
                if (!resolveDetail.StartsWith(
                        "storage-policy-target-missing ",
                        StringComparison.Ordinal))
                {
                    LogReplicationStoragePolicyRetainedThrottled(
                        "state-send-resolution",
                        requestedKey,
                        resolveDetail);
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "state-send-resolution", requestedKey))
                    {
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-state-resolution-nonconvergent target="
                            + requestedKey + " detail=" + resolveDetail);
                    }
                    return;
                }

                if (!TryProveReplicationStoragePolicyCanonicalTargetDisposed(
                        requested, out var disposalDetail))
                {
                    LogReplicationStoragePolicyRetainedThrottled(
                        "state-send-missing-unproven",
                        requestedKey,
                        "resolve=" + resolveDetail + " disposal=" + disposalDetail);
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "state-send-missing-unproven", requestedKey))
                    {
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-state-target-missing-unproven target="
                            + requestedKey + " resolve=" + resolveDetail
                            + " disposal=" + disposalDetail);
                    }
                    return;
                }

                // A tombstone is authority only for an identity that the host had
                // already canonicalized and whose native object proves disposal.
                canonical = requested.Clone();
                var empty = new ReplicationStoragePolicySnapshot
                {
                    Target = canonical.Clone(),
                    TopologySignature = request.TopologySignature
                };
                if (TryCreateReplicationStoragePolicyState(
                        empty,
                        canonical,
                        exists: false,
                        proofThrough: proofThroughClientSequence,
                        advanceRevision: true,
                        out var tombstone,
                        out var tombstoneDetail)
                    && LockstepCommandPayloads.TryCreateStoragePolicyStatePayload(
                        tombstone, out var tombstonePayload))
                {
                    if (SendReplicationManagementDelta(
                            tombstonePayload,
                            source + ":storage-policy-tombstone",
                            proofThroughClientSequence))
                    {
                        CommitReplicationStoragePolicyStateOrdering(tombstone);
                        ClearReplicationStoragePolicyRetainedThrottle(
                            "state-send-tombstone", requestedKey);
                        return;
                    }
                    tombstoneDetail = "state-not-queued";
                }

                LogReplicationWarning(
                    "Going Cooperative storage-policy tombstone send failed target="
                    + FormatReplicationStoragePolicyTargetKey(requested)
                    + " resolve=" + resolveDetail
                    + " state=" + tombstoneDetail);
                if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                        "state-send-tombstone", requestedKey))
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-state-tombstone-send-nonconvergent target="
                        + requestedKey + " detail=" + tombstoneDetail);
                }
                return;
            }

            var readDetail = "not-read";
            var stateDetail = "not-created";
            if (!TryReadReplicationStoragePolicySnapshot(
                    storage, canonical, out var snapshot, out readDetail)
                || !TryCreateReplicationStoragePolicyState(
                    snapshot,
                    canonical,
                    exists: true,
                    proofThrough: proofThroughClientSequence,
                    advanceRevision: true,
                    out var state,
                    out stateDetail)
                || !LockstepCommandPayloads.TryCreateStoragePolicyStatePayload(
                    state, out var payload))
            {
                var canonicalKey = FormatReplicationStoragePolicyTargetKey(canonical);
                LogReplicationWarning(
                    "Going Cooperative storage-policy state send failed target="
                    + canonicalKey
                    + " read=" + readDetail + " state=" + stateDetail);
                if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                        "state-send-encode", canonicalKey))
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-state-send-nonconvergent target="
                        + canonicalKey + " read=" + readDetail
                        + " state=" + stateDetail);
                }
                return;
            }

            var sentTargetKey = FormatReplicationStoragePolicyTargetKey(canonical);
            if (SendReplicationManagementDelta(
                    payload,
                    source + ":storage-policy-state",
                    proofThroughClientSequence))
            {
                CommitReplicationStoragePolicyStateOrdering(state);
                ClearReplicationStoragePolicyRetainedThrottle(
                    "state-send-encode", sentTargetKey);
                ClearReplicationStoragePolicyRetainedThrottle(
                    "state-send-not-queued", sentTargetKey);
            }
            else
            {
                LogReplicationStoragePolicyRetainedThrottled(
                    "state-send-not-queued",
                    sentTargetKey,
                    source);
                if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                        "state-send-not-queued", sentTargetKey))
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-state-queue-nonconvergent target="
                        + sentTargetKey + " source=" + source);
                }
            }
        }

        private static bool TryApplyReplicationStoragePolicyUpdate(
            StoragePolicyUpdate update,
            out string detail)
        {
            if (!StoragePolicyV4Enabled())
            {
                detail = "storage-policy-v4-disabled";
                return false;
            }
            if (update.Epoch != GetReplicationStoragePolicyEpoch())
            {
                detail = "storage-policy-update-epoch-mismatch expected="
                    + GetReplicationStoragePolicyEpoch().ToString(CultureInfo.InvariantCulture)
                    + " actual=" + update.Epoch.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (replicationStoragePolicyFailStopped)
            {
                detail = "storage-policy-lane-fail-stopped reason="
                    + replicationStoragePolicyRecoveryReason;
                return false;
            }

            var catalogDetail = "not-read";
            if (!StoragePolicyPayloadCodec.TryValidateUpdate(update, out detail)
                || !TryGetReplicationStoragePolicyCatalog(out var catalog, out catalogDetail)
                || update.CatalogCount != catalog.ResourceIds.Length
                || !string.Equals(
                    update.CatalogSignature, catalog.Signature, StringComparison.Ordinal))
            {
                detail = "storage-policy-update-catalog-invalid " + detail
                    + " local=" + catalogDetail;
                return false;
            }

            var requested = FromReplicationStoragePolicyWireTarget(update.Target);
            var resolveDetail = "not-resolved";
            var readDetail = "not-read";
            if (!TryResolveReplicationStoragePolicyTarget(
                    requested, out var storage, out var canonical, out resolveDetail)
                || storage == null
                || !TryReadReplicationStoragePolicySnapshot(
                    storage, canonical, out var current, out readDetail))
            {
                detail = "storage-policy-update-target-missing "
                    + resolveDetail + " read=" + readDetail;
                return false;
            }

            if (!string.Equals(
                    update.TopologySignature,
                    current.TopologySignature,
                    StringComparison.Ordinal))
            {
                detail = "storage-policy-update-topology-mismatch remote="
                    + update.TopologySignature + " local=" + current.TopologySignature;
                return false;
            }

            var desired = current.Clone();
            var accepted = new List<StoragePolicyChange>();
            var stale = 0;
            var remoteSequence = replicationApplyingRemoteManagementCommandSequence;
            var targetKey = FormatReplicationStoragePolicyTargetKey(canonical);
            for (var i = 0; i < update.Changes.Length; i++)
            {
                var change = update.Changes[i];
                var orderingKey = targetKey + "|" + change.CellKey;
                if (remoteSequence > 0L
                    && ReplicationStoragePolicyHostHighWaterByCell.TryGetValue(
                        orderingKey, out var highWater)
                    && remoteSequence <= highWater)
                {
                    stale++;
                    continue;
                }

                if (!TryApplyReplicationStoragePolicyWireChangeToSnapshot(
                        desired, change, out var changeDetail))
                {
                    detail = "storage-policy-update-cell-invalid key="
                        + change.CellKey + " " + changeDetail;
                    return false;
                }
                accepted.Add(change);
            }

            if (accepted.Count == 0)
            {
                detail = "ok storage-policy-update-stale-noop target=" + targetKey
                    + " stale=" + stale.ToString(CultureInfo.InvariantCulture)
                    + " sequence=" + remoteSequence.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            var changed = !ReplicationStoragePolicySnapshotsEqual(current, desired);
            var irreversibleResourceSideEffectsStarted = false;
            replicationStoragePolicyAuthoritativeApplyDepth++;
            try
            {
                var applyDetail = "not-applied";
                if (changed
                    && !TryApplyReplicationStoragePolicySnapshot(
                        storage,
                        current,
                        desired,
                        out irreversibleResourceSideEffectsStarted,
                        out applyDetail))
                {
                    var rollbackSucceeded = TryRollbackReplicationStoragePolicySnapshot(
                        storage,
                        current,
                        out var rollbackResourceSideEffectsStarted,
                        out var rollbackDetail);
                    if (irreversibleResourceSideEffectsStarted
                        || rollbackResourceSideEffectsStarted)
                    {
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-host-update-apply-native-resource-side-effects-unproven target="
                            + targetKey + " failure=" + applyDetail
                            + " policyRollback=" + rollbackDetail
                            + " forwardSideEffects="
                            + irreversibleResourceSideEffectsStarted.ToString().ToLowerInvariant()
                            + " rollbackSideEffects="
                            + rollbackResourceSideEffectsStarted.ToString().ToLowerInvariant());
                    }
                    else
                    {
                        HandleReplicationStoragePolicyRollbackResult(
                            rollbackSucceeded,
                            "host-update-apply",
                            targetKey,
                            applyDetail,
                            rollbackDetail);
                    }
                    detail = "storage-policy-update-apply-failed " + applyDetail
                        + " rollback=" + rollbackDetail;
                    return false;
                }

                var readbackDetail = "not-read";
                if (!TryReadReplicationStoragePolicySnapshot(
                        storage, canonical, out var readback, out readbackDetail)
                    || !ReplicationStoragePolicySnapshotsEqual(readback, desired))
                {
                    var rollbackSucceeded = TryRollbackReplicationStoragePolicySnapshot(
                        storage,
                        current,
                        out var rollbackResourceSideEffectsStarted,
                        out var rollbackDetail);
                    if (irreversibleResourceSideEffectsStarted
                        || rollbackResourceSideEffectsStarted)
                    {
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-host-update-readback-native-resource-side-effects-unproven target="
                            + targetKey + " failure=" + readbackDetail
                            + " policyRollback=" + rollbackDetail
                            + " forwardSideEffects="
                            + irreversibleResourceSideEffectsStarted.ToString().ToLowerInvariant()
                            + " rollbackSideEffects="
                            + rollbackResourceSideEffectsStarted.ToString().ToLowerInvariant());
                    }
                    else
                    {
                        HandleReplicationStoragePolicyRollbackResult(
                            rollbackSucceeded,
                            "host-update-readback",
                            targetKey,
                            readbackDetail,
                            rollbackDetail);
                    }
                    detail = "storage-policy-update-readback-failed read="
                        + readbackDetail + " rollback=" + rollbackDetail;
                    return false;
                }

                if (remoteSequence > 0L)
                {
                    for (var i = 0; i < accepted.Count; i++)
                    {
                        ReplicationStoragePolicyHostHighWaterByCell[
                            targetKey + "|" + accepted[i].CellKey] = remoteSequence;
                    }
                }
                var uiDetail = RefreshReplicationStoragePolicyUi(targetKey);
                detail = "ok storage-policy-update target=" + targetKey
                    + " accepted=" + accepted.Count.ToString(CultureInfo.InvariantCulture)
                    + " stale=" + stale.ToString(CultureInfo.InvariantCulture)
                    + " changed=" + changed.ToString().ToLowerInvariant()
                    + " sequence=" + remoteSequence.ToString(CultureInfo.InvariantCulture)
                    + " ui=" + uiDetail;
                return true;
            }
            catch (Exception ex)
            {
                var exceptionDetail = FormatReflectionExceptionDetail(ex);
                var rollbackSucceeded = TryRollbackReplicationStoragePolicySnapshot(
                    storage,
                    current,
                    out var rollbackResourceSideEffectsStarted,
                    out var rollbackDetail);
                if (irreversibleResourceSideEffectsStarted
                    || rollbackResourceSideEffectsStarted)
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-host-update-exception-native-resource-side-effects-unproven target="
                        + targetKey + " failure=" + exceptionDetail
                        + " policyRollback=" + rollbackDetail
                        + " forwardSideEffects="
                        + irreversibleResourceSideEffectsStarted.ToString().ToLowerInvariant()
                        + " rollbackSideEffects="
                        + rollbackResourceSideEffectsStarted.ToString().ToLowerInvariant());
                }
                else
                {
                    HandleReplicationStoragePolicyRollbackResult(
                        rollbackSucceeded,
                        "host-update-exception",
                        targetKey,
                        exceptionDetail,
                        rollbackDetail);
                }
                detail = "storage-policy-update-exception "
                    + exceptionDetail
                    + " rollback=" + rollbackDetail;
                return false;
            }
            finally
            {
                replicationStoragePolicyAuthoritativeApplyDepth--;
            }
        }

        private static bool TryApplyReplicationStoragePolicyWireChangeToSnapshot(
            ReplicationStoragePolicySnapshot snapshot,
            StoragePolicyChange change,
            out string detail)
        {
            switch (change.Kind)
            {
                case StoragePolicyChangeKind.Priority:
                    snapshot.Priority = change.IntegerValue;
                    detail = "ok";
                    return true;
                case StoragePolicyChangeKind.ProductionUse:
                    snapshot.UseInProduction = change.BooleanValue;
                    detail = "ok";
                    return true;
                case StoragePolicyChangeKind.Name:
                    snapshot.Name = change.StringValue;
                    detail = "ok";
                    return true;
                case StoragePolicyChangeKind.HitPointsRange:
                    if (!TryGetReplicationStoragePolicyChangeSlot(
                            snapshot, change.SlotIndex, out var hpSlot, out detail))
                    {
                        return false;
                    }
                    hpSlot.HitPointsMinimum = change.Minimum;
                    hpSlot.HitPointsMaximum = change.Maximum;
                    detail = "ok";
                    return true;
                case StoragePolicyChangeKind.QualityRange:
                    if (!TryGetReplicationStoragePolicyChangeSlot(
                            snapshot, change.SlotIndex, out var qualitySlot, out detail))
                    {
                        return false;
                    }
                    qualitySlot.QualityMinimum = change.Minimum;
                    qualitySlot.QualityMaximum = change.Maximum;
                    detail = "ok";
                    return true;
                case StoragePolicyChangeKind.ResourceAllowed:
                    if (!TryGetReplicationStoragePolicyChangeSlot(
                            snapshot, change.SlotIndex, out var resourceSlot, out detail)
                        || change.CatalogIndex < 0
                        || change.CatalogIndex >= resourceSlot.Allowed.Length
                        || !resourceSlot.DefaultAllowed[change.CatalogIndex])
                    {
                        detail = "storage-policy-resource-outside-slot-capability slot="
                            + change.SlotIndex.ToString(CultureInfo.InvariantCulture)
                            + " resource="
                            + change.CatalogIndex.ToString(CultureInfo.InvariantCulture)
                            + " " + detail;
                        return false;
                    }
                    resourceSlot.Allowed[change.CatalogIndex] = change.BooleanValue;
                    detail = "ok";
                    return true;
                default:
                    detail = "storage-policy-change-kind-unsupported";
                    return false;
            }
        }

        private static bool TryGetReplicationStoragePolicyChangeSlot(
            ReplicationStoragePolicySnapshot snapshot,
            int slotIndex,
            out ReplicationStoragePolicySlotSnapshot slot,
            out string detail)
        {
            slot = null!;
            if (slotIndex < 0
                || slotIndex >= snapshot.Slots.Count
                || snapshot.Slots[slotIndex].Ordinal != slotIndex)
            {
                detail = "storage-policy-slot-index-invalid slot="
                    + slotIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            slot = snapshot.Slots[slotIndex];
            detail = "ok";
            return true;
        }

        private static bool TryApplyReplicationStoragePolicySnapshot(
            object storage,
            ReplicationStoragePolicySnapshot previous,
            ReplicationStoragePolicySnapshot desired,
            out string detail)
        {
            return TryApplyReplicationStoragePolicySnapshot(
                storage,
                previous,
                desired,
                out _,
                out detail);
        }

        private static bool TryApplyReplicationStoragePolicySnapshot(
            object storage,
            ReplicationStoragePolicySnapshot previous,
            ReplicationStoragePolicySnapshot desired,
            out bool irreversibleResourceSideEffectsStarted,
            out string detail)
        {
            irreversibleResourceSideEffectsStarted = false;
            detail = "not-read";
            if (previous.Slots.Count != desired.Slots.Count
                || !string.Equals(
                    previous.TopologySignature,
                    desired.TopologySignature,
                    StringComparison.Ordinal)
                || !TryGetReplicationStoragePolicyCatalog(out var catalog, out detail))
            {
                detail = "storage-policy-transaction-shape-invalid " + detail;
                return false;
            }

            var rangeType = AccessTools.TypeByName("NSEipix.Model.IntRange");
            var priorityType = AccessTools.TypeByName("NSMedieval.State.ZonePriority");
            var resourceType = AccessTools.TypeByName("NSMedieval.Model.Resource");
            var setPriority = priorityType == null
                ? null
                : AccessTools.Method(storage.GetType(), "SetPriority", new[] { priorityType });
            var setProduction = AccessTools.Method(
                storage.GetType(), "SetCanBeUsedInProduction", new[] { typeof(bool) });
            var setName = AccessTools.Method(
                storage.GetType(), "SetName", new[] { typeof(string) });
            if (rangeType == null || priorityType == null || resourceType == null
                || setPriority == null || setProduction == null || setName == null)
            {
                detail = "storage-policy-transaction-common-surface-missing";
                return false;
            }

            // Resolve every reflection surface before the first mutation. Host
            // resource changes must traverse the same native AllowResource roots as
            // a host-local click so animal feeders execute DropResource. Client
            // projection deliberately writes the filter only; authoritative pile
            // deltas own the host-created drop on that peer.
            var nativeResourceTargets = new object?[desired.Slots.Count];
            var resourceMutators = new MethodInfo?[desired.Slots.Count];
            var hitPointMutators = new MethodInfo?[desired.Slots.Count];
            var qualityMutators = new MethodInfo?[desired.Slots.Count];
            for (var i = 0; i < desired.Slots.Count; i++)
            {
                var before = previous.Slots[i];
                var after = desired.Slots[i];
                var filter = after.Filter;
                if (filter == null)
                {
                    detail = "storage-policy-transaction-filter-missing slot="
                        + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (!ReplicationStoragePolicyBooleanArraysEqual(
                        before.Allowed, after.Allowed))
                {
                    nativeResourceTargets[i] = replicationConfigHostMode
                        ? after.UniversalStorage ?? storage
                        : filter;
                    resourceMutators[i] = replicationConfigHostMode
                        ? AccessTools.Method(
                            nativeResourceTargets[i]!.GetType(),
                            "AllowResource",
                            new[] { resourceType, typeof(bool) })
                        : AccessTools.Method(
                            filter.GetType(), "SetAllowedResourceTypes");
                    if (resourceMutators[i] == null)
                    {
                        detail = "storage-policy-resource-mutation-surface-missing slot="
                            + i.ToString(CultureInfo.InvariantCulture)
                            + " hostNative="
                            + replicationConfigHostMode.ToString().ToLowerInvariant();
                        return false;
                    }
                }

                if (before.HitPointsMinimum != after.HitPointsMinimum
                    || before.HitPointsMaximum != after.HitPointsMaximum)
                {
                    hitPointMutators[i] = AccessTools.Method(
                        filter.GetType(), "SetHitPointsPercent", new[] { rangeType });
                    if (hitPointMutators[i] == null)
                    {
                        detail = "storage-policy-set-hp-surface-missing slot="
                            + i.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }

                if (before.QualityMinimum != after.QualityMinimum
                    || before.QualityMaximum != after.QualityMaximum)
                {
                    qualityMutators[i] = AccessTools.Method(
                        filter.GetType(), "SetQuality", new[] { rangeType });
                    if (qualityMutators[i] == null)
                    {
                        detail = "storage-policy-set-quality-surface-missing slot="
                            + i.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }
            }

            var ownsFilterNotificationScope =
                replicationStoragePolicyFilterNotificationSuppressionDepth == 0;
            if (ownsFilterNotificationScope)
            {
                ReplicationStoragePolicyDeferredFilterNotifications.Clear();
            }
            replicationStoragePolicyFilterNotificationSuppressionDepth++;
            try
            {
                for (var i = 0; i < desired.Slots.Count; i++)
                {
                    var before = previous.Slots[i];
                    var after = desired.Slots[i];
                    var filter = after.Filter;
                    if (filter == null) return false;

                    var filterChanged = false;
                    if (!ReplicationStoragePolicyBooleanArraysEqual(
                            before.Allowed, after.Allowed))
                    {
                        if (replicationConfigHostMode)
                        {
                            for (var resource = 0; resource < after.Allowed.Length; resource++)
                            {
                                if (before.Allowed[resource] == after.Allowed[resource])
                                {
                                    continue;
                                }
                                if (before.Allowed[resource] && !after.Allowed[resource]
                                    && after.UniversalStorage != null)
                                {
                                    // UniversalStorage may drop feeder contents. If
                                    // anything after this point fails, filter readback
                                    // alone cannot prove that world/resource topology
                                    // was rolled back.
                                    irreversibleResourceSideEffectsStarted = true;
                                }
                                resourceMutators[i]!.Invoke(
                                    nativeResourceTargets[i],
                                    new object[]
                                    {
                                        catalog.Resources[resource],
                                        after.Allowed[resource]
                                    });
                            }
                        }
                        else
                        {
                            var resourceArray = Array.CreateInstance(
                                resourceType,
                                CountReplicationStoragePolicyAllowed(after.Allowed));
                            var destination = 0;
                            for (var resource = 0; resource < after.Allowed.Length; resource++)
                            {
                                if (after.Allowed[resource])
                                {
                                    resourceArray.SetValue(
                                        catalog.Resources[resource], destination++);
                                }
                            }
                            resourceMutators[i]!.Invoke(
                                nativeResourceTargets[i], new object[] { resourceArray });
                        }
                        filterChanged = true;
                    }

                    if (before.HitPointsMinimum != after.HitPointsMinimum
                        || before.HitPointsMaximum != after.HitPointsMaximum)
                    {
                        hitPointMutators[i]!.Invoke(filter, new[]
                        {
                            Activator.CreateInstance(
                                rangeType,
                                after.HitPointsMinimum,
                                after.HitPointsMaximum)
                        });
                        filterChanged = true;
                    }

                    if (before.QualityMinimum != after.QualityMinimum
                        || before.QualityMaximum != after.QualityMaximum)
                    {
                        qualityMutators[i]!.Invoke(filter, new[]
                        {
                            Activator.CreateInstance(
                                rangeType,
                                after.QualityMinimum,
                                after.QualityMaximum)
                        });
                        filterChanged = true;
                    }

                    if (filterChanged)
                    {
                        ReplicationStoragePolicyDeferredFilterNotifications.Add(filter);
                    }
                }

                if (previous.Priority != desired.Priority)
                {
                    setPriority.Invoke(storage, new[]
                    {
                        Enum.ToObject(priorityType, desired.Priority)
                    });
                }
                if (previous.UseInProduction != desired.UseInProduction)
                {
                    setProduction.Invoke(storage, new object[] { desired.UseInProduction });
                }
                if (!string.Equals(previous.Name, desired.Name, StringComparison.Ordinal))
                {
                    setName.Invoke(storage, new object[] { desired.Name });
                }
            }
            catch (Exception ex)
            {
                detail = "storage-policy-transaction-invoke-failed "
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                replicationStoragePolicyFilterNotificationSuppressionDepth--;
            }

            if (!ownsFilterNotificationScope)
            {
                detail = "ok filters-deferred="
                    + ReplicationStoragePolicyDeferredFilterNotifications.Count.ToString(
                        CultureInfo.InvariantCulture);
                return true;
            }

            return FlushReplicationStoragePolicyDeferredFilterNotifications(
                out detail);
        }

        private static bool FlushReplicationStoragePolicyDeferredFilterNotifications(
            out string detail)
        {
            var deferred = new List<object>(
                ReplicationStoragePolicyDeferredFilterNotifications);
            ReplicationStoragePolicyDeferredFilterNotifications.Clear();
            for (var i = 0; i < deferred.Count; i++)
            {
                try
                {
                    var parametersChanged = AccessTools.Method(
                        deferred[i].GetType(),
                        "ParametersChanged",
                        Type.EmptyTypes);
                    if (parametersChanged == null)
                    {
                        detail = "storage-policy-filter-notification-surface-missing";
                        return false;
                    }
                    parametersChanged.Invoke(deferred[i], null);
                }
                catch (Exception ex)
                {
                    detail = "storage-policy-filter-notification-failed "
                        + FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            detail = "ok filters="
                + deferred.Count.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryRollbackReplicationStoragePolicySnapshot(
            object storage,
            ReplicationStoragePolicySnapshot previous,
            out bool irreversibleResourceSideEffectsStarted,
            out string detail)
        {
            irreversibleResourceSideEffectsStarted = false;
            var targetDetail = "not-resolved";
            var currentDetail = "not-read";
            var applyDetail = "not-applied";
            var readbackDetail = "not-read";
            if (!TryCreateReplicationStoragePolicyTargetReference(
                    storage, out var target, out _, out targetDetail)
                || !TryReadReplicationStoragePolicySnapshot(
                    storage, target, out var current, out currentDetail)
                || !TryApplyReplicationStoragePolicySnapshot(
                    storage,
                    current,
                    previous,
                    out irreversibleResourceSideEffectsStarted,
                    out applyDetail)
                || !TryReadReplicationStoragePolicySnapshot(
                    storage, target, out var readback, out readbackDetail)
                || !ReplicationStoragePolicySnapshotsEqual(readback, previous))
            {
                detail = "rollback-failed target=" + targetDetail
                    + " current=" + currentDetail
                    + " apply=" + applyDetail
                    + " readback=" + readbackDetail;
                return false;
            }
            detail = "ok";
            return true;
        }

        private static bool ReplicationStoragePolicySnapshotsEqual(
            ReplicationStoragePolicySnapshot left,
            ReplicationStoragePolicySnapshot right)
        {
            if (left.Priority != right.Priority
                || left.UseInProduction != right.UseInProduction
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !string.Equals(
                    left.TopologySignature, right.TopologySignature, StringComparison.Ordinal)
                || left.Slots.Count != right.Slots.Count)
            {
                return false;
            }
            for (var i = 0; i < left.Slots.Count; i++)
            {
                var first = left.Slots[i];
                var second = right.Slots[i];
                if (first.Ordinal != second.Ordinal
                    || !string.Equals(
                        first.UniversalStorageId,
                        second.UniversalStorageId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        first.DefaultAllowedFingerprint,
                        second.DefaultAllowedFingerprint,
                        StringComparison.Ordinal)
                    || first.HitPointsMinimum != second.HitPointsMinimum
                    || first.HitPointsMaximum != second.HitPointsMaximum
                    || first.QualityMinimum != second.QualityMinimum
                    || first.QualityMaximum != second.QualityMaximum
                    || !ReplicationStoragePolicyBooleanArraysEqual(
                        first.Allowed, second.Allowed))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ReplicationStoragePolicyBooleanArraysEqual(
            bool[] left,
            bool[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static int CountReplicationStoragePolicyAllowed(bool[] values)
        {
            var count = 0;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i])
                {
                    count++;
                }
            }
            return count;
        }

        private static bool TryApplyReplicationStoragePolicyState(
            StoragePolicyState state,
            out string detail)
        {
            // CompleteReplicationStoragePolicyStateProof is invoked immediately by
            // the management lane after this method succeeds. Reset the one-shot
            // context up front so a failed or unrelated state can never complete an
            // in-flight sparse command.
            replicationStoragePolicyStateCompletionContext = null;
            if (!StoragePolicyV4Enabled())
            {
                detail = "storage-policy-v4-disabled";
                return false;
            }
            if (state.Epoch != GetReplicationStoragePolicyEpoch())
            {
                detail = "storage-policy-state-epoch-mismatch expected="
                    + GetReplicationStoragePolicyEpoch().ToString(CultureInfo.InvariantCulture)
                    + " actual=" + state.Epoch.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (replicationStoragePolicyFailStopped)
            {
                detail = "storage-policy-lane-fail-stopped reason="
                    + replicationStoragePolicyRecoveryReason;
                return false;
            }

            var catalogDetail = "not-read";
            if (!StoragePolicyPayloadCodec.TryValidateState(state, out detail)
                || !TryGetReplicationStoragePolicyCatalog(out var catalog, out catalogDetail)
                || state.CatalogCount != catalog.ResourceIds.Length
                || !string.Equals(
                    state.CatalogSignature, catalog.Signature, StringComparison.Ordinal))
            {
                detail = "storage-policy-state-catalog-invalid " + detail
                    + " local=" + catalogDetail;
                RequestReplicationStoragePolicyRecovery(detail);
                return false;
            }

            var target = FromReplicationStoragePolicyWireTarget(state.Target);
            var targetKey = FormatReplicationStoragePolicyTargetKey(target);
            if (ReplicationStoragePolicyClientRevisionByTarget.TryGetValue(
                    targetKey, out var appliedRevision)
                && state.Revision <= appliedRevision)
            {
                SetReplicationStoragePolicyStateCompletionContext(
                    state,
                    targetKey);
                detail = "ok storage-policy-state-stale target=" + targetKey
                    + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                    + " applied=" + appliedRevision.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (!state.Exists)
            {
                var localStillExists = TryResolveReplicationStoragePolicyTarget(
                    target, out _, out _, out var tombstoneResolveDetail);
                ReplicationStoragePolicyClientRevisionByTarget[targetKey] = state.Revision;
                ReplicationStoragePolicyClientTombstones.Add(targetKey);
                SetReplicationStoragePolicyStateCompletionContext(
                    state,
                    targetKey);
                RefreshReplicationStoragePolicyUi(targetKey);
                if (localStillExists)
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-tombstone-local-target target=" + targetKey);
                }
                detail = "ok storage-policy-tombstone target=" + targetKey
                    + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                    + " localExists=" + localStillExists.ToString().ToLowerInvariant()
                    + " resolve=" + tombstoneResolveDetail;
                return true;
            }

            var resolveDetail = "not-resolved";
            var readDetail = "not-read";
            var resolved = TryResolveReplicationStoragePolicyTarget(
                target, out var storage, out var canonical, out resolveDetail);
            if ((!resolved || storage == null)
                && TryResolveReplicationStoragePolicyStateProofTarget(
                    state,
                    target,
                    out var proofStorage,
                    out var proofCanonical,
                    out var proofResolveDetail))
            {
                storage = proofStorage;
                canonical = proofCanonical;
                resolveDetail += " proof=" + proofResolveDetail;
                resolved = storage != null;
            }
            if (!resolved
                || storage == null
                || !TryReadReplicationStoragePolicySnapshot(
                    storage, canonical, out var current, out readDetail))
            {
                detail = "storage-policy-state-target-missing " + resolveDetail
                    + " read=" + readDetail;
                if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                        "client-state-target", targetKey))
                {
                    RequestReplicationStoragePolicyRecovery(
                        detail + " target=" + targetKey);
                }
                return false;
            }

            if (!string.Equals(
                    state.TopologySignature,
                    current.TopologySignature,
                    StringComparison.Ordinal)
                || state.Filters.Length != current.Slots.Count)
            {
                detail = "storage-policy-state-topology-mismatch remote="
                    + state.TopologySignature + "/"
                    + state.Filters.Length.ToString(CultureInfo.InvariantCulture)
                    + " local=" + current.TopologySignature + "/"
                    + current.Slots.Count.ToString(CultureInfo.InvariantCulture);
                if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                        "client-state-topology", targetKey))
                {
                    RequestReplicationStoragePolicyRecovery(
                        detail + " target=" + targetKey);
                }
                return false;
            }

            var desired = current.Clone();
            desired.Target = target.Clone();
            desired.Priority = state.Priority;
            desired.UseInProduction = state.CanBeUsedInProduction;
            desired.Name = state.Name;
            for (var i = 0; i < state.Filters.Length; i++)
            {
                var filter = state.Filters[i];
                var maskDetail = "not-validated";
                if (filter.SlotIndex != i
                    || !string.Equals(
                        filter.SlotId,
                        current.Slots[i].UniversalStorageId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        filter.DefaultAllowedFingerprint,
                        current.Slots[i].DefaultAllowedFingerprint,
                        StringComparison.Ordinal)
                    || !StoragePolicyPayloadCodec.TryValidateAllowedMask(
                        filter.AllowedResourceMask,
                        state.CatalogCount,
                        out maskDetail))
                {
                    detail = "storage-policy-state-slot-topology-invalid slot="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " mask=" + maskDetail;
                    if (ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "client-state-topology", targetKey))
                    {
                        RequestReplicationStoragePolicyRecovery(
                            detail + " target=" + targetKey);
                    }
                    return false;
                }

                var desiredSlot = desired.Slots[i];
                desiredSlot.HitPointsMinimum = filter.HitPointsMinimum;
                desiredSlot.HitPointsMaximum = filter.HitPointsMaximum;
                desiredSlot.QualityMinimum = filter.QualityMinimum;
                desiredSlot.QualityMaximum = filter.QualityMaximum;
                for (var resource = 0; resource < state.CatalogCount; resource++)
                {
                    var allowed = StoragePolicyPayloadCodec.IsResourceAllowed(
                        filter.AllowedResourceMask,
                        state.CatalogCount,
                        resource);
                    if (allowed && !desiredSlot.DefaultAllowed[resource])
                    {
                        detail = "storage-policy-state-resource-outside-capability slot="
                            + i.ToString(CultureInfo.InvariantCulture)
                            + " resource=" + resource.ToString(CultureInfo.InvariantCulture);
                        RequestReplicationStoragePolicyRecovery(
                            detail + " target=" + targetKey);
                        return false;
                    }
                    desiredSlot.Allowed[resource] = allowed;
                }
            }

            replicationStoragePolicyAuthoritativeApplyDepth++;
            try
            {
                var applyDetail = "not-applied";
                var readbackDetail = "not-read";
                if (!TryApplyReplicationStoragePolicySnapshot(
                        storage, current, desired, out applyDetail)
                    || !TryReadReplicationStoragePolicySnapshot(
                        storage, canonical, out var readback, out readbackDetail)
                    || !ReplicationStoragePolicySnapshotsEqual(readback, desired))
                {
                    var rollbackSucceeded = TryRollbackReplicationStoragePolicySnapshot(
                        storage,
                        current,
                        out var rollbackResourceSideEffectsStarted,
                        out var rollbackDetail);
                    if (rollbackResourceSideEffectsStarted)
                    {
                        RequestReplicationStoragePolicyRecovery(
                            "storage-policy-client-state-rollback-native-resource-side-effects-unproven target="
                            + targetKey + " apply=" + applyDetail
                            + " read=" + readbackDetail
                            + " rollback=" + rollbackDetail);
                    }
                    else
                    {
                        HandleReplicationStoragePolicyRollbackResult(
                            rollbackSucceeded,
                            "client-state-apply-readback",
                            targetKey,
                            "apply=" + applyDetail + " read=" + readbackDetail,
                            rollbackDetail);
                    }
                    detail = "storage-policy-state-apply-or-readback-failed apply="
                        + applyDetail + " read=" + readbackDetail
                        + " rollback=" + rollbackDetail;
                    if (!replicationStoragePolicyFailStopped
                        && ShouldEscalateReplicationStoragePolicyMissingTarget(
                            "client-state-apply", targetKey))
                    {
                        RequestReplicationStoragePolicyRecovery(
                            detail + " target=" + targetKey);
                    }
                    return false;
                }

                RegisterReplicationStoragePolicyCanonicalIdentity(storage, target);
                ClearReplicationStoragePolicyRetainedThrottle(
                    "client-state-target", targetKey);
                ClearReplicationStoragePolicyRetainedThrottle(
                    "client-state-topology", targetKey);
                ClearReplicationStoragePolicyRetainedThrottle(
                    "client-state-apply", targetKey);
                ReplicationStoragePolicyClientRevisionByTarget[targetKey] = state.Revision;
                ReplicationStoragePolicyClientTombstones.Remove(targetKey);
                SetReplicationStoragePolicyStateCompletionContext(
                    state,
                    targetKey);
                var uiDetail = RefreshReplicationStoragePolicyUi(targetKey);
                detail = "ok storage-policy-state target=" + targetKey
                    + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                    + " proofThrough="
                    + state.ProofThroughClientSequence.ToString(CultureInfo.InvariantCulture)
                    + " ui=" + uiDetail;
                return true;
            }
            catch (Exception ex)
            {
                var exceptionDetail = FormatReflectionExceptionDetail(ex);
                var rollbackSucceeded = TryRollbackReplicationStoragePolicySnapshot(
                    storage,
                    current,
                    out var rollbackResourceSideEffectsStarted,
                    out var rollbackDetail);
                if (rollbackResourceSideEffectsStarted)
                {
                    RequestReplicationStoragePolicyRecovery(
                        "storage-policy-client-state-exception-rollback-native-resource-side-effects-unproven target="
                        + targetKey + " failure=" + exceptionDetail
                        + " rollback=" + rollbackDetail);
                }
                else
                {
                    HandleReplicationStoragePolicyRollbackResult(
                        rollbackSucceeded,
                        "client-state-exception",
                        targetKey,
                        exceptionDetail,
                        rollbackDetail);
                }
                detail = "storage-policy-state-exception "
                    + exceptionDetail
                    + " rollback=" + rollbackDetail;
                if (!replicationStoragePolicyFailStopped
                    && ShouldEscalateReplicationStoragePolicyMissingTarget(
                        "client-state-apply", targetKey))
                {
                    RequestReplicationStoragePolicyRecovery(
                        detail + " target=" + targetKey);
                }
                return false;
            }
            finally
            {
                replicationStoragePolicyAuthoritativeApplyDepth--;
            }
        }

        private static void RegisterReplicationStoragePolicyCanonicalIdentity(
            object storage,
            ReplicationStoragePolicyTargetReference canonical)
        {
            if (!canonical.Canonical || canonical.HostUid <= 0L)
            {
                return;
            }

            object identityObject = storage;
            if (string.Equals(
                    canonical.Kind,
                    ReplicationStoragePolicyShelfKind,
                    StringComparison.Ordinal))
            {
                identityObject = AccessTools.Property(storage.GetType(), "OwnerBuilding")
                    ?.GetValue(storage, null) ?? storage;
            }
            RegisterReplicationHostIdentity(
                canonical.HostUid,
                identityObject,
                "storage-policy-state-proof");
        }

        private static void SetReplicationStoragePolicyStateCompletionContext(
            StoragePolicyState state,
            string targetKey)
        {
            replicationStoragePolicyStateCompletionContext =
                new ReplicationStoragePolicyStateCompletionContext
                {
                    TargetKey = targetKey,
                    Revision = state.Revision,
                    Exists = state.Exists
                };
        }

        private static bool CompleteReplicationStoragePolicyStateProof(
            StoragePolicyState state,
            out string detail)
        {
            var targetKey = FormatReplicationStoragePolicyTargetKey(
                FromReplicationStoragePolicyWireTarget(state.Target));
            var context = replicationStoragePolicyStateCompletionContext;
            replicationStoragePolicyStateCompletionContext = null;
            if (context == null
                || context.Revision != state.Revision
                || context.Exists != state.Exists
                || !string.Equals(
                    context.TargetKey, targetKey, StringComparison.Ordinal))
            {
                detail = "storage-policy-state-proof-context-mismatch target="
                    + targetKey;
                return false;
            }

            var proofThrough = state.ProofThroughClientSequence;
            var completedCommandKeys = new List<string>();
            if (proofThrough > 0L)
            {
                foreach (var pair in ReplicationPendingCommandIntents)
                {
                    if (pair.Value.Command.Sequence == proofThrough
                        && LockstepCommandPayloads.TryReadStoragePolicyUpdatePayload(
                            pair.Value.Command.PayloadJson, out var pendingUpdate)
                        && pendingUpdate.Epoch == state.Epoch)
                    {
                        // Client command sequence is unique within the negotiated
                        // epoch. Exact equality closes only the command whose result
                        // this state proves; a scalar high-water must never absorb a
                        // different target or an earlier lost sparse chunk.
                        completedCommandKeys.Add(pair.Key);
                    }
                }
            }
            for (var i = 0; i < completedCommandKeys.Count; i++)
            {
                ReplicationPendingCommandIntents.Remove(completedCommandKeys[i]);
            }

            object? resolvedStorage = null;
            TryResolveReplicationStoragePolicyTarget(
                FromReplicationStoragePolicyWireTarget(state.Target),
                out resolvedStorage,
                out _,
                out _);
            var removedCells = 0;
            var remainingCells = 0;
            var pendingKeys = new List<string>(ReplicationStoragePolicyPendingByTarget.Keys);
            for (var i = 0; i < pendingKeys.Count; i++)
            {
                if (!ReplicationStoragePolicyPendingByTarget.TryGetValue(
                        pendingKeys[i], out var pending))
                {
                    continue;
                }

                var ownsExactProof = proofThrough > 0L
                    && pending.InFlightSequence == proofThrough;
                var sameResolvedObject = resolvedStorage != null
                    && ReferenceEquals(resolvedStorage, pending.Storage);
                var exactCanonicalTarget = pending.Target.Canonical
                    && ReplicationStoragePolicyTargetsMatch(
                        state.Target,
                        ToReplicationStoragePolicyWireTarget(pending.Target));
                if (!ownsExactProof
                    && !sameResolvedObject
                    && !exactCanonicalTarget)
                {
                    continue;
                }

                if (ownsExactProof)
                {
                    pending.InFlightSequence = 0L;
                }

                var cellKeys = new List<string>(pending.Cells.Keys);
                for (var cell = 0; cell < cellKeys.Count; cell++)
                {
                    var cellKey = cellKeys[cell];
                    if (pending.SequenceByCell.TryGetValue(cellKey, out var sequence)
                        && sequence == proofThrough)
                    {
                        pending.Cells.Remove(cellKey);
                        pending.SequenceByCell.Remove(cellKey);
                        removedCells++;
                    }
                }

                if (pending.Cells.Count == 0)
                {
                    ReplicationStoragePolicyPendingByTarget.Remove(pendingKeys[i]);
                    ReplicationStoragePolicyClientQuarantinedTargets.Remove(pendingKeys[i]);
                }
                else if (state.Exists && (ownsExactProof || sameResolvedObject))
                {
                    remainingCells += pending.Cells.Count;
                    pending.Target = FromReplicationStoragePolicyWireTarget(state.Target);
                    if (resolvedStorage != null)
                    {
                        pending.Storage = resolvedStorage;
                    }
                    var canonicalKey = FormatReplicationStoragePolicyTargetKey(pending.Target);
                    if (!string.Equals(
                            canonicalKey, pendingKeys[i], StringComparison.Ordinal))
                    {
                        ReplicationStoragePolicyPendingByTarget.Remove(pendingKeys[i]);
                        if (ReplicationStoragePolicyPendingByTarget.TryGetValue(
                                canonicalKey, out var canonicalPending)
                            && !ReferenceEquals(canonicalPending, pending))
                        {
                            // Canonical identity can be registered by another lane
                            // while unproved UI cells are still stored under their
                            // provisional key. Merge by per-cell freshness; assigning
                            // the dictionary slot would silently discard one side.
                            MergeReplicationStoragePolicyPendingTarget(
                                canonicalPending, pending);
                            canonicalPending.Target = pending.Target.Clone();
                            if (resolvedStorage != null)
                            {
                                canonicalPending.Storage = resolvedStorage;
                            }
                        }
                        else
                        {
                            ReplicationStoragePolicyPendingByTarget[canonicalKey] = pending;
                        }
                    }
                }
                else
                {
                    // A tombstone only proves the exact in-flight cells above. Any
                    // newer/unsent gesture stays attached to its original object and
                    // will either resolve independently or trigger bounded recovery;
                    // it can never be erased because a replacement reused an anchor.
                    remainingCells += pending.Cells.Count;
                }
            }

            RefreshReplicationStoragePolicyUi(
                FormatReplicationStoragePolicyTargetKey(
                    FromReplicationStoragePolicyWireTarget(state.Target)));
            detail = "ok completedCommands="
                + completedCommandKeys.Count.ToString(CultureInfo.InvariantCulture)
                + " removedCells=" + removedCells.ToString(CultureInfo.InvariantCulture)
                + " newerCells=" + remainingCells.ToString(CultureInfo.InvariantCulture)
                + " proofThrough=" + proofThrough.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool replicationStoragePolicyRecoveryRequested;
        private static bool replicationStoragePolicyRecoveryAttempted;
        private static bool replicationStoragePolicyFailStopped;
        private static string replicationStoragePolicyRecoveryReason = string.Empty;

        private static void ProcessReplicationStoragePolicyRecoveryIfRequested()
        {
            if (!replicationStoragePolicyRecoveryRequested
                || replicationStoragePolicyRecoveryAttempted
                || instance == null)
            {
                return;
            }

            replicationStoragePolicyRecoveryAttempted = true;
            var reason = replicationStoragePolicyRecoveryReason;
            if (replicationConfigHostMode)
            {
                const string hostMessage =
                    "Going Cooperative: storage synchronization stopped safely. "
                    + "Have the client press FULL RESYNC before continuing.";
                MultiplayerMenu.StatusMessage = hostMessage;
                ShowReplicationBuildMessage(hostMessage);
                instance.LogReplicationWarning(
                    "Going Cooperative storage-policy host lane fail-stopped; "
                    + "client full resync required reason=" + reason);
                return;
            }

            const string clientMessage =
                "Going Cooperative: storage synchronization could not converge. "
                + "A Full Resync is starting.";
            MultiplayerMenu.StatusMessage = clientMessage;
            ShowReplicationBuildMessage(clientMessage);
            if (!instance.TryRequestFullMultiplayerResync(out var error))
            {
                var manualMessage =
                    "Going Cooperative: automatic storage recovery could not start. "
                    + "Press FULL RESYNC. " + error;
                MultiplayerMenu.StatusMessage = manualMessage;
                ShowReplicationBuildMessage(manualMessage);
                instance.LogReplicationWarning(
                    "Going Cooperative storage-policy client lane remains fail-stopped; "
                    + "manual full resync required reason="
                    + reason + " error=" + error);
                return;
            }
            instance.LogReplicationWarning(
                "Going Cooperative storage-policy recovery started full resync reason=" + reason);
        }

        private static void ResetReplicationStoragePolicyRuntimeState()
        {
            replicationStoragePolicyAuthoritativeApplyDepth = 0;
            replicationStoragePolicyUiRefreshDepth = 0;
            replicationStoragePolicyRegisterStorageDepth = 0;
            replicationStoragePolicyFilterNotificationSuppressionDepth = 0;
            replicationStoragePolicyNextFailClosedLogRealtime = 0f;
            replicationStoragePolicyLastFailClosedDetail = string.Empty;
            replicationStoragePolicyRecoveryRequested = false;
            replicationStoragePolicyRecoveryAttempted = false;
            replicationStoragePolicyFailStopped = false;
            replicationStoragePolicyRecoveryReason = string.Empty;
            replicationStoragePolicyStateCompletionContext = null;
            replicationStoragePolicyLastBaselineEpoch = -1L;
            replicationStoragePolicyRuntimeEpoch = 0L;
            ReplicationStoragePolicyDeferredFilterNotifications.Clear();
            ReplicationStoragePolicyPendingByTarget.Clear();
            ReplicationStoragePolicyHostDirtyByTarget.Clear();
            ReplicationStoragePolicyHostHighWaterByCell.Clear();
            ReplicationStoragePolicyHostRevisionByTarget.Clear();
            ReplicationStoragePolicyHostProofThroughByTarget.Clear();
            ReplicationStoragePolicyHostKnownTargets.Clear();
            ReplicationStoragePolicyClientRevisionByTarget.Clear();
            ReplicationStoragePolicyClientTombstones.Clear();
            ReplicationStoragePolicyClientQuarantinedTargets.Clear();
            ReplicationStoragePolicyHostQuarantinedTargets.Clear();
            ReplicationStoragePolicyRetainedLogAtByKey.Clear();
            ReplicationStoragePolicyRetryAtByKey.Clear();
            ReplicationStoragePolicyMissingSinceByKey.Clear();
            replicationStoragePolicyCaptureSnapshotFrame = -1;
            ReplicationStoragePolicyCaptureSnapshotsByStorage.Clear();
            replicationStoragePolicyCatalog = null;
        }

        private static void RequestReplicationStoragePolicyPanelCorrection()
        {
            var panelType = AccessTools.TypeByName("NSMedieval.UI.SelectionExtraStockpile");
            if (panelType == null)
            {
                return;
            }
            var panels = Resources.FindObjectsOfTypeAll(panelType);
            for (var i = 0; i < panels.Length; i++)
            {
                var panel = panels[i];
                if (panel == null)
                {
                    continue;
                }
                TrySetInstanceMemberValue(panel, "refreshSliders", true);
                TrySetInstanceMemberValue(panel, "refreshInput", true);
            }
        }

        private static string RefreshReplicationStoragePolicyUi(string targetKey)
        {
            var panelType = AccessTools.TypeByName("NSMedieval.UI.SelectionExtraStockpile");
            var refreshTree = panelType == null
                ? null
                : AccessTools.Method(panelType, "RefreshTree", Type.EmptyTypes);
            var refreshSliders = panelType == null
                ? null
                : AccessTools.Method(panelType, "RefreshSliders", Type.EmptyTypes);
            if (panelType == null || refreshTree == null || refreshSliders == null)
            {
                return "surface-missing";
            }

            var refreshed = 0;
            var failed = 0;
            var panels = Resources.FindObjectsOfTypeAll(panelType);
            for (var i = 0; i < panels.Length; i++)
            {
                var panel = panels[i];
                if (panel == null
                    || !TryGetListMember(panel, "storageObjects", out var storages)
                    || storages.Count == 0
                    || (!string.IsNullOrEmpty(targetKey)
                        && !ReplicationStoragePolicyPanelContainsTarget(
                            storages, targetKey)))
                {
                    continue;
                }

                replicationStoragePolicyUiRefreshDepth++;
                try
                {
                    TrySetInstanceMemberValue(panel, "refreshSliders", true);
                    TrySetInstanceMemberValue(panel, "refreshInput", true);
                    refreshSliders.Invoke(panel, null);
                    refreshTree.Invoke(panel, null);
                    refreshed++;
                }
                catch
                {
                    failed++;
                }
                finally
                {
                    replicationStoragePolicyUiRefreshDepth--;
                }
                RepaintReplicationStoragePolicyPendingOverlay(panel);
            }

            return "panels:" + refreshed.ToString(CultureInfo.InvariantCulture)
                + " failed:" + failed.ToString(CultureInfo.InvariantCulture);
        }

        private static bool ReplicationStoragePolicyPanelContainsTarget(
            IList storages,
            string targetKey)
        {
            for (var i = 0; i < storages.Count; i++)
            {
                if (storages[i] != null
                    && TryCreateReplicationStoragePolicyTargetReference(
                        storages[i]!, out var target, out _, out _)
                    && string.Equals(
                        FormatReplicationStoragePolicyTargetKey(target),
                        targetKey,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void RepaintReplicationStoragePolicyPendingOverlay(object panel)
        {
            if (replicationConfigHostMode
                || ReplicationStoragePolicyPendingByTarget.Count == 0
                || !TryGetListMember(panel, "storageObjects", out var storages)
                || storages.Count == 0)
            {
                return;
            }

            var snapshots = new List<ReplicationStoragePolicySnapshot>();
            var selectedHasPending = false;
            for (var i = 0; i < storages.Count; i++)
            {
                var storage = storages[i];
                if (storage == null
                    || !TryCreateReplicationStoragePolicyTargetReference(
                        storage, out var target, out var normalizedStorage, out _)
                    || !TryReadEffectiveReplicationStoragePolicySnapshot(
                        normalizedStorage, target, out var snapshot, out _))
                {
                    return;
                }

                var key = FormatReplicationStoragePolicyTargetKey(target);
                selectedHasPending |= TryGetReplicationStoragePolicyPendingTarget(
                    key, normalizedStorage, out _);
                snapshots.Add(snapshot);
            }
            if (!selectedHasPending || snapshots.Count == 0)
            {
                return;
            }

            replicationStoragePolicyUiRefreshDepth++;
            try
            {
                var last = snapshots[snapshots.Count - 1];
                SetReplicationStoragePolicyUiValueWithoutNotify(
                    panel, "priorityDropdown", "SetValueWithoutNotify",
                    last.Priority - 1);
                SetReplicationStoragePolicyUiValueWithoutNotify(
                    panel, "forbidUseInProductionToggle", "SetIsOnWithoutNotify",
                    last.UseInProduction);

                var commonName = snapshots[0].Name;
                for (var i = 1; i < snapshots.Count; i++)
                {
                    if (!string.Equals(
                            commonName, snapshots[i].Name, StringComparison.Ordinal))
                    {
                        commonName = "-";
                        break;
                    }
                }
                SetReplicationStoragePolicyUiValueWithoutNotify(
                    panel, "stockpileName", "SetTextWithoutNotify", commonName);

                if (last.Slots.Count > 0)
                {
                    SetReplicationStoragePolicyRangeSliderWithoutNotify(
                        panel,
                        "hitpointsSliderGroup",
                        last.Slots[0].HitPointsMinimum,
                        last.Slots[0].HitPointsMaximum);
                    SetReplicationStoragePolicyRangeSliderWithoutNotify(
                        panel,
                        "itemQualitySliderGroup",
                        last.Slots[0].QualityMinimum,
                        last.Slots[0].QualityMaximum);
                }

                RepaintReplicationStoragePolicyResourceTree(panel, snapshots);
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning(
                    "Going Cooperative storage-policy overlay repaint failed "
                    + FormatReflectionExceptionDetail(ex));
            }
            finally
            {
                replicationStoragePolicyUiRefreshDepth--;
            }
        }

        private static void SetReplicationStoragePolicyUiValueWithoutNotify(
            object panel,
            string memberName,
            string methodName,
            object value)
        {
            if (!TryReadInstanceMemberValue(panel, memberName, out var control)
                || control == null)
            {
                return;
            }
            var method = AccessTools.Method(control.GetType(), methodName);
            method?.Invoke(control, new[] { value });
        }

        private static void SetReplicationStoragePolicyRangeSliderWithoutNotify(
            object panel,
            string memberName,
            int minimum,
            int maximum)
        {
            if (!TryReadInstanceMemberValue(panel, memberName, out var sliderGroup)
                || sliderGroup == null)
            {
                return;
            }
            var slider = AccessTools.Property(sliderGroup.GetType(), "Slider")
                ?.GetValue(sliderGroup, null);
            AccessTools.Method(slider?.GetType(), "SetValueWithoutNotify")
                ?.Invoke(slider, new object[] { (float)minimum, (float)maximum });
        }

        private static void RepaintReplicationStoragePolicyResourceTree(
            object panel,
            List<ReplicationStoragePolicySnapshot> snapshots)
        {
            if (!TryReadInstanceMemberValue(panel, "Resources", out var resourcesValue)
                || !(resourcesValue is IDictionary resourceViews)
                || !TryGetReplicationStoragePolicyCatalog(out var catalog, out _))
            {
                return;
            }

            object? mutualAllowed = null;
            TryReadInstanceMemberValue(panel, "mutualAllowedResources", out mutualAllowed);
            AccessTools.Method(mutualAllowed?.GetType(), "Clear", Type.EmptyTypes)
                ?.Invoke(mutualAllowed, null);
            var updateParent = AccessTools.Method(
                panel.GetType().BaseType, "UpdateResourceParentSelection");
            foreach (DictionaryEntry pair in resourceViews)
            {
                if (pair.Key == null
                    || pair.Value == null
                    || !TryGetReplicationStoragePolicyCatalogIndex(
                        catalog, pair.Key, out var resourceIndex))
                {
                    continue;
                }

                var allowedByAll = true;
                for (var i = 0; i < snapshots.Count; i++)
                {
                    var allowedByAnySlot = false;
                    for (var slot = 0; slot < snapshots[i].Slots.Count; slot++)
                    {
                        allowedByAnySlot |= snapshots[i].Slots[slot].Allowed[resourceIndex];
                    }
                    allowedByAll &= allowedByAnySlot;
                }
                AccessTools.Method(pair.Value.GetType(), "SetSelected", new[] { typeof(bool) })
                    ?.Invoke(pair.Value, new object[] { allowedByAll });
                if (allowedByAll)
                {
                    AccessTools.Method(mutualAllowed?.GetType(), "Add")
                        ?.Invoke(mutualAllowed, new[] { pair.Key });
                }
                updateParent?.Invoke(panel, new[] { pair.Key });
            }
        }
    }
}
