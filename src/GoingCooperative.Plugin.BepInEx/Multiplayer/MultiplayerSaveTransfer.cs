using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GoingCooperative.Core;
using NSMedieval;

namespace GoingCooperative.Plugin.BepInEx
{
    internal sealed class MultiplayerSaveTransfer : IDisposable
    {
        private const string Magic = "GOING_COOPERATIVE_CONTROL_V5";
        private readonly object stateLock = new object();
        private readonly object clientWriteLock = new object();
        private readonly Dictionary<string, HostPeerConnection> hostPeers =
            new Dictionary<string, HostPeerConnection>(StringComparer.Ordinal);
        private readonly Queue<string> pendingJoinCaptures = new Queue<string>();
        private readonly Queue<string> pendingResyncCaptures = new Queue<string>();

        private TcpListener? listener;
        private Thread? acceptWorker;
        private TcpClient? client;
        private Stream? controlStream;
        private Thread? clientWorker;
        private string saveRoot = string.Empty;
        private volatile bool stopping;
        private volatile bool hostMode;
        private volatile bool resyncCaptureRequested;
        private volatile int loadGeneration;
        private volatile int resumeGeneration;
        private volatile int epoch;
        private long sessionId;
        private int maxPlayers = MultiplayerPeerLimits.StableTargetPlayers;
        private string localNickname = MultiplayerNickname.DefaultNickname;
        private string hostNickname = MultiplayerNickname.DefaultNickname;
        private string assignedPeerId = MultiplayerPeerIds.Host;
        private string phase = "Idle";
        private string detail = "Start or join a multiplayer session.";
        private float progress;
        private string receivedSavePath = string.Empty;
        private string receivedVillageName = string.Empty;
        private Exception? failure;
        private bool directSecurityEnabled;
        private byte[] directSecurityKey = new byte[0];

        public string Phase { get { lock (stateLock) return phase; } }
        public string Detail { get { lock (stateLock) return detail; } }
        public float Progress { get { lock (stateLock) return progress; } }
        public bool TransferComplete
        {
            get
            {
                var value = Phase;
                return value == "Connected"
                    || value == "Loading"
                    || value == "Waiting for Host"
                    || value == "Playing";
            }
        }

        public int LoadGeneration { get { return loadGeneration; } }
        public int ResumeGeneration { get { return resumeGeneration; } }
        public int Epoch { get { return epoch; } }
        public long SessionId { get { return Interlocked.Read(ref sessionId); } }
        public bool ResyncCaptureRequested { get { return resyncCaptureRequested; } }
        public string ReceivedSavePath { get { lock (stateLock) return receivedSavePath; } }
        public string ReceivedVillageName { get { lock (stateLock) return receivedVillageName; } }
        public Exception? Failure { get { return failure; } }
        public string AssignedPeerId { get { lock (stateLock) return assignedPeerId; } }
        public string HostNickname { get { lock (stateLock) return hostNickname; } }
        public int MaxPlayers { get { lock (stateLock) return maxPlayers; } }

        public int ConnectedPeerCount
        {
            get
            {
                lock (stateLock)
                {
                    if (!hostMode)
                    {
                        return string.IsNullOrEmpty(assignedPeerId) ? 0 : 2;
                    }

                    var count = 1;
                    foreach (var peer in hostPeers.Values)
                    {
                        if (!peer.Closed)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public IReadOnlyList<MultiplayerTransferPeerSnapshot> GetPeerSnapshots()
        {
            lock (stateLock)
            {
                var result = new List<MultiplayerTransferPeerSnapshot>();
                if (hostMode)
                {
                    result.Add(new MultiplayerTransferPeerSnapshot(
                        MultiplayerPeerIds.Host,
                        localNickname,
                        "Playing",
                        true,
                        true));
                    foreach (var peer in hostPeers.Values)
                    {
                        result.Add(new MultiplayerTransferPeerSnapshot(
                            peer.PeerId,
                            peer.Nickname,
                            peer.Phase,
                            !peer.Closed,
                            peer.ReadyForReplication));
                    }
                }
                else
                {
                    result.Add(new MultiplayerTransferPeerSnapshot(
                        MultiplayerPeerIds.Host,
                        hostNickname,
                        "Host",
                        true,
                        true));
                    if (!string.IsNullOrEmpty(assignedPeerId))
                    {
                        result.Add(new MultiplayerTransferPeerSnapshot(
                            assignedPeerId,
                            localNickname,
                            phase,
                            client != null && client.Connected,
                            phase == "Playing"));
                    }
                }

                return result;
            }
        }

        public void StartHost(
            int port,
            bool securityEnabled = false,
            string sessionCode = "",
            string nickname = "",
            int requestedMaxPlayers = MultiplayerPeerLimits.StableTargetPlayers)
        {
            Stop();
            if (requestedMaxPlayers < 2
                || requestedMaxPlayers > MultiplayerPeerLimits.ExperimentalMaximumPlayers)
            {
                throw new ArgumentOutOfRangeException(nameof(requestedMaxPlayers));
            }

            Interlocked.Exchange(ref sessionId, CreateSessionId());
            hostMode = true;
            stopping = false;
            localNickname = MultiplayerNickname.Normalize(nickname);
            hostNickname = localNickname;
            assignedPeerId = MultiplayerPeerIds.Host;
            maxPlayers = requestedMaxPlayers;
            ConfigureSecurity(securityEnabled, sessionCode);
            SetState(
                "Hosting",
                "World is available. Players may join at any time.",
                1f);
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(Math.Max(4, requestedMaxPlayers * 2));
            acceptWorker = new Thread(HostAcceptLoop)
            {
                IsBackground = true,
                Name = "Going Cooperative Control Accept"
            };
            acceptWorker.Start();
        }

        public void StartClient(
            string host,
            int port,
            string clientSaveRoot,
            bool securityEnabled = false,
            string sessionCode = "",
            string nickname = "")
        {
            Stop();
            hostMode = false;
            saveRoot = clientSaveRoot;
            stopping = false;
            localNickname = MultiplayerNickname.Normalize(nickname);
            assignedPeerId = string.Empty;
            ConfigureSecurity(securityEnabled, sessionCode);
            SetState(
                "Connecting",
                "Opening the multiplayer control channel.",
                0f);
            clientWorker = new Thread(() => ClientWorker(host, port))
            {
                IsBackground = true,
                Name = "Going Cooperative Control Client"
            };
            clientWorker.Start();
        }

        public void BeginHostWorldLoad()
        {
            if (!hostMode)
            {
                throw new InvalidOperationException("Only the host can load the host world.");
            }

            loadGeneration++;
            SetState("Loading Host World", "Loading the authoritative host world.", 1f);
        }

        public bool TryDequeueJoinCapture(out string peerId)
        {
            lock (stateLock)
            {
                while (pendingJoinCaptures.Count > 0)
                {
                    var candidate = pendingJoinCaptures.Dequeue();
                    if (hostPeers.TryGetValue(candidate, out var peer)
                        && !peer.Closed
                        && !peer.ReadyForReplication)
                    {
                        peerId = candidate;
                        return true;
                    }
                }
            }

            peerId = string.Empty;
            return false;
        }

        public bool TryDequeueResyncCapture(out string peerId)
        {
            lock (stateLock)
            {
                while (pendingResyncCaptures.Count > 0)
                {
                    var candidate = pendingResyncCaptures.Dequeue();
                    if (hostPeers.TryGetValue(candidate, out var peer)
                        && !peer.Closed)
                    {
                        resyncCaptureRequested = pendingResyncCaptures.Count > 0;
                        peerId = candidate;
                        return true;
                    }
                }

                resyncCaptureRequested = false;
            }

            peerId = string.Empty;
            return false;
        }

        public void QueueJoinCheckpoint(string peerId, VillageSaveInfo save)
        {
            if (!hostMode || save == null)
            {
                throw new InvalidOperationException(
                    "Only the host can send a join checkpoint.");
            }

            var peer = GetHostPeer(peerId);
            var sendThread = new Thread(() =>
            {
                try
                {
                    SendBundle(peer, save, isResync: false);
                }
                catch (Exception ex)
                {
                    FailPeer(peer, ex);
                }
            })
            {
                IsBackground = true,
                Name = "Going Cooperative Join Sender " + peerId
            };
            sendThread.Start();
        }

        public void RejectJoin(string peerId, string reason)
        {
            if (!hostMode)
            {
                return;
            }

            HostPeerConnection? peer = null;
            lock (stateLock)
            {
                hostPeers.TryGetValue(peerId, out peer);
            }

            if (peer == null)
            {
                return;
            }

            try
            {
                SendHostCommand(peer, "JOIN_FAILED", peer.Epoch, reason);
            }
            catch
            {
            }

            peer.Phase = "Failed";
            peer.Detail = reason;
        }

        public bool RequestFullResync(out string error)
        {
            if (hostMode)
            {
                error = "Only a client can request its own full resync.";
                return false;
            }

            if (client == null
                || !client.Connected
                || resumeGeneration == 0)
            {
                error = "The multiplayer control channel is not ready.";
                return false;
            }

            if (Phase != "Playing")
            {
                error = "A load or resync operation is already in progress.";
                return false;
            }

            SetState(
                "Requesting Resync",
                "Asking the host for a fresh checkpoint.",
                0f);
            SendClientCommand("RESYNC_REQUEST", epoch);
            error = string.Empty;
            return true;
        }

        public void QueueResyncCheckpoint(
            string peerId,
            VillageSaveInfo save)
        {
            if (!hostMode || save == null)
            {
                throw new InvalidOperationException(
                    "Only the host can send a resync checkpoint.");
            }

            var peer = GetHostPeer(peerId);
            var sendThread = new Thread(() =>
            {
                try
                {
                    SendBundle(peer, save, isResync: true);
                }
                catch (Exception ex)
                {
                    FailPeer(peer, ex);
                }
            })
            {
                IsBackground = true,
                Name = "Going Cooperative Resync Sender " + peerId
            };
            sendThread.Start();
        }

        public void RejectResync(string peerId, string reason)
        {
            if (!hostMode)
            {
                return;
            }

            var peer = GetHostPeer(peerId);
            try
            {
                SendHostCommand(peer, "RESYNC_FAILED", peer.Epoch, reason);
            }
            catch
            {
            }

            peer.Phase = "Playing";
            peer.Detail = "Resync failed; peer remains in the session.";
        }

        public void NotifyNativeLoadFinished()
        {
            if (hostMode)
            {
                resumeGeneration++;
                SetState(
                    "Playing",
                    "Host world ready. Players may join at any time.",
                    1f);
                return;
            }

            SendClientCommand("LOADED", epoch);
            SetState(
                "Waiting for Host",
                "World loaded. Waiting for the host to enable replication.",
                1f);
        }

        public void ReportLoadFailure(string reason)
        {
            if (!hostMode)
            {
                try
                {
                    SendClientCommand("LOAD_FAILED", epoch, reason);
                }
                catch
                {
                }
            }

            SetState(
                "Failed",
                "Native checkpoint load failed. " + reason,
                0f);
        }

        public void Stop()
        {
            stopping = true;
            try { client?.Close(); } catch { }
            try { listener?.Stop(); } catch { }

            lock (stateLock)
            {
                foreach (var peer in hostPeers.Values)
                {
                    try { peer.Client.Close(); } catch { }
                    peer.Closed = true;
                }

                hostPeers.Clear();
                pendingJoinCaptures.Clear();
                pendingResyncCaptures.Clear();
                assignedPeerId = MultiplayerPeerIds.Host;
                hostNickname = MultiplayerNickname.DefaultNickname;
                maxPlayers = MultiplayerPeerLimits.StableTargetPlayers;
            }

            client = null;
            controlStream = null;
            listener = null;
            loadGeneration = resumeGeneration = epoch = 0;
            resyncCaptureRequested = false;
            Interlocked.Exchange(ref sessionId, 0L);
            receivedSavePath = receivedVillageName = string.Empty;
            failure = null;
            SetState("Idle", "Start or join a multiplayer session.", 0f);
        }

        public void Dispose()
        {
            Stop();
        }

        private void ConfigureSecurity(bool enabled, string sessionCode)
        {
            directSecurityEnabled = enabled;
            if (!enabled)
            {
                directSecurityKey = Array.Empty<byte>();
                return;
            }

            if (!DirectTransportSecurity.TryDeriveKey(
                    sessionCode,
                    out directSecurityKey,
                    out var error))
            {
                throw new ArgumentException(error, nameof(sessionCode));
            }
        }

        private void HostAcceptLoop()
        {
            while (!stopping)
            {
                TcpClient? accepted = null;
                try
                {
                    accepted = listener!.AcceptTcpClient();
                    accepted.NoDelay = true;
                    var raw = accepted.GetStream();
                    Stream stream = raw;
                    if (directSecurityEnabled)
                    {
                        accepted.ReceiveTimeout = 5000;
                        accepted.SendTimeout = 5000;
                        stream = DirectTransportSecurity.AuthenticateTcpHost(
                            raw,
                            directSecurityKey);
                        accepted.ReceiveTimeout = 0;
                        accepted.SendTimeout = 0;
                    }

                    var reader = new BinaryReader(stream, Encoding.UTF8, true);
                    if (reader.ReadString() != "CLIENT_HELLO"
                        || reader.ReadInt32() != 0)
                    {
                        throw new InvalidDataException(
                            "The client control protocol is incompatible.");
                    }

                    var nickname = MultiplayerNickname.Normalize(reader.ReadString());
                    var peerId = AllocateHostPeerId();
                    if (string.IsNullOrEmpty(peerId))
                    {
                        var rejected = new HostPeerConnection(
                            string.Empty,
                            nickname,
                            accepted,
                            stream);
                        SendHostCommand(
                            rejected,
                            "SESSION_FULL",
                            0,
                            "The host session is full.");
                        accepted.Close();
                        continue;
                    }

                    var peer = new HostPeerConnection(
                        peerId,
                        nickname,
                        accepted,
                        stream);
                    lock (stateLock)
                    {
                        hostPeers.Add(peerId, peer);
                    }

                    SendHostCommand(
                        peer,
                        "HELLO",
                        0,
                        FormatHelloPayload(
                            SessionId,
                            peerId,
                            maxPlayers,
                            localNickname));
                    peer.Phase = "Waiting for Checkpoint";
                    peer.Detail = "Connected. Waiting for a current-world checkpoint.";
                    lock (stateLock)
                    {
                        pendingJoinCaptures.Enqueue(peerId);
                    }

                    var peerThread = new Thread(
                        () => HostPeerWorker(peer, reader))
                    {
                        IsBackground = true,
                        Name = "Going Cooperative Control " + peerId
                    };
                    peer.Worker = peerThread;
                    peerThread.Start();
                    SetHostSummaryDetail();
                }
                catch (Exception ex)
                {
                    try { accepted?.Close(); } catch { }
                    if (!stopping)
                    {
                        SetDetail(
                            "Rejected control connection: "
                            + ex.GetType().Name
                            + ": "
                            + ex.Message);
                    }
                }
            }
        }

        private void HostPeerWorker(
            HostPeerConnection peer,
            BinaryReader reader)
        {
            try
            {
                while (!stopping && !peer.Closed)
                {
                    var command = reader.ReadString();
                    var commandEpoch = reader.ReadInt32();
                    if (command == "VERIFIED")
                    {
                        if (commandEpoch != peer.Epoch)
                        {
                            continue;
                        }

                        peer.Phase = "Loading";
                        peer.Detail = "Checkpoint verified. Loading automatically.";
                        SendHostCommand(
                            peer,
                            peer.Epoch == 0 ? "LOAD" : "RESYNC_LOAD",
                            peer.Epoch);
                    }
                    else if (command == "LOADED")
                    {
                        if (commandEpoch != peer.Epoch)
                        {
                            continue;
                        }

                        peer.ReadyForReplication = true;
                        peer.Phase = "Playing";
                        peer.Detail = "Joined the live host world.";
                        SendHostCommand(peer, "RESUME", peer.Epoch);
                        SetHostSummaryDetail();
                    }
                    else if (command == "RESYNC_REQUEST")
                    {
                        if (commandEpoch != peer.Epoch
                            || !peer.ReadyForReplication)
                        {
                            continue;
                        }

                        peer.ReadyForReplication = false;
                        peer.Phase = "Waiting for Resync";
                        peer.Detail = "Requested a fresh host checkpoint.";
                        lock (stateLock)
                        {
                            pendingResyncCaptures.Enqueue(peer.PeerId);
                            resyncCaptureRequested = true;
                        }
                    }
                    else if (command == "LOAD_FAILED")
                    {
                        var reason = reader.ReadString();
                        peer.ReadyForReplication = false;
                        peer.Phase = "Failed";
                        peer.Detail = reason;
                        SetHostSummaryDetail();
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException
                || ex is EndOfStreamException
                || ex is SocketException
                || ex is ObjectDisposedException)
            {
                if (!stopping)
                {
                    peer.Detail = "Disconnected: " + ex.Message;
                }
            }
            finally
            {
                peer.Closed = true;
                peer.ReadyForReplication = false;
                try { peer.Client.Close(); } catch { }
                SetHostSummaryDetail();
            }
        }

        private void ClientWorker(string host, int port)
        {
            try
            {
                client = new TcpClient { NoDelay = true };
                if (directSecurityEnabled)
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                }

                client.Connect(host, port);
                var raw = client.GetStream();
                if (directSecurityEnabled)
                {
                    try
                    {
                        controlStream =
                            DirectTransportSecurity.AuthenticateTcpClient(
                                raw,
                                directSecurityKey);
                    }
                    catch (Exception ex) when (
                        ex is IOException
                        || ex is InvalidDataException
                        || ex is SocketException)
                    {
                        throw new InvalidDataException(
                            "Direct connection authentication failed. "
                            + "Confirm the host address and session code.",
                            ex);
                    }
                }
                else
                {
                    controlStream = raw;
                }

                client.ReceiveTimeout = 0;
                client.SendTimeout = 0;
                SendClientCommand("CLIENT_HELLO", 0, localNickname);
                var reader = new BinaryReader(controlStream, Encoding.UTF8, true);
                var helloCommand = reader.ReadString();
                var helloEpoch = reader.ReadInt32();
                var helloPayload = reader.ReadString();
                if (helloCommand == "SESSION_FULL")
                {
                    throw new InvalidOperationException(helloPayload);
                }

                if (helloCommand != "HELLO"
                    || helloEpoch != 0
                    || !TryReadHelloPayload(
                        helloPayload,
                        out var remoteSessionId,
                        out var peerId,
                        out var remoteMaxPlayers,
                        out var remoteHostNickname))
                {
                    throw new InvalidDataException(
                        "The host control protocol is incompatible.");
                }

                Interlocked.Exchange(ref sessionId, remoteSessionId);
                lock (stateLock)
                {
                    assignedPeerId = peerId;
                    maxPlayers = remoteMaxPlayers;
                    hostNickname = remoteHostNickname;
                }

                SetState(
                    "Waiting for Checkpoint",
                    "Connected as "
                        + peerId
                        + ". The host is capturing the current world.",
                    0f);
                ReadClientCommands(reader);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private static long CreateSessionId()
        {
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            var value = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            return value == 0L ? 1L : value;
        }

        private static string FormatHelloPayload(
            long value,
            string peerId,
            int maxPlayers,
            string hostDisplayName)
        {
            return Magic
                + "|"
                + value.ToString(CultureInfo.InvariantCulture)
                + "|"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(peerId))
                + "|"
                + maxPlayers.ToString(CultureInfo.InvariantCulture)
                + "|"
                + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(
                        MultiplayerNickname.Normalize(hostDisplayName)));
        }

        private static bool TryReadHelloPayload(
            string payload,
            out long value,
            out string peerId,
            out int remoteMaxPlayers,
            out string remoteHostNickname)
        {
            value = 0L;
            peerId = string.Empty;
            remoteMaxPlayers = 0;
            remoteHostNickname = MultiplayerNickname.DefaultNickname;
            if (string.IsNullOrEmpty(payload))
            {
                return false;
            }

            var parts = payload.Split('|');
            if (parts.Length != 5
                || !string.Equals(parts[0], Magic, StringComparison.Ordinal)
                || !long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                || value <= 0L
                || !int.TryParse(
                    parts[3],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out remoteMaxPlayers)
                || remoteMaxPlayers < 2
                || remoteMaxPlayers
                    > MultiplayerPeerLimits.ExperimentalMaximumPlayers)
            {
                return false;
            }

            try
            {
                peerId = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[2]));
                remoteHostNickname = MultiplayerNickname.Normalize(
                    Encoding.UTF8.GetString(
                        Convert.FromBase64String(parts[4])));
            }
            catch (FormatException)
            {
                return false;
            }

            return MultiplayerPeerIds.TryParseClientSlot(peerId, out _);
        }

        private void ReadClientCommands(BinaryReader reader)
        {
            while (!stopping)
            {
                var command = reader.ReadString();
                var commandEpoch = reader.ReadInt32();
                if (command == "BUNDLE")
                {
                    ReceiveBundle(reader, commandEpoch);
                }
                else if ((command == "LOAD"
                        || command == "RESYNC_LOAD")
                    && commandEpoch == epoch)
                {
                    BeginLoad();
                }
                else if (command == "RESUME"
                    && commandEpoch == epoch)
                {
                    resumeGeneration++;
                    SetState(
                        "Playing",
                        "Joined "
                            + hostNickname
                            + "'s live world.",
                        1f);
                }
                else if (command == "RESYNC_FAILED"
                    && commandEpoch == epoch)
                {
                    var reason = reader.ReadString();
                    resumeGeneration++;
                    SetState(
                        "Playing",
                        "Host could not create a resync checkpoint. "
                            + reason,
                        1f);
                }
                else if (command == "JOIN_FAILED")
                {
                    throw new InvalidOperationException(reader.ReadString());
                }
                else if (command == "LOAD_FAILED"
                    && commandEpoch == epoch)
                {
                    SetState(
                        "Failed",
                        "Host rejected the checkpoint load. "
                            + reader.ReadString(),
                        0f);
                }
            }
        }

        private void SendBundle(
            HostPeerConnection peer,
            VillageSaveInfo save,
            bool isResync)
        {
            WaitForSaveBundleReady(
                save.FilePath,
                TimeSpan.FromSeconds(10));
            if (isResync)
            {
                peer.Epoch++;
                peer.ReadyForReplication = false;
            }

            var bundleEpoch = peer.Epoch;
            var files = GetSaveBundle(save.FilePath);
            lock (peer.WriteLock)
            {
                if (peer.Closed)
                {
                    throw new IOException("Peer is disconnected.");
                }

                var writer = new BinaryWriter(
                    peer.Stream,
                    Encoding.UTF8,
                    true);
                writer.Write("BUNDLE");
                writer.Write(bundleEpoch);
                writer.Write(Path.GetFileName(save.FilePath));
                writer.Write(save.VillageName ?? string.Empty);
                writer.Write(files.Count);
                long total = 0L;
                foreach (var filePath in files)
                {
                    total += new FileInfo(filePath).Length;
                }

                writer.Write(total);
                long sent = 0L;
                var buffer = new byte[64 * 1024];
                foreach (var filePath in files)
                {
                    var info = new FileInfo(filePath);
                    writer.Write(Path.GetFileName(filePath));
                    writer.Write(info.Length);
                    writer.Write(ComputeSha256(filePath));
                    using (var input = File.OpenRead(filePath))
                    {
                        int read;
                        while ((read = input.Read(
                            buffer,
                            0,
                            buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, read);
                            sent += read;
                            peer.Phase = isResync
                                ? "Transferring Resync"
                                : "Transferring Join";
                            peer.Detail = Path.GetFileName(filePath);
                            peer.Progress = total <= 0L
                                ? 0f
                                : (float)sent / total;
                        }
                    }
                }

                writer.Flush();
            }

            peer.Phase = "Waiting for Verification";
            peer.Detail = "Checkpoint sent.";
            peer.Progress = 1f;
            SetHostSummaryDetail();
        }

        private void ReceiveBundle(
            BinaryReader reader,
            int bundleEpoch)
        {
            if (bundleEpoch < epoch)
            {
                throw new InvalidDataException(
                    "Received a stale checkpoint epoch.");
            }

            epoch = bundleEpoch;
            var primaryName = SafeFileName(reader.ReadString());
            var villageName = SafeFileName(reader.ReadString());
            var count = reader.ReadInt32();
            var total = reader.ReadInt64();
            if (count < 1
                || count > 16
                || total < 1
                || total > 2L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException(
                    "Invalid save manifest.");
            }

            var stem = "GoingCooperative_"
                + (bundleEpoch == 0
                    ? "Join_"
                    : "Resync_" + bundleEpoch + "_")
                + DateTime.UtcNow.ToString(
                    "yyyyMMdd_HHmmssfff",
                    CultureInfo.InvariantCulture);
            var targetPrimary = stem + ".sav";
            var root = Path.Combine(saveRoot, villageName);
            Directory.CreateDirectory(root);
            long received = 0L;
            var buffer = new byte[64 * 1024];
            for (var i = 0; i < count; i++)
            {
                var sourceName = SafeFileName(reader.ReadString());
                var targetName = MapBundleName(
                    sourceName,
                    primaryName,
                    targetPrimary);
                var length = reader.ReadInt64();
                var hash = reader.ReadString();
                if (length < 0 || length > total)
                {
                    throw new InvalidDataException(
                        "Invalid file length.");
                }

                var path = Path.Combine(root, targetName);
                var partialPath = path + ".part";
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }

                using (var output = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    long remaining = length;
                    while (remaining > 0)
                    {
                        var read = reader.Read(
                            buffer,
                            0,
                            (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0)
                        {
                            throw new EndOfStreamException(
                                "Save transfer ended early.");
                        }

                        output.Write(buffer, 0, read);
                        remaining -= read;
                        received += read;
                        SetState(
                            bundleEpoch == 0
                                ? "Receiving Join"
                                : "Receiving Resync",
                            sourceName,
                            total <= 0L ? 0f : (float)received / total);
                    }
                }

                if (!string.Equals(
                    ComputeSha256(partialPath),
                    hash,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Checksum mismatch for " + sourceName + ".");
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(partialPath, path);
            }

            lock (stateLock)
            {
                receivedSavePath = Path.Combine(root, targetPrimary);
                receivedVillageName = villageName;
            }

            SendClientCommand("VERIFIED", bundleEpoch);
            SetState(
                "Connected",
                bundleEpoch == 0
                    ? "Checkpoint verified. Loading automatically."
                    : "Resync verified. Loading automatically.",
                1f);
        }

        private void BeginLoad()
        {
            loadGeneration++;
            SetState(
                "Loading",
                "Loading authoritative host checkpoint.",
                1f);
        }

        private void SendClientCommand(
            string command,
            int commandEpoch,
            string? payload = null)
        {
            if (client == null || controlStream == null)
            {
                throw new IOException(
                    "Control channel is not connected.");
            }

            lock (clientWriteLock)
            {
                var writer = new BinaryWriter(
                    controlStream,
                    Encoding.UTF8,
                    true);
                writer.Write(command);
                writer.Write(commandEpoch);
                if (payload != null)
                {
                    writer.Write(payload);
                }

                writer.Flush();
            }
        }

        private static void SendHostCommand(
            HostPeerConnection peer,
            string command,
            int commandEpoch,
            string? payload = null)
        {
            lock (peer.WriteLock)
            {
                if (peer.Closed)
                {
                    throw new IOException("Peer is disconnected.");
                }

                var writer = new BinaryWriter(
                    peer.Stream,
                    Encoding.UTF8,
                    true);
                writer.Write(command);
                writer.Write(commandEpoch);
                if (payload != null)
                {
                    writer.Write(payload);
                }

                writer.Flush();
            }
        }

        private string AllocateHostPeerId()
        {
            lock (stateLock)
            {
                for (var slot = 1; slot < maxPlayers; slot++)
                {
                    var peerId = MultiplayerPeerIds.Client(slot);
                    if (!hostPeers.TryGetValue(peerId, out var existing)
                        || existing.Closed)
                    {
                        if (existing != null)
                        {
                            hostPeers.Remove(peerId);
                        }

                        return peerId;
                    }
                }
            }

            return string.Empty;
        }

        private HostPeerConnection GetHostPeer(string peerId)
        {
            lock (stateLock)
            {
                if (!hostPeers.TryGetValue(peerId, out var peer)
                    || peer.Closed)
                {
                    throw new InvalidOperationException(
                        "Peer is not connected: " + peerId);
                }

                return peer;
            }
        }

        private void SetHostSummaryDetail()
        {
            if (!hostMode)
            {
                return;
            }

            var playing = 1;
            var connected = 1;
            lock (stateLock)
            {
                foreach (var peer in hostPeers.Values)
                {
                    if (peer.Closed)
                    {
                        continue;
                    }

                    connected++;
                    if (peer.ReadyForReplication)
                    {
                        playing++;
                    }
                }
            }

            SetState(
                "Playing",
                playing.ToString(CultureInfo.InvariantCulture)
                    + "/"
                    + maxPlayers.ToString(CultureInfo.InvariantCulture)
                    + " playing, "
                    + connected.ToString(CultureInfo.InvariantCulture)
                    + " connected. New players may join at any time.",
                1f);
        }

        private static string MapBundleName(
            string source,
            string primary,
            string targetPrimary)
        {
            if (source.Equals(
                primary,
                StringComparison.OrdinalIgnoreCase))
            {
                return targetPrimary;
            }

            if (source.Equals(
                primary + ".meta",
                StringComparison.OrdinalIgnoreCase))
            {
                return targetPrimary + ".meta";
            }

            if (source.Equals(
                Path.ChangeExtension(primary, ".gmevents"),
                StringComparison.OrdinalIgnoreCase))
            {
                return Path.ChangeExtension(
                    targetPrimary,
                    ".gmevents");
            }

            throw new InvalidDataException(
                "Unexpected save companion " + source + ".");
        }

        private static List<string> GetSaveBundle(string primary)
        {
            if (!File.Exists(primary))
            {
                throw new FileNotFoundException(
                    "Selected save is missing.",
                    primary);
            }

            var files = new List<string> { primary };
            if (!File.Exists(primary + ".meta"))
            {
                throw new FileNotFoundException(
                    "Save metadata is missing.",
                    primary + ".meta");
            }

            files.Add(primary + ".meta");
            var eventsFile = Path.ChangeExtension(
                primary,
                ".gmevents");
            if (File.Exists(eventsFile))
            {
                files.Add(eventsFile);
            }

            return files;
        }

        private static void WaitForSaveBundleReady(
            string primary,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            Exception? lastError = null;
            var eventsPath = Path.ChangeExtension(
                primary,
                ".gmevents");
            DateTime? fingerprintStableSince = null;
            string lastFingerprint = string.Empty;
            var stableSamples = 0;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(primary)
                        && File.Exists(primary + ".meta"))
                    {
                        using (File.Open(
                            primary,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read))
                        {
                        }

                        using (File.Open(
                            primary + ".meta",
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read))
                        {
                        }

                        var eventsExist = File.Exists(eventsPath);
                        if (eventsExist)
                        {
                            using (File.Open(
                                eventsPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read))
                            {
                            }
                        }

                        var primaryInfo = new FileInfo(primary);
                        var metaInfo = new FileInfo(primary + ".meta");
                        var eventsInfo = eventsExist
                            ? new FileInfo(eventsPath)
                            : null;
                        var fingerprint =
                            primaryInfo.Length.ToString(
                                CultureInfo.InvariantCulture)
                            + ":"
                            + primaryInfo.LastWriteTimeUtc.Ticks.ToString(
                                CultureInfo.InvariantCulture)
                            + "|"
                            + metaInfo.Length.ToString(
                                CultureInfo.InvariantCulture)
                            + ":"
                            + metaInfo.LastWriteTimeUtc.Ticks.ToString(
                                CultureInfo.InvariantCulture)
                            + "|"
                            + (eventsInfo == null
                                ? "absent"
                                : eventsInfo.Length.ToString(
                                        CultureInfo.InvariantCulture)
                                    + ":"
                                    + eventsInfo.LastWriteTimeUtc.Ticks.ToString(
                                        CultureInfo.InvariantCulture));
                        if (string.Equals(
                            fingerprint,
                            lastFingerprint,
                            StringComparison.Ordinal))
                        {
                            stableSamples++;
                        }
                        else
                        {
                            lastFingerprint = fingerprint;
                            stableSamples = 1;
                            fingerprintStableSince = DateTime.UtcNow;
                        }

                        if (stableSamples >= 2
                            && fingerprintStableSince.HasValue
                            && DateTime.UtcNow
                                - fingerprintStableSince.Value
                                >= TimeSpan.FromMilliseconds(500))
                        {
                            return;
                        }
                    }
                    else
                    {
                        fingerprintStableSince = null;
                        lastFingerprint = string.Empty;
                        stableSamples = 0;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    fingerprintStableSince = null;
                    lastFingerprint = string.Empty;
                    stableSamples = 0;
                }

                Thread.Sleep(50);
            }

            throw new IOException(
                "Timed out waiting for the native save checkpoint "
                    + "to finish writing.",
                lastError);
        }

        private static string SafeFileName(string value)
        {
            var name = Path.GetFileName(value ?? string.Empty);
            if (name.Length == 0
                || name != value
                || name.IndexOfAny(
                    Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException(
                    "Invalid save name.");
            }

            return name;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                {
                    builder.Append(
                        value.ToString(
                            "x2",
                            CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private void SetState(
            string value,
            string message,
            float valueProgress)
        {
            lock (stateLock)
            {
                phase = value;
                detail = message;
                progress = valueProgress;
            }
        }

        private void SetDetail(string message)
        {
            lock (stateLock)
            {
                detail = message;
            }
        }

        private void Fail(Exception ex)
        {
            if (stopping)
            {
                return;
            }

            failure = ex;
            SetState(
                "Failed",
                ex.GetType().Name + ": " + ex.Message,
                0f);
            try { client?.Close(); } catch { }
        }

        private void FailPeer(
            HostPeerConnection peer,
            Exception ex)
        {
            peer.ReadyForReplication = false;
            peer.Phase = "Failed";
            peer.Detail = ex.GetType().Name + ": " + ex.Message;
            try { peer.Client.Close(); } catch { }
            peer.Closed = true;
            SetHostSummaryDetail();
        }

        private sealed class HostPeerConnection
        {
            public HostPeerConnection(
                string peerId,
                string nickname,
                TcpClient client,
                Stream stream)
            {
                PeerId = peerId;
                Nickname = MultiplayerNickname.Normalize(nickname);
                Client = client;
                Stream = stream;
            }

            public string PeerId { get; }
            public string Nickname { get; }
            public TcpClient Client { get; }
            public Stream Stream { get; }
            public object WriteLock { get; } = new object();
            public Thread? Worker { get; set; }
            public int Epoch { get; set; }
            public bool ReadyForReplication { get; set; }
            public bool Closed { get; set; }
            public float Progress { get; set; }
            public string Phase { get; set; } = "Connecting";
            public string Detail { get; set; } = string.Empty;
        }
    }

    internal sealed class MultiplayerTransferPeerSnapshot
    {
        public MultiplayerTransferPeerSnapshot(
            string peerId,
            string nickname,
            string phase,
            bool connected,
            bool playing)
        {
            PeerId = peerId;
            Nickname = nickname;
            Phase = phase;
            Connected = connected;
            Playing = playing;
        }

        public string PeerId { get; }
        public string Nickname { get; }
        public string Phase { get; }
        public bool Connected { get; }
        public bool Playing { get; }
    }
}
