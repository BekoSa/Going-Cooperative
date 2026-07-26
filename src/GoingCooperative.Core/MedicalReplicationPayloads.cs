using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core
{
    public sealed class MedicalWoundRecord
    {
        public MedicalWoundRecord(
            string name,
            long startTime,
            int stackCount,
            float duration,
            float durationModifier,
            float currentSeverity,
            float minimumSeverity,
            bool needsTending,
            bool needsRest,
            long lastTickTime,
            long lastTendTime,
            float lastTendQuality,
            string causeCreatureName,
            int causeBodyType,
            string causePerkId)
        {
            Name = name ?? string.Empty;
            StartTime = startTime;
            StackCount = stackCount;
            Duration = duration;
            DurationModifier = durationModifier;
            CurrentSeverity = currentSeverity;
            MinimumSeverity = minimumSeverity;
            NeedsTending = needsTending;
            NeedsRest = needsRest;
            LastTickTime = lastTickTime;
            LastTendTime = lastTendTime;
            LastTendQuality = lastTendQuality;
            CauseCreatureName = causeCreatureName ?? string.Empty;
            CauseBodyType = causeBodyType;
            CausePerkId = causePerkId ?? string.Empty;
        }

        public string Name { get; }
        public long StartTime { get; }
        public int StackCount { get; }
        public float Duration { get; }
        public float DurationModifier { get; }
        public float CurrentSeverity { get; }
        public float MinimumSeverity { get; }
        public bool NeedsTending { get; }
        public bool NeedsRest { get; }
        public long LastTickTime { get; }
        public long LastTendTime { get; }
        public float LastTendQuality { get; }
        public string CauseCreatureName { get; }
        public int CauseBodyType { get; }
        public string CausePerkId { get; }
    }

    public sealed class MedicalWoundState
    {
        public MedicalWoundState(
            string entityId,
            long revision,
            bool checkpoint,
            bool receivingTreatment,
            bool canReceiveTreatment,
            IReadOnlyList<MedicalWoundRecord> wounds)
        {
            EntityId = entityId ?? string.Empty;
            Revision = revision;
            Checkpoint = checkpoint;
            ReceivingTreatment = receivingTreatment;
            CanReceiveTreatment = canReceiveTreatment;
            Wounds = wounds ?? Array.Empty<MedicalWoundRecord>();
        }

        public string EntityId { get; }
        public long Revision { get; }
        public bool Checkpoint { get; }
        public bool ReceivingTreatment { get; }
        public bool CanReceiveTreatment { get; }
        public IReadOnlyList<MedicalWoundRecord> Wounds { get; }
    }

    public static class MedicalReplicationPayloads
    {
        public const string WoundStatePrefix = "medical-wounds-v1";
        public const string TreatmentOrderPrefix = "medical-order-v1";
        public const string StateRequestPrefix = "medical-request-v1";
        public const int MaxWoundsPerPawn = 64;
        public const int MaxPayloadChars = 65536;

        public static string CreateTreatmentOrder(
            string orderKind,
            string doctorEntityId,
            string patientEntityId,
            string requestId)
        {
            return TreatmentOrderPrefix
                + "|" + Encode(orderKind)
                + "|" + Encode(doctorEntityId)
                + "|" + Encode(patientEntityId)
                + "|" + Encode(requestId);
        }

        public static bool TryReadTreatmentOrder(
            string payload,
            out string orderKind,
            out string doctorEntityId,
            out string patientEntityId,
            out string requestId)
        {
            orderKind = string.Empty;
            doctorEntityId = string.Empty;
            patientEntityId = string.Empty;
            requestId = string.Empty;
            var parts = Split(payload, 5);
            return parts != null
                && string.Equals(parts[0], TreatmentOrderPrefix, StringComparison.Ordinal)
                && TryDecode(parts[1], 32, out orderKind)
                && TryDecode(parts[2], 256, out doctorEntityId)
                && TryDecode(parts[3], 256, out patientEntityId)
                && TryDecode(parts[4], 128, out requestId)
                && doctorEntityId.Length > 0
                && patientEntityId.Length > 0
                && requestId.Length > 0;
        }

        public static string CreateStateRequest(string entityId, string requestId)
        {
            return StateRequestPrefix + "|" + Encode(entityId) + "|" + Encode(requestId);
        }

        public static bool TryReadStateRequest(string payload, out string entityId, out string requestId)
        {
            entityId = string.Empty;
            requestId = string.Empty;
            var parts = Split(payload, 3);
            return parts != null
                && string.Equals(parts[0], StateRequestPrefix, StringComparison.Ordinal)
                && TryDecode(parts[1], 256, out entityId)
                && TryDecode(parts[2], 128, out requestId)
                && entityId.Length > 0
                && requestId.Length > 0;
        }

        public static string CreateWoundState(MedicalWoundState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Wounds.Count > MaxWoundsPerPawn) throw new ArgumentOutOfRangeException(nameof(state));

            var records = new StringBuilder();
            for (var i = 0; i < state.Wounds.Count; i++)
            {
                if (i > 0) records.Append('\n');
                var wound = state.Wounds[i];
                records.Append(Encode(wound.Name)).Append(',')
                    .Append(wound.StartTime.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.StackCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.Duration.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.DurationModifier.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.CurrentSeverity.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.MinimumSeverity.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.NeedsTending ? '1' : '0').Append(',')
                    .Append(wound.NeedsRest ? '1' : '0').Append(',')
                    .Append(wound.LastTickTime.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.LastTendTime.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(wound.LastTendQuality.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Encode(wound.CauseCreatureName)).Append(',')
                    .Append(wound.CauseBodyType.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Encode(wound.CausePerkId));
            }

            var payload = WoundStatePrefix
                + "|" + Encode(state.EntityId)
                + "|" + state.Revision.ToString(CultureInfo.InvariantCulture)
                + "|" + (state.Checkpoint ? "1" : "0")
                + "|" + (state.ReceivingTreatment ? "1" : "0")
                + "|" + (state.CanReceiveTreatment ? "1" : "0")
                + "|" + Encode(records.ToString());
            if (payload.Length > MaxPayloadChars) throw new InvalidOperationException("Medical wound payload exceeds limit.");
            return payload;
        }

        public static bool TryReadWoundState(string payload, out MedicalWoundState? state)
        {
            state = null;
            if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadChars) return false;
            var parts = Split(payload, 7);
            if (parts == null
                || !string.Equals(parts[0], WoundStatePrefix, StringComparison.Ordinal)
                || !TryDecode(parts[1], 256, out var entityId)
                || entityId.Length == 0
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                || revision <= 0
                || (parts[3] != "0" && parts[3] != "1")
                || (parts[4] != "0" && parts[4] != "1")
                || (parts[5] != "0" && parts[5] != "1")
                || !TryDecode(parts[6], MaxPayloadChars, out var encodedRecords)) return false;

            var wounds = new List<MedicalWoundRecord>();
            if (encodedRecords.Length > 0)
            {
                var lines = encodedRecords.Split(new[] { '\n' }, StringSplitOptions.None);
                if (lines.Length > MaxWoundsPerPawn) return false;
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < lines.Length; i++)
                {
                    var fields = lines[i].Split(new[] { ',' }, StringSplitOptions.None);
                    if (fields.Length != 15
                        || !TryDecode(fields[0], 256, out var name)
                        || name.Length == 0
                        || !names.Add(name)
                        || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTime)
                        || startTime < 0L
                        || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stackCount)
                        || stackCount < 0 || stackCount > 100000
                        || !TryFiniteFloat(fields[3], out var duration)
                        || duration < -1f || duration > 10000000f
                        || !TryFiniteFloat(fields[4], out var durationModifier)
                        || durationModifier < 0f || durationModifier > 1000f
                        || !TryFiniteFloat(fields[5], out var severity)
                        || severity < 0f || severity > 1000000f
                        || !TryFiniteFloat(fields[6], out var minimumSeverity)
                        || minimumSeverity < 0f || minimumSeverity > 1000000f
                        || (fields[7] != "0" && fields[7] != "1")
                        || (fields[8] != "0" && fields[8] != "1")
                        || !long.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastTickTime)
                        || lastTickTime < 0L
                        || !long.TryParse(fields[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastTendTime)
                        || lastTendTime < 0L
                        || !TryFiniteFloat(fields[11], out var lastTendQuality)
                        || lastTendQuality < 0f || lastTendQuality > 100000f
                        || !TryDecode(fields[12], 512, out var causeName)
                        || !int.TryParse(fields[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out var causeBodyType)
                        || causeBodyType < -1 || causeBodyType > 1024
                        || !TryDecode(fields[14], 256, out var causePerkId)) return false;

                    wounds.Add(new MedicalWoundRecord(
                        name, startTime, stackCount, duration, durationModifier,
                        severity, minimumSeverity, fields[7] == "1", fields[8] == "1",
                        lastTickTime, lastTendTime, lastTendQuality,
                        causeName, causeBodyType, causePerkId));
                }
            }

            state = new MedicalWoundState(entityId, revision, parts[3] == "1", parts[4] == "1", parts[5] == "1", wounds);
            return true;
        }

        private static string[]? Split(string value, int expectedParts)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxPayloadChars) return null;
            var parts = value.Split(new[] { '|' }, StringSplitOptions.None);
            return parts.Length == expectedParts ? parts : null;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static bool TryDecode(string value, int maxCharacters, out string decoded)
        {
            decoded = string.Empty;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
                return decoded.Length <= maxCharacters;
            }
            catch (FormatException)
            {
                decoded = string.Empty;
                return false;
            }
        }

        private static bool TryFiniteFloat(string value, out float parsed)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                && !float.IsNaN(parsed)
                && !float.IsInfinity(parsed);
        }
    }
}
