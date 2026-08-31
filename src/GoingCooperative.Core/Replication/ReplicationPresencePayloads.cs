using System;
using System.Globalization;

namespace GoingCooperative.Core.Replication
{
    public sealed class ReplicationPlayerPresence
    {
        public ReplicationPlayerPresence(long sequence, bool visible, float worldX, float worldY, float worldZ)
        {
            Sequence = sequence; Visible = visible; WorldX = worldX; WorldY = worldY; WorldZ = worldZ;
        }
        public long Sequence { get; }
        public bool Visible { get; }
        public float WorldX { get; }
        public float WorldY { get; }
        public float WorldZ { get; }
    }

    public sealed class ReplicationPlayerPing
    {
        public ReplicationPlayerPing(long sequence, float worldX, float worldY, float worldZ)
        {
            Sequence = sequence; WorldX = worldX; WorldY = worldY; WorldZ = worldZ;
        }
        public long Sequence { get; }
        public float WorldX { get; }
        public float WorldY { get; }
        public float WorldZ { get; }
    }

    public static class ReplicationPresencePayloadCodec
    {
        private const float MaxAbsoluteWorldCoordinate = 1000000f;

        public static TransportEnvelope ForPresence(string senderId, ReplicationPlayerPresence presence)
        {
            if (presence == null) throw new ArgumentNullException(nameof(presence));
            return new TransportEnvelope(
                TransportMessageKind.ReplicationPlayerPresence,
                presence.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ReplicationPayloadCodec.ProtocolVersion,
                    presence.Sequence.ToString(CultureInfo.InvariantCulture),
                    presence.Visible ? "1" : "0",
                    presence.WorldX.ToString("R", CultureInfo.InvariantCulture),
                    presence.WorldY.ToString("R", CultureInfo.InvariantCulture),
                    presence.WorldZ.ToString("R", CultureInfo.InvariantCulture)
                }));
        }

        public static bool TryReadPresence(TransportEnvelope envelope, out ReplicationPlayerPresence? presence, out string error)
        {
            presence = null; error = string.Empty;
            if (envelope.Kind != TransportMessageKind.ReplicationPlayerPresence)
            {
                error = "envelope is not player presence"; return false;
            }
            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 6
                || !string.Equals(parts[0], ReplicationPayloadCodec.ProtocolVersion, StringComparison.Ordinal)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || sequence <= 0
                || (parts[2] != "0" && parts[2] != "1")
                || !TryReadCoordinate(parts[3], out var x)
                || !TryReadCoordinate(parts[4], out var y)
                || !TryReadCoordinate(parts[5], out var z))
            {
                error = "invalid player presence payload"; return false;
            }
            presence = new ReplicationPlayerPresence(sequence, parts[2] == "1", x, y, z);
            return true;
        }

        public static TransportEnvelope ForPing(string senderId, ReplicationPlayerPing ping)
        {
            if (ping == null) throw new ArgumentNullException(nameof(ping));
            return new TransportEnvelope(
                TransportMessageKind.ReplicationPlayerPing,
                ping.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ReplicationPayloadCodec.ProtocolVersion,
                    ping.Sequence.ToString(CultureInfo.InvariantCulture),
                    ping.WorldX.ToString("R", CultureInfo.InvariantCulture),
                    ping.WorldY.ToString("R", CultureInfo.InvariantCulture),
                    ping.WorldZ.ToString("R", CultureInfo.InvariantCulture)
                }));
        }

        public static bool TryReadPing(TransportEnvelope envelope, out ReplicationPlayerPing? ping, out string error)
        {
            ping = null; error = string.Empty;
            if (envelope.Kind != TransportMessageKind.ReplicationPlayerPing)
            {
                error = "envelope is not player ping"; return false;
            }
            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 5
                || !string.Equals(parts[0], ReplicationPayloadCodec.ProtocolVersion, StringComparison.Ordinal)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || sequence <= 0
                || !TryReadCoordinate(parts[2], out var x)
                || !TryReadCoordinate(parts[3], out var y)
                || !TryReadCoordinate(parts[4], out var z))
            {
                error = "invalid player ping payload"; return false;
            }
            ping = new ReplicationPlayerPing(sequence, x, y, z);
            return true;
        }

        private static bool TryReadCoordinate(string value, out float coordinate)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)
                || float.IsNaN(coordinate) || float.IsInfinity(coordinate)
                || Math.Abs(coordinate) > MaxAbsoluteWorldCoordinate)
            {
                coordinate = 0f; return false;
            }
            return true;
        }
    }
}
