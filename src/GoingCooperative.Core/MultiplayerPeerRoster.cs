using System;
using System.Collections.Generic;

namespace GoingCooperative.Core
{
    public static class MultiplayerPeerLimits
    {
        // Current Direct runtime remains point-to-point until the transport fan-out
        // refactor is complete. The menu may expose the target capacity but must not
        // start a larger session yet.
        public const int CurrentDirectRuntimePlayers = 2;
        public const int StableTargetPlayers = 4;
        public const int ExperimentalMaximumPlayers = 8;
    }

    public static class MultiplayerPeerIds
    {
        public const string Host = "host";

        public static string Client(int slot)
        {
            if (slot < 1
                || slot >= MultiplayerPeerLimits.ExperimentalMaximumPlayers)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            return "client-" + slot.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool TryParseClientSlot(
            string peerId,
            out int slot)
        {
            slot = 0;
            if (string.IsNullOrWhiteSpace(peerId)
                || !peerId.StartsWith("client-", StringComparison.Ordinal))
            {
                return false;
            }

            if (!int.TryParse(
                    peerId.Substring("client-".Length),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out slot)
                || slot < 1
                || slot >= MultiplayerPeerLimits.ExperimentalMaximumPlayers)
            {
                slot = 0;
                return false;
            }

            return true;
        }

        public static bool IsValid(string peerId)
        {
            return string.Equals(peerId, Host, StringComparison.Ordinal)
                || TryParseClientSlot(peerId, out _);
        }
    }

    public sealed class MultiplayerPeerInfo
    {
        public MultiplayerPeerInfo(
            string peerId,
            string displayName,
            bool isHost,
            bool connected)
        {
            if (string.IsNullOrWhiteSpace(peerId))
            {
                throw new ArgumentException("Peer id is required.", nameof(peerId));
            }

            PeerId = peerId.Trim();
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? PeerId
                : displayName.Trim();
            IsHost = isHost;
            Connected = connected;
        }

        public string PeerId { get; }

        public string DisplayName { get; }

        public bool IsHost { get; }

        public bool Connected { get; set; }
    }

    public sealed class MultiplayerPeerRoster
    {
        private readonly Dictionary<string, MultiplayerPeerInfo> peers =
            new Dictionary<string, MultiplayerPeerInfo>(StringComparer.Ordinal);

        public MultiplayerPeerRoster(
            string hostPeerId,
            string hostDisplayName,
            int maxPlayers)
        {
            if (maxPlayers < 2
                || maxPlayers > MultiplayerPeerLimits.ExperimentalMaximumPlayers)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            }

            MaxPlayers = maxPlayers;
            var host = new MultiplayerPeerInfo(
                hostPeerId,
                hostDisplayName,
                isHost: true,
                connected: true);
            peers.Add(host.PeerId, host);
        }

        public int MaxPlayers { get; }

        public int Count
        {
            get { return peers.Count; }
        }

        public int ConnectedCount
        {
            get
            {
                var count = 0;
                foreach (var peer in peers.Values)
                {
                    if (peer.Connected)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public IReadOnlyCollection<MultiplayerPeerInfo> Peers
        {
            get { return peers.Values; }
        }

        public bool IsFull
        {
            get { return Count >= MaxPlayers; }
        }

        public bool TryAddPeer(
            string peerId,
            string displayName,
            out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(peerId))
            {
                error = "peer-id-required";
                return false;
            }

            peerId = peerId.Trim();
            if (peers.ContainsKey(peerId))
            {
                error = "peer-id-already-present";
                return false;
            }

            if (IsFull)
            {
                error = "session-full";
                return false;
            }

            peers.Add(
                peerId,
                new MultiplayerPeerInfo(
                    peerId,
                    displayName,
                    isHost: false,
                    connected: true));
            return true;
        }

        public bool TrySetConnected(
            string peerId,
            bool connected)
        {
            if (!peers.TryGetValue(peerId, out var peer))
            {
                return false;
            }

            peer.Connected = connected;
            return true;
        }

        public bool RemovePeer(string peerId)
        {
            if (!peers.TryGetValue(peerId, out var peer)
                || peer.IsHost)
            {
                return false;
            }

            return peers.Remove(peerId);
        }

        public bool Contains(string peerId)
        {
            return !string.IsNullOrWhiteSpace(peerId)
                && peers.ContainsKey(peerId.Trim());
        }
    }
}
