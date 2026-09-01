using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const float ReplicationPresenceSendIntervalSeconds = 0.1f;
        private const float ReplicationPresenceTimeoutSeconds = 1.25f;
        private const float ReplicationSelectionHeartbeatSeconds = 2f;
        private const float ReplicationSelectionTimeoutSeconds = 5f;
        private const float ReplicationSelectionResolveSeconds = 0.5f;
        private const float ReplicationPingLifetimeSeconds = 4f;
        private const int ReplicationMaxVisiblePings = 24;
        private const int ReplicationMaxSelectedEntities = 16;

        private static float replicationNextPresenceSendRealtime;
        private static float replicationNextSelectionHeartbeatRealtime;
        private static float replicationNextSelectionResolveRealtime;
        private static float replicationNextPresenceDiagnosticsRealtime;
        private static long replicationPresenceSequence;
        private static long replicationPingSequence;
        private static long replicationSelectionSequence;
        private static Camera? replicationPresenceCamera;
        private static Type? replicationSelectableObjectManagerType;
        private static Type? replicationRaycastUtilsType;
        private static MethodInfo? replicationMouseWorldPositionMethod;
        private static bool replicationMouseWorldPositionMethodResolved;
        private static string replicationPresenceCursorSource = "none";
        private static string replicationLastLocalSelectionSignature = string.Empty;
        private static int replicationLastLocalSelectionUnresolved;
        private static readonly Dictionary<string, ReplicationRemotePresenceState>
            ReplicationRemotePresenceByPeerId =
                new Dictionary<string, ReplicationRemotePresenceState>(
                    StringComparer.Ordinal);
        private static readonly List<ReplicationPresencePingState>
            ReplicationPresencePings =
                new List<ReplicationPresencePingState>();

        private sealed class ReplicationRemotePresenceState
        {
            public ReplicationRemotePresenceState(string peerId)
            {
                PeerId = peerId;
                DisplayName = peerId;
            }

            public string PeerId { get; }
            public string DisplayName { get; set; }
            public long LastPresenceSequence { get; set; }
            public long LastPingSequence { get; set; }
            public long LastSelectionSequence { get; set; }
            public bool CursorVisible { get; set; }
            public bool CursorDisplayInitialized { get; set; }
            public Vector3 CursorWorld { get; set; }
            public Vector3 CursorDisplayWorld { get; set; }
            public float PresenceReceivedRealtime { get; set; }
            public float SelectionReceivedRealtime { get; set; }
            public List<string> SelectedEntityIds { get; } =
                new List<string>();
            public Dictionary<string, Transform> SelectionTransforms { get; } =
                new Dictionary<string, Transform>(StringComparer.Ordinal);
        }

        private sealed class ReplicationPresencePingState
        {
            public string PeerId = string.Empty;
            public long Sequence;
            public Vector3 WorldPosition;
            public float CreatedRealtime;
            public float ExpiresRealtime;
        }

        private void UpdateReplicationPresence()
        {
            var now = Time.realtimeSinceStartup;
            PruneReplicationPresencePings(now);
            if (!replicationRuntimeStarted
                || replicationTransport == null
                || !replicationRemoteHelloReceived
                || multiplayerLoadingInProgress
                || multiplayerMainMenuActive)
            {
                return;
            }

            if (now >= replicationNextPresenceSendRealtime)
            {
                replicationNextPresenceSendRealtime = now + ReplicationPresenceSendIntervalSeconds;
                SendReplicationLocalPresence();
            }

            SendReplicationLocalSelectionIfChangedOrDue(now);

            if (Input.GetKeyDown(KeyCode.F9)
                && TryGetReplicationCursorWorldPoint(out var pingPosition))
            {
                SendReplicationLocalPing(pingPosition);
            }

            LogReplicationPresenceDiagnosticsIfDue(now);
        }

        private void SendReplicationLocalPresence()
        {
            if (replicationTransport == null)
            {
                return;
            }

            var visible = TryGetReplicationCursorWorldPoint(out var world);
            if (!visible)
            {
                world = Vector3.zero;
            }

            try
            {
                var senderId = replicationConfigHostMode ? ReplicationHostPeerId : GetReplicationLocalPeerId();
                var message = new ReplicationPlayerPresence(
                    ++replicationPresenceSequence,
                    visible,
                    world.x,
                    world.y,
                    world.z);
                replicationTransport.Send(ReplicationPresencePayloadCodec.ForPresence(senderId, message));
            }
            catch (Exception ex)
            {
                LogReplicationWarning("[MP/NET] presence send failed error="
                    + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void SendReplicationLocalSelectionIfChangedOrDue(float now)
        {
            if (replicationTransport == null)
            {
                return;
            }

            var entityIds = CollectReplicationLocalSelectedEntityIds(out var unresolved);
            var signature = string.Join("\n", entityIds);
            if (string.Equals(signature, replicationLastLocalSelectionSignature, StringComparison.Ordinal)
                && now < replicationNextSelectionHeartbeatRealtime)
            {
                replicationLastLocalSelectionUnresolved = unresolved;
                return;
            }

            replicationLastLocalSelectionSignature = signature;
            replicationLastLocalSelectionUnresolved = unresolved;
            replicationNextSelectionHeartbeatRealtime = now + ReplicationSelectionHeartbeatSeconds;
            try
            {
                var senderId = replicationConfigHostMode ? ReplicationHostPeerId : GetReplicationLocalPeerId();
                replicationTransport.Send(ReplicationPresencePayloadCodec.ForSelection(
                    senderId,
                    new ReplicationPlayerSelection(++replicationSelectionSequence, entityIds)));
            }
            catch (Exception ex)
            {
                LogReplicationWarning("[MP/NET] selection send failed error="
                    + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void SendReplicationLocalPing(Vector3 world)
        {
            if (replicationTransport == null)
            {
                return;
            }

            var sequence = ++replicationPingSequence;
            AddReplicationPresencePing(
                GetReplicationLocalPeerId(),
                sequence,
                world,
                Time.realtimeSinceStartup);
            try
            {
                var senderId = replicationConfigHostMode ? ReplicationHostPeerId : GetReplicationLocalPeerId();
                replicationTransport.Send(ReplicationPresencePayloadCodec.ForPing(
                    senderId,
                    new ReplicationPlayerPing(sequence, world.x, world.y, world.z)));
                LogReplicationInfo("[MP/NET] ping sent sequence=" + sequence);
            }
            catch (Exception ex)
            {
                LogReplicationWarning("[MP/NET] ping send failed error="
                    + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void HandleReplicationPlayerPresence(
            TransportEnvelope envelope)
        {
            if (!ReplicationPresencePayloadCodec.TryReadPresence(
                    envelope,
                    out var presence,
                    out var error)
                || presence == null)
            {
                LogReplicationWarning(
                    "[MP/NET] presence decode failed error=" + error);
                return;
            }

            if (!TryGetReplicationRemotePresenceState(
                    envelope.SenderId,
                    create: true,
                    out var state)
                || state == null
                || presence.Sequence <= state.LastPresenceSequence)
            {
                return;
            }

            state.LastPresenceSequence = presence.Sequence;
            state.CursorVisible = presence.Visible;
            state.CursorWorld = new Vector3(
                presence.WorldX,
                presence.WorldY,
                presence.WorldZ);
            state.PresenceReceivedRealtime = Time.realtimeSinceStartup;
            if (!state.CursorDisplayInitialized && presence.Visible)
            {
                state.CursorDisplayWorld = state.CursorWorld;
                state.CursorDisplayInitialized = true;
            }

            RelayReplicationPresenceFromClientIfNeeded(envelope);
        }

        private void HandleReplicationPlayerPing(
            TransportEnvelope envelope)
        {
            if (!ReplicationPresencePayloadCodec.TryReadPing(
                    envelope,
                    out var ping,
                    out var error)
                || ping == null)
            {
                LogReplicationWarning(
                    "[MP/NET] ping decode failed error=" + error);
                return;
            }

            if (!TryGetReplicationRemotePresenceState(
                    envelope.SenderId,
                    create: true,
                    out var state)
                || state == null
                || ping.Sequence <= state.LastPingSequence)
            {
                return;
            }

            state.LastPingSequence = ping.Sequence;
            AddReplicationPresencePing(
                envelope.SenderId,
                ping.Sequence,
                new Vector3(
                    ping.WorldX,
                    ping.WorldY,
                    ping.WorldZ),
                Time.realtimeSinceStartup);
            RelayReplicationPresenceFromClientIfNeeded(envelope);
            LogReplicationInfo(
                "[MP/NET] ping received peer="
                + envelope.SenderId
                + " sequence="
                + ping.Sequence.ToString());
        }

        private void HandleReplicationPlayerSelection(
            TransportEnvelope envelope)
        {
            if (!ReplicationPresencePayloadCodec.TryReadSelection(
                    envelope,
                    out var selection,
                    out var error)
                || selection == null)
            {
                LogReplicationWarning(
                    "[MP/NET] selection decode failed error=" + error);
                return;
            }

            if (!TryGetReplicationRemotePresenceState(
                    envelope.SenderId,
                    create: true,
                    out var state)
                || state == null
                || selection.Sequence <= state.LastSelectionSequence)
            {
                return;
            }

            state.LastSelectionSequence = selection.Sequence;
            state.SelectionReceivedRealtime = Time.realtimeSinceStartup;
            state.SelectedEntityIds.Clear();
            for (var i = 0; i < selection.EntityIds.Count; i++)
            {
                state.SelectedEntityIds.Add(selection.EntityIds[i]);
            }

            state.SelectionTransforms.Clear();
            replicationNextSelectionResolveRealtime = 0f;
            RelayReplicationPresenceFromClientIfNeeded(envelope);
        }

        private void RelayReplicationPresenceFromClientIfNeeded(
            TransportEnvelope envelope)
        {
            if (!replicationConfigHostMode
                || replicationTransport == null
                || !MultiplayerPeerIds.TryParseClientSlot(
                    envelope.SenderId,
                    out _)
                || !ReplicationCompatiblePeerIds.Contains(
                    envelope.SenderId))
            {
                return;
            }

            try
            {
                replicationTransport.SendToAllExcept(
                    envelope.SenderId,
                    envelope);
            }
            catch (Exception ex)
            {
                LogReplicationWarning(
                    "[MP/PRESENCE] relay failed peer="
                    + envelope.SenderId
                    + " kind="
                    + envelope.Kind
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private static List<string> CollectReplicationLocalSelectedEntityIds(out int unresolved)
        {
            unresolved = 0;
            var result = new List<string>();
            if (!TryGetReplicationSelectableObjectManager(out var manager)
                || !TryReadInstanceMemberValue(manager, "SelectedObjects", out var selectedObjects)
                || selectedObjects is not IEnumerable enumerable)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var selected in enumerable)
            {
                if (selected == null)
                {
                    continue;
                }

                if (TryGetReplicationSelectedEntityId(selected, 0, out var entityId))
                {
                    if (seen.Add(entityId))
                    {
                        result.Add(entityId);
                        if (result.Count >= ReplicationMaxSelectedEntities)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    unresolved++;
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static bool TryGetReplicationSelectableObjectManager(out object manager)
        {
            manager = null!;
            var type = replicationSelectableObjectManagerType
                ??= AccessTools.TypeByName("NSMedieval.Manager.SelectableObjectManager");
            if (type == null)
            {
                return false;
            }

            for (var current = type; current != null; current = current.BaseType)
            {
                try
                {
                    var property = current.GetProperty(
                        "Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        var value = property.GetValue(null, null);
                        if (value != null)
                        {
                            manager = value;
                            return true;
                        }
                    }

                    var field = current.GetField(
                        "Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field != null)
                    {
                        var value = field.GetValue(null);
                        if (value != null)
                        {
                            manager = value;
                            return true;
                        }
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryGetReplicationSelectedEntityId(object selected, int depth, out string entityId)
        {
            entityId = string.Empty;
            if (selected == null || depth > 2)
            {
                return false;
            }

            if (TryGetReplicationViewEntityId(selected, out entityId))
            {
                return true;
            }

            if (selected is Component component
                && TryGetReplicationAnimatedAgentViewFromComponent(component, out var componentView)
                && componentView != null
                && TryGetReplicationViewEntityId(componentView, out entityId))
            {
                return true;
            }

            if (selected is GameObject gameObject
                && TryGetReplicationAnimatedAgentViewFromGameObject(gameObject, out var gameObjectView)
                && gameObjectView != null
                && TryGetReplicationViewEntityId(gameObjectView, out entityId))
            {
                return true;
            }

            foreach (var memberName in new[] { "View", "WorkerView", "SelectableObject" })
            {
                if (TryReadInstanceMemberValue(selected, memberName, out var nested)
                    && nested != null
                    && !ReferenceEquals(nested, selected)
                    && TryGetReplicationSelectedEntityId(nested, depth + 1, out entityId))
                {
                    return true;
                }
            }

            // Last-resort fallback for versions where SelectedObjects exposes
            // an owner/data object rather than the actual AnimatedAgentView.
            return TryGetReplicationStableEntityId(selected, out entityId);
        }

        private static bool TryGetReplicationAnimatedAgentViewFromComponent(
            Component component,
            out UnityEngine.Object? view)
        {
            view = null;
            var type = AccessTools.TypeByName("NSMedieval.View.AnimatedAgentView");
            if (type == null)
            {
                return false;
            }

            try
            {
                view = component.GetComponent(type)
                    ?? component.GetComponentInParent(type)
                    ?? component.GetComponentInChildren(type);
                return view != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetReplicationAnimatedAgentViewFromGameObject(
            GameObject gameObject,
            out UnityEngine.Object? view)
        {
            view = null;
            var type = AccessTools.TypeByName("NSMedieval.View.AnimatedAgentView");
            if (type == null)
            {
                return false;
            }

            try
            {
                view = gameObject.GetComponent(type)
                    ?? gameObject.GetComponentInParent(type)
                    ?? gameObject.GetComponentInChildren(type);
                return view != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetReplicationCursorWorldPoint(out Vector3 world)
        {
            world = Vector3.zero;
            var camera = GetReplicationPresenceCamera();
            if (camera == null)
            {
                replicationPresenceCursorSource = "no-camera";
                return false;
            }

            var mouse = Input.mousePosition;
            if (mouse.x < 0f
                || mouse.y < 0f
                || mouse.x > Screen.width
                || mouse.y > Screen.height)
            {
                replicationPresenceCursorSource = "mouse-outside-screen";
                return false;
            }

            if (TryGetReplicationGameMouseWorldPoint(camera, mouse, out world))
            {
                replicationPresenceCursorSource = "game-raycast";
                return true;
            }

            var ray = camera.ScreenPointToRay(mouse);
            if (Physics.Raycast(
                    ray,
                    out var hit,
                    10000f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                world = hit.point;
                if (IsReplicationPresenceWorldPointValid(world))
                {
                    replicationPresenceCursorSource = "physics";
                    return true;
                }
            }

            // Going Medieval's terrain picking can bypass Unity colliders. A plane
            // fallback still preserves a useful map-space cursor instead of hiding it.
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out var enter) && enter > 0f && enter <= 10000f)
            {
                world = ray.GetPoint(enter);
                if (IsReplicationPresenceWorldPointValid(world))
                {
                    replicationPresenceCursorSource = "plane-fallback";
                    return true;
                }
            }

            replicationPresenceCursorSource = "no-world-hit";
            return false;
        }

        private static bool TryGetReplicationGameMouseWorldPoint(
            Camera camera,
            Vector3 mousePosition,
            out Vector3 world)
        {
            world = Vector3.zero;
            if (!replicationMouseWorldPositionMethodResolved)
            {
                ResolveReplicationMouseWorldPositionMethod();
            }

            var method = replicationMouseWorldPositionMethod;
            if (method == null)
            {
                return false;
            }

            try
            {
                var parameters = method.GetParameters();
                var arguments = new object?[parameters.Length];
                var outVectorIndex = -1;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameterType = parameters[i].ParameterType;
                    if (parameterType.IsByRef
                        && parameterType.GetElementType() == typeof(Vector3))
                    {
                        arguments[i] = Vector3.zero;
                        outVectorIndex = i;
                    }
                    else if (parameterType == typeof(Camera))
                    {
                        arguments[i] = camera;
                    }
                    else if (parameterType == typeof(Vector3))
                    {
                        arguments[i] = mousePosition;
                    }
                    else if (parameterType == typeof(Vector2))
                    {
                        arguments[i] = new Vector2(mousePosition.x, mousePosition.y);
                    }
                    else if (parameters[i].HasDefaultValue)
                    {
                        arguments[i] = parameters[i].DefaultValue;
                    }
                    else
                    {
                        return false;
                    }
                }

                var result = method.Invoke(null, arguments);
                if (result is Vector3 returnedVector
                    && IsReplicationPresenceWorldPointValid(returnedVector))
                {
                    world = returnedVector;
                    return true;
                }

                if (result is bool succeeded
                    && succeeded
                    && outVectorIndex >= 0
                    && arguments[outVectorIndex] is Vector3 outVector
                    && IsReplicationPresenceWorldPointValid(outVector))
                {
                    world = outVector;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ResolveReplicationMouseWorldPositionMethod()
        {
            replicationMouseWorldPositionMethodResolved = true;
            replicationRaycastUtilsType ??= AccessTools.TypeByName("NSMedieval.Tools.RaycastUtils");
            var type = replicationRaycastUtilsType;
            if (type == null)
            {
                return;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (!string.Equals(method.Name, "GetMouseWorldPosition", StringComparison.Ordinal))
                {
                    continue;
                }

                if (method.ReturnType != typeof(Vector3) && method.ReturnType != typeof(bool))
                {
                    continue;
                }

                var supported = true;
                var parameters = method.GetParameters();
                for (var p = 0; p < parameters.Length; p++)
                {
                    var parameterType = parameters[p].ParameterType;
                    var supportedType = parameterType == typeof(Camera)
                        || parameterType == typeof(Vector2)
                        || parameterType == typeof(Vector3)
                        || (parameterType.IsByRef && parameterType.GetElementType() == typeof(Vector3))
                        || parameters[p].HasDefaultValue;
                    if (!supportedType)
                    {
                        supported = false;
                        break;
                    }
                }

                if (supported)
                {
                    replicationMouseWorldPositionMethod = method;
                    return;
                }
            }
        }

        private static Camera? GetReplicationPresenceCamera()
        {
            if (replicationPresenceCamera != null
                && replicationPresenceCamera.isActiveAndEnabled)
            {
                return replicationPresenceCamera;
            }

            replicationPresenceCamera = Camera.main;
            if (replicationPresenceCamera != null)
            {
                return replicationPresenceCamera;
            }

            var cameras = Camera.allCameras;
            for (var i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].isActiveAndEnabled)
                {
                    replicationPresenceCamera = cameras[i];
                    break;
                }
            }

            return replicationPresenceCamera;
        }

        private static void RemoveReplicationRemotePeerPresence(
            string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId))
            {
                return;
            }

            ReplicationRemotePresenceByPeerId.Remove(peerId);
            for (var i = ReplicationPresencePings.Count - 1; i >= 0; i--)
            {
                if (string.Equals(
                        ReplicationPresencePings[i].PeerId,
                        peerId,
                        StringComparison.Ordinal))
                {
                    ReplicationPresencePings.RemoveAt(i);
                }
            }
        }

        private static void SetReplicationRemotePeerDisplayName(
            string peerId,
            string displayName)
        {
            if (string.IsNullOrWhiteSpace(peerId)
                || string.Equals(
                    peerId,
                    GetReplicationLocalPeerId(),
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!ReplicationRemotePresenceByPeerId.TryGetValue(
                    peerId,
                    out var state))
            {
                state = new ReplicationRemotePresenceState(peerId);
                ReplicationRemotePresenceByPeerId.Add(peerId, state);
            }

            state.DisplayName =
                MultiplayerNickname.Normalize(displayName);
        }

        private static string GetReplicationRemoteDisplayName(
            string peerId)
        {
            return ReplicationRemotePresenceByPeerId.TryGetValue(
                    peerId,
                    out var state)
                ? MultiplayerNickname.Normalize(state.DisplayName)
                : peerId;
        }

        private static string GetReplicationRemoteDisplayName()
        {
            var preferredPeerId = replicationConfigHostMode
                ? string.Empty
                : ReplicationHostPeerId;
            if (preferredPeerId.Length > 0
                && ReplicationRemotePresenceByPeerId.TryGetValue(
                    preferredPeerId,
                    out var preferred))
            {
                return MultiplayerNickname.Normalize(
                    preferred.DisplayName);
            }

            foreach (var state in ReplicationRemotePresenceByPeerId.Values)
            {
                return MultiplayerNickname.Normalize(state.DisplayName);
            }

            return MultiplayerNickname.DefaultNickname;
        }

        private static bool TryGetReplicationRemotePresenceState(
            string peerId,
            bool create,
            out ReplicationRemotePresenceState? state)
        {
            state = null;
            if (string.IsNullOrWhiteSpace(peerId)
                || string.Equals(
                    peerId,
                    GetReplicationLocalPeerId(),
                    StringComparison.Ordinal))
            {
                return false;
            }

            var validPeer = string.Equals(
                    peerId,
                    ReplicationHostPeerId,
                    StringComparison.Ordinal)
                || MultiplayerPeerIds.TryParseClientSlot(
                    peerId,
                    out _);
            if (!validPeer)
            {
                return false;
            }

            if (ReplicationRemotePresenceByPeerId.TryGetValue(
                    peerId,
                    out state))
            {
                return true;
            }

            if (!create)
            {
                return false;
            }

            state = new ReplicationRemotePresenceState(peerId);
            ReplicationRemotePresenceByPeerId.Add(peerId, state);
            return true;
        }

        private static List<ReplicationRemotePresenceState>
            GetReplicationRemotePresenceStates()
        {
            var now = Time.realtimeSinceStartup;
            var result = new List<ReplicationRemotePresenceState>();
            foreach (var state in ReplicationRemotePresenceByPeerId.Values)
            {
                if (string.Equals(
                        state.PeerId,
                        GetReplicationLocalPeerId(),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var hasCursor =
                    now - state.PresenceReceivedRealtime
                        <= ReplicationPresenceTimeoutSeconds;
                var hasSelection =
                    now - state.SelectionReceivedRealtime
                        <= ReplicationSelectionTimeoutSeconds
                    && state.SelectedEntityIds.Count > 0;
                if (hasCursor || hasSelection)
                {
                    result.Add(state);
                }
            }

            result.Sort(
                (left, right) => string.Compare(
                    left.PeerId,
                    right.PeerId,
                    StringComparison.Ordinal));
            return result;
        }

        private static bool TryGetReplicationRemotePresenceWorldPoint(
            string peerId,
            out Vector3 world)
        {
            world = Vector3.zero;
            if (!TryGetReplicationRemotePresenceState(
                    peerId,
                    create: false,
                    out var state)
                || state == null
                || !state.CursorVisible
                || Time.realtimeSinceStartup
                    - state.PresenceReceivedRealtime
                    > ReplicationPresenceTimeoutSeconds)
            {
                return false;
            }

            if (!state.CursorDisplayInitialized)
            {
                state.CursorDisplayWorld = state.CursorWorld;
                state.CursorDisplayInitialized = true;
            }
            else
            {
                var blend = 1f - Mathf.Exp(
                    -18f * Mathf.Max(0f, Time.unscaledDeltaTime));
                state.CursorDisplayWorld = Vector3.Lerp(
                    state.CursorDisplayWorld,
                    state.CursorWorld,
                    blend);
            }

            world = state.CursorDisplayWorld;
            return true;
        }

        private static IReadOnlyList<string>
            GetReplicationRemoteSelectedEntityIds(string peerId)
        {
            if (!TryGetReplicationRemotePresenceState(
                    peerId,
                    create: false,
                    out var state)
                || state == null
                || Time.realtimeSinceStartup
                    - state.SelectionReceivedRealtime
                    > ReplicationSelectionTimeoutSeconds)
            {
                return Array.Empty<string>();
            }

            return state.SelectedEntityIds;
        }

        private static bool TryGetReplicationRemoteSelectedEntityWorldPoint(
            string peerId,
            string entityId,
            out Vector3 world)
        {
            world = Vector3.zero;
            RefreshReplicationRemoteSelectionTransformsIfDue();
            if (!TryGetReplicationRemotePresenceState(
                    peerId,
                    create: false,
                    out var state)
                || state == null
                || !state.SelectionTransforms.TryGetValue(
                    entityId,
                    out var transform)
                || transform == null)
            {
                return false;
            }

            world = transform.position + Vector3.up * 1.65f;
            return IsReplicationPresenceWorldPointValid(world);
        }

        private static void RefreshReplicationRemoteSelectionTransformsIfDue()
        {
            var now = Time.realtimeSinceStartup;
            if (now < replicationNextSelectionResolveRealtime)
            {
                return;
            }

            replicationNextSelectionResolveRealtime =
                now + ReplicationSelectionResolveSeconds;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in ReplicationRemotePresenceByPeerId.Values)
            {
                state.SelectionTransforms.Clear();
                if (now - state.SelectionReceivedRealtime
                    > ReplicationSelectionTimeoutSeconds)
                {
                    continue;
                }

                for (var i = 0;
                    i < state.SelectedEntityIds.Count;
                    i++)
                {
                    wanted.Add(state.SelectedEntityIds[i]);
                }
            }

            if (wanted.Count == 0)
            {
                return;
            }

            var resolved =
                new Dictionary<string, Transform>(StringComparer.Ordinal);
            var views = FindReplicationAnimatedAgentViews();
            for (var i = 0;
                i < views.Length && wanted.Count > 0;
                i++)
            {
                var view = views[i];
                if (view == null
                    || view is not MonoBehaviour behaviour
                    || !TryGetReplicationViewEntityId(
                        view,
                        out var entityId)
                    || !wanted.Remove(entityId))
                {
                    continue;
                }

                resolved[entityId] = behaviour.transform;
            }

            foreach (var state in ReplicationRemotePresenceByPeerId.Values)
            {
                for (var i = 0;
                    i < state.SelectedEntityIds.Count;
                    i++)
                {
                    var entityId = state.SelectedEntityIds[i];
                    if (resolved.TryGetValue(
                            entityId,
                            out var transform))
                    {
                        state.SelectionTransforms[entityId] = transform;
                    }
                }
            }
        }

        private static bool IsReplicationPresenceWorldPointValid(Vector3 world)
        {
            return IsReplicationPresenceFinite(world.x)
                && IsReplicationPresenceFinite(world.y)
                && IsReplicationPresenceFinite(world.z)
                && Mathf.Abs(world.x) <= 1000000f
                && Mathf.Abs(world.y) <= 1000000f
                && Mathf.Abs(world.z) <= 1000000f;
        }

        private static bool IsReplicationPresenceFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void AddReplicationPresencePing(
            string peerId,
            long sequence,
            Vector3 world,
            float now)
        {
            while (ReplicationPresencePings.Count
                >= ReplicationMaxVisiblePings)
            {
                ReplicationPresencePings.RemoveAt(0);
            }

            ReplicationPresencePings.Add(
                new ReplicationPresencePingState
                {
                    PeerId = peerId,
                    Sequence = sequence,
                    WorldPosition = world,
                    CreatedRealtime = now,
                    ExpiresRealtime =
                        now + ReplicationPingLifetimeSeconds
                });
        }

        private static void PruneReplicationPresencePings(float now)
        {
            for (var i = ReplicationPresencePings.Count - 1; i >= 0; i--)
            {
                if (now >= ReplicationPresencePings[i].ExpiresRealtime)
                {
                    ReplicationPresencePings.RemoveAt(i);
                }
            }
        }

        private void LogReplicationPresenceDiagnosticsIfDue(float now)
        {
            if (!replicationConfigPathingPerfDiagnostics
                || now < replicationNextPresenceDiagnosticsRealtime)
            {
                return;
            }

            replicationNextPresenceDiagnosticsRealtime = now + 10f;
            var visibleCursors = 0;
            var remoteSelected = 0;
            var remoteResolved = 0;
            foreach (var state in ReplicationRemotePresenceByPeerId.Values)
            {
                if (state.CursorVisible
                    && now - state.PresenceReceivedRealtime
                        <= ReplicationPresenceTimeoutSeconds)
                {
                    visibleCursors++;
                }

                if (now - state.SelectionReceivedRealtime
                    <= ReplicationSelectionTimeoutSeconds)
                {
                    remoteSelected += state.SelectedEntityIds.Count;
                    remoteResolved += state.SelectionTransforms.Count;
                }
            }

            LogReplicationInfo(
                "[MP/PRESENCE]"
                + " cursorSource="
                + replicationPresenceCursorSource
                + " remotePeers="
                + ReplicationRemotePresenceByPeerId.Count.ToString()
                + " visibleCursors="
                + visibleCursors.ToString()
                + " localSelected="
                + (replicationLastLocalSelectionSignature.Length == 0
                    ? "0"
                    : replicationLastLocalSelectionSignature
                        .Split('\n').Length.ToString())
                + " localSelectionUnresolved="
                + replicationLastLocalSelectionUnresolved.ToString()
                + " remoteSelected="
                + remoteSelected.ToString()
                + " remoteResolved="
                + remoteResolved.ToString());
        }

        private static void ResetReplicationPresence()
        {
            replicationNextPresenceSendRealtime = 0f;
            replicationNextSelectionHeartbeatRealtime = 0f;
            replicationNextSelectionResolveRealtime = 0f;
            replicationNextPresenceDiagnosticsRealtime = 0f;
            replicationPresenceSequence = 0L;
            replicationPingSequence = 0L;
            replicationSelectionSequence = 0L;
            replicationPresenceCamera = null;
            replicationSelectableObjectManagerType = null;
            replicationRaycastUtilsType = null;
            replicationMouseWorldPositionMethod = null;
            replicationMouseWorldPositionMethodResolved = false;
            replicationPresenceCursorSource = "none";
            replicationLastLocalSelectionSignature = string.Empty;
            replicationLastLocalSelectionUnresolved = 0;
            ReplicationRemotePresenceByPeerId.Clear();
            ReplicationPresencePings.Clear();
        }
    }
}
