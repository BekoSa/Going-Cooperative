using System;

namespace GoingCooperative.Core
{
    public sealed class ReplicationPeerStatus
    {
        public ReplicationPeerStatus(
            string peerId,
            string displayName,
            string phase,
            bool connected,
            bool playing)
        {
            if (!MultiplayerPeerIds.IsValid(peerId))
            {
                throw new ArgumentException(
                    "Invalid multiplayer peer id.",
                    nameof(peerId));
            }

            PeerId = peerId;
            DisplayName = MultiplayerNickname.Normalize(displayName);
            Phase = NormalizePhase(phase);
            Connected = connected;
            Playing = connected && playing;
        }

        public string PeerId { get; }
        public string DisplayName { get; }
        public string Phase { get; }
        public bool Connected { get; }
        public bool Playing { get; }

        private static string NormalizePhase(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unknown";
            }

            value = value.Trim();
            return value.Length <= 32
                ? value
                : value.Substring(0, 32);
        }
    }

    public static class ReplicationPeerStatusCodec
    {
        private const string WireVersion = "peer-status-v1";

        public static TransportEnvelope ForStatus(
            string senderId,
            ReplicationPeerStatus status)
        {
            if (status == null)
            {
                throw new ArgumentNullException(nameof(status));
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationPeerStatus,
                0L,
                senderId,
                string.Join("|", new[]
                {
                    WireVersion,
                    Encode(status.PeerId),
                    Encode(status.DisplayName),
                    Encode(status.Phase),
                    status.Connected ? "1" : "0",
                    status.Playing ? "1" : "0"
                }));
        }

        public static bool TryReadStatus(
            TransportEnvelope envelope,
            out ReplicationPeerStatus? status,
            out string error)
        {
            status = null;
            error = string.Empty;
            if (envelope.Kind != TransportMessageKind.ReplicationPeerStatus)
            {
                error = "not-peer-status";
                return false;
            }

            var parts = envelope.Payload.Split(
                new[] { '|' },
                StringSplitOptions.None);
            if (parts.Length != 6
                || !string.Equals(
                    parts[0],
                    WireVersion,
                    StringComparison.Ordinal))
            {
                error = "peer-status-wire-version";
                return false;
            }

            try
            {
                var peerId = Decode(parts[1]);
                var displayName = Decode(parts[2]);
                var phase = Decode(parts[3]);
                if ((parts[4] != "0" && parts[4] != "1")
                    || (parts[5] != "0" && parts[5] != "1"))
                {
                    error = "peer-status-bool";
                    return false;
                }

                status = new ReplicationPeerStatus(
                    peerId,
                    displayName,
                    phase,
                    parts[4] == "1",
                    parts[5] == "1");
                return true;
            }
            catch (Exception ex)
            {
                error = "peer-status-decode-" + ex.GetType().Name;
                return false;
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            return System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(value));
        }
    }
}
