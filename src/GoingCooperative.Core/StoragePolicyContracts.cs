using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core
{
    public enum StoragePolicyTargetKind
    {
        GroundStockpile = 1,
        Shelf = 2
    }

    public readonly struct StoragePolicyAnchor
    {
        public StoragePolicyAnchor(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
    }

    public sealed class StoragePolicyTarget
    {
        public StoragePolicyTarget(
            StoragePolicyTargetKind kind,
            long hostUidCandidate,
            bool isCanonicalHostUid,
            string blueprintFingerprint,
            int componentOrdinal,
            StoragePolicyAnchor anchor)
        {
            Kind = kind;
            HostUidCandidate = hostUidCandidate;
            IsCanonicalHostUid = isCanonicalHostUid;
            BlueprintFingerprint = blueprintFingerprint ?? string.Empty;
            ComponentOrdinal = componentOrdinal;
            Anchor = anchor;
        }

        public StoragePolicyTargetKind Kind { get; }
        public long HostUidCandidate { get; }
        public bool IsCanonicalHostUid { get; }
        public string BlueprintFingerprint { get; }
        public int ComponentOrdinal { get; }
        public StoragePolicyAnchor Anchor { get; }
    }

    public enum StoragePolicyChangeKind
    {
        Priority = 1,
        ProductionUse = 2,
        Name = 3,
        HitPointsRange = 4,
        QualityRange = 5,
        ResourceAllowed = 6
    }

    public sealed class StoragePolicyChange
    {
        private StoragePolicyChange(
            StoragePolicyChangeKind kind,
            int slotIndex,
            int catalogIndex,
            int minimum,
            int maximum,
            int integerValue,
            bool booleanValue,
            string stringValue)
        {
            Kind = kind;
            SlotIndex = slotIndex;
            CatalogIndex = catalogIndex;
            Minimum = minimum;
            Maximum = maximum;
            IntegerValue = integerValue;
            BooleanValue = booleanValue;
            StringValue = stringValue ?? string.Empty;
        }

        public StoragePolicyChangeKind Kind { get; }
        public int SlotIndex { get; }
        public int CatalogIndex { get; }
        public int Minimum { get; }
        public int Maximum { get; }
        public int IntegerValue { get; }
        public bool BooleanValue { get; }
        public string StringValue { get; }

        public string CellKey
        {
            get
            {
                switch (Kind)
                {
                    case StoragePolicyChangeKind.Priority: return "common|priority";
                    case StoragePolicyChangeKind.ProductionUse: return "common|production";
                    case StoragePolicyChangeKind.Name: return "common|name";
                    case StoragePolicyChangeKind.HitPointsRange: return "slot|" + SlotIndex.ToString(CultureInfo.InvariantCulture) + "|hp";
                    case StoragePolicyChangeKind.QualityRange: return "slot|" + SlotIndex.ToString(CultureInfo.InvariantCulture) + "|quality";
                    case StoragePolicyChangeKind.ResourceAllowed: return "slot|" + SlotIndex.ToString(CultureInfo.InvariantCulture) + "|resource|" + CatalogIndex.ToString(CultureInfo.InvariantCulture);
                    default: return string.Empty;
                }
            }
        }

        public static StoragePolicyChange ForPriority(int priority)
        {
            return new StoragePolicyChange(StoragePolicyChangeKind.Priority, -1, -1, 0, 0, priority, false, string.Empty);
        }

        public static StoragePolicyChange ForProductionUse(bool canBeUsedInProduction)
        {
            return new StoragePolicyChange(StoragePolicyChangeKind.ProductionUse, -1, -1, 0, 0, 0, canBeUsedInProduction, string.Empty);
        }

        public static StoragePolicyChange ForName(string name)
        {
            return new StoragePolicyChange(StoragePolicyChangeKind.Name, -1, -1, 0, 0, 0, false, name);
        }

        public static StoragePolicyChange ForHitPointsRange(int slotIndex, int minimum, int maximum)
        {
            return new StoragePolicyChange(StoragePolicyChangeKind.HitPointsRange, slotIndex, -1, minimum, maximum, 0, false, string.Empty);
        }

        public static StoragePolicyChange ForQualityRange(int slotIndex, int minimum, int maximum)
        {
            return new StoragePolicyChange(StoragePolicyChangeKind.QualityRange, slotIndex, -1, minimum, maximum, 0, false, string.Empty);
        }

        public static StoragePolicyChange ForResourceAllowed(int slotIndex, int catalogIndex, bool allowed)
        {
            return new StoragePolicyChange(StoragePolicyChangeKind.ResourceAllowed, slotIndex, catalogIndex, 0, 0, 0, allowed, string.Empty);
        }
    }

    public sealed class StoragePolicyUpdate
    {
        public StoragePolicyUpdate(
            StoragePolicyTarget target,
            long epoch,
            string catalogSignature,
            int catalogCount,
            string topologySignature,
            StoragePolicyChange[] changes)
        {
            Target = target;
            Epoch = epoch;
            CatalogSignature = catalogSignature ?? string.Empty;
            CatalogCount = catalogCount;
            TopologySignature = topologySignature ?? string.Empty;
            Changes = changes ?? Array.Empty<StoragePolicyChange>();
        }

        public StoragePolicyTarget Target { get; }
        public long Epoch { get; }
        public string CatalogSignature { get; }
        public int CatalogCount { get; }
        public string TopologySignature { get; }
        public StoragePolicyChange[] Changes { get; }
    }

    public sealed class StoragePolicyFilterState
    {
        public StoragePolicyFilterState(
            int slotIndex,
            string slotId,
            string defaultAllowedFingerprint,
            int hitPointsMinimum,
            int hitPointsMaximum,
            int qualityMinimum,
            int qualityMaximum,
            byte[] allowedResourceMask)
        {
            SlotIndex = slotIndex;
            SlotId = slotId ?? string.Empty;
            DefaultAllowedFingerprint = defaultAllowedFingerprint ?? string.Empty;
            HitPointsMinimum = hitPointsMinimum;
            HitPointsMaximum = hitPointsMaximum;
            QualityMinimum = qualityMinimum;
            QualityMaximum = qualityMaximum;
            AllowedResourceMask = allowedResourceMask ?? Array.Empty<byte>();
        }

        public int SlotIndex { get; }
        public string SlotId { get; }
        public string DefaultAllowedFingerprint { get; }
        public int HitPointsMinimum { get; }
        public int HitPointsMaximum { get; }
        public int QualityMinimum { get; }
        public int QualityMaximum { get; }
        public byte[] AllowedResourceMask { get; }
    }

    public sealed class StoragePolicyState
    {
        public StoragePolicyState(
            StoragePolicyTarget target,
            bool exists,
            long epoch,
            long revision,
            long proofThroughClientSequence,
            string catalogSignature,
            int catalogCount,
            string topologySignature,
            int priority,
            bool canBeUsedInProduction,
            string name,
            StoragePolicyFilterState[] filters)
        {
            Target = target;
            Exists = exists;
            Epoch = epoch;
            Revision = revision;
            ProofThroughClientSequence = proofThroughClientSequence;
            CatalogSignature = catalogSignature ?? string.Empty;
            CatalogCount = catalogCount;
            TopologySignature = topologySignature ?? string.Empty;
            Priority = priority;
            CanBeUsedInProduction = canBeUsedInProduction;
            Name = name ?? string.Empty;
            Filters = filters ?? Array.Empty<StoragePolicyFilterState>();
        }

        public StoragePolicyTarget Target { get; }
        public bool Exists { get; }
        public long Epoch { get; }
        public long Revision { get; }
        public long ProofThroughClientSequence { get; }
        public string CatalogSignature { get; }
        public int CatalogCount { get; }
        public string TopologySignature { get; }
        public int Priority { get; }
        public bool CanBeUsedInProduction { get; }
        public string Name { get; }
        public StoragePolicyFilterState[] Filters { get; }
    }

    public static class StoragePolicyPayloadCodec
    {
        public const int SchemaVersion = 1;
        public const int MaximumPayloadUtf8Bytes = 128 * 1024;
        public const int MaximumSlots = 64;
        public const int MaximumCells = 4096;
        public const int MaximumCatalogEntries = 4096;
        public const int MaximumIdentityCharacters = 256;
        public const int MaximumNameCharacters = 128;
        public const int MaximumNameUtf8Bytes = 512;
        public const int MinimumPriority = 1;
        public const int MaximumPriority = 4;
        public const int MinimumHitPoints = 0;
        public const int MaximumHitPoints = 100;
        public const int MinimumQuality = 1;
        public const int MaximumQuality = 6;

        public static bool TryCreateUpdatePayload(StoragePolicyUpdate update, out string payloadJson)
        {
            payloadJson = string.Empty;
            if (!TryValidateUpdate(update, out _))
            {
                return false;
            }

            var changes = CopyAndSortChanges(update.Changes);
            var builder = new StringBuilder(512 + changes.Length * 20);
            AppendHeader(builder, LockstepCommandPayloads.StoragePolicyUpdateAction, update.Epoch, update.Target, update.CatalogSignature, update.CatalogCount, update.TopologySignature);
            builder.Append(",\"changes\":[");
            for (var i = 0; i < changes.Length; i++)
            {
                if (i > 0) builder.Append(',');
                AppendJsonString(builder, FormatChange(changes[i]));
            }

            builder.Append("]}");
            var candidate = builder.ToString();
            if (!FitsPayloadBound(candidate))
            {
                return false;
            }

            payloadJson = candidate;
            return true;
        }

        public static bool TryReadUpdatePayload(string payloadJson, out StoragePolicyUpdate update)
        {
            update = null!;
            if (!FitsPayloadBound(payloadJson)
                || !FlatJsonObject.TryParse(payloadJson, out var json)
                || !TryReadHeader(json, LockstepCommandPayloads.StoragePolicyUpdateAction, out var epoch, out var target, out var catalogSignature, out var catalogCount, out var topologySignature)
                || !json.TryGetStringArray("changes", out var encodedChanges)
                || encodedChanges.Length == 0
                || encodedChanges.Length > MaximumCells)
            {
                return false;
            }

            var changes = new StoragePolicyChange[encodedChanges.Length];
            for (var i = 0; i < encodedChanges.Length; i++)
            {
                if (!TryParseChange(encodedChanges[i], out changes[i]))
                {
                    return false;
                }
            }

            var candidate = new StoragePolicyUpdate(target, epoch, catalogSignature, catalogCount, topologySignature, changes);
            if (!TryValidateUpdate(candidate, out _))
            {
                return false;
            }

            Array.Sort(changes, CompareChanges);
            update = candidate;
            return true;
        }

        public static bool TryCreateStatePayload(StoragePolicyState state, out string payloadJson)
        {
            payloadJson = string.Empty;
            if (!TryValidateState(state, out _))
            {
                return false;
            }

            var filters = CopyAndSortFilters(state.Filters);
            var builder = new StringBuilder(512 + filters.Length * 128);
            AppendHeader(builder, LockstepCommandPayloads.StoragePolicyStateAction, state.Epoch, state.Target, state.CatalogSignature, state.CatalogCount, state.TopologySignature);
            builder.Append(",\"exists\":").Append(state.Exists ? "true" : "false");
            builder.Append(",\"revision\":").Append(state.Revision.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"proofThrough\":").Append(state.ProofThroughClientSequence.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"priority\":").Append(state.Priority.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"productionUse\":").Append(state.CanBeUsedInProduction ? "true" : "false");
            builder.Append(",\"name\":");
            AppendJsonString(builder, state.Name);
            builder.Append(",\"slots\":[");
            for (var i = 0; i < filters.Length; i++)
            {
                if (i > 0) builder.Append(',');
                AppendJsonString(builder, FormatFilter(filters[i]));
            }

            builder.Append("]}");
            var candidate = builder.ToString();
            if (!FitsPayloadBound(candidate))
            {
                return false;
            }

            payloadJson = candidate;
            return true;
        }

        public static bool TryReadStatePayload(string payloadJson, out StoragePolicyState state)
        {
            state = null!;
            if (!FitsPayloadBound(payloadJson)
                || !FlatJsonObject.TryParse(payloadJson, out var json)
                || !TryReadHeader(json, LockstepCommandPayloads.StoragePolicyStateAction, out var epoch, out var target, out var catalogSignature, out var catalogCount, out var topologySignature)
                || !json.TryGetBool("exists", out var exists)
                || !json.TryGetLong("revision", out var revision)
                || !json.TryGetLong("proofThrough", out var proofThrough)
                || !json.TryGetInt("priority", out var priority)
                || !json.TryGetBool("productionUse", out var productionUse)
                || !json.TryGetString("name", out var name)
                || !json.TryGetStringArray("slots", out var encodedFilters)
                || encodedFilters.Length > MaximumSlots)
            {
                return false;
            }

            var filters = new StoragePolicyFilterState[encodedFilters.Length];
            for (var i = 0; i < encodedFilters.Length; i++)
            {
                if (!TryParseFilter(encodedFilters[i], out filters[i]))
                {
                    return false;
                }
            }

            var candidate = new StoragePolicyState(target, exists, epoch, revision, proofThrough, catalogSignature, catalogCount, topologySignature, priority, productionUse, name, filters);
            if (!TryValidateState(candidate, out _))
            {
                return false;
            }

            Array.Sort(filters, CompareFilters);
            state = candidate;
            return true;
        }

        public static bool TryValidateUpdate(StoragePolicyUpdate update, out string error)
        {
            if (update == null)
            {
                error = "update-null";
                return false;
            }

            if (update.Epoch < 0)
            {
                error = "epoch-invalid";
                return false;
            }

            if (!TryValidateTarget(update.Target, false, out error)
                || !TryValidateCatalog(update.CatalogSignature, update.CatalogCount, out error)
                || !IsBoundedIdentity(update.TopologySignature, false))
            {
                if (string.IsNullOrEmpty(error)) error = "topology-signature-invalid";
                return false;
            }

            if (update.Changes == null || update.Changes.Length == 0 || update.Changes.Length > MaximumCells)
            {
                error = "change-count-invalid";
                return false;
            }

            var cells = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < update.Changes.Length; i++)
            {
                var change = update.Changes[i];
                if (!TryValidateChange(change, update.CatalogCount, out error))
                {
                    return false;
                }

                if (!cells.Add(change.CellKey))
                {
                    error = "change-cell-duplicate";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static bool TryValidateState(StoragePolicyState state, out string error)
        {
            if (state == null)
            {
                error = "state-null";
                return false;
            }

            if (state.Epoch < 0)
            {
                error = "epoch-invalid";
                return false;
            }

            if (!TryValidateTarget(state.Target, true, out error)
                || !TryValidateCatalog(state.CatalogSignature, state.CatalogCount, out error)
                || !IsBoundedIdentity(state.TopologySignature, false))
            {
                if (string.IsNullOrEmpty(error)) error = "topology-signature-invalid";
                return false;
            }

            if (state.Revision < 0 || state.ProofThroughClientSequence < 0)
            {
                error = "state-ordering-invalid";
                return false;
            }

            if (!state.Exists)
            {
                if (state.Priority != 0 || state.CanBeUsedInProduction || state.Name.Length != 0 || state.Filters.Length != 0)
                {
                    error = "tombstone-state-not-empty";
                    return false;
                }

                error = string.Empty;
                return true;
            }

            if (state.Priority < MinimumPriority || state.Priority > MaximumPriority || !IsValidName(state.Name))
            {
                error = "common-state-invalid";
                return false;
            }

            if (state.Filters == null || state.Filters.Length == 0 || state.Filters.Length > MaximumSlots)
            {
                error = "slot-count-invalid";
                return false;
            }

            if (state.Target.Kind == StoragePolicyTargetKind.GroundStockpile && state.Filters.Length != 1)
            {
                error = "ground-slot-count-invalid";
                return false;
            }

            var slotIndices = new HashSet<int>();
            for (var i = 0; i < state.Filters.Length; i++)
            {
                var filter = state.Filters[i];
                if (!TryValidateFilter(filter, state.CatalogCount, out error))
                {
                    return false;
                }

                // Slot identity is the ordered tuple (ordinal, UniversalStorageID,
                // default-allowed fingerprint). A shelf blueprint may legitimately
                // repeat the same UniversalStorageID in two distinct ordered slots.
                if (!slotIndices.Add(filter.SlotIndex))
                {
                    error = "slot-duplicate";
                    return false;
                }
            }

            for (var i = 0; i < state.Filters.Length; i++)
            {
                if (!slotIndices.Contains(i))
                {
                    error = "slot-topology-not-contiguous";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static int GetAllowedMaskByteLength(int catalogCount)
        {
            return catalogCount <= 0 || catalogCount > MaximumCatalogEntries ? -1 : (catalogCount + 7) / 8;
        }

        public static bool TryValidateAllowedMask(byte[] mask, int catalogCount, out string error)
        {
            var expectedLength = GetAllowedMaskByteLength(catalogCount);
            if (mask == null || expectedLength < 0 || mask.Length != expectedLength)
            {
                error = "allowed-mask-length-invalid";
                return false;
            }

            var usedBits = catalogCount & 7;
            if (usedBits != 0)
            {
                var unusedMask = (byte)~((1 << usedBits) - 1);
                if ((mask[mask.Length - 1] & unusedMask) != 0)
                {
                    error = "allowed-mask-unused-bits-set";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static bool IsResourceAllowed(byte[] mask, int catalogCount, int catalogIndex)
        {
            return TryValidateAllowedMask(mask, catalogCount, out _)
                && catalogIndex >= 0
                && catalogIndex < catalogCount
                && (mask[catalogIndex >> 3] & (1 << (catalogIndex & 7))) != 0;
        }

        private static void AppendHeader(StringBuilder builder, string action, long epoch, StoragePolicyTarget target, string catalogSignature, int catalogCount, string topologySignature)
        {
            builder.Append("{\"action\":");
            AppendJsonString(builder, action);
            builder.Append(",\"schema\":").Append(SchemaVersion.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"epoch\":").Append(epoch.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"targetKind\":");
            AppendJsonString(builder, FormatTargetKind(target.Kind));
            builder.Append(",\"hostUid\":").Append(target.HostUidCandidate.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"canonicalHostUid\":").Append(target.IsCanonicalHostUid ? "true" : "false");
            builder.Append(",\"blueprintFingerprint\":");
            AppendJsonString(builder, target.BlueprintFingerprint);
            builder.Append(",\"componentOrdinal\":").Append(target.ComponentOrdinal.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"anchorX\":").Append(target.Anchor.X.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"anchorY\":").Append(target.Anchor.Y.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"anchorZ\":").Append(target.Anchor.Z.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"catalogSignature\":");
            AppendJsonString(builder, catalogSignature);
            builder.Append(",\"catalogCount\":").Append(catalogCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"topologySignature\":");
            AppendJsonString(builder, topologySignature);
        }

        private static bool TryReadHeader(FlatJsonObject json, string expectedAction, out long epoch, out StoragePolicyTarget target, out string catalogSignature, out int catalogCount, out string topologySignature)
        {
            epoch = -1L;
            target = null!;
            catalogSignature = string.Empty;
            catalogCount = 0;
            topologySignature = string.Empty;
            if (!json.TryGetString("action", out var action)
                || !string.Equals(action, expectedAction, StringComparison.Ordinal)
                || !json.TryGetInt("schema", out var schema)
                || schema != SchemaVersion
                || !json.TryGetLong("epoch", out epoch)
                || epoch < 0
                || !json.TryGetString("targetKind", out var targetKindText)
                || !TryParseTargetKind(targetKindText, out var targetKind)
                || !json.TryGetLong("hostUid", out var hostUid)
                || !json.TryGetBool("canonicalHostUid", out var canonical)
                || !json.TryGetString("blueprintFingerprint", out var blueprint)
                || !json.TryGetInt("componentOrdinal", out var componentOrdinal)
                || !json.TryGetInt("anchorX", out var anchorX)
                || !json.TryGetInt("anchorY", out var anchorY)
                || !json.TryGetInt("anchorZ", out var anchorZ)
                || !json.TryGetString("catalogSignature", out catalogSignature)
                || !json.TryGetInt("catalogCount", out catalogCount)
                || !json.TryGetString("topologySignature", out topologySignature))
            {
                return false;
            }

            target = new StoragePolicyTarget(targetKind, hostUid, canonical, blueprint, componentOrdinal, new StoragePolicyAnchor(anchorX, anchorY, anchorZ));
            return true;
        }

        private static bool TryValidateTarget(StoragePolicyTarget target, bool requireCanonical, out string error)
        {
            if (target == null
                || (target.Kind != StoragePolicyTargetKind.GroundStockpile && target.Kind != StoragePolicyTargetKind.Shelf)
                || target.HostUidCandidate <= 0
                || (requireCanonical && !target.IsCanonicalHostUid)
                || !IsBoundedIdentity(target.BlueprintFingerprint, false)
                || target.ComponentOrdinal < 0
                || target.ComponentOrdinal >= MaximumSlots
                || (target.Kind == StoragePolicyTargetKind.GroundStockpile && target.ComponentOrdinal != 0))
            {
                error = "target-invalid";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryValidateCatalog(string signature, int count, out string error)
        {
            if (!IsBoundedIdentity(signature, false) || count <= 0 || count > MaximumCatalogEntries)
            {
                error = "catalog-invalid";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryValidateChange(StoragePolicyChange change, int catalogCount, out string error)
        {
            if (change == null)
            {
                error = "change-null";
                return false;
            }

            switch (change.Kind)
            {
                case StoragePolicyChangeKind.Priority:
                    if (change.IntegerValue < MinimumPriority || change.IntegerValue > MaximumPriority) break;
                    error = string.Empty;
                    return true;
                case StoragePolicyChangeKind.ProductionUse:
                    error = string.Empty;
                    return true;
                case StoragePolicyChangeKind.Name:
                    if (!IsValidName(change.StringValue)) break;
                    error = string.Empty;
                    return true;
                case StoragePolicyChangeKind.HitPointsRange:
                    if (IsValidSlot(change.SlotIndex) && IsOrderedRange(change.Minimum, change.Maximum, MinimumHitPoints, MaximumHitPoints))
                    {
                        error = string.Empty;
                        return true;
                    }

                    break;
                case StoragePolicyChangeKind.QualityRange:
                    if (IsValidSlot(change.SlotIndex) && IsOrderedRange(change.Minimum, change.Maximum, MinimumQuality, MaximumQuality))
                    {
                        error = string.Empty;
                        return true;
                    }

                    break;
                case StoragePolicyChangeKind.ResourceAllowed:
                    if (IsValidSlot(change.SlotIndex) && change.CatalogIndex >= 0 && change.CatalogIndex < catalogCount)
                    {
                        error = string.Empty;
                        return true;
                    }

                    break;
            }

            error = "change-invalid";
            return false;
        }

        private static bool TryValidateFilter(StoragePolicyFilterState filter, int catalogCount, out string error)
        {
            error = string.Empty;
            if (filter == null
                || !IsValidSlot(filter.SlotIndex)
                || !IsBoundedIdentity(filter.SlotId, false)
                || !IsBoundedIdentity(filter.DefaultAllowedFingerprint, false)
                || !IsOrderedRange(filter.HitPointsMinimum, filter.HitPointsMaximum, MinimumHitPoints, MaximumHitPoints)
                || !IsOrderedRange(filter.QualityMinimum, filter.QualityMaximum, MinimumQuality, MaximumQuality)
                || !TryValidateAllowedMask(filter.AllowedResourceMask, catalogCount, out error))
            {
                if (string.IsNullOrEmpty(error)) error = "filter-invalid";
                return false;
            }

            return true;
        }

        private static bool IsValidSlot(int slotIndex) => slotIndex >= 0 && slotIndex < MaximumSlots;

        private static bool IsOrderedRange(int minimum, int maximum, int lowerBound, int upperBound)
        {
            return minimum >= lowerBound && maximum <= upperBound && minimum <= maximum;
        }

        private static bool IsValidName(string value)
        {
            return value != null
                && TryGetUnicodeScalarCount(value, out var characterCount)
                && characterCount <= MaximumNameCharacters
                && value.IndexOf('\0') < 0
                && TryGetUtf8ByteCount(value, out var byteCount)
                && byteCount <= MaximumNameUtf8Bytes;
        }

        private static bool IsBoundedIdentity(string value, bool allowEmpty)
        {
            return value != null
                && (allowEmpty || value.Length > 0)
                && TryGetUnicodeScalarCount(value, out var characterCount)
                && characterCount <= MaximumIdentityCharacters
                && value.IndexOf('\0') < 0
                && HasValidUtf16(value);
        }

        private static bool TryGetUnicodeScalarCount(string value, out int count)
        {
            count = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsLowSurrogate(value[i])) return false;
                if (char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                    i++;
                }

                count++;
            }

            return true;
        }

        private static bool HasValidUtf16(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsSurrogate(value[i])) continue;
                if (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                i++;
            }

            return true;
        }

        private static bool TryGetUtf8ByteCount(string value, out int byteCount)
        {
            byteCount = 0;
            if (!HasValidUtf16(value)) return false;
            byteCount = Encoding.UTF8.GetByteCount(value);
            return true;
        }

        private static bool FitsPayloadBound(string payloadJson)
        {
            return payloadJson != null
                && TryGetUtf8ByteCount(payloadJson, out var byteCount)
                && byteCount <= MaximumPayloadUtf8Bytes;
        }

        private static string FormatTargetKind(StoragePolicyTargetKind kind)
        {
            return kind == StoragePolicyTargetKind.GroundStockpile ? "ground-stockpile" : "shelf";
        }

        private static bool TryParseTargetKind(string text, out StoragePolicyTargetKind kind)
        {
            if (string.Equals(text, "ground-stockpile", StringComparison.Ordinal))
            {
                kind = StoragePolicyTargetKind.GroundStockpile;
                return true;
            }

            if (string.Equals(text, "shelf", StringComparison.Ordinal))
            {
                kind = StoragePolicyTargetKind.Shelf;
                return true;
            }

            kind = default;
            return false;
        }

        private static string FormatChange(StoragePolicyChange change)
        {
            switch (change.Kind)
            {
                case StoragePolicyChangeKind.Priority: return "P:" + change.IntegerValue.ToString(CultureInfo.InvariantCulture);
                case StoragePolicyChangeKind.ProductionUse: return "U:" + (change.BooleanValue ? "1" : "0");
                case StoragePolicyChangeKind.Name: return "N:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(change.StringValue));
                case StoragePolicyChangeKind.HitPointsRange: return "H:" + change.SlotIndex.ToString(CultureInfo.InvariantCulture) + ":" + change.Minimum.ToString(CultureInfo.InvariantCulture) + ":" + change.Maximum.ToString(CultureInfo.InvariantCulture);
                case StoragePolicyChangeKind.QualityRange: return "Q:" + change.SlotIndex.ToString(CultureInfo.InvariantCulture) + ":" + change.Minimum.ToString(CultureInfo.InvariantCulture) + ":" + change.Maximum.ToString(CultureInfo.InvariantCulture);
                case StoragePolicyChangeKind.ResourceAllowed: return "R:" + change.SlotIndex.ToString(CultureInfo.InvariantCulture) + ":" + change.CatalogIndex.ToString(CultureInfo.InvariantCulture) + ":" + (change.BooleanValue ? "1" : "0");
                default: return string.Empty;
            }
        }

        private static bool TryParseChange(string text, out StoragePolicyChange change)
        {
            change = null!;
            var parts = (text ?? string.Empty).Split(new[] { ':' }, StringSplitOptions.None);
            if (parts.Length == 2 && parts[0] == "P" && TryParseCanonicalInt(parts[1], out var priority))
            {
                change = StoragePolicyChange.ForPriority(priority);
                return true;
            }

            if (parts.Length == 2 && parts[0] == "U" && TryParseBit(parts[1], out var production))
            {
                change = StoragePolicyChange.ForProductionUse(production);
                return true;
            }

            if (parts.Length == 2 && parts[0] == "N" && TryDecodeBase64String(parts[1], out var name))
            {
                change = StoragePolicyChange.ForName(name);
                return true;
            }

            if (parts.Length == 4 && TryParseCanonicalInt(parts[1], out var slot) && TryParseCanonicalInt(parts[2], out var first))
            {
                if (parts[0] == "H" && TryParseCanonicalInt(parts[3], out var hpMaximum))
                {
                    change = StoragePolicyChange.ForHitPointsRange(slot, first, hpMaximum);
                    return true;
                }

                if (parts[0] == "Q" && TryParseCanonicalInt(parts[3], out var qualityMaximum))
                {
                    change = StoragePolicyChange.ForQualityRange(slot, first, qualityMaximum);
                    return true;
                }

                if (parts[0] == "R" && TryParseBit(parts[3], out var allowed))
                {
                    change = StoragePolicyChange.ForResourceAllowed(slot, first, allowed);
                    return true;
                }
            }

            return false;
        }

        private static string FormatFilter(StoragePolicyFilterState filter)
        {
            return filter.SlotIndex.ToString(CultureInfo.InvariantCulture)
                + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(filter.SlotId))
                + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(filter.DefaultAllowedFingerprint))
                + ":" + filter.HitPointsMinimum.ToString(CultureInfo.InvariantCulture)
                + ":" + filter.HitPointsMaximum.ToString(CultureInfo.InvariantCulture)
                + ":" + filter.QualityMinimum.ToString(CultureInfo.InvariantCulture)
                + ":" + filter.QualityMaximum.ToString(CultureInfo.InvariantCulture)
                + ":" + Convert.ToBase64String(filter.AllowedResourceMask);
        }

        private static bool TryParseFilter(string text, out StoragePolicyFilterState filter)
        {
            filter = null!;
            var parts = (text ?? string.Empty).Split(new[] { ':' }, StringSplitOptions.None);
            if (parts.Length != 8
                || !TryParseCanonicalInt(parts[0], out var slotIndex)
                || !TryDecodeBase64String(parts[1], out var slotId)
                || !TryDecodeBase64String(parts[2], out var defaultFingerprint)
                || !TryParseCanonicalInt(parts[3], out var hpMinimum)
                || !TryParseCanonicalInt(parts[4], out var hpMaximum)
                || !TryParseCanonicalInt(parts[5], out var qualityMinimum)
                || !TryParseCanonicalInt(parts[6], out var qualityMaximum)
                || !TryDecodeBase64Bytes(parts[7], out var mask))
            {
                return false;
            }

            filter = new StoragePolicyFilterState(slotIndex, slotId, defaultFingerprint, hpMinimum, hpMaximum, qualityMinimum, qualityMaximum, mask);
            return true;
        }

        private static StoragePolicyChange[] CopyAndSortChanges(StoragePolicyChange[] changes)
        {
            var copy = (StoragePolicyChange[])changes.Clone();
            Array.Sort(copy, CompareChanges);
            return copy;
        }

        private static int CompareChanges(StoragePolicyChange left, StoragePolicyChange right)
        {
            var kind = ((int)left.Kind).CompareTo((int)right.Kind);
            if (kind != 0) return kind;
            var slot = left.SlotIndex.CompareTo(right.SlotIndex);
            return slot != 0 ? slot : left.CatalogIndex.CompareTo(right.CatalogIndex);
        }

        private static StoragePolicyFilterState[] CopyAndSortFilters(StoragePolicyFilterState[] filters)
        {
            var copy = (StoragePolicyFilterState[])filters.Clone();
            Array.Sort(copy, CompareFilters);
            return copy;
        }

        private static int CompareFilters(StoragePolicyFilterState left, StoragePolicyFilterState right) => left.SlotIndex.CompareTo(right.SlotIndex);

        private static bool TryParseCanonicalInt(string text, out int value)
        {
            value = 0;
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
                && string.Equals(value.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        }

        private static bool TryParseBit(string text, out bool value)
        {
            value = text == "1";
            return value || text == "0";
        }

        private static bool TryDecodeBase64String(string text, out string value)
        {
            value = string.Empty;
            if (!TryDecodeBase64Bytes(text, out var bytes)) return false;
            try
            {
                var strictUtf8 = new UTF8Encoding(false, true);
                value = strictUtf8.GetString(bytes);
                return HasValidUtf16(value) && string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool TryDecodeBase64Bytes(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            try
            {
                bytes = Convert.FromBase64String(text ?? string.Empty);
                return string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void AppendJsonString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var ch in value ?? string.Empty)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (ch < ' ')
                        {
                            builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private sealed class FlatJsonValue
        {
            public FlatJsonValue(char kind, string text, string[]? items = null)
            {
                Kind = kind;
                Text = text;
                Items = items;
            }

            public char Kind { get; }
            public string Text { get; }
            public string[]? Items { get; }
        }

        private sealed class FlatJsonObject
        {
            private readonly Dictionary<string, FlatJsonValue> values;

            private FlatJsonObject(Dictionary<string, FlatJsonValue> values)
            {
                this.values = values;
            }

            public bool TryGetString(string name, out string value)
            {
                value = string.Empty;
                if (!values.TryGetValue(name, out var item) || item.Kind != 's') return false;
                value = item.Text;
                return true;
            }

            public bool TryGetBool(string name, out bool value)
            {
                value = false;
                if (!values.TryGetValue(name, out var item) || item.Kind != 'b') return false;
                value = item.Text == "true";
                return true;
            }

            public bool TryGetLong(string name, out long value)
            {
                value = 0;
                return values.TryGetValue(name, out var item)
                    && item.Kind == 'n'
                    && long.TryParse(item.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
                    && string.Equals(value.ToString(CultureInfo.InvariantCulture), item.Text, StringComparison.Ordinal);
            }

            public bool TryGetInt(string name, out int value)
            {
                value = 0;
                return values.TryGetValue(name, out var item)
                    && item.Kind == 'n'
                    && TryParseCanonicalInt(item.Text, out value);
            }

            public bool TryGetStringArray(string name, out string[] value)
            {
                value = Array.Empty<string>();
                if (!values.TryGetValue(name, out var item) || item.Kind != 'a' || item.Items == null) return false;
                value = item.Items;
                return true;
            }

            public static bool TryParse(string json, out FlatJsonObject result)
            {
                result = null!;
                var reader = new FlatJsonReader(json);
                if (!reader.TryReadObject(out var values)) return false;
                result = new FlatJsonObject(values);
                return true;
            }
        }

        private sealed class FlatJsonReader
        {
            private readonly string source;
            private int position;

            public FlatJsonReader(string source)
            {
                this.source = source ?? string.Empty;
            }

            public bool TryReadObject(out Dictionary<string, FlatJsonValue> values)
            {
                values = new Dictionary<string, FlatJsonValue>(StringComparer.Ordinal);
                SkipWhitespace();
                if (!Take('{')) return false;
                SkipWhitespace();
                if (Take('}')) return AtEnd();
                while (true)
                {
                    if (!TryReadString(out var name) || values.ContainsKey(name)) return false;
                    SkipWhitespace();
                    if (!Take(':')) return false;
                    SkipWhitespace();
                    if (!TryReadValue(out var value)) return false;
                    values.Add(name, value);
                    SkipWhitespace();
                    if (Take('}')) return AtEnd();
                    if (!Take(',')) return false;
                    SkipWhitespace();
                }
            }

            private bool TryReadValue(out FlatJsonValue value)
            {
                value = null!;
                if (position >= source.Length) return false;
                if (source[position] == '"')
                {
                    if (!TryReadString(out var text)) return false;
                    value = new FlatJsonValue('s', text);
                    return true;
                }

                if (source[position] == '[')
                {
                    if (!TryReadStringArray(out var items)) return false;
                    value = new FlatJsonValue('a', string.Empty, items);
                    return true;
                }

                if (StartsWith("true"))
                {
                    position += 4;
                    value = new FlatJsonValue('b', "true");
                    return true;
                }

                if (StartsWith("false"))
                {
                    position += 5;
                    value = new FlatJsonValue('b', "false");
                    return true;
                }

                var start = position;
                if (source[position] == '-') position++;
                if (position >= source.Length || !char.IsDigit(source[position])) return false;
                if (source[position] == '0')
                {
                    position++;
                    if (position < source.Length && char.IsDigit(source[position])) return false;
                }
                else
                {
                    while (position < source.Length && char.IsDigit(source[position])) position++;
                }

                value = new FlatJsonValue('n', source.Substring(start, position - start));
                return true;
            }

            private bool TryReadStringArray(out string[] items)
            {
                items = Array.Empty<string>();
                if (!Take('[')) return false;
                SkipWhitespace();
                var list = new List<string>();
                if (Take(']'))
                {
                    items = list.ToArray();
                    return true;
                }

                while (true)
                {
                    if (!TryReadString(out var item)) return false;
                    list.Add(item);
                    if (list.Count > MaximumCells) return false;
                    SkipWhitespace();
                    if (Take(']'))
                    {
                        items = list.ToArray();
                        return true;
                    }

                    if (!Take(',')) return false;
                    SkipWhitespace();
                }
            }

            private bool TryReadString(out string value)
            {
                value = string.Empty;
                if (!Take('"')) return false;
                var builder = new StringBuilder();
                while (position < source.Length)
                {
                    var ch = source[position++];
                    if (ch == '"')
                    {
                        value = builder.ToString();
                        return HasValidUtf16(value);
                    }

                    if (ch < ' ') return false;
                    if (ch != '\\')
                    {
                        builder.Append(ch);
                        continue;
                    }

                    if (position >= source.Length) return false;
                    ch = source[position++];
                    switch (ch)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (position + 4 > source.Length
                                || !int.TryParse(source.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint)) return false;
                            builder.Append((char)codePoint);
                            position += 4;
                            break;
                        default: return false;
                    }
                }

                return false;
            }

            private bool StartsWith(string value)
            {
                return position + value.Length <= source.Length
                    && string.CompareOrdinal(source, position, value, 0, value.Length) == 0;
            }

            private void SkipWhitespace()
            {
                while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
            }

            private bool Take(char expected)
            {
                if (position >= source.Length || source[position] != expected) return false;
                position++;
                return true;
            }

            private bool AtEnd()
            {
                SkipWhitespace();
                return position == source.Length;
            }
        }
    }
}
