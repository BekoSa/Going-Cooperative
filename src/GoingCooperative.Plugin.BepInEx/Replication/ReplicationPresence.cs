using System;
using System.Collections.Generic;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const float ReplicationPresenceSendIntervalSeconds = 0.1f;
        private const float ReplicationPresenceTimeoutSeconds = 1.25f;
        private const float ReplicationPingLifetimeSeconds = 4f;
        private const int ReplicationMaxVisiblePings = 8;

        private static float replicationNextPresenceSendRealtime;
        private static long replicationPresenceSequence;
        private static long replicationPingSequence;
        private static long replicationLastRemotePresenceSequence;
        private static long replicationLastRemotePingSequence;
        private static bool replicationRemotePresenceVisible;
        private static bool replicationRemotePresenceDisplayInitialized;
        private static Vector3 replicationRemotePresenceWorld;
        private static Vector3 replicationRemotePresenceDisplayWorld;
        private static float replicationRemotePresenceReceivedRealtime;
        private static Camera? replicationPresenceCamera;
        private static readonly List<ReplicationPresencePingState> ReplicationPresencePings =
            new List<ReplicationPresencePingState>();

        private sealed class ReplicationPresencePingState
        {
            public long Sequence;
            public bool Remote;
            public Vector3 WorldPosition;
            public float CreatedRealtime;
            public float ExpiresRealtime;
        }

        private void UpdateReplicationPresence()
        {
            var now = Time.realtimeSinceStartup;
            PruneReplicationPresencePings(now);
            if (!replicationRuntimeStarted || replicationTransport == null || !replicationRemoteHelloReceived
                || multiplayerLoadingInProgress || multiplayerMainMenuActive) return;

            if (now >= replicationNextPresenceSendRealtime)
            {
                replicationNextPresenceSendRealtime = now + ReplicationPresenceSendIntervalSeconds;
                SendReplicationLocalPresence();
            }

            if (Input.GetKeyDown(KeyCode.F9) && TryGetReplicationCursorWorldPoint(out var pingPosition))
            {
                SendReplicationLocalPing(pingPosition);
            }
        }

        private void SendReplicationLocalPresence()
        {
            if (replicationTransport == null) return;
            var visible = TryGetReplicationCursorWorldPoint(out var world);
            if (!visible) world = Vector3.zero;
            try
            {
                var senderId = replicationConfigHostMode ? ReplicationHostPeerId : ReplicationClientPeerId;
                var message = new ReplicationPlayerPresence(++replicationPresenceSequence, visible, world.x, world.y, world.z);
                replicationTransport.Send(ReplicationPresencePayloadCodec.ForPresence(senderId, message));
            }
            catch (Exception ex)
            {
                LogReplicationWarning("[MP/NET] presence send failed error=" + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void SendReplicationLocalPing(Vector3 world)
        {
            if (replicationTransport == null) return;
            var sequence = ++replicationPingSequence;
            AddReplicationPresencePing(sequence, false, world, Time.realtimeSinceStartup);
            try
            {
                var senderId = replicationConfigHostMode ? ReplicationHostPeerId : ReplicationClientPeerId;
                replicationTransport.Send(ReplicationPresencePayloadCodec.ForPing(
                    senderId, new ReplicationPlayerPing(sequence, world.x, world.y, world.z)));
                LogReplicationInfo("[MP/NET] ping sent sequence=" + sequence);
            }
            catch (Exception ex)
            {
                LogReplicationWarning("[MP/NET] ping send failed error=" + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void HandleReplicationPlayerPresence(TransportEnvelope envelope)
        {
            if (!ReplicationPresencePayloadCodec.TryReadPresence(envelope, out var presence, out var error)
                || presence == null)
            {
                LogReplicationWarning("[MP/NET] presence decode failed error=" + error);
                return;
            }
            if (presence.Sequence <= replicationLastRemotePresenceSequence) return;
            replicationLastRemotePresenceSequence = presence.Sequence;
            replicationRemotePresenceVisible = presence.Visible;
            replicationRemotePresenceWorld = new Vector3(presence.WorldX, presence.WorldY, presence.WorldZ);
            replicationRemotePresenceReceivedRealtime = Time.realtimeSinceStartup;
            if (!replicationRemotePresenceDisplayInitialized && presence.Visible)
            {
                replicationRemotePresenceDisplayWorld = replicationRemotePresenceWorld;
                replicationRemotePresenceDisplayInitialized = true;
            }
        }

        private void HandleReplicationPlayerPing(TransportEnvelope envelope)
        {
            if (!ReplicationPresencePayloadCodec.TryReadPing(envelope, out var ping, out var error) || ping == null)
            {
                LogReplicationWarning("[MP/NET] ping decode failed error=" + error);
                return;
            }
            if (ping.Sequence <= replicationLastRemotePingSequence) return;
            replicationLastRemotePingSequence = ping.Sequence;
            AddReplicationPresencePing(ping.Sequence, true, new Vector3(ping.WorldX, ping.WorldY, ping.WorldZ), Time.realtimeSinceStartup);
            LogReplicationInfo("[MP/NET] ping received sequence=" + ping.Sequence);
        }

        private static bool TryGetReplicationCursorWorldPoint(out Vector3 world)
        {
            world = Vector3.zero;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return false;
            var camera = GetReplicationPresenceCamera();
            if (camera == null) return false;
            var mouse = Input.mousePosition;
            if (mouse.x < 0f || mouse.y < 0f || mouse.x > Screen.width || mouse.y > Screen.height) return false;
            var ray = camera.ScreenPointToRay(mouse);
            if (!Physics.Raycast(ray, out var hit, 10000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            world = hit.point;
            return IsReplicationPresenceFinite(world.x) && IsReplicationPresenceFinite(world.y) && IsReplicationPresenceFinite(world.z);
        }

        private static Camera? GetReplicationPresenceCamera()
        {
            if (replicationPresenceCamera != null && replicationPresenceCamera.isActiveAndEnabled) return replicationPresenceCamera;
            replicationPresenceCamera = Camera.main;
            if (replicationPresenceCamera != null) return replicationPresenceCamera;
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

        private static bool TryGetReplicationRemotePresenceWorldPoint(out Vector3 world)
        {
            world = replicationRemotePresenceDisplayWorld;
            if (!replicationRemotePresenceVisible
                || Time.realtimeSinceStartup - replicationRemotePresenceReceivedRealtime > ReplicationPresenceTimeoutSeconds) return false;
            if (!replicationRemotePresenceDisplayInitialized)
            {
                replicationRemotePresenceDisplayWorld = replicationRemotePresenceWorld;
                replicationRemotePresenceDisplayInitialized = true;
            }
            else
            {
                var blend = 1f - Mathf.Exp(-18f * Mathf.Max(0f, Time.unscaledDeltaTime));
                replicationRemotePresenceDisplayWorld = Vector3.Lerp(
                    replicationRemotePresenceDisplayWorld, replicationRemotePresenceWorld, blend);
            }
            world = replicationRemotePresenceDisplayWorld;
            return true;
        }

        private static bool IsReplicationPresenceFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void AddReplicationPresencePing(long sequence, bool remote, Vector3 world, float now)
        {
            while (ReplicationPresencePings.Count >= ReplicationMaxVisiblePings) ReplicationPresencePings.RemoveAt(0);
            ReplicationPresencePings.Add(new ReplicationPresencePingState
            {
                Sequence = sequence,
                Remote = remote,
                WorldPosition = world,
                CreatedRealtime = now,
                ExpiresRealtime = now + ReplicationPingLifetimeSeconds
            });
        }

        private static void PruneReplicationPresencePings(float now)
        {
            for (var i = ReplicationPresencePings.Count - 1; i >= 0; i--)
            {
                if (now >= ReplicationPresencePings[i].ExpiresRealtime) ReplicationPresencePings.RemoveAt(i);
            }
        }

        private static void ResetReplicationPresence()
        {
            replicationNextPresenceSendRealtime = 0f;
            replicationPresenceSequence = replicationPingSequence = 0L;
            replicationLastRemotePresenceSequence = replicationLastRemotePingSequence = 0L;
            replicationRemotePresenceVisible = false;
            replicationRemotePresenceDisplayInitialized = false;
            replicationRemotePresenceWorld = replicationRemotePresenceDisplayWorld = Vector3.zero;
            replicationRemotePresenceReceivedRealtime = 0f;
            replicationPresenceCamera = null;
            ReplicationPresencePings.Clear();
        }
    }
}
