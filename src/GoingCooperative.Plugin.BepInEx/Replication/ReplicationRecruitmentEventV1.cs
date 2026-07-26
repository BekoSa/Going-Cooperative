using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using NSMedieval;
using NSMedieval.Manager;
using NSMedieval.Serialization;
using NSMedieval.State;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationRecruitmentBeginDeltaKind = "RecruitmentWorkerBegin";
        private const string ReplicationRecruitmentChunkDeltaKind = "RecruitmentWorkerChunk";
        private const string ReplicationRecruitmentAdoptDeltaKind = "RecruitmentWorkerAdopt";
        private const string ReplicationRecruitmentWireVersion = "recruitment-worker-v1";
        private const string ReplicationRecruitmentWriterId = "going-cooperative-recruitment-worker-v1";
        private const int ReplicationRecruitmentBundleMagic = 0x31575247; // GRW1
        private const int ReplicationRecruitmentBundleVersion = 1;
        private const int ReplicationRecruitmentChunkBytes = 512;
        private const int ReplicationRecruitmentMaxBundleBytes = 96 * 1024;
        private const int ReplicationRecruitmentMaxRawBytes = 1024 * 1024;
        private const int ReplicationRecruitmentMaxChunks = 192;

        private static readonly object ReplicationRecruitmentLock = new object();
        private static readonly Dictionary<string, ClientRecruitmentTransfer> ReplicationClientRecruitmentTransfers =
            new Dictionary<string, ClientRecruitmentTransfer>(StringComparer.Ordinal);
        private static readonly Dictionary<object, string> ReplicationRecruitmentWorkerIdByObject =
            new Dictionary<object, string>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, object> ReplicationRecruitmentWorkerById =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private static int replicationRecruitmentApplicationDepth;

        private static bool RecruitmentEventAuthorityV1Enabled()
        {
            return replicationConfigEventReplication
                && replicationConfigEventLifecycleReplication
                && replicationConfigEventRecruitmentAuthorityV1
                && replicationConfigEventDialogReplication
                && replicationConfigEventChoiceCommands
                && replicationRecruitmentEventHooksReady
                && string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase)
                && IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode);
        }

        private static bool RunawayEventAuthorityV1Enabled()
        {
            return replicationConfigEventReplication
                && replicationConfigEventLifecycleReplication
                && replicationConfigEventRunawayAuthorityV1
                && replicationConfigEventDialogReplication
                && replicationConfigEventChoiceCommands
                && replicationRecruitmentEventHooksReady
                && ValidateReplicationRunawayNativeSurfaces()
                && string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase)
                && IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode);
        }

        private static bool ValidateReplicationRunawayNativeSurfaces()
        {
            try
            {
                var eventType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.RunawayEvent");
                return eventType != null
                    && AccessTools.Property(eventType, "HumanoidToAdd") != null
                    && AccessTools.Method(eventType, "Unsubscribe") != null;
            }
            catch
            {
                return false;
            }
        }

        private static int TryInstallReplicationRecruitmentEventHooks(Harmony harmonyInstance)
        {
            try
            {
                var phaseType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.AddWorkerPhase");
                var execute = phaseType == null ? null : AccessTools.Method(phaseType, "Execute", Type.EmptyTypes);
                var postfix = new HarmonyMethod(typeof(GoingCooperativePlugin), nameof(ReplicationRecruitmentAddWorkerPostfix));
                if (execute == null) return 0;
                harmonyInstance.Patch(execute, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative recruitment V1 hook installation failed error="
                    + FormatReflectionExceptionDetail(ex));
                return 0;
            }
        }

        private static bool ValidateReplicationRecruitmentNativeSurfaces()
        {
            try
            {
                var eventType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.NewWorkerEvent");
                var phaseType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.AddWorkerPhase");
                return eventType != null
                    && phaseType != null
                    && AccessTools.Property(eventType, "HumanoidToAdd") != null
                    && AccessTools.Property(phaseType, "HumanoidToAdd") != null
                    && AccessTools.Property(phaseType.BaseType, "EventInstance") != null
                    && AccessTools.Method(typeof(WorkerController), "CreateWorker", new[] { typeof(HumanoidInstance) }) != null
                    && AccessTools.Method(typeof(FVSerializer), "GetBytes", new[] { typeof(string) }) != null
                    && AccessTools.Method(typeof(FVSerializer), "GetReferenceBytes", Type.EmptyTypes) != null
                    && AccessTools.Method(typeof(FVDeserializer), "ReadReferences", new[] { typeof(byte[]) }) != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsReplicationRecruitmentEventInstance(object? nativeEvent)
        {
            return string.Equals(
                nativeEvent?.GetType().FullName,
                "NSMedieval.GameEventSystem.Events.NewWorkerEvent",
                StringComparison.Ordinal);
        }

        private static bool IsReplicationRunawayEventInstance(object? nativeEvent)
        {
            return string.Equals(
                nativeEvent?.GetType().FullName,
                "NSMedieval.GameEventSystem.Events.RunawayEvent",
                StringComparison.Ordinal);
        }

        private static bool IsReplicationAuthoritativeWorkerOfferEvent(object? nativeEvent)
        {
            return (IsReplicationRecruitmentEventInstance(nativeEvent) && RecruitmentEventAuthorityV1Enabled())
                || (IsReplicationRunawayEventInstance(nativeEvent) && RunawayEventAuthorityV1Enabled());
        }

        private static bool IsReplicationAuthoritativeWorkerOfferBlueprint(string blueprintId)
        {
            return (RecruitmentEventAuthorityV1Enabled() && IsReplicationRecruitmentEventBlueprintId(blueprintId))
                || (RunawayEventAuthorityV1Enabled() && IsReplicationRunawayEventBlueprintId(blueprintId));
        }

        private static bool IsReplicationRecruitmentEventBlueprintId(string blueprintId)
        {
            if (string.IsNullOrWhiteSpace(blueprintId)) return false;
            try
            {
                var repositoryType = AccessTools.TypeByName("NSMedieval.Repository.GameEventSettingsRepository");
                var eventType = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEvent");
                var openRepository = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
                if (repositoryType == null || eventType == null || openRepository == null) return false;
                var closedRepository = openRepository.MakeGenericType(repositoryType, eventType);
                var repository = AccessTools.Property(closedRepository, "Instance")?.GetValue(null, null);
                var blueprint = repository == null
                    ? null
                    : AccessTools.Method(closedRepository, "GetByID", new[] { typeof(string) })
                        ?.Invoke(repository, new object[] { blueprintId });
                var className = blueprint == null
                    ? string.Empty
                    : AccessTools.Property(blueprint.GetType(), "ClassName")?.GetValue(blueprint, null) as string;
                return string.Equals(className, "NewWorkerEvent", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative recruitment blueprint classification failed id="
                    + blueprintId + " error=" + FormatReflectionExceptionDetail(ex));
                return false;
            }
        }

        private static bool IsReplicationRunawayEventBlueprintId(string blueprintId)
        {
            if (string.IsNullOrWhiteSpace(blueprintId)) return false;
            try
            {
                var repositoryType = AccessTools.TypeByName("NSMedieval.Repository.GameEventSettingsRepository");
                var eventType = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEvent");
                var openRepository = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
                if (repositoryType == null || eventType == null || openRepository == null) return false;
                var closedRepository = openRepository.MakeGenericType(repositoryType, eventType);
                var repository = AccessTools.Property(closedRepository, "Instance")?.GetValue(null, null);
                var blueprint = repository == null
                    ? null
                    : AccessTools.Method(closedRepository, "GetByID", new[] { typeof(string) })
                        ?.Invoke(repository, new object[] { blueprintId });
                var className = blueprint == null
                    ? string.Empty
                    : AccessTools.Property(blueprint.GetType(), "ClassName")?.GetValue(blueprint, null) as string;
                return string.Equals(className, "RunawayEvent", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative runaway blueprint classification failed id="
                    + blueprintId + " error=" + FormatReflectionExceptionDetail(ex));
                return false;
            }
        }

        private static bool ShouldSuppressReplicationClientRecruitmentEventStart(bool recruitmentEvent)
        {
            if (!recruitmentEvent
                || replicationConfigHostMode
                || !RecruitmentEventAuthorityV1Enabled()
                || replicationEventApplicationDepth > 0
                || replicationRecruitmentApplicationDepth > 0)
            {
                return false;
            }
            return multiplayerLoadingInProgress
                || (replicationConfigEnabled && replicationRuntimeStarted && replicationRemoteHelloReceived);
        }

        private static bool ShouldSuppressReplicationClientRunawayEventStart(bool runawayEvent)
        {
            if (!runawayEvent
                || replicationConfigHostMode
                || !RunawayEventAuthorityV1Enabled()
                || replicationEventApplicationDepth > 0
                || replicationRecruitmentApplicationDepth > 0)
            {
                return false;
            }
            return multiplayerLoadingInProgress
                || (replicationConfigEnabled && replicationRuntimeStarted && replicationRemoteHelloReceived);
        }

        private static void QuarantineReplicationClientNativeRecruitmentEvents(string source)
        {
            if (replicationConfigHostMode
                || (!RecruitmentEventAuthorityV1Enabled() && !RunawayEventAuthorityV1Enabled())) return;
            try
            {
                var systemType = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEventSystem");
                var system = systemType == null ? null : ResolveReplicationUnityManagerInstance(systemType);
                var running = systemType == null
                    ? null
                    : AccessTools.Property(systemType, "RunningEvents")?.GetValue(system, null) as System.Collections.IList;
                var remove = systemType == null ? null : AccessTools.Method(systemType, "RemoveFromRunningEvents");
                if (system == null || running == null || remove == null) return;
                var candidates = new List<object>();
                for (var i = 0; i < running.Count; i++)
                    if (running[i] != null && IsReplicationAuthoritativeWorkerOfferEvent(running[i])) candidates.Add(running[i]!);
                for (var i = 0; i < candidates.Count; i++)
                {
                    var nativeEvent = candidates[i];
                    if (TryReadInstanceMemberValue(nativeEvent, "HumanoidToAdd", out var workerValue)
                        && workerValue is HumanoidInstance candidate
                        && !GlobalSaveController.CurrentVillageData.Workers.Contains(candidate))
                    {
                        try { candidate.DestroyStorage(); } catch { }
                        try { candidate.DestroyEquipment(); } catch { }
                    }
                    remove.Invoke(system, new[] { nativeEvent });
                    replicationEventsSuppressed++;
                    instance?.LogReplicationInfo("Going Cooperative worker-offer authority quarantined client native event type="
                        + nativeEvent.GetType().Name + " source=" + source);
                }
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative recruitment V1 quarantine failed source="
                    + source + " error=" + FormatReflectionExceptionDetail(ex));
            }
        }

        private static void ReplicationRecruitmentAddWorkerPostfix(object __instance)
        {
            if (!replicationConfigHostMode
                || !RecruitmentEventAuthorityV1Enabled()
                || replicationRecruitmentApplicationDepth > 0
                || __instance == null
                || !TryReadInstanceMemberValue(__instance, "EventInstance", out var nativeEvent)
                || nativeEvent == null
                || !IsReplicationAuthoritativeWorkerOfferEvent(nativeEvent)
                || !TryReadInstanceMemberValue(__instance, "HumanoidToAdd", out var workerValue)
                || workerValue is not HumanoidInstance worker)
            {
                return;
            }

            var record = EnsureHostReplicationEventRecord(nativeEvent, "recruitment-add-worker");
            if (record == null)
            {
                instance?.LogReplicationWarning("Going Cooperative recruitment V1 failed closed at adoption: host event identity missing.");
                return;
            }
            RegisterReplicationRecruitmentWorker(record.EventId, worker);
            instance?.SendHostReplicationRecruitmentWorker(record, worker);
        }

        private void SendHostReplicationRecruitmentWorker(HostReplicationEventRecord record, HumanoidInstance worker)
        {
            if (!replicationRuntimeStarted || !replicationRemoteHelloReceived) return;
            if (!TrySerializeReplicationRecruitmentWorker(record.EventId, worker, out var bundle, out var hash, out var error))
            {
                LogReplicationWarning("Going Cooperative recruitment V1 worker transfer failed eventId="
                    + record.EventId + " error=" + error + "; full-session-resync-required");
                return;
            }

            EnsureReplicationEventHostScope();
            var transferId = record.EventId + ":worker:" + record.Revision.ToString(CultureInfo.InvariantCulture);
            var chunkCount = (bundle.Length + ReplicationRecruitmentChunkBytes - 1) / ReplicationRecruitmentChunkBytes;
            var envelope = "wire=" + ReplicationRecruitmentWireVersion
                + " scope=" + replicationEventHostSessionNonce
                + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                + " eventIdB64=" + EncodeReplicationDetailBase64(record.EventId)
                + " transferB64=" + EncodeReplicationDetailBase64(transferId)
                + " chunks=" + chunkCount.ToString(CultureInfo.InvariantCulture)
                + " bytes=" + bundle.Length.ToString(CultureInfo.InvariantCulture)
                + " sha256=" + hash
                + " gameAssemblyMvid=" + GetReplicationGameAssemblyModuleVersionId();
            SendReplicationEventDelta(ReplicationRecruitmentBeginDeltaKind, record.Revision, record.BlueprintId, envelope);
            for (var i = 0; i < chunkCount; i++)
            {
                var offset = i * ReplicationRecruitmentChunkBytes;
                var count = Math.Min(ReplicationRecruitmentChunkBytes, bundle.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(bundle, offset, chunk, 0, count);
                SendReplicationEventDelta(
                    ReplicationRecruitmentChunkDeltaKind,
                    record.Revision,
                    record.BlueprintId,
                    envelope + " index=" + i.ToString(CultureInfo.InvariantCulture)
                        + " dataB64=" + Convert.ToBase64String(chunk));
            }
            SendReplicationEventDelta(ReplicationRecruitmentAdoptDeltaKind, record.Revision, record.BlueprintId, envelope);
            LogReplicationInfo("Going Cooperative recruitment V1 worker transfer sent eventId="
                + record.EventId + " chunks=" + chunkCount.ToString(CultureInfo.InvariantCulture));
        }

        private static bool TrySerializeReplicationRecruitmentWorker(
            string eventId,
            HumanoidInstance worker,
            out byte[] bundle,
            out string hash,
            out string detail)
        {
            bundle = Array.Empty<byte>();
            hash = string.Empty;
            detail = string.Empty;
            try
            {
                byte[] data;
                byte[] references;
                using (var serializer = new FVSerializer(ReplicationRecruitmentWriterId, Array.Empty<string>()))
                {
                    serializer.Write<HumanoidInstance>("workers", new List<HumanoidInstance> { worker });
                    serializer.WriteReferences();
                    data = serializer.GetBytes(ReplicationRecruitmentWriterId);
                    references = serializer.GetReferenceBytes();
                }
                byte[] raw;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(ReplicationRecruitmentBundleMagic);
                    writer.Write(ReplicationRecruitmentBundleVersion);
                    writer.Write(GetReplicationGameAssemblyModuleVersionId());
                    writer.Write(eventId);
                    writer.Write(worker.UniqueId);
                    writer.Write(data.Length);
                    writer.Write(data);
                    writer.Write(references.Length);
                    writer.Write(references);
                    writer.Flush();
                    raw = stream.ToArray();
                }
                if (raw.Length <= 0 || raw.Length > ReplicationRecruitmentMaxRawBytes)
                {
                    detail = "raw-size=" + raw.Length.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                using (var compressed = new MemoryStream())
                {
                    using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                        gzip.Write(raw, 0, raw.Length);
                    bundle = compressed.ToArray();
                }
                if (bundle.Length <= 0 || bundle.Length > ReplicationRecruitmentMaxBundleBytes)
                {
                    detail = "bundle-size=" + bundle.Length.ToString(CultureInfo.InvariantCulture);
                    bundle = Array.Empty<byte>();
                    return false;
                }
                hash = ComputeReplicationRecruitmentSha256(bundle);
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + ex.Message;
                bundle = Array.Empty<byte>();
                return false;
            }
        }

        private static bool IsReplicationRecruitmentEventDeltaKind(string kind)
        {
            return string.Equals(kind, ReplicationRecruitmentBeginDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationRecruitmentChunkDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationRecruitmentAdoptDeltaKind, StringComparison.Ordinal);
        }

        private static bool TryApplyReplicationRecruitmentEventWorldDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            detail = string.Empty;
            if (!IsReplicationAuthoritativeWorkerOfferBlueprint(delta.BlueprintId))
            {
                detail = "worker-offer-authority-disabled-or-family-mismatch";
                return false;
            }
            if (replicationConfigHostMode)
            {
                detail = "recruitment-v1-ignored-on-host";
                return false;
            }
            if (!TryParseReplicationRecruitmentEnvelope(delta.Detail, out var transfer, out detail)) return false;
            if (!TryAcceptReplicationEventScope(transfer.Scope, transfer.Epoch, out detail)) return false;

            lock (ReplicationRecruitmentLock)
            {
                if (string.Equals(delta.DeltaKind, ReplicationRecruitmentBeginDeltaKind, StringComparison.Ordinal))
                {
                    ReplicationClientRecruitmentTransfers[transfer.TransferId] = transfer;
                    detail = "ok recruitment-begin eventId=" + transfer.EventId;
                    return true;
                }
                if (!ReplicationClientRecruitmentTransfers.TryGetValue(transfer.TransferId, out var current)
                    || !current.SemanticallyMatches(transfer))
                {
                    detail = "recruitment-transfer-missing-or-conflicting";
                    return false;
                }
                if (string.Equals(delta.DeltaKind, ReplicationRecruitmentChunkDeltaKind, StringComparison.Ordinal))
                {
                    if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "index", out var index)
                        || index < 0 || index >= current.ChunkCount
                        || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "dataB64", out var token))
                    {
                        detail = "recruitment-chunk-malformed";
                        return false;
                    }
                    byte[] chunk;
                    try { chunk = Convert.FromBase64String(token); }
                    catch { detail = "recruitment-chunk-base64"; return false; }
                    if (chunk.Length <= 0 || chunk.Length > ReplicationRecruitmentChunkBytes)
                    {
                        detail = "recruitment-chunk-size";
                        return false;
                    }
                    if (current.Chunks.TryGetValue(index, out var prior)
                        && !prior.SequenceEqual(chunk))
                    {
                        detail = "recruitment-chunk-conflict";
                        return false;
                    }
                    current.Chunks[index] = chunk;
                    detail = "ok recruitment-chunk index=" + index.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (current.Chunks.Count != current.ChunkCount)
                {
                    detail = "recruitment-adopt-incomplete chunks=" + current.Chunks.Count.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                var bundle = new byte[current.ByteCount];
                var offset = 0;
                for (var i = 0; i < current.ChunkCount; i++)
                {
                    if (!current.Chunks.TryGetValue(i, out var chunk) || offset + chunk.Length > bundle.Length)
                    {
                        detail = "recruitment-adopt-chunk-layout";
                        return false;
                    }
                    Buffer.BlockCopy(chunk, 0, bundle, offset, chunk.Length);
                    offset += chunk.Length;
                }
                if (offset != bundle.Length
                    || !string.Equals(ComputeReplicationRecruitmentSha256(bundle), current.Hash, StringComparison.Ordinal))
                {
                    detail = "recruitment-adopt-integrity";
                    return false;
                }
                if (!TryDeserializeAndAdoptReplicationRecruitmentWorker(current, bundle, out detail)) return false;
                ReplicationClientRecruitmentTransfers.Remove(current.TransferId);
                return true;
            }
        }

        private static bool TryParseReplicationRecruitmentEnvelope(
            string value,
            out ClientRecruitmentTransfer transfer,
            out string detail)
        {
            transfer = new ClientRecruitmentTransfer();
            if (!TryReadReplicationWorldObjectDetailToken(value, "wire", out var wire)
                || !string.Equals(wire, ReplicationRecruitmentWireVersion, StringComparison.Ordinal)
                || !TryReadReplicationEventEnvelope(value, out transfer.Scope, out transfer.Epoch)
                || !TryReadReplicationWorldObjectDetailToken(value, "eventIdB64", out var eventToken)
                || !TryReadReplicationWorldObjectDetailToken(value, "transferB64", out var transferToken)
                || !TryDecodeReplicationDetailBase64(eventToken, out transfer.EventId)
                || !TryDecodeReplicationDetailBase64(transferToken, out transfer.TransferId)
                || !TryReadReplicationWorldObjectDetailInt(value, "chunks", out transfer.ChunkCount)
                || !TryReadReplicationWorldObjectDetailInt(value, "bytes", out transfer.ByteCount)
                || !TryReadReplicationWorldObjectDetailToken(value, "sha256", out transfer.Hash)
                || !TryReadReplicationWorldObjectDetailToken(value, "gameAssemblyMvid", out transfer.GameAssemblyMvid)
                || string.IsNullOrWhiteSpace(transfer.EventId)
                || string.IsNullOrWhiteSpace(transfer.TransferId)
                || transfer.ChunkCount <= 0 || transfer.ChunkCount > ReplicationRecruitmentMaxChunks
                || transfer.ByteCount <= 0 || transfer.ByteCount > ReplicationRecruitmentMaxBundleBytes
                || transfer.ChunkCount != (transfer.ByteCount + ReplicationRecruitmentChunkBytes - 1) / ReplicationRecruitmentChunkBytes
                || transfer.Hash.Length != 64
                || !string.Equals(transfer.GameAssemblyMvid, GetReplicationGameAssemblyModuleVersionId(), StringComparison.Ordinal))
            {
                detail = "recruitment-envelope-malformed-or-incompatible";
                return false;
            }
            detail = string.Empty;
            return true;
        }

        private static bool TryDeserializeAndAdoptReplicationRecruitmentWorker(
            ClientRecruitmentTransfer transfer,
            byte[] bundle,
            out string detail)
        {
            detail = string.Empty;
            HumanoidInstance? worker = null;
            try
            {
                byte[] raw;
                using (var input = new MemoryStream(bundle, writable: false))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    while (true)
                    {
                        var read = gzip.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        if (output.Length + read > ReplicationRecruitmentMaxRawBytes)
                        {
                            detail = "recruitment-decompression-cap";
                            return false;
                        }
                        output.Write(buffer, 0, read);
                    }
                    raw = output.ToArray();
                }
                byte[] data;
                byte[] references;
                using (var stream = new MemoryStream(raw, writable: false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != ReplicationRecruitmentBundleMagic
                        || reader.ReadInt32() != ReplicationRecruitmentBundleVersion
                        || reader.ReadString() != transfer.GameAssemblyMvid
                        || reader.ReadString() != transfer.EventId)
                    {
                        detail = "recruitment-bundle-header";
                        return false;
                    }
                    _ = reader.ReadInt32(); // Host UID is diagnostic only; local UID is collision-safe.
                    data = ReadReplicationRecruitmentBytes(reader);
                    references = ReadReplicationRecruitmentBytes(reader);
                    if (stream.Position != stream.Length)
                    {
                        detail = "recruitment-bundle-trailing-bytes";
                        return false;
                    }
                }
                using (var deserializer = new FVDeserializer(ReplicationRecruitmentWriterId, data))
                {
                    deserializer.ReadReferences(references);
                    var workers = deserializer.ReadObjectList("workers", new List<HumanoidInstance>());
                    worker = workers.Count == 1 ? workers[0] : null;
                }
                if (worker == null)
                {
                    detail = "recruitment-worker-null";
                    return false;
                }

                replicationRecruitmentApplicationDepth++;
                ResetTraderPartyLocalUniqueId(worker);
                WorkerController.Instance.CreateWorker(worker);
                if (!GlobalSaveController.CurrentVillageData.Workers.Contains(worker)
                    || !WorkerManager.Instance.AllWorkers.ContainsKey(worker))
                {
                    throw new InvalidOperationException("Native worker adoption postcondition failed.");
                }
                RegisterReplicationRecruitmentWorker(transfer.EventId, worker);
                detail = "ok recruitment-worker-adopted eventId=" + transfer.EventId
                    + " localUid=" + worker.UniqueId.ToString(CultureInfo.InvariantCulture);
                instance?.LogReplicationInfo("Going Cooperative " + detail);
                return true;
            }
            catch (Exception ex)
            {
                if (worker != null)
                {
                    try { WorkerManager.Instance.RemoveWorker(worker); } catch { }
                    try { GlobalSaveController.CurrentVillageData.Workers.Remove(worker); } catch { }
                }
                detail = "recruitment-adopt=" + ex.GetType().Name + ":" + ex.Message;
                instance?.LogReplicationWarning("Going Cooperative recruitment V1 adoption failed "
                    + detail + "; full-session-resync-required");
                return false;
            }
            finally
            {
                replicationRecruitmentApplicationDepth = Math.Max(0, replicationRecruitmentApplicationDepth - 1);
            }
        }

        private static byte[] ReadReplicationRecruitmentBytes(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > ReplicationRecruitmentMaxRawBytes) throw new InvalidDataException("Recruitment component cap exceeded.");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return bytes;
        }

        private static string ComputeReplicationRecruitmentSha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static void RegisterReplicationRecruitmentWorker(string eventId, HumanoidInstance worker)
        {
            var networkId = "event-recruit:" + eventId;
            lock (ReplicationRecruitmentLock)
            {
                ReplicationRecruitmentWorkerIdByObject[worker] = networkId;
                ReplicationRecruitmentWorkerById[networkId] = worker;
            }
        }

        private static bool TryGetReplicationRecruitmentWorkerNetworkId(object owner, out string networkId)
        {
            lock (ReplicationRecruitmentLock)
                return ReplicationRecruitmentWorkerIdByObject.TryGetValue(owner, out networkId!);
        }

        private static bool TryGetReplicationRecruitmentWorker(string networkId, out object? worker)
        {
            lock (ReplicationRecruitmentLock)
                return ReplicationRecruitmentWorkerById.TryGetValue(networkId, out worker);
        }

        private static void ResetReplicationRecruitmentEventRuntimeState()
        {
            lock (ReplicationRecruitmentLock)
            {
                ReplicationClientRecruitmentTransfers.Clear();
                ReplicationRecruitmentWorkerIdByObject.Clear();
                ReplicationRecruitmentWorkerById.Clear();
            }
            replicationRecruitmentApplicationDepth = 0;
        }

        private sealed class ClientRecruitmentTransfer
        {
            public string Scope = string.Empty;
            public int Epoch;
            public string EventId = string.Empty;
            public string TransferId = string.Empty;
            public int ChunkCount;
            public int ByteCount;
            public string Hash = string.Empty;
            public string GameAssemblyMvid = string.Empty;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();

            public bool SemanticallyMatches(ClientRecruitmentTransfer other)
            {
                return string.Equals(Scope, other.Scope, StringComparison.Ordinal)
                    && Epoch == other.Epoch
                    && string.Equals(EventId, other.EventId, StringComparison.Ordinal)
                    && string.Equals(TransferId, other.TransferId, StringComparison.Ordinal)
                    && ChunkCount == other.ChunkCount
                    && ByteCount == other.ByteCount
                    && string.Equals(Hash, other.Hash, StringComparison.Ordinal)
                    && string.Equals(GameAssemblyMvid, other.GameAssemblyMvid, StringComparison.Ordinal);
            }
        }
    }
}
