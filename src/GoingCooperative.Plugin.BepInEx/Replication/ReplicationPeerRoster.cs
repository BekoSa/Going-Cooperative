using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GoingCooperative.Core;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly Dictionary<string, ReplicationPeerStatus>
            ReplicationPeerStatuses =
                new Dictionary<string, ReplicationPeerStatus>(
                    StringComparer.Ordinal);
        private static float replicationNextPeerRosterBroadcastRealtime;
        private static string replicationLastPeerRosterSignature =
            string.Empty;

        private void UpdateReplicationPeerRosterStatus()
        {
            if (!replicationRuntimeStarted
                || replicationTransport == null
                || Time.realtimeSinceStartup
                    < replicationNextPeerRosterBroadcastRealtime)
            {
                return;
            }

            replicationNextPeerRosterBroadcastRealtime =
                Time.realtimeSinceStartup + 0.5f;

            if (!replicationConfigHostMode)
            {
                return;
            }

            var snapshots = multiplayerSaveTransfer.GetPeerSnapshots();
            var signature = BuildReplicationPeerRosterSignature(snapshots);
            if (string.Equals(
                    signature,
                    replicationLastPeerRosterSignature,
                    StringComparison.Ordinal))
            {
                return;
            }

            replicationLastPeerRosterSignature = signature;
            for (var i = 0; i < snapshots.Count; i++)
            {
                BroadcastReplicationPeerStatus(snapshots[i]);
            }
        }

        private void SendReplicationPeerRosterToPeer(string peerId)
        {
            if (!replicationConfigHostMode
                || replicationTransport == null
                || !MultiplayerPeerIds.TryParseClientSlot(
                    peerId,
                    out _))
            {
                return;
            }

            var snapshots = multiplayerSaveTransfer.GetPeerSnapshots();
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                try
                {
                    replicationTransport.SendToPeer(
                        peerId,
                        ReplicationPeerStatusCodec.ForStatus(
                            ReplicationHostPeerId,
                            new ReplicationPeerStatus(
                                snapshot.PeerId,
                                snapshot.Nickname,
                                snapshot.Phase,
                                snapshot.Connected,
                                snapshot.Playing)));
                }
                catch (Exception ex)
                {
                    LogReplicationWarning(
                        "[MP/SESSION] roster send failed target="
                        + peerId
                        + " subject="
                        + snapshot.PeerId
                        + " error="
                        + ex.GetType().Name
                        + ":"
                        + ex.Message);
                }
            }
        }

        private void BroadcastReplicationPeerStatus(
            MultiplayerTransferPeerSnapshot snapshot)
        {
            if (replicationTransport == null)
            {
                return;
            }

            try
            {
                replicationTransport.Send(
                    ReplicationPeerStatusCodec.ForStatus(
                        ReplicationHostPeerId,
                        new ReplicationPeerStatus(
                            snapshot.PeerId,
                            snapshot.Nickname,
                            snapshot.Phase,
                            snapshot.Connected,
                            snapshot.Playing)));
            }
            catch (Exception ex)
            {
                LogReplicationWarning(
                    "[MP/SESSION] roster broadcast failed subject="
                    + snapshot.PeerId
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private void HandleReplicationPeerStatus(
            TransportEnvelope envelope)
        {
            if (!ReplicationPeerStatusCodec.TryReadStatus(
                    envelope,
                    out var status,
                    out var error)
                || status == null)
            {
                LogReplicationWarning(
                    "[MP/SESSION] peer status decode failed error="
                    + error);
                return;
            }

            if (replicationConfigHostMode)
            {
                LogReplicationWarning(
                    "[MP/SESSION] client peer-status message rejected sender="
                    + envelope.SenderId);
                return;
            }

            if (!string.Equals(
                    envelope.SenderId,
                    ReplicationHostPeerId,
                    StringComparison.Ordinal))
            {
                LogReplicationWarning(
                    "[MP/SESSION] peer status rejected non-host sender="
                    + envelope.SenderId);
                return;
            }

            if (!status.Connected)
            {
                ReplicationPeerStatuses.Remove(status.PeerId);
                if (!string.Equals(
                        status.PeerId,
                        ReplicationHostPeerId,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        status.PeerId,
                        GetReplicationLocalPeerId(),
                        StringComparison.Ordinal))
                {
                    ReplicationCompatiblePeerIds.Remove(status.PeerId);
                    ReplicationCompatiblePeerHellos.Remove(status.PeerId);
                    RemoveReplicationRemotePeerPresence(status.PeerId);
                }

                return;
            }

            ReplicationPeerStatuses[status.PeerId] = status;
            if (!string.Equals(
                    status.PeerId,
                    GetReplicationLocalPeerId(),
                    StringComparison.Ordinal))
            {
                SetReplicationRemotePeerDisplayName(
                    status.PeerId,
                    status.DisplayName);
            }
        }

        private static string BuildReplicationPeerRosterSignature(
            IReadOnlyList<MultiplayerTransferPeerSnapshot> snapshots)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                builder.Append(snapshot.PeerId);
                builder.Append('|');
                builder.Append(snapshot.Nickname);
                builder.Append('|');
                builder.Append(snapshot.Phase);
                builder.Append('|');
                builder.Append(snapshot.Connected ? '1' : '0');
                builder.Append(snapshot.Playing ? '1' : '0');
                builder.Append(';');
            }

            return builder.ToString();
        }

        private int GetReplicationVisibleConnectedPeerCount()
        {
            if (replicationConfigHostMode)
            {
                return multiplayerSaveTransfer.ConnectedPeerCount;
            }

            var count = 0;
            foreach (var status in ReplicationPeerStatuses.Values)
            {
                if (status.Connected)
                {
                    count++;
                }
            }

            if (count > 0)
            {
                return count;
            }

            return replicationRuntimeStarted ? 2 : 0;
        }

        private static void ResetReplicationPeerRosterStatus()
        {
            ReplicationPeerStatuses.Clear();
            replicationNextPeerRosterBroadcastRealtime = 0f;
            replicationLastPeerRosterSignature = string.Empty;
        }
    }
}
