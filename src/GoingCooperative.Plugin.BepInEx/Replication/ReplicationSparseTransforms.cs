using System;
using System.Collections.Generic;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const float ReplicationIdleTransformHeartbeatSeconds = 1f;
        private const float ReplicationSparseTransformPruneSeconds = 10f;
        private const float ReplicationSparsePositionThresholdSqr = 0.0025f;
        private const float ReplicationSparseRotationDotThreshold = 0.9995f;
        private static readonly Dictionary<string, ReplicationSparseTransformState> ReplicationSparseTransformStates =
            new Dictionary<string, ReplicationSparseTransformState>(StringComparer.Ordinal);
        private static readonly List<string> ReplicationSparseTransformExpiredIds = new List<string>();
        private static float replicationNextSparseTransformPruneRealtime;
        private static long replicationSparseTransformRowsSent;
        private static long replicationSparseTransformRowsSuppressed;

        private sealed class ReplicationSparseTransformState
        {
            public Vector3 LastSentPosition;
            public Quaternion LastSentRotation;
            public bool LastMoving;
            public long LastPathRevision;
            public float LastSentRealtime;
            public float LastObservedRealtime;
        }

        private static ReplicationTransformSnapshot PrepareReplicationTransformSnapshotForWire(ReplicationTransformSnapshot snapshot)
        {
            var now = Time.realtimeSinceStartup;
            var rows = new List<ReplicationEntityTransform>(snapshot.Entities.Count);
            for (var i = 0; i < snapshot.Entities.Count; i++)
            {
                var entity = snapshot.Entities[i];
                var position = new Vector3(entity.PositionX, entity.PositionY, entity.PositionZ);
                var rotation = new Quaternion(entity.RotationX, entity.RotationY, entity.RotationZ, entity.RotationW);
                var motion = entity.Motion;
                var moving = IsReplicationTransformMoving(motion);
                var pathRevision = motion.HasValue ? motion.Value.PathRevision : 0L;

                if (!ReplicationSparseTransformStates.TryGetValue(entity.EntityId, out var state))
                {
                    state = new ReplicationSparseTransformState();
                    ReplicationSparseTransformStates[entity.EntityId] = state;
                    RecordReplicationSparseTransformRow(state, position, rotation, moving, pathRevision, now);
                    rows.Add(entity);
                    replicationSparseTransformRowsSent++;
                    continue;
                }

                state.LastObservedRealtime = now;
                var positionChanged = (position - state.LastSentPosition).sqrMagnitude >= ReplicationSparsePositionThresholdSqr;
                var rotationChanged = Mathf.Abs(Quaternion.Dot(rotation, state.LastSentRotation)) < ReplicationSparseRotationDotThreshold;
                var movingChanged = moving != state.LastMoving;
                var pathChanged = pathRevision != state.LastPathRevision;
                var heartbeatDue = now - state.LastSentRealtime >= ReplicationIdleTransformHeartbeatSeconds;

                if (moving || positionChanged || rotationChanged || movingChanged || pathChanged || heartbeatDue)
                {
                    RecordReplicationSparseTransformRow(state, position, rotation, moving, pathRevision, now);
                    rows.Add(entity);
                    replicationSparseTransformRowsSent++;
                }
                else
                {
                    replicationSparseTransformRowsSuppressed++;
                }
            }

            PruneReplicationSparseTransformStatesIfDue(now);
            return new ReplicationTransformSnapshot(snapshot.Sequence, snapshot.SentRealtime, rows);
        }

        private static bool IsReplicationTransformMoving(ReplicationEntityMotionMetadata? motion)
        {
            if (!motion.HasValue) return false;
            var value = motion.Value;
            return value.IsMoving || value.IsRunning || value.IsSwimming || value.IsClimbing
                || value.MovementSpeed >= 0.01f
                || value.VelocityX * value.VelocityX
                    + value.VelocityY * value.VelocityY
                    + value.VelocityZ * value.VelocityZ >= 0.0025f;
        }

        private static void RecordReplicationSparseTransformRow(
            ReplicationSparseTransformState state,
            Vector3 position,
            Quaternion rotation,
            bool moving,
            long pathRevision,
            float now)
        {
            state.LastSentPosition = position;
            state.LastSentRotation = rotation;
            state.LastMoving = moving;
            state.LastPathRevision = pathRevision;
            state.LastSentRealtime = now;
            state.LastObservedRealtime = now;
        }

        private static void PruneReplicationSparseTransformStatesIfDue(float now)
        {
            if (now < replicationNextSparseTransformPruneRealtime) return;
            replicationNextSparseTransformPruneRealtime = now + 2f;
            ReplicationSparseTransformExpiredIds.Clear();
            foreach (var pair in ReplicationSparseTransformStates)
            {
                if (now - pair.Value.LastObservedRealtime >= ReplicationSparseTransformPruneSeconds)
                {
                    ReplicationSparseTransformExpiredIds.Add(pair.Key);
                }
            }
            for (var i = 0; i < ReplicationSparseTransformExpiredIds.Count; i++)
            {
                ReplicationSparseTransformStates.Remove(ReplicationSparseTransformExpiredIds[i]);
            }
        }

        private static void ResetReplicationSparseTransformState()
        {
            ReplicationSparseTransformStates.Clear();
            ReplicationSparseTransformExpiredIds.Clear();
            replicationNextSparseTransformPruneRealtime = 0f;
            replicationSparseTransformRowsSent = 0L;
            replicationSparseTransformRowsSuppressed = 0L;
        }
    }
}
