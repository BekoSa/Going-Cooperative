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
        private readonly ConcurrentQueue<TransportEnvelope> outbox =
            new ConcurrentQueue<TransportEnvelope>();
        private readonly TransportChunkReassembler chunkReassembler =
            new TransportChunkReassembler();
        private readonly object latestStateLock = new object();
        private readonly object receiveStateLock = new object();
        private readonly object sendLock = new object();

        private UdpClient? udpClient;
        private IPEndPoint? remoteEndpoint;
        private bool isHostEndpoint;
        private long nextChunkId;
        private readonly bool securityEnabled;
        private readonly byte[] securityKey;
        private byte[]? clientNonce;
        private byte[]? hostNonce;
        private byte[]? sessionId;
        private IPEndPoint? pendingEndpoint;
        private long sendSecuritySequence;
        private long highestReceiveSecuritySequence;
        private readonly HashSet<long> receivedSecuritySequences = new HashSet<long>();
        private DateTime nextClientHelloUtc;
        private int unauthenticatedDatagramsThisWindow;
        private DateTime unauthenticatedWindowUtc;
        private volatile bool binarySecurityDataEnabled;
        private volatile bool isConnected;
        private volatile bool authenticationEstablished;
        private int receiveGeneration;
        private int receivePending;
        private int sendWorkerActive;

        // High-frequency state never belongs in the reliable FIFO backlog. Only the
        // newest value matters and replacing an older one reduces latency and GC.
        private TransportEnvelope? latestTransformSnapshot;
        private TransportEnvelope? latestPlayerPresence;
        private TransportEnvelope? latestPlayerSelection;
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

        private const int MaxUnchunkedDatagramBytes = 1100;
        private const int SecureDataV2HeaderBytes = 32;
        private const int SecureDataV2TagBytes = 32;
        private const int MaxSecureDataV2PayloadBytes = 60 * 1024;
        private static readonly byte[] SecureDataV2Magic =
            { (byte)'G', (byte)'C', (byte)'D', (byte)'2' };
        private const int MaxChunkEnvelopeChars = 700;

        public bool IsConnected
        {
            get { return isConnected; }
        }

        public bool AuthenticationEstablished
        {
            get { return authenticationEstablished; }
        }

        public long AuthenticationFailures
        {
            get { return Interlocked.Read(ref authenticationFailures); }
        }

        /// <summary>Datagrams that failed envelope decode and were silently dropped.</summary>
        public long DecodeFailures
        {
            get { return Interlocked.Read(ref decodeFailures); }
        }

        /// <summary>Chunk datagrams that failed reassembly and were dropped.</summary>
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

        public int PendingMessages
        {
            get
            {
                var pending = inbox.Count;
                lock (latestStateLock)
                {
                    if (latestTransformSnapshot != null) pending++;
                    if (latestPlayerPresence != null) pending++;
                    if (latestPlayerSelection != null) pending++;
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
            get { return remoteEndpoint != null; }
        }

        public UdpNetworkTransport()
            : this(false, string.Empty)
        {
        }

        public UdpNetworkTransport(bool securityEnabled, string sessionCode)
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
                securityKey = new byte[0];
                authenticationEstablished = true;
            }
        }

        public void StartHost(int port)
        {
            Stop();
            udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            isHostEndpoint = true;
            isConnected = true;
            authenticationEstablished = !securityEnabled;
            StartReceiveLoop();
        }

        public void Connect(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host is required.", nameof(host));
            }

            Stop();
            udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            remoteEndpoint = new IPEndPoint(ResolveHost(host), port);
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
            if (securityEnabled && authenticationEstablished)
            {
                binarySecurityDataEnabled = true;
            }
        }

        public void Send(TransportEnvelope envelope)
        {
            if (!isConnected || udpClient == null)
            {
                throw new InvalidOperationException("Transport is not connected.");
            }

            if (remoteEndpoint == null)
            {
                throw new InvalidOperationException(
                    isHostEndpoint
                        ? "Host has no remote endpoint yet."
                        : "Client has no host endpoint.");
            }

            envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            EnqueueOutgoingEnvelope(envelope);
            ScheduleSendWorker();
        }

        private void EnqueueOutgoingEnvelope(TransportEnvelope envelope)
        {
            switch (envelope.Kind)
            {
                case TransportMessageKind.ReplicationTransformSnapshot:
                    ReplaceLatestOutgoingState(
                        ref latestOutgoingTransformSnapshot,
                        envelope);
                    return;
                case TransportMessageKind.ReplicationPlayerPresence:
                    ReplaceLatestOutgoingState(
                        ref latestOutgoingPlayerPresence,
                        envelope);
                    return;
                case TransportMessageKind.ReplicationPlayerSelection:
                    ReplaceLatestOutgoingState(
                        ref latestOutgoingPlayerSelection,
                        envelope);
                    return;
                default:
                    outbox.Enqueue(envelope);
                    return;
            }
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
                    if (!TryDequeueOutgoingEnvelope(out var envelope))
                    {
                        break;
                    }

                    try
                    {
                        SendEnvelopeImmediate(envelope);
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
                    // If this worker belongs to an old session generation, this
                    // schedules a fresh worker for the current one without allowing
                    // the old worker to send new-session envelopes.
                    ScheduleSendWorker();
                }
            }
        }

        private bool TryDequeueOutgoingEnvelope(
            out TransportEnvelope envelope)
        {
            if (outbox.TryDequeue(out var queued))
            {
                envelope = queued;
                return true;
            }

            lock (latestStateLock)
            {
                if (latestOutgoingPlayerPresence != null)
                {
                    envelope = latestOutgoingPlayerPresence;
                    latestOutgoingPlayerPresence = null;
                    return true;
                }

                if (latestOutgoingPlayerSelection != null)
                {
                    envelope = latestOutgoingPlayerSelection;
                    latestOutgoingPlayerSelection = null;
                    return true;
                }

                if (latestOutgoingTransformSnapshot != null)
                {
                    envelope = latestOutgoingTransformSnapshot;
                    latestOutgoingTransformSnapshot = null;
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
            TransportEnvelope envelope)
        {
            var target = remoteEndpoint;
            if (!isConnected || target == null)
            {
                return;
            }

            var encoded = TransportEnvelopeCodec.Encode(envelope);
            var bytes = Encoding.UTF8.GetBytes(encoded);
            var useLegacySecureDataFrame =
                securityEnabled && !binarySecurityDataEnabled;
            var maxUnchunkedBytes =
                useLegacySecureDataFrame
                    ? 850
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
                        ? 450
                        : MaxChunkEnvelopeChars);
                Interlocked.Add(ref chunkEnvelopesSent, chunks.Count);
                for (var i = 0; i < chunks.Count; i++)
                {
                    SendEncodedEnvelope(chunks[i], target);
                }

                return;
            }

            SendPayload(bytes, target);
        }

        public bool TryReceive(out TransportEnvelope envelope)
        {
            if (securityEnabled
                && !isHostEndpoint
                && !authenticationEstablished)
            {
                SendClientHelloIfDue();
            }

            if (inbox.TryDequeue(out var queued))
            {
                envelope = queued;
                return true;
            }

            lock (latestStateLock)
            {
                // Cursor/selection are tiny user-presence state. Prefer them ahead of
                // transform presentation once the reliable FIFO has been drained.
                if (latestPlayerPresence != null)
                {
                    envelope = latestPlayerPresence;
                    latestPlayerPresence = null;
                    return true;
                }

                if (latestPlayerSelection != null)
                {
                    envelope = latestPlayerSelection;
                    latestPlayerSelection = null;
                    return true;
                }

                if (latestTransformSnapshot != null)
                {
                    envelope = latestTransformSnapshot;
                    latestTransformSnapshot = null;
                    return true;
                }
            }

            envelope = new TransportEnvelope(
                TransportMessageKind.ReplicationHello,
                0,
                string.Empty,
                string.Empty);
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
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }

            while (inbox.TryDequeue(out _))
            {
            }

            while (outbox.TryDequeue(out _))
            {
            }

            lock (latestStateLock)
            {
                latestTransformSnapshot = null;
                latestPlayerPresence = null;
                latestPlayerSelection = null;
                latestOutgoingTransformSnapshot = null;
                latestOutgoingPlayerPresence = null;
                latestOutgoingPlayerSelection = null;
            }

            lock (receiveStateLock)
            {
                remoteEndpoint = null;
                chunkReassembler.Clear();
                authenticationEstablished = !securityEnabled;
                clientNonce = hostNonce = sessionId = null;
                pendingEndpoint = null;
                binarySecurityDataEnabled = false;
                sendSecuritySequence = highestReceiveSecuritySequence = 0;
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
        }

        private void StartReceiveLoop()
        {
            var client = udpClient;
            if (!isConnected || client == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref receivePending, 1, 0) != 0)
            {
                return;
            }

            var state = new ReceiveState(
                client,
                Volatile.Read(ref receiveGeneration));
            try
            {
                client.BeginReceive(ReceiveCompleted, state);
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
                bytes = state.Client.EndReceive(asyncResult, ref sender);
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
                || state.Generation != Volatile.Read(ref receiveGeneration))
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
                            ProcessReceivedDatagram(bytes, sender);
                        }
                        catch
                        {
                            // The receive worker must survive malformed datagrams and
                            // decoder bugs. Protocol paths maintain specific counters.
                            Interlocked.Increment(ref decodeFailures);
                        }
                    }
                }
            }

            StartReceiveLoop();
        }

        private void ProcessReceivedDatagram(byte[] bytes, IPEndPoint sender)
        {
            Interlocked.Increment(ref datagramsReceived);
            Interlocked.Add(ref bytesReceived, bytes.Length);

            if (!securityEnabled && isHostEndpoint)
            {
                remoteEndpoint = sender;
            }

            if (securityEnabled
                && !TryUnwrapSecureDatagram(bytes, sender, out bytes))
            {
                return;
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
                if (chunkReassembler.TryAddChunk(
                        decoded,
                        out var reassembled,
                        out var chunkError)
                    && reassembled != null)
                {
                    Interlocked.Increment(ref reassembledMessages);
                    EnqueueReceivedEnvelope(reassembled);
                }
                else if (!string.IsNullOrEmpty(chunkError))
                {
                    Interlocked.Increment(ref chunkFailures);
                }

                return;
            }

            EnqueueReceivedEnvelope(decoded);
        }

        private void EnqueueReceivedEnvelope(TransportEnvelope envelope)
        {
            switch (envelope.Kind)
            {
                case TransportMessageKind.ReplicationTransformSnapshot:
                    ReplaceLatestState(ref latestTransformSnapshot, envelope);
                    return;
                case TransportMessageKind.ReplicationPlayerPresence:
                    ReplaceLatestState(ref latestPlayerPresence, envelope);
                    return;
                case TransportMessageKind.ReplicationPlayerSelection:
                    ReplaceLatestState(ref latestPlayerSelection, envelope);
                    return;
                default:
                    inbox.Enqueue(envelope);
                    return;
            }
        }

        private void ReplaceLatestState(
            ref TransportEnvelope? slot,
            TransportEnvelope envelope)
        {
            lock (latestStateLock)
            {
                if (slot != null)
                {
                    Interlocked.Increment(ref coalescedStateReplacements);
                }

                slot = envelope;
            }
        }

        private void SendEncodedEnvelope(
            TransportEnvelope envelope,
            IPEndPoint target)
        {
            var encoded = TransportEnvelopeCodec.Encode(envelope);
            var bytes = Encoding.UTF8.GetBytes(encoded);
            SendPayload(bytes, target);
        }

        private void SendPayload(byte[] payload, IPEndPoint target)
        {
            if (udpClient == null)
            {
                return;
            }

            if (!securityEnabled)
            {
                SendDatagram(payload, target);
                return;
            }

            if (!authenticationEstablished || sessionId == null)
            {
                return;
            }

            var sequence = Interlocked.Increment(ref sendSecuritySequence);
            var sequenceBytes = BitConverter.GetBytes(sequence);
            if (binarySecurityDataEnabled)
            {
                var lengthBytes = BitConverter.GetBytes(payload.Length);
                var tagV2 = DirectTransportSecurity.Mac(
                    securityKey,
                    "UDP-DATA2",
                    sessionId,
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
                Buffer.BlockCopy(sessionId, 0, packetV2, 4, 16);
                Buffer.BlockCopy(sequenceBytes, 0, packetV2, 20, 8);
                Buffer.BlockCopy(lengthBytes, 0, packetV2, 28, 4);
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
                Interlocked.Increment(ref secureBinaryPacketsSent);
                SendDatagram(packetV2, target);
                return;
            }

            var tag = DirectTransportSecurity.Mac(
                securityKey,
                "UDP-DATA",
                sessionId,
                sequenceBytes,
                payload);
            var packet = DirectTransportSecurity.UdpData
                + "\t"
                + Convert.ToBase64String(sessionId)
                + "\t"
                + sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + "\t"
                + Convert.ToBase64String(payload)
                + "\t"
                + Convert.ToBase64String(tag);
            SendDatagram(Encoding.UTF8.GetBytes(packet), target);
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

            nextClientHelloUtc = DateTime.UtcNow.AddSeconds(1);
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

        private bool TryUnwrapSecureDatagram(
            byte[] datagram,
            IPEndPoint sender,
            out byte[] payload)
        {
            payload = new byte[0];
            if (authenticationEstablished
                && remoteEndpoint != null
                && !EndpointEquals(sender, remoteEndpoint))
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }

            if (LooksLikeSecureDataV2(datagram))
            {
                return TryUnwrapSecureDataV2(datagram, out payload);
            }

            var line = Encoding.UTF8.GetString(datagram);
            var fields = line.Split('\t');
            try
            {
                if (fields.Length == 3
                    && fields[0] == DirectTransportSecurity.UdpClientHello
                    && isHostEndpoint
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

                    clientNonce = nonce;
                    hostNonce = DirectTransportSecurity.RandomBytes(16);
                    pendingEndpoint = sender;
                    var responseTag = DirectTransportSecurity.Mac(
                        securityKey,
                        "UDP-S1",
                        clientNonce,
                        hostNonce);
                    SendRawSecurityPacket(
                        DirectTransportSecurity.UdpServerHello
                            + "\t"
                            + fields[1]
                            + "\t"
                            + Convert.ToBase64String(hostNonce)
                            + "\t"
                            + Convert.ToBase64String(responseTag),
                        sender);
                    return false;
                }

                if (fields.Length == 4
                    && fields[0] == DirectTransportSecurity.UdpServerHello
                    && !isHostEndpoint
                    && clientNonce != null
                    && remoteEndpoint != null
                    && EndpointEquals(sender, remoteEndpoint))
                {
                    var echoedClient = Convert.FromBase64String(fields[1]);
                    var receivedHost = Convert.FromBase64String(fields[2]);
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
                    sessionId = SessionId(clientNonce, hostNonce);
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

                if (fields.Length == 4
                    && fields[0] == DirectTransportSecurity.UdpClientFinish
                    && isHostEndpoint
                    && pendingEndpoint != null
                    && EndpointEquals(sender, pendingEndpoint)
                    && clientNonce != null
                    && hostNonce != null)
                {
                    var receivedClient = Convert.FromBase64String(fields[1]);
                    var receivedHost = Convert.FromBase64String(fields[2]);
                    var tag = Convert.FromBase64String(fields[3]);
                    if (!DirectTransportSecurity.FixedTimeEquals(
                            receivedClient,
                            clientNonce)
                        || !DirectTransportSecurity.FixedTimeEquals(
                            receivedHost,
                            hostNonce)
                        || !DirectTransportSecurity.FixedTimeEquals(
                            tag,
                            DirectTransportSecurity.Mac(
                                securityKey,
                                "UDP-C2",
                                clientNonce,
                                hostNonce)))
                    {
                        throw new InvalidDataException();
                    }

                    remoteEndpoint = sender;
                    sessionId = SessionId(clientNonce, hostNonce);
                    authenticationEstablished = true;
                    pendingEndpoint = null;
                    return false;
                }

                if (fields.Length == 5
                    && fields[0] == DirectTransportSecurity.UdpData
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
                        || !AcceptReceiveSequence(sequence))
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

        private static bool LooksLikeSecureDataV2(byte[] datagram)
        {
            if (datagram == null
                || datagram.Length
                    < SecureDataV2HeaderBytes + SecureDataV2TagBytes)
            {
                return false;
            }

            for (var i = 0; i < SecureDataV2Magic.Length; i++)
            {
                if (datagram[i] != SecureDataV2Magic[i])
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryUnwrapSecureDataV2(
            byte[] datagram,
            out byte[] payload)
        {
            payload = new byte[0];
            if (!authenticationEstablished || sessionId == null)
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }

            try
            {
                var receivedSession = new byte[16];
                var sequenceBytes = new byte[8];
                var lengthBytes = new byte[4];
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

                var sequence = BitConverter.ToInt64(sequenceBytes, 0);
                var payloadLength = BitConverter.ToInt32(lengthBytes, 0);
                if (payloadLength < 0
                    || payloadLength > MaxSecureDataV2PayloadBytes)
                {
                    throw new InvalidDataException();
                }

                var expectedPacketLength =
                    SecureDataV2HeaderBytes
                    + payloadLength
                    + SecureDataV2TagBytes;
                if (datagram.Length != expectedPacketLength)
                {
                    throw new InvalidDataException();
                }

                var receivedPayload = new byte[payloadLength];
                var tag = new byte[SecureDataV2TagBytes];
                Buffer.BlockCopy(
                    datagram,
                    SecureDataV2HeaderBytes,
                    receivedPayload,
                    0,
                    receivedPayload.Length);
                Buffer.BlockCopy(
                    datagram,
                    SecureDataV2HeaderBytes + receivedPayload.Length,
                    tag,
                    0,
                    tag.Length);

                var expectedTag = DirectTransportSecurity.Mac(
                    securityKey,
                    "UDP-DATA2",
                    sessionId,
                    sequenceBytes,
                    lengthBytes,
                    receivedPayload);
                if (!DirectTransportSecurity.FixedTimeEquals(
                        receivedSession,
                        sessionId)
                    || !DirectTransportSecurity.FixedTimeEquals(
                        tag,
                        expectedTag)
                    || !AcceptReceiveSequence(sequence))
                {
                    throw new InvalidDataException();
                }

                Interlocked.Increment(ref secureBinaryPacketsReceived);
                payload = receivedPayload;
                return true;
            }
            catch
            {
                Interlocked.Increment(ref authenticationFailures);
                return false;
            }
        }

        private bool AcceptReceiveSequence(long sequence)
        {
            if (sequence <= 0
                || sequence <= highestReceiveSecuritySequence - 2048
                || receivedSecuritySequences.Contains(sequence))
            {
                return false;
            }

            receivedSecuritySequences.Add(sequence);
            if (sequence > highestReceiveSecuritySequence)
            {
                highestReceiveSecuritySequence = sequence;
            }

            if (receivedSecuritySequences.Count > 4096)
            {
                receivedSecuritySequences.RemoveWhere(
                    value =>
                        value <= highestReceiveSecuritySequence - 2048);
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

        private void SendRawSecurityPacket(
            string packet,
            IPEndPoint target)
        {
            SendDatagram(Encoding.UTF8.GetBytes(packet), target);
        }

        private void SendDatagram(byte[] bytes, IPEndPoint target)
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

                sent = client.Send(bytes, bytes.Length, target);
            }

            Interlocked.Increment(ref datagramsSent);
            Interlocked.Add(ref bytesSent, sent);
        }

        private static byte[] SessionId(byte[] client, byte[] host)
        {
            using (var sha = SHA256.Create())
            {
                var combined = new byte[client.Length + host.Length];
                Buffer.BlockCopy(client, 0, combined, 0, client.Length);
                Buffer.BlockCopy(
                    host,
                    0,
                    combined,
                    client.Length,
                    host.Length);
                var full = sha.ComputeHash(combined);
                var result = new byte[16];
                Buffer.BlockCopy(full, 0, result, 0, result.Length);
                return result;
            }
        }

        private static bool EndpointEquals(
            IPEndPoint left,
            IPEndPoint right)
        {
            return left.Port == right.Port
                && left.Address.Equals(right.Address);
        }

        private static IPAddress ResolveHost(string host)
        {
            if (IPAddress.TryParse(host, out var address))
            {
                return address;
            }

            var addresses = Dns.GetHostAddresses(host);
            for (var i = 0; i < addresses.Length; i++)
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

        private sealed class ReceiveState
        {
            public ReceiveState(UdpClient client, int generation)
            {
                Client = client;
                Generation = generation;
            }

            public UdpClient Client { get; }

            public int Generation { get; }
        }
    }
}
