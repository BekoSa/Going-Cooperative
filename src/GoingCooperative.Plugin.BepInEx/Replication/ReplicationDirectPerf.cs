using System.Globalization;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static float replicationNextDirectTransportPerfLogRealtime;
        private static long replicationLastPerfDatagramsSent;
        private static long replicationLastPerfDatagramsReceived;
        private static long replicationLastPerfBytesSent;
        private static long replicationLastPerfBytesReceived;

        private void LogReplicationDirectTransportPerfIfDue()
        {
            if (!replicationConfigPathingPerfDiagnostics || replicationTransport == null
                || Time.realtimeSinceStartup < replicationNextDirectTransportPerfLogRealtime) return;

            replicationNextDirectTransportPerfLogRealtime = Time.realtimeSinceStartup + 10f;
            var txd = replicationTransport.DatagramsSent;
            var rxd = replicationTransport.DatagramsReceived;
            var txb = replicationTransport.BytesSent;
            var rxb = replicationTransport.BytesReceived;
            if (txd < replicationLastPerfDatagramsSent || rxd < replicationLastPerfDatagramsReceived
                || txb < replicationLastPerfBytesSent || rxb < replicationLastPerfBytesReceived)
            {
                replicationLastPerfDatagramsSent = replicationLastPerfDatagramsReceived = 0L;
                replicationLastPerfBytesSent = replicationLastPerfBytesReceived = 0L;
            }

            LogReplicationInfo("[MP/NET] direct perf"
                + " txDatagrams=+" + (txd - replicationLastPerfDatagramsSent).ToString(CultureInfo.InvariantCulture)
                + " rxDatagrams=+" + (rxd - replicationLastPerfDatagramsReceived).ToString(CultureInfo.InvariantCulture)
                + " txBytes=+" + (txb - replicationLastPerfBytesSent).ToString(CultureInfo.InvariantCulture)
                + " rxBytes=+" + (rxb - replicationLastPerfBytesReceived).ToString(CultureInfo.InvariantCulture)
                + " chunksTx=" + replicationTransport.ChunkEnvelopesSent.ToString(CultureInfo.InvariantCulture)
                + " chunksRx=" + replicationTransport.ChunkEnvelopesReceived.ToString(CultureInfo.InvariantCulture)
                + " reassembled=" + replicationTransport.ReassembledMessages.ToString(CultureInfo.InvariantCulture)
                + " binaryTx=" + replicationTransport.SecureBinaryPacketsSent.ToString(CultureInfo.InvariantCulture)
                + " binaryRx=" + replicationTransport.SecureBinaryPacketsReceived.ToString(CultureInfo.InvariantCulture)
                + " pending=" + replicationTransport.PendingMessages.ToString(CultureInfo.InvariantCulture)
                + " coalescedRx=" + replicationTransport.CoalescedStateReplacements.ToString(CultureInfo.InvariantCulture)
                + " coalescedTx=" + replicationTransport.OutgoingCoalescedStateReplacements.ToString(CultureInfo.InvariantCulture)
                + " sendFailures=" + replicationTransport.SendFailures.ToString(CultureInfo.InvariantCulture)
                + " sparseSent=" + replicationSparseTransformRowsSent.ToString(CultureInfo.InvariantCulture)
                + " sparseSuppressed=" + replicationSparseTransformRowsSuppressed.ToString(CultureInfo.InvariantCulture)
                + " sparsePrefiltered=" + replicationSparseTransformRowsPrefiltered.ToString(CultureInfo.InvariantCulture)
                + " idlePresentationSkipped=" + replicationPresentationIdleTracksSkipped.ToString(CultureInfo.InvariantCulture));

            replicationLastPerfDatagramsSent = txd;
            replicationLastPerfDatagramsReceived = rxd;
            replicationLastPerfBytesSent = txb;
            replicationLastPerfBytesReceived = rxb;
        }

        private static void ResetReplicationDirectPerfState()
        {
            replicationNextDirectTransportPerfLogRealtime = 0f;
            replicationLastPerfDatagramsSent = replicationLastPerfDatagramsReceived = 0L;
            replicationLastPerfBytesSent = replicationLastPerfBytesReceived = 0L;
        }
    }
}
