using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GoingCooperative.Core
{
    public sealed class UdpNetworkTransport : INetworkTransport
    {
        private readonly ConcurrentQueue<TransportEnvelope> inbox =
            new ConcurrentQueue<TransportEnvelope>();
        private readonly ConcurrentQueue<OutgoingTransportItem> outbox =
            new ConcurrentQueue<OutgoingTransportItem>();
        private readonly TransportChunkReassembler chunkReassembler =
            new TransportChunkReassembler();
        private readonly object latestStateLock = new object();
        private readonly object receiveStateLock = new object();
        private readonly object sendLock = new object();
        private readonly object hostPeerLock = new object();

        private readonly Dictionary<string, TransportEnvelope> latestPlayerPresenceBySender =
            new Dictionary<string, TransportEnvelope>(StringComparer.Ordinal);
        private readonly Dictionary<string, TransportEnvelope> latestPlayerSelectionBySender =
            new Dictionary<string, TransportEnvelope>(StringComparer.Ordinal);
        private readonly Dictionary<string, HostUdpPeerSession> hostPeersByEndpoint =
            new Dictionary<string, HostUdpPeerSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, HostUdpPeerSession> hostPeersById =
            new Dictionary<string, HostUdpPeerSession>(StringComparer.Ordinal);

        private UdpClient? udpClient;
        private IPEndPoint? remoteEndpoint;
        private bool isHostEndpoint;
        private long nextChunkId;
        private readonly bool securityEnabled;
        private readonly byte[] securityKey;

        // Client-side security state. Host security state is isolated per peer below.
        private byte[]? clientNonce;
        private byte[]? hostNonce;
        private byte[]? sessionId;
        private long sendSecuritySequence;
        private long highestReceiveSecuritySequence;
        private readonly HashSet<long> receivedSecuritySequences =
            new HashSet<long>();
        private DateTime nextClientHelloUtc;
        private volatile bool binarySecurityDataEnabled;
        private volatile bool authenticationEstablished;

        private volatile bool isConnected;
        private int receiveGeneration;
        private int receivePending;
        private int sendWorkerActive;
        private int unauthenticatedDatagramsThisWindow;
        private DateTime unauthenticatedWindowUtc;

        // Transform is host-authored global state. Presence and selection are keyed by
        // sender so one player's high-frequency state can never overwrite another's.
        private TransportEnvelope? latestTransformSnapshot;
        private TransportEnvelope? latestOutgoingTransformSnapshot;
        private TransportEnvelope? latestOutgoingPlayerPresence;
        private TransportEnvelope? latestOutgoingPlayerSelection;

        private long authenticationFailures;
        private long decodeFailures;
        private long chunkFailures;
        private long datagramsSent;
        private long datagramsReceived;
        private long bytesSent;
        private long bytesReceived;
        private long chunkEnvelopesSent;
        private long chunkEnvelopesReceived;
        private long reassembledMessages;
        private long secureBinaryPacketsSent;
        private long secureBinaryPacketsReceived;
        private long coalescedStateReplacements;
        private long outgoingCoalescedStateReplacements;
        private long sendFailures;
        private long peerBindingFailures;

        private const int MaxUnchunkedDatagramBytes = 1100;
        private const int LegacySecureUnchunkedBytes = 850;
        private const int LegacySecureChunkChars = 450;
        private const int SecureDataV2HeaderBytes = 32;
        private const int SecureDataV2TagBytes = 32;
        private const int MaxSecureDataV2PayloadBytes = 60 * 1024;
        private const int MaxChunkEnvelopeChars = 700;
        private const int MaxHostPeerSessions =
            MultiplayerPeerLimits.ExperimentalMaximumPlayers * 2;
        private static readonly byte[] SecureDataV2Magic =
            { (byte)'G', (byte)'C', (byte)'D', (byte)'2' };

        public bool IsConnected
        {
            get { return isConnected; }
        }

        public bool AuthenticationEstablished
        {
            get
            {
                if (!securityEnabled)
                {
                    return isConnected;
                }

                if (!isHostEndpoint)
                {
                    return authenticationEstablished;
                }

                lock (hostPeerLock)
                {
                    foreach (var peer in hostPeersByEndpoint.Values)
                    {
                        if (peer.AuthenticationEstablished)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        public int BoundPeerCount
        {
            get
            {
                if (!isHostEndpoint)
                {
                    return authenticationEstablished ? 1 : 0;
                }

                lock (hostPeerLock)
                {
                    var count = 0;
                    foreach (var peer in hostPeersById.Values)
                    {
                        if (peer.AuthenticationEstablished
                            && peer.ApplicationCompatible)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public long AuthenticationFailures
        {
            get { return Interlocked.Read(ref authenticationFailures); }
        }

        public long DecodeFailures
        {
            get { return Interlocked.Read(ref decodeFailures); }
        }

        public long ChunkFailures
        {
            get { return Interlocked.Read(ref chunkFailures); }
        }

        public long DatagramsSent
        {
            get { return Interlocked.Read(ref datagramsSent); }
        }

        public long DatagramsReceived
        {
            get { return Interlocked.Read(ref datagramsReceived); }
        }

        public long BytesSent
        {
            get { return Interlocked.Read(ref bytesSent); }
        }

        public long BytesReceived
        {
            get { return Interlocked.Read(ref bytesReceived); }
        }

        public long ChunkEnvelopesSent
        {
            get { return Interlocked.Read(ref chunkEnvelopesSent); }
        }

        public long ChunkEnvelopesReceived
        {
            get { return Interlocked.Read(ref chunkEnvelopesReceived); }
        }

        public long ReassembledMessages
        {
            get { return Interlocked.Read(ref reassembledMessages); }
        }

        public long SecureBinaryPacketsSent
        {
            get { return Interlocked.Read(ref secureBinaryPacketsSent); }
        }

        public long SecureBinaryPacketsReceived
        {
            get { return Interlocked.Read(ref secureBinaryPacketsReceived); }
        }

        public long CoalescedStateReplacements
        {
            get { return Interlocked.Read(ref coalescedStateReplacements); }
        }

        public long OutgoingCoalescedStateReplacements
        {
            get { return Interlocked.Read(ref outgoingCoalescedStateReplacements); }
        }

        public long SendFailures
        {
            get { return Interlocked.Read(ref sendFailures); }
        }

        public long PeerBindingFailures
        {
            get { return Interlocked.Read(ref peerBindingFailures); }
        }

        public int PendingMessages
        {
            get
            {
                var pending = inbox.Count;
                lock (latestStateLock)
                {
                    if (latestTransformSnapshot != null) pending++;
                    pending += latestPlayerPresenceBySender.Count;
                    pending += latestPlayerSelectionBySender.Count;
                }

                return pending;
            }
        }

        public int PendingOutgoingMessages
        {
            get
            {
                var pending = outbox.Count;
                lock (latestStateLock)
                {
                    if (latestOutgoingTransformSnapshot != null) pending++;
                    if (latestOutgoingPlayerPresence != null) pending++;
                    if (latestOutgoingPlayerSelection != null) pending++;
                }

                return pending;
            }
        }

        public int LocalPort
        {
            get
            {
                var client = udpClient;
                if (client == null || client.Client.LocalEndPoint == null)
                {
                    return 0;
                }

                return ((IPEndPoint)client.Client.LocalEndPoint).Port;
            }
        }

        public bool RemoteEndpointKnown
        {
            get
            {
                if (!isHostEndpoint)
                {
                    return remoteEndpoint != null;
                }

                lock (hostPeerLock)
                {
                    return hostPeersByEndpoint.Count > 0;
                }
            }
        }

        public UdpNetworkTransport()
            : this(false, string.Empty)
        {
        }

        public UdpNetworkTransport(
            bool securityEnabled,
            string sessionCode)
        {
            this.securityEnabled = securityEnabled;
            if (securityEnabled)
            {
                if (!DirectTransportSecurity.TryDeriveKey(
                        sessionCode,
                        out securityKey,
                        out var error))
                {
                    throw new ArgumentException(error, nameof(sessionCode));
                }
            }
            else
            {
                securityKey = Array.Empty<byte>();
                authenticationEstablished = true;
            }
        }

        public void StartHost(int port)
        {
            Stop();
            udpClient = new UdpClient(
                new IPEndPoint(IPAddress.Any, port));
            isHostEndpoint = true;
            isConnected = true;
            authenticationEstablished = !securityEnabled;
            StartReceiveLoop();
        }

        public void Connect(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException(
                    "Host is required.",
                    nameof(host));
            }

            Stop();
            udpClient = new UdpClient(
                new IPEndPoint(IPAddress.Any, 0));
            remoteEndpoint = new IPEndPoint(
                ResolveHost(host),
                port);
            isHostEndpoint = false;
            isConnected = true;
            authenticationEstablished = !securityEnabled;
            if (securityEnabled)
            {
                clientNonce = DirectTransportSecurity.RandomBytes(16);
                nextClientHelloUtc = DateTime.MinValue;
                SendClientHelloIfDue();
            }

            StartReceiveLoop();
        }

        public void EnableBinarySecurityData()
        {
            if (!securityEnabled)
            {
                return;
            }

            if (!isHostEndpoint && authenticationEstablished)
            {
                binarySecurityDataEnabled = true;
            }
        }

        public void EnableBinarySecurityData(string peerId)
        {
            if (!securityEnabled)
            {
                return;
            }

            if (!isHostEndpoint)
            {
                EnableBinarySecurityData();
                return;
            }

            lock (hostPeerLock)
            {
                if (hostPeersById.TryGetValue(peerId, out var peer)
                    && peer.AuthenticationEstablished)
                {
                    peer.BinarySecurityDataEnabled = true;
                    peer.ApplicationCompatible = true;
                }
            }
        }

        public string[] GetBoundPeerIds()
        {
            if (!isHostEndpoint)
            {
                return Array.Empty<string>();
            }

            lock (hostPeerLock)
            {
                var result = new List<string>();
                foreach (var pair in hostPeersById)
                {
                    if (pair.Value.AuthenticationEstablished
                        && pair.Value.ApplicationCompatible)
                    {
                        result.Add(pair.Key);
                    }
                }

                result.Sort(StringComparer.Ordinal);
                return result.ToArray();
            }
        }

        public bool IsPeerApplicationReady(string peerId)
        {
            if (!isHostEndpoint
                || string.IsNullOrWhiteSpace(peerId))
            {
                return false;
            }

            lock (hostPeerLock)
            {
                return hostPeersById.TryGetValue(peerId, out var peer)
                    && !peer.Closed
                    && peer.AuthenticationEstablished
                    && peer.ApplicationCompatible;
            }
        }

        public bool RemovePeer(string peerId)
        {
            if (!isHostEndpoint
                || string.IsNullOrWhiteSpace(peerId))
            {
                return false;
            }

            lock (hostPeerLock)
            {
                if (!hostPeersById.TryGetValue(peerId, out var peer))
                {
                    return false;
                }

                hostPeersById.Remove(peerId);
                hostPeersByEndpoint.Remove(
                    FormatEndpointKey(peer.Endpoint));
                peer.Closed = true;
                return true;
            }
        }

        public void Send(TransportEnvelope envelope)
        {
            QueueOutgoing(
                envelope,
                targetPeerId: null,
                excludedPeerId: null,
                coalesceBroadcastState: true);
        }

        public void SendToPeer(
            string peerId,
            TransportEnvelope envelope)
        {
            if (!isHostEndpoint)
            {
                throw new InvalidOperationException(
                    "Targeted sends are host-only.");
            }

            if (!MultiplayerPeerIds.TryParseClientSlot(peerId, out _))
            {
                throw new ArgumentException(
                    "Invalid client peer id.",
                    nameof(peerId));
            }

            QueueOutgoing(
                envelope,
                peerId,
                excludedPeerId: null,
                coalesceBroadcastState: false);
        }

        public void SendToAllExcept(
            string excludedPeerId,
            TransportEnvelope envelope)
        {
            if (!isHostEndpoint)
            {
                throw new InvalidOperationException(
                    "Fan-out exclusion is host-only.");
            }

            QueueOutgoing(
                envelope,
                targetPeerId: null,
                excludedPeerId,
                coalesceBroadcastState: false);
        }

        private void QueueOutgoing(
            TransportEnvelope envelope,
            string? targetPeerId,
            string? excludedPeerId,
            bool coalesceBroadcastState)
        {
            if (!isConnected || udpClient == null)
            {
                throw new InvalidOperationException(
                    "Transport is not connected.");
            }

            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (!isHostEndpoint && remoteEndpoint == null)
            {
                throw new InvalidOperationException(
                    "Client has no host endpoint.");
            }

            if (coalesceBroadcastState
                && targetPeerId == null
                && excludedPeerId == null)
            {
                switch (envelope.Kind)
                {
                    case TransportMessageKind.ReplicationTransformSnapshot:
                        ReplaceLatestOutgoingState(
                            ref latestOutgoingTransformSnapshot,
                            envelope);
                        ScheduleSendWorker();
                        return;
                    case TransportMessageKind.ReplicationPlayerPresence:
                        ReplaceLatestOutgoingState(
                            ref latestOutgoingPlayerPresence,
                            envelope);
                        ScheduleSendWorker();
                        return;
                    case TransportMessageKind.ReplicationPlayerSelection:
                        ReplaceLatestOutgoingState(
                            ref latestOutgoingPlayerSelection,
                            envelope);
                        ScheduleSendWorker();
                        return;
                }
            }

            outbox.Enqueue(
                new OutgoingTransportItem(
                    envelope,
                    targetPeerId,
                    excludedPeerId));
            ScheduleSendWorker();
        }

        private void ReplaceLatestOutgoingState(
            ref TransportEnvelope? slot,
            TransportEnvelope envelope)
        {
            lock (latestStateLock)
            {
                if (slot != null)
                {
                    Interlocked.Increment(
                        ref outgoingCoalescedStateReplacements);
                }

                slot = envelope;
            }
        }

        private void ScheduleSendWorker()
        {
            if (!isConnected)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref sendWorkerActive,
                    1,
                    0) != 0)
            {
                return;
            }

            var generation = Volatile.Read(ref receiveGeneration);
            ThreadPool.QueueUserWorkItem(
                _ => DrainSendQueue(generation));
        }

        private void DrainSendQueue(int generation)
        {
            try
            {
                var processed = 0;
                while (isConnected
                    && generation == Volatile.Read(ref receiveGeneration)
                    && processed++ < 512)
                {
                    if (!TryDequeueOutgoingItem(out var item))
                    {
                        break;
                    }

                    try
                    {
                        SendEnvelopeImmediate(item);
                    }
                    catch
                    {
                        Interlocked.Increment(ref sendFailures);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref sendWorkerActive, 0);
                if (isConnected && HasPendingOutgoingEnvelope())
                {
                    ScheduleSendWorker();
                }
            }
        }

        private bool TryDequeueOutgoingItem(
            out OutgoingTransportItem item)
        {
            if (outbox.TryDequeue(out var queued))
            {
                item = queued;
                return true;
            }

            lock (latestStateLock)
            {
                if (latestOutgoingPlayerPresence != null)
                {
                    item = new OutgoingTransportItem(
                        latestOutgoingPlayerPresence,
                        null,
                        null);
                    latestOutgoingPlayerPresence = null;
                    return true;
                }

                if (latestOutgoingPlayerSelection != null)
                {
                    item = new OutgoingTransportItem(
                        latestOutgoingPlayerSelection,
                        null,
                        null);
                    latestOutgoingPlayerSelection = null;
                    return true;
                }

                if (latestOutgoingTransformSnapshot != null)
                {
                    item = new OutgoingTransportItem(
                        latestOutgoingTransformSnapshot,
                        null,
                        null);
                    latestOutgoingTransformSnapshot = null;
                    return true;
                }
            }

            item = new OutgoingTransportItem(
                new TransportEnvelope(
                    TransportMessageKind.ReplicationHello,
                    0L,
                    string.Empty,
                    string.Empty),
                null,
                null);
            return false;
        }

        private bool HasPendingOutgoingEnvelope()
        {
            if (!outbox.IsEmpty)
            {
                return true;
            }

            lock (latestStateLock)
            {
                return latestOutgoingTransformSnapshot != null
                    || latestOutgoingPlayerPresence != null
                    || latestOutgoingPlayerSelection != null;
            }
        }

        private void SendEnvelopeImmediate(
            OutgoingTransportItem item)
        {
            if (!isConnected)
            {
                return;
            }

            if (!isHostEndpoint)
            {
                var target = remoteEndpoint;
                if (target == null)
                {
                    return;
                }

                SendEnvelopeToClientTarget(
                    item.Envelope,
                    target);
                return;
            }

            var peers = GetHostSendTargets(
                item.Envelope.Kind,
                item.TargetPeerId,
                item.ExcludedPeerId);
            if (peers.Length == 0)
            {
                return;
            }

            var encoded = TransportEnvelopeCodec.Encode(item.Envelope);
            var encodedBytes = Encoding.UTF8.GetBytes(encoded);
            IReadOnlyList<TransportEnvelope>? legacyChunks = null;
            IReadOnlyList<TransportEnvelope>? binaryChunks = null;
            for (var i = 0; i < peers.Length; i++)
            {
                var peer = peers[i];
                var legacySecurity =
                    securityEnabled
                    && !peer.BinarySecurityDataEnabled;
                var maxBytes = legacySecurity
                    ? LegacySecureUnchunkedBytes
                    : MaxUnchunkedDatagramBytes;
                if (item.Envelope.Kind != TransportMessageKind.Chunk
                    && encodedBytes.Length > maxBytes)
                {
                    var chunks = legacySecurity
                        ? legacyChunks
                        : binaryChunks;
                    if (chunks == null)
                    {
                        chunks = TransportChunkCodec.CreateChunks(
                            item.Envelope,
                            item.Envelope.SenderId
                                + "-"
                                + Interlocked.Increment(ref nextChunkId)
                                    .ToString(
                                        System.Globalization.CultureInfo.InvariantCulture),
                            legacySecurity
                                ? LegacySecureChunkChars
                                : MaxChunkEnvelopeChars);
                        if (legacySecurity)
                        {
                            legacyChunks = chunks;
                        }
                        else
                        {
                            binaryChunks = chunks;
                        }
                    }

                    Interlocked.Add(
                        ref chunkEnvelopesSent,
                        chunks.Count);
                    for (var chunkIndex = 0;
                        chunkIndex < chunks.Count;
                        chunkIndex++)
                    {
                        var chunkBytes = Encoding.UTF8.GetBytes(
                            TransportEnvelopeCodec.Encode(
                                chunks[chunkIndex]));
                        SendPayloadToHostPeer(
                            chunkBytes,
                            peer);
                    }

                    continue;
                }

                SendPayloadToHostPeer(
                    encodedBytes,
                    peer);
            }
        }

        private void SendEnvelopeToClientTarget(
            TransportEnvelope envelope,
            IPEndPoint target)
        {
            var encoded = TransportEnvelopeCodec.Encode(envelope);
            var bytes = Encoding.UTF8.GetBytes(encoded);
            var useLegacySecureDataFrame =
                securityEnabled && !binarySecurityDataEnabled;
            var maxUnchunkedBytes = useLegacySecureDataFrame
                ? LegacySecureUnchunkedBytes
                : MaxUnchunkedDatagramBytes;
            if (envelope.Kind != TransportMessageKind.Chunk
                && bytes.Length > maxUnchunkedBytes)
            {
                var chunkId = envelope.SenderId
                    + "-"
                    + Interlocked.Increment(ref nextChunkId)
                        .ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                var chunks = TransportChunkCodec.CreateChunks(
                    envelope,
                    chunkId,
                    useLegacySecureDataFrame
                        ? LegacySecureChunkChars
                        : MaxChunkEnvelopeChars);
                Interlocked.Add(
                    ref chunkEnvelopesSent,
                    chunks.Count);
                for (var i = 0; i < chunks.Count; i++)
                {
                    var chunkBytes = Encoding.UTF8.GetBytes(
                        TransportEnvelopeCodec.Encode(chunks[i]));
                    SendPayloadToClient(
                        chunkBytes,
                        target);
                }

                return;
            }

            SendPayloadToClient(bytes, target);
        }

        private HostUdpPeerSession[] GetHostSendTargets(
            TransportMessageKind kind,
            string? targetPeerId,
            string? excludedPeerId)
        {
            lock (hostPeerLock)
            {
                var result = new List<HostUdpPeerSession>();
                if (!string.IsNullOrEmpty(targetPeerId))
                {
                    if (hostPeersById.TryGetValue(
                            targetPeerId!,
                            out var target)
                        && target.AuthenticationEstablished
                        && !target.Closed
                        && (kind == TransportMessageKind.ReplicationHello
                            || target.ApplicationCompatible))
                    {
                        result.Add(target);
                    }

                    return result.ToArray();
                }

                foreach (var peer in hostPeersById.Values)
                {
                    if (peer.Closed
                        || !peer.AuthenticationEstablished
                        || (!string.IsNullOrEmpty(excludedPeerId)
                            && string.Equals(
                                peer.PeerId,
                                excludedPeerId,
                                StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (kind != TransportMessageKind.ReplicationHello
                        && !peer.ApplicationCompatible)
                    {
                        continue;
                    }

                    result.Add(peer);
                }

                return result.ToArray();
            }
        }

        public bool TryReceive(out TransportEnvelope envelope)
        {
            if (securityEnabled
                && !isHostEndpoint
                && !authenticationEstablished)
            {
                SendClientHelloIfDue();
            }

            // Presence/selection are already coalesced to one latest state per
            // sender. Read them before the bulk FIFO so a burst of durable world
            // deltas cannot make a live cursor expire while its newest position is
            // already waiting in the transport. The bounded per-sender dictionaries
            // prevent this priority lane from starving reliable application traffic.
            lock (latestStateLock)
            {
                if (TryTakeLatestState(
                    latestPlayerPresenceBySender,
                    out envelope))
                {
                    return true;
                }

                if (TryTakeLatestState(
                    latestPlayerSelectionBySender,
                    out envelope))
                {
                    return true;
                }
            }

            if (inbox.TryDequeue(out var queued))
            {
                envelope = queued;
                return true;
            }

            lock (latestStateLock)
            {
                if (latestTransformSnapshot != null)
                {
                    envelope = latestTransformSnapshot;
                    latestTransformSnapshot = null;
                    return true;
                }
            }

            envelope = new TransportEnvelope(
                TransportMessageKind.ReplicationHello,
                0L,
                string.Empty,
                string.Empty);
            return false;
        }

        private static bool TryTakeLatestState(
            Dictionary<string, TransportEnvelope> states,
            out TransportEnvelope envelope)
        {
            string? selectedKey = null;
            TransportEnvelope? selected = null;
            foreach (var pair in states)
            {
                selectedKey = pair.Key;
                selected = pair.Value;
                break;
            }

            if (selectedKey != null && selected != null)
            {
                states.Remove(selectedKey);
                envelope = selected;
                return true;
            }

            envelope = null!;
            return false;
        }

        public void Stop()
        {
            isConnected = false;
            Interlocked.Increment(ref receiveGeneration);
            var client = udpClient;
            udpClient = null;
            if (client != null)
            {
                try { client.Close(); } catch { }
            }

            while (inbox.TryDequeue(out _)) { }
            while (outbox.TryDequeue(out _)) { }

            lock (latestStateLock)
            {
                latestTransformSnapshot = null;
                latestPlayerPresenceBySender.Clear();
                latestPlayerSelectionBySender.Clear();
                latestOutgoingTransformSnapshot = null;
                latestOutgoingPlayerPresence = null;
                latestOutgoingPlayerSelection = null;
            }

            lock (hostPeerLock)
            {
                hostPeersByEndpoint.Clear();
                hostPeersById.Clear();
            }

            lock (receiveStateLock)
            {
                remoteEndpoint = null;
                chunkReassembler.Clear();
                authenticationEstablished = !securityEnabled;
                clientNonce = hostNonce = sessionId = null;
                binarySecurityDataEnabled = false;
                sendSecuritySequence = 0L;
                highestReceiveSecuritySequence = 0L;
                receivedSecuritySequences.Clear();
            }

            Interlocked.Exchange(ref receivePending, 0);
            Interlocked.Exchange(ref authenticationFailures, 0L);
            Interlocked.Exchange(ref decodeFailures, 0L);
            Interlocked.Exchange(ref chunkFailures, 0L);
            Interlocked.Exchange(ref datagramsSent, 0L);
            Interlocked.Exchange(ref datagramsReceived, 0L);
            Interlocked.Exchange(ref bytesSent, 0L);
            Interlocked.Exchange(ref bytesReceived, 0L);
            Interlocked.Exchange(ref chunkEnvelopesSent, 0L);
            Interlocked.Exchange(ref chunkEnvelopesReceived, 0L);
            Interlocked.Exchange(ref reassembledMessages, 0L);
            Interlocked.Exchange(ref secureBinaryPacketsSent, 0L);
            Interlocked.Exchange(ref secureBinaryPacketsReceived, 0L);
            Interlocked.Exchange(ref coalescedStateReplacements, 0L);
            Interlocked.Exchange(ref outgoingCoalescedStateReplacements, 0L);
            Interlocked.Exchange(ref sendFailures, 0L);
            Interlocked.Exchange(ref peerBindingFailures, 0L);
        }

        private void StartReceiveLoop()
        {
            var client = udpClient;
            if (!isConnected || client == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref receivePending,
                    1,
                    0) != 0)
            {
                return;
            }

            var state = new ReceiveState(
                client,
                Volatile.Read(ref receiveGeneration));
            try
            {
                client.BeginReceive(
                    ReceiveCompleted,
                    state);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref receivePending, 0);
            }
            catch (SocketException)
            {
                Interlocked.Exchange(ref receivePending, 0);
            }
        }

        private void ReceiveCompleted(IAsyncResult asyncResult)
        {
            var state = asyncResult.AsyncState as ReceiveState;
            if (state == null)
            {
                Interlocked.Exchange(ref receivePending, 0);
                return;
            }

            byte[]? bytes = null;
            var sender = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                bytes = state.Client.EndReceive(
                    asyncResult,
                    ref sender);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
            catch
            {
                Interlocked.Increment(ref decodeFailures);
            }
            finally
            {
                Interlocked.Exchange(ref receivePending, 0);
            }

            if (!isConnected
                || state.Generation
                    != Volatile.Read(ref receiveGeneration))
            {
                return;
            }

            if (bytes != null && bytes.Length > 0)
            {
                lock (receiveStateLock)
                {
                    if (isConnected
                        && state.Generation
                            == Volatile.Read(ref receiveGeneration))
                    {
                        try
                        {
                            ProcessReceivedDatagram(
                                bytes,
                                sender);
                        }
                        catch
                        {
                            Interlocked.Increment(ref decodeFailures);
                        }
                    }
                }
            }

            StartReceiveLoop();
        }

        private void ProcessReceivedDatagram(
            byte[] bytes,
            IPEndPoint sender)
        {
            Interlocked.Increment(ref datagramsReceived);
            Interlocked.Add(ref bytesReceived, bytes.Length);

            HostUdpPeerSession? hostPeer = null;
            if (securityEnabled)
            {
                if (isHostEndpoint)
                {
                    if (!TryUnwrapHostSecureDatagram(
                            bytes,
                            sender,
                            out bytes,
                            out hostPeer))
                    {
                        return;
                    }
                }
                else if (!TryUnwrapClientSecureDatagram(
                    bytes,
                    sender,
                    out bytes))
                {
                    return;
                }
            }
            else if (isHostEndpoint)
            {
                hostPeer = GetOrCreateInsecureHostPeer(sender);
            }

            var line = Encoding.UTF8.GetString(bytes);
            if (!TransportEnvelopeCodec.TryDecode(
                    line,
                    out var decoded,
                    out _)
                || decoded == null)
            {
                Interlocked.Increment(ref decodeFailures);
                return;
            }

            if (decoded.Kind == TransportMessageKind.Chunk)
            {
                Interlocked.Increment(ref chunkEnvelopesReceived);
                var reassembler = chunkReassembler;
                if (isHostEndpoint)
                {
                    if (hostPeer == null
                        || !ValidateHostChunkEnvelope(
                            hostPeer,
                            decoded))
                    {
                        Interlocked.Increment(ref peerBindingFailures);
                        return;
                    }

                    reassembler = hostPeer.ChunkReassembler;
                }

                if (reassembler.TryAddChunk(
                        decoded,
                        out var reassembled,
                        out var chunkError)
                    && reassembled != null)
                {
                    if (isHostEndpoint
                        && hostPeer != null
                        && !ValidateAndBindHostEnvelope(
                            hostPeer,
                            reassembled))
                    {
                        Interlocked.Increment(ref peerBindingFailures);
                        return;
                    }

                    Interlocked.Increment(ref reassembledMessages);
                    EnqueueReceivedEnvelope(reassembled);
                }
                else if (!string.IsNullOrEmpty(chunkError))
                {
                    Interlocked.Increment(ref chunkFailures);
                }

                return;
            }

            if (isHostEndpoint)
            {
                if (hostPeer == null
                    || !ValidateAndBindHostEnvelope(
                        hostPeer,
                        decoded))
                {
                    Interlocked.Increment(ref peerBindingFailures);
                    return;
                }
            }

            EnqueueReceivedEnvelope(decoded);
        }

        private bool ValidateHostChunkEnvelope(
            HostUdpPeerSession peer,
            TransportEnvelope envelope)
        {
            peer.LastSeenUtc = DateTime.UtcNow;
            lock (hostPeerLock)
            {
                if (!MultiplayerPeerIds.TryParseClientSlot(
                        envelope.SenderId,
                        out _))
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(peer.PeerId))
                {
                    return string.Equals(
                        peer.PeerId,
                        envelope.SenderId,
                        StringComparison.Ordinal);
                }

                if (hostPeersById.TryGetValue(
                        envelope.SenderId,
                        out var existing)
                    && !ReferenceEquals(existing, peer)
                    && !existing.Closed)
                {
                    return false;
                }

                return true;
            }
        }

        private bool ValidateAndBindHostEnvelope(
            HostUdpPeerSession peer,
            TransportEnvelope envelope)
        {
            peer.LastSeenUtc = DateTime.UtcNow;
            lock (hostPeerLock)
            {
                if (string.IsNullOrEmpty(peer.PeerId))
                {
                    if (envelope.Kind
                            != TransportMessageKind.ReplicationHello
                        || !MultiplayerPeerIds.TryParseClientSlot(
                            envelope.SenderId,
                            out _))
                    {
                        return false;
                    }

                    if (hostPeersById.TryGetValue(
                            envelope.SenderId,
                            out var existing)
                        && !ReferenceEquals(existing, peer)
                        && !existing.Closed)
                    {
                        return false;
                    }

                    peer.PeerId = envelope.SenderId;
                    hostPeersById[peer.PeerId] = peer;
                    return true;
                }

                return string.Equals(
                    peer.PeerId,
                    envelope.SenderId,
                    StringComparison.Ordinal);
            }
        }

        private void EnqueueReceivedEnvelope(
            TransportEnvelope envelope)
        {
            lock (latestStateLock)
            {
                switch (envelope.Kind)
                {
                    case TransportMessageKind.ReplicationTransformSnapshot:
                        if (latestTransformSnapshot != null)
                        {
                            Interlocked.Increment(
                                ref coalescedStateReplacements);
                        }

                        latestTransformSnapshot = envelope;
                        return;

                    case TransportMessageKind.ReplicationPlayerPresence:
                        ReplaceLatestStateBySender(
                            latestPlayerPresenceBySender,
                            envelope);
                        return;

                    case TransportMessageKind.ReplicationPlayerSelection:
                        ReplaceLatestStateBySender(
                            latestPlayerSelectionBySender,
                            envelope);
                        return;
                }
            }

            inbox.Enqueue(envelope);
        }

        private void ReplaceLatestStateBySender(
            Dictionary<string, TransportEnvelope> states,
            TransportEnvelope envelope)
        {
            if (states.ContainsKey(envelope.SenderId))
            {
                Interlocked.Increment(
                    ref coalescedStateReplacements);
            }

            states[envelope.SenderId] = envelope;
        }

        private HostUdpPeerSession GetOrCreateInsecureHostPeer(
            IPEndPoint sender)
        {
            var key = FormatEndpointKey(sender);
            lock (hostPeerLock)
            {
                if (hostPeersByEndpoint.TryGetValue(
                        key,
                        out var existing))
                {
                    return existing;
                }

                PruneHostPeerSessionsLocked();
                if (hostPeersByEndpoint.Count >= MaxHostPeerSessions)
                {
                    throw new InvalidOperationException(
                        "Too many UDP peer sessions.");
                }

                var peer = new HostUdpPeerSession(
                    CloneEndpoint(sender))
                {
                    AuthenticationEstablished = true,
                    BinarySecurityDataEnabled = false,
                    LastSeenUtc = DateTime.UtcNow
                };
                hostPeersByEndpoint[key] = peer;
                return peer;
            }
        }

        private bool TryUnwrapHostSecureDatagram(
            byte[] datagram,
            IPEndPoint sender,
            out byte[] payload,
            out HostUdpPeerSession? peer)
        {
            payload = Array.Empty<byte>();
            peer = null;
            var key = FormatEndpointKey(sender);

            if (LooksLikeSecureDataV2(datagram))
            {
                lock (hostPeerLock)
                {
                    if (!hostPeersByEndpoint.TryGetValue(
                            key,
                            out peer)
                        || !peer.AuthenticationEstablished)
                    {
                        Interlocked.Increment(
                            ref authenticationFailures);
                        return false;
                    }
                }

                return TryUnwrapHostSecureDataV2(
                    datagram,
                    peer,
                    out payload);
            }

            var line = Encoding.UTF8.GetString(datagram);
            var fields = line.Split(
                new[] { '\t' },
                StringSplitOptions.None);
            try
            {
                if (fields.Length == 3
                    && fields[0]
                        == DirectTransportSecurity.UdpClientHello
                    && AllowUnauthenticatedDatagram())
                {
                    var nonce = Convert.FromBase64String(fields[1]);
                    var tag = Convert.FromBase64String(fields[2]);
                    if (nonce.Length != 16
                        || !DirectTransportSecurity.FixedTimeEquals(
                            tag,
                            DirectTransportSecurity.Mac(
                                securityKey,
                                "UDP-C1",
                                nonce)))
                    {
                        throw new InvalidDataException();
                    }

                    lock (hostPeerLock)
                    {
                        PruneHostPeerSessionsLocked();
                        if (!hostPeersByEndpoint.TryGetValue(
                                key,
                                out peer))
                        {
                            if (hostPeersByEndpoint.Count
                                >= MaxHostPeerSessions)
                            {
                                throw new InvalidDataException(
                                    "too many peer sessions");
                            }

                            peer = new HostUdpPeerSession(
                                CloneEndpoint(sender));
                            hostPeersByEndpoint[key] = peer;
                        }

                        peer.ClientNonce = nonce;
                        peer.HostNonce =
                            DirectTransportSecurity.RandomBytes(16);
                        peer.SessionId = null;
                        peer.AuthenticationEstablished = false;
                        peer.BinarySecurityDataEnabled = false;
                        peer.ApplicationCompatible = false;
                        peer.LastSeenUtc = DateTime.UtcNow;
                    }

                    var responseTag = DirectTransportSecurity.Mac(
                        securityKey,
                        "UDP-S1",
                        peer.ClientNonce!,
                        peer.HostNonce!);
                    SendRawSecurityPacket(
                        DirectTransportSecurity.UdpServerHello
                            + "\t"
                            + fields[1]
                            + "\t"
                            + Convert.ToBase64String(peer.HostNonce!)
                            + "\t"
                            + Convert.ToBase64String(responseTag),
                        sender);
                    return false;
                }

                if (fields.Length == 4
                    && fields[0]
                        == DirectTransportSecurity.UdpClientFinish)
                {
                    lock (hostPeerLock)
                    {
                        hostPeersByEndpoint.TryGetValue(
                            key,
                            out peer);
                    }

                    if (peer == null
                        || peer.ClientNonce == null
                        || peer.HostNonce == null)
                    {
                        throw new InvalidDataException();
                    }

                    var receivedClient =
                        Convert.FromBase64String(fields[1]);
                    var receivedHost =
                        Convert.FromBase64String(fields[2]);
                    var tag = Convert.FromBase64String(fields[3]);
                    if (!DirectTransportSecurity.FixedTimeEquals(
                            receivedClient,
                            peer.ClientNonce)
                        || !DirectTransportSecurity.FixedTimeEquals(
                            receivedHost,
                            peer.HostNonce)
                        || !DirectTransportSecurity.FixedTimeEquals(
                            tag,
                            DirectTransportSecurity.Mac(
                                securityKey,
                                "UDP-C2",
                                peer.ClientNonce,
                                peer.HostNonce)))
                    {
                        throw new InvalidDataException();
                    }

                    peer.SessionId = SessionId(
                        peer.ClientNonce,
                        peer.HostNonce);
                    peer.AuthenticationEstablished = true;
                    peer.SendSequence = 0L;
                    peer.HighestReceiveSequence = 0L;
                    peer.ReceivedSequences.Clear();
                    peer.LastSeenUtc = DateTime.UtcNow;
                    return false;
                }

                if (fields.Length == 5
                    && fields[0]
                        == DirectTransportSecurity.UdpData)
                {
                    lock (hostPeerLock)
                    {
                        hostPeersByEndpoint.TryGetValue(
                            key,
                            out peer);
                    }

                    if (peer == null
                        || !peer.AuthenticationEstablished
                        || peer.SessionId == null)
                    {
                        throw new InvalidDataException();
                    }

                    var receivedSession =
                        Convert.FromBase64String(fields[1]);
                    if (!long.TryParse(
                            fields[2],
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var sequence))
                    {
                        throw new InvalidDataException();
                    }

                    var receivedPayload =
                        Convert.FromBase64String(fields[3]);
                    var tag = Convert.FromBase64String(fields[4]);
                    if (!DirectTransportSecurity.FixedTimeEquals(
                            receivedSession,
                            peer.SessionId)
                        || !DirectTransportSecurity.FixedTimeEquals(
                            tag,
                            DirectTransportSecurity.Mac(
                                securityKey,
                                "UDP-DATA",
                                peer.SessionId,
                                BitConverter.GetBytes(sequence),
                                receivedPayload))
                        || !AcceptHostReceiveSequence(
                            peer,
                            sequence))
                    {
                        throw new InvalidDataException();
                    }

                    peer.LastSeenUtc = DateTime.UtcNow;
                    payload = receivedPayload;
                    return true;
                }
            }
            catch
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }

            Interlocked.Increment(ref authenticationFailures);
            return false;
        }

        private bool TryUnwrapClientSecureDatagram(
            byte[] datagram,
            IPEndPoint sender,
            out byte[] payload)
        {
            payload = Array.Empty<byte>();
            if (remoteEndpoint == null
                || !EndpointEquals(sender, remoteEndpoint))
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }

            if (LooksLikeSecureDataV2(datagram))
            {
                return TryUnwrapClientSecureDataV2(
                    datagram,
                    out payload);
            }

            var line = Encoding.UTF8.GetString(datagram);
            var fields = line.Split(
                new[] { '\t' },
                StringSplitOptions.None);
            try
            {
                if (fields.Length == 4
                    && fields[0]
                        == DirectTransportSecurity.UdpServerHello
                    && clientNonce != null)
                {
                    var echoedClient =
                        Convert.FromBase64String(fields[1]);
                    var receivedHost =
                        Convert.FromBase64String(fields[2]);
                    var tag = Convert.FromBase64String(fields[3]);
                    if (!DirectTransportSecurity.FixedTimeEquals(
                            echoedClient,
                            clientNonce)
                        || receivedHost.Length != 16
                        || !DirectTransportSecurity.FixedTimeEquals(
                            tag,
                            DirectTransportSecurity.Mac(
                                securityKey,
                                "UDP-S1",
                                clientNonce,
                                receivedHost)))
                    {
                        throw new InvalidDataException();
                    }

                    hostNonce = receivedHost;
                    sessionId = SessionId(
                        clientNonce,
                        hostNonce);
                    var finish = DirectTransportSecurity.Mac(
                        securityKey,
                        "UDP-C2",
                        clientNonce,
                        hostNonce);
                    SendRawSecurityPacket(
                        DirectTransportSecurity.UdpClientFinish
                            + "\t"
                            + Convert.ToBase64String(clientNonce)
                            + "\t"
                            + Convert.ToBase64String(hostNonce)
                            + "\t"
                            + Convert.ToBase64String(finish),
                        sender);
                    authenticationEstablished = true;
                    return false;
                }

                if (fields.Length == 5
                    && fields[0]
                        == DirectTransportSecurity.UdpData
                    && authenticationEstablished
                    && sessionId != null)
                {
                    var receivedSession =
                        Convert.FromBase64String(fields[1]);
                    if (!long.TryParse(
                            fields[2],
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var sequence))
                    {
                        throw new InvalidDataException();
                    }

                    var receivedPayload =
                        Convert.FromBase64String(fields[3]);
                    var tag = Convert.FromBase64String(fields[4]);
                    if (!DirectTransportSecurity.FixedTimeEquals(
                            receivedSession,
                            sessionId)
                        || !DirectTransportSecurity.FixedTimeEquals(
                            tag,
                            DirectTransportSecurity.Mac(
                                securityKey,
                                "UDP-DATA",
                                sessionId,
                                BitConverter.GetBytes(sequence),
                                receivedPayload))
                        || !AcceptClientReceiveSequence(sequence))
                    {
                        throw new InvalidDataException();
                    }

                    payload = receivedPayload;
                    return true;
                }
            }
            catch
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }

            Interlocked.Increment(ref authenticationFailures);
            return false;
        }

        private static bool LooksLikeSecureDataV2(
            byte[] datagram)
        {
            if (datagram == null
                || datagram.Length
                    < SecureDataV2HeaderBytes
                        + SecureDataV2TagBytes)
            {
                return false;
            }

            for (var i = 0;
                i < SecureDataV2Magic.Length;
                i++)
            {
                if (datagram[i] != SecureDataV2Magic[i])
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryUnwrapHostSecureDataV2(
            byte[] datagram,
            HostUdpPeerSession peer,
            out byte[] payload)
        {
            payload = Array.Empty<byte>();
            if (peer.SessionId == null
                || !peer.AuthenticationEstablished)
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }

            try
            {
                if (!TryReadSecureDataV2(
                    datagram,
                    peer.SessionId,
                    out var sequence,
                    out var receivedPayload,
                    out var sequenceBytes,
                    out var lengthBytes,
                    out var tag))
                {
                    throw new InvalidDataException();
                }

                var expectedTag = DirectTransportSecurity.Mac(
                    securityKey,
                    "UDP-DATA2",
                    peer.SessionId,
                    sequenceBytes,
                    lengthBytes,
                    receivedPayload);
                if (!DirectTransportSecurity.FixedTimeEquals(
                        tag,
                        expectedTag)
                    || !AcceptHostReceiveSequence(
                        peer,
                        sequence))
                {
                    throw new InvalidDataException();
                }

                peer.LastSeenUtc = DateTime.UtcNow;
                Interlocked.Increment(
                    ref secureBinaryPacketsReceived);
                payload = receivedPayload;
                return true;
            }
            catch
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }
        }

        private bool TryUnwrapClientSecureDataV2(
            byte[] datagram,
            out byte[] payload)
        {
            payload = Array.Empty<byte>();
            if (!authenticationEstablished
                || sessionId == null)
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }

            try
            {
                if (!TryReadSecureDataV2(
                    datagram,
                    sessionId,
                    out var sequence,
                    out var receivedPayload,
                    out var sequenceBytes,
                    out var lengthBytes,
                    out var tag))
                {
                    throw new InvalidDataException();
                }

                var expectedTag = DirectTransportSecurity.Mac(
                    securityKey,
                    "UDP-DATA2",
                    sessionId,
                    sequenceBytes,
                    lengthBytes,
                    receivedPayload);
                if (!DirectTransportSecurity.FixedTimeEquals(
                        tag,
                        expectedTag)
                    || !AcceptClientReceiveSequence(sequence))
                {
                    throw new InvalidDataException();
                }

                Interlocked.Increment(
                    ref secureBinaryPacketsReceived);
                payload = receivedPayload;
                return true;
            }
            catch
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }
        }

        private static bool TryReadSecureDataV2(
            byte[] datagram,
            byte[] expectedSessionId,
            out long sequence,
            out byte[] payload,
            out byte[] sequenceBytes,
            out byte[] lengthBytes,
            out byte[] tag)
        {
            sequence = 0L;
            payload = Array.Empty<byte>();
            sequenceBytes = Array.Empty<byte>();
            lengthBytes = Array.Empty<byte>();
            tag = Array.Empty<byte>();
            if (datagram.Length
                < SecureDataV2HeaderBytes
                    + SecureDataV2TagBytes)
            {
                return false;
            }

            var receivedSession = new byte[16];
            sequenceBytes = new byte[8];
            lengthBytes = new byte[4];
            Buffer.BlockCopy(
                datagram,
                4,
                receivedSession,
                0,
                receivedSession.Length);
            Buffer.BlockCopy(
                datagram,
                20,
                sequenceBytes,
                0,
                sequenceBytes.Length);
            Buffer.BlockCopy(
                datagram,
                28,
                lengthBytes,
                0,
                lengthBytes.Length);
            if (!DirectTransportSecurity.FixedTimeEquals(
                    receivedSession,
                    expectedSessionId))
            {
                return false;
            }

            sequence = BitConverter.ToInt64(
                sequenceBytes,
                0);
            var payloadLength = BitConverter.ToInt32(
                lengthBytes,
                0);
            if (payloadLength < 0
                || payloadLength > MaxSecureDataV2PayloadBytes
                || datagram.Length
                    != SecureDataV2HeaderBytes
                        + payloadLength
                        + SecureDataV2TagBytes)
            {
                return false;
            }

            payload = new byte[payloadLength];
            tag = new byte[SecureDataV2TagBytes];
            Buffer.BlockCopy(
                datagram,
                SecureDataV2HeaderBytes,
                payload,
                0,
                payloadLength);
            Buffer.BlockCopy(
                datagram,
                SecureDataV2HeaderBytes + payloadLength,
                tag,
                0,
                tag.Length);
            return true;
        }

        private void SendPayloadToClient(
            byte[] payload,
            IPEndPoint target)
        {
            if (!securityEnabled)
            {
                SendDatagram(payload, target);
                return;
            }

            if (!authenticationEstablished
                || sessionId == null)
            {
                return;
            }

            var sequence =
                Interlocked.Increment(ref sendSecuritySequence);
            SendSecuredPayload(
                payload,
                target,
                sessionId,
                sequence,
                binarySecurityDataEnabled);
        }

        private void SendPayloadToHostPeer(
            byte[] payload,
            HostUdpPeerSession peer)
        {
            if (!securityEnabled)
            {
                SendDatagram(payload, peer.Endpoint);
                return;
            }

            if (!peer.AuthenticationEstablished
                || peer.SessionId == null)
            {
                return;
            }

            long sequence;
            lock (peer.SequenceLock)
            {
                sequence = ++peer.SendSequence;
            }

            SendSecuredPayload(
                payload,
                peer.Endpoint,
                peer.SessionId,
                sequence,
                peer.BinarySecurityDataEnabled);
        }

        private void SendSecuredPayload(
            byte[] payload,
            IPEndPoint target,
            byte[] activeSessionId,
            long sequence,
            bool binary)
        {
            var sequenceBytes = BitConverter.GetBytes(sequence);
            if (binary)
            {
                var lengthBytes = BitConverter.GetBytes(payload.Length);
                var tagV2 = DirectTransportSecurity.Mac(
                    securityKey,
                    "UDP-DATA2",
                    activeSessionId,
                    sequenceBytes,
                    lengthBytes,
                    payload);
                var packetV2 = new byte[
                    SecureDataV2HeaderBytes
                    + payload.Length
                    + SecureDataV2TagBytes];
                Buffer.BlockCopy(
                    SecureDataV2Magic,
                    0,
                    packetV2,
                    0,
                    SecureDataV2Magic.Length);
                Buffer.BlockCopy(
                    activeSessionId,
                    0,
                    packetV2,
                    4,
                    16);
                Buffer.BlockCopy(
                    sequenceBytes,
                    0,
                    packetV2,
                    20,
                    8);
                Buffer.BlockCopy(
                    lengthBytes,
                    0,
                    packetV2,
                    28,
                    4);
                Buffer.BlockCopy(
                    payload,
                    0,
                    packetV2,
                    SecureDataV2HeaderBytes,
                    payload.Length);
                Buffer.BlockCopy(
                    tagV2,
                    0,
                    packetV2,
                    SecureDataV2HeaderBytes + payload.Length,
                    tagV2.Length);
                Interlocked.Increment(
                    ref secureBinaryPacketsSent);
                SendDatagram(packetV2, target);
                return;
            }

            var tag = DirectTransportSecurity.Mac(
                securityKey,
                "UDP-DATA",
                activeSessionId,
                sequenceBytes,
                payload);
            var packet = DirectTransportSecurity.UdpData
                + "\t"
                + Convert.ToBase64String(activeSessionId)
                + "\t"
                + sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + "\t"
                + Convert.ToBase64String(payload)
                + "\t"
                + Convert.ToBase64String(tag);
            SendDatagram(
                Encoding.UTF8.GetBytes(packet),
                target);
        }

        private void SendClientHelloIfDue()
        {
            if (!securityEnabled
                || isHostEndpoint
                || authenticationEstablished
                || udpClient == null
                || remoteEndpoint == null
                || clientNonce == null)
            {
                return;
            }

            if (DateTime.UtcNow < nextClientHelloUtc)
            {
                return;
            }

            nextClientHelloUtc =
                DateTime.UtcNow.AddSeconds(1);
            var tag = DirectTransportSecurity.Mac(
                securityKey,
                "UDP-C1",
                clientNonce);
            SendRawSecurityPacket(
                DirectTransportSecurity.UdpClientHello
                    + "\t"
                    + Convert.ToBase64String(clientNonce)
                    + "\t"
                    + Convert.ToBase64String(tag),
                remoteEndpoint);
        }

        private bool AcceptClientReceiveSequence(
            long sequence)
        {
            return AcceptReceiveSequence(
                sequence,
                ref highestReceiveSecuritySequence,
                receivedSecuritySequences);
        }

        private static bool AcceptHostReceiveSequence(
            HostUdpPeerSession peer,
            long sequence)
        {
            lock (peer.SequenceLock)
            {
                return AcceptReceiveSequence(
                    sequence,
                    ref peer.HighestReceiveSequence,
                    peer.ReceivedSequences);
            }
        }

        private static bool AcceptReceiveSequence(
            long sequence,
            ref long highestSequence,
            HashSet<long> receivedSequences)
        {
            if (sequence <= 0
                || sequence <= highestSequence - 2048
                || receivedSequences.Contains(sequence))
            {
                return false;
            }

            receivedSequences.Add(sequence);
            if (sequence > highestSequence)
            {
                highestSequence = sequence;
            }

            if (receivedSequences.Count > 4096)
            {
                var cutoff = highestSequence - 2048;
                receivedSequences.RemoveWhere(
                    value => value <= cutoff);
            }

            return true;
        }

        private bool AllowUnauthenticatedDatagram()
        {
            var now = DateTime.UtcNow;
            if ((now - unauthenticatedWindowUtc).TotalSeconds >= 1)
            {
                unauthenticatedWindowUtc = now;
                unauthenticatedDatagramsThisWindow = 0;
            }

            return ++unauthenticatedDatagramsThisWindow <= 128;
        }

        private void PruneHostPeerSessionsLocked()
        {
            var now = DateTime.UtcNow;
            var remove = new List<string>();
            foreach (var pair in hostPeersByEndpoint)
            {
                var peer = pair.Value;
                if (peer.Closed
                    || (string.IsNullOrEmpty(peer.PeerId)
                        && now - peer.LastSeenUtc
                            > TimeSpan.FromSeconds(15))
                    || (now - peer.LastSeenUtc
                        > TimeSpan.FromMinutes(2)))
                {
                    remove.Add(pair.Key);
                }
            }

            for (var i = 0; i < remove.Count; i++)
            {
                if (!hostPeersByEndpoint.TryGetValue(
                        remove[i],
                        out var peer))
                {
                    continue;
                }

                hostPeersByEndpoint.Remove(remove[i]);
                if (!string.IsNullOrEmpty(peer.PeerId)
                    && hostPeersById.TryGetValue(
                        peer.PeerId,
                        out var indexed)
                    && ReferenceEquals(indexed, peer))
                {
                    hostPeersById.Remove(peer.PeerId);
                }

                peer.Closed = true;
            }
        }

        private void SendRawSecurityPacket(
            string packet,
            IPEndPoint target)
        {
            SendDatagram(
                Encoding.UTF8.GetBytes(packet),
                target);
        }

        private void SendDatagram(
            byte[] bytes,
            IPEndPoint target)
        {
            var client = udpClient;
            if (client == null)
            {
                return;
            }

            int sent;
            lock (sendLock)
            {
                client = udpClient;
                if (client == null)
                {
                    return;
                }

                sent = client.Send(
                    bytes,
                    bytes.Length,
                    target);
            }

            Interlocked.Increment(ref datagramsSent);
            Interlocked.Add(ref bytesSent, sent);
        }

        private static byte[] SessionId(
            byte[] client,
            byte[] host)
        {
            using (var sha = SHA256.Create())
            {
                var combined =
                    new byte[client.Length + host.Length];
                Buffer.BlockCopy(
                    client,
                    0,
                    combined,
                    0,
                    client.Length);
                Buffer.BlockCopy(
                    host,
                    0,
                    combined,
                    client.Length,
                    host.Length);
                var full = sha.ComputeHash(combined);
                var result = new byte[16];
                Buffer.BlockCopy(
                    full,
                    0,
                    result,
                    0,
                    result.Length);
                return result;
            }
        }

        private static string FormatEndpointKey(
            IPEndPoint endpoint)
        {
            return endpoint.Address
                + ":"
                + endpoint.Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        private static IPEndPoint CloneEndpoint(
            IPEndPoint endpoint)
        {
            return new IPEndPoint(
                endpoint.Address,
                endpoint.Port);
        }

        private static bool EndpointEquals(
            IPEndPoint left,
            IPEndPoint right)
        {
            return left.Port == right.Port
                && left.Address.Equals(right.Address);
        }

        private static IPAddress ResolveHost(
            string host)
        {
            if (IPAddress.TryParse(
                    host,
                    out var address))
            {
                return address;
            }

            var addresses =
                Dns.GetHostAddresses(host);
            for (var i = 0;
                i < addresses.Length;
                i++)
            {
                if (addresses[i].AddressFamily
                    == AddressFamily.InterNetwork)
                {
                    return addresses[i];
                }
            }

            if (addresses.Length > 0)
            {
                return addresses[0];
            }

            throw new InvalidOperationException(
                "Could not resolve host: " + host);
        }

        private sealed class OutgoingTransportItem
        {
            public OutgoingTransportItem(
                TransportEnvelope envelope,
                string? targetPeerId,
                string? excludedPeerId)
            {
                Envelope = envelope;
                TargetPeerId = targetPeerId;
                ExcludedPeerId = excludedPeerId;
            }

            public TransportEnvelope Envelope { get; }
            public string? TargetPeerId { get; }
            public string? ExcludedPeerId { get; }
        }

        private sealed class HostUdpPeerSession
        {
            public HostUdpPeerSession(
                IPEndPoint endpoint)
            {
                Endpoint = endpoint;
                LastSeenUtc = DateTime.UtcNow;
            }

            public IPEndPoint Endpoint { get; }
            public string PeerId { get; set; } = string.Empty;
            public byte[]? ClientNonce { get; set; }
            public byte[]? HostNonce { get; set; }
            public byte[]? SessionId { get; set; }
            public bool AuthenticationEstablished { get; set; }
            public bool BinarySecurityDataEnabled { get; set; }
            public bool ApplicationCompatible { get; set; }
            public bool Closed { get; set; }
            public long SendSequence;
            public long HighestReceiveSequence;
            public HashSet<long> ReceivedSequences { get; } =
                new HashSet<long>();
            public TransportChunkReassembler ChunkReassembler { get; } =
                new TransportChunkReassembler();
            public object SequenceLock { get; } = new object();
            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class ReceiveState
        {
            public ReceiveState(
                UdpClient client,
                int generation)
            {
                Client = client;
                Generation = generation;
            }

            public UdpClient Client { get; }
            public int Generation { get; }
        }
    }
}
