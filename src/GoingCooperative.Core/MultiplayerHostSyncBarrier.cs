using System;
using System.Collections.Generic;

namespace GoingCooperative.Core
{
    public sealed class MultiplayerHostSyncBarrier
    {
        private readonly Dictionary<string, long> peerGenerations =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public int Count
        {
            get { return peerGenerations.Count; }
        }

        public bool Enter(string peerId, long connectionGeneration)
        {
            if (string.IsNullOrWhiteSpace(peerId))
            {
                throw new ArgumentException(
                    "Peer id is required.",
                    nameof(peerId));
            }

            if (connectionGeneration <= 0L)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(connectionGeneration));
            }

            var wasEmpty = peerGenerations.Count == 0;
            peerGenerations[peerId] = connectionGeneration;
            return wasEmpty;
        }

        public bool Exit(string peerId, long connectionGeneration)
        {
            if (string.IsNullOrWhiteSpace(peerId)
                || connectionGeneration <= 0L
                || !peerGenerations.TryGetValue(
                    peerId,
                    out var currentGeneration)
                || currentGeneration != connectionGeneration)
            {
                return false;
            }

            peerGenerations.Remove(peerId);
            return peerGenerations.Count == 0;
        }

        public bool ExitCurrent(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)
                || !peerGenerations.Remove(peerId))
            {
                return false;
            }

            return peerGenerations.Count == 0;
        }

        public bool Contains(string peerId)
        {
            return !string.IsNullOrWhiteSpace(peerId)
                && peerGenerations.ContainsKey(peerId);
        }

        public bool Clear()
        {
            if (peerGenerations.Count == 0)
            {
                return false;
            }

            peerGenerations.Clear();
            return true;
        }
    }
}
