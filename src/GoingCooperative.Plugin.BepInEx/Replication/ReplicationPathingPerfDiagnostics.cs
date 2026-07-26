using System;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const float ReplicationPathingPerfWindowSeconds = 10f;

        private static float replicationPathingPerfWindowStartRealtime;
        private static float replicationPathingPerfFrameSeconds;
        private static float replicationPathingPerfWorstFrameMs;
        private static int replicationPathingPerfFrames;
        private static int replicationPathingPerfFramesOver33Ms;
        private static int replicationPathingPerfFramesOver50Ms;
        private static int replicationPathingPerfFramesOver100Ms;
        private static long replicationPathingPerfSnapshotCollectTicks;
        private static long replicationPathingPerfSnapshotCollectMaxTicks;
        private static long replicationPathingPerfSnapshotEncodeSendTicks;
        private static long replicationPathingPerfSnapshotEncodeSendMaxTicks;
        private static long replicationPathingPerfIdentityTicks;
        private static long replicationPathingPerfSemanticTicks;
        private static long replicationPathingPerfCornerTicks;
        private static long replicationPathingPerfPumpTicks;
        private static long replicationPathingPerfPumpMaxTicks;
        private static long replicationPathingPerfRetryTicks;
        private static int replicationPathingPerfSnapshots;
        private static int replicationPathingPerfSnapshotEntities;
        private static int replicationPathingPerfSnapshotMaxEntities;
        private static int replicationPathingPerfSnapshotWireCharacters;
        private static int replicationPathingPerfIdentityCalls;
        private static int replicationPathingPerfIdentityFailures;
        private static int replicationPathingPerfSemanticCalls;
        private static int replicationPathingPerfSemanticRows;
        private static int replicationPathingPerfMovingWorkers;
        private static int replicationPathingPerfMovingNpcs;
        private static int replicationPathingPerfMovingAnimals;
        private static int replicationPathingPerfCornerExtractions;
        private static int replicationPathingPerfCornerExtractionFailures;
        private static int replicationPathingPerfMotionBegins;
        private static int replicationPathingPerfMotionChanges;
        private static int replicationPathingPerfMotionEnds;
        private static int replicationPathingPerfMotionCorners;
        private static int replicationPathingPerfPumpMessages;
        private static int replicationPathingPerfPumpMaxMessages;
        private static int replicationPathingPerfRetryScans;
        private static int replicationPathingPerfRetryRowsInspected;
        private static int replicationPathingPerfRetryRowsDue;
        private static int replicationPathingPerfRetryPendingMax;
        private static int replicationPathingPerfGc0Start;
        private static int replicationPathingPerfGc1Start;
        private static int replicationPathingPerfGc2Start;
        private static long replicationPathingPerfManagedBytesStart;
        private static bool replicationPathingPerfEnabledLogged;
        private static bool replicationPathingPerfDisabledLogged;

        private static long BeginReplicationPathingPerfSample()
        {
            return replicationConfigPathingPerfDiagnostics
                ? Stopwatch.GetTimestamp()
                : 0L;
        }

        private static long GetReplicationPathingPerfElapsedTicks(long started)
        {
            return started <= 0L ? 0L : Math.Max(0L, Stopwatch.GetTimestamp() - started);
        }

        private static double ReplicationPathingPerfTicksToMs(long ticks)
        {
            return ticks <= 0L
                ? 0d
                : ticks * 1000d / Stopwatch.Frequency;
        }

        private void UpdateReplicationPathingPerfDiagnostics()
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                if (!replicationPathingPerfDisabledLogged)
                {
                    replicationPathingPerfDisabledLogged = true;
                    replicationPathingPerfEnabledLogged = false;
                    AppendPluginLog("Going Cooperative pathing perf diagnostics disabled");
                    ResetReplicationPathingPerfDiagnostics();
                }

                return;
            }

            var now = Time.realtimeSinceStartup;
            if (replicationPathingPerfWindowStartRealtime <= 0f)
            {
                StartReplicationPathingPerfWindow(now);
                if (!replicationPathingPerfEnabledLogged)
                {
                    replicationPathingPerfEnabledLogged = true;
                    replicationPathingPerfDisabledLogged = false;
                    AppendPluginLog("Going Cooperative pathing perf diagnostics enabled windowSeconds="
                        + ReplicationPathingPerfWindowSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                        + " side="
                        + (replicationConfigHostMode ? "host" : "client"));
                }
            }

            var frameSeconds = Math.Max(0f, Time.unscaledDeltaTime);
            var frameMs = frameSeconds * 1000f;
            replicationPathingPerfFrameSeconds += frameSeconds;
            replicationPathingPerfFrames++;
            replicationPathingPerfWorstFrameMs = Math.Max(replicationPathingPerfWorstFrameMs, frameMs);
            if (frameMs >= 33.333f) replicationPathingPerfFramesOver33Ms++;
            if (frameMs >= 50f) replicationPathingPerfFramesOver50Ms++;
            if (frameMs >= 100f) replicationPathingPerfFramesOver100Ms++;

            if (now - replicationPathingPerfWindowStartRealtime < ReplicationPathingPerfWindowSeconds)
            {
                return;
            }

            LogReplicationPathingPerfWindow(now);
            StartReplicationPathingPerfWindow(now);
        }

        private static void RecordReplicationPathingSnapshotCollection(long started, int entityCount)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            var elapsed = GetReplicationPathingPerfElapsedTicks(started);
            replicationPathingPerfSnapshots++;
            replicationPathingPerfSnapshotEntities += Math.Max(0, entityCount);
            replicationPathingPerfSnapshotMaxEntities = Math.Max(replicationPathingPerfSnapshotMaxEntities, entityCount);
            replicationPathingPerfSnapshotCollectTicks += elapsed;
            replicationPathingPerfSnapshotCollectMaxTicks = Math.Max(replicationPathingPerfSnapshotCollectMaxTicks, elapsed);
        }

        private static void RecordReplicationPathingSnapshotEncodeSend(long started, int wireCharacters)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            var elapsed = GetReplicationPathingPerfElapsedTicks(started);
            replicationPathingPerfSnapshotEncodeSendTicks += elapsed;
            replicationPathingPerfSnapshotEncodeSendMaxTicks = Math.Max(replicationPathingPerfSnapshotEncodeSendMaxTicks, elapsed);
            replicationPathingPerfSnapshotWireCharacters += Math.Max(0, wireCharacters);
        }

        private static void RecordReplicationPathingIdentity(long started, bool resolved)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            replicationPathingPerfIdentityCalls++;
            if (!resolved) replicationPathingPerfIdentityFailures++;
            replicationPathingPerfIdentityTicks += GetReplicationPathingPerfElapsedTicks(started);
        }

        private static void RecordReplicationPathingSemantic(
            long started,
            string kind,
            bool emittedRow,
            bool moving)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            replicationPathingPerfSemanticCalls++;
            if (emittedRow) replicationPathingPerfSemanticRows++;
            if (moving)
            {
                if (string.Equals(kind, "worker", StringComparison.Ordinal)) replicationPathingPerfMovingWorkers++;
                else if (string.Equals(kind, "npc", StringComparison.Ordinal)) replicationPathingPerfMovingNpcs++;
                else if (string.Equals(kind, "animal", StringComparison.Ordinal)) replicationPathingPerfMovingAnimals++;
            }

            replicationPathingPerfSemanticTicks += GetReplicationPathingPerfElapsedTicks(started);
        }

        private static void RecordReplicationPathingCornerExtraction(long started, bool succeeded)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            replicationPathingPerfCornerExtractions++;
            if (!succeeded) replicationPathingPerfCornerExtractionFailures++;
            replicationPathingPerfCornerTicks += GetReplicationPathingPerfElapsedTicks(started);
        }

        private static void RecordReplicationPathingMotionEvent(string phase, int cornerCount)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            if (string.Equals(phase, "Begin", StringComparison.Ordinal)) replicationPathingPerfMotionBegins++;
            else if (string.Equals(phase, "PathChanged", StringComparison.Ordinal)) replicationPathingPerfMotionChanges++;
            else if (string.Equals(phase, "End", StringComparison.Ordinal)) replicationPathingPerfMotionEnds++;
            replicationPathingPerfMotionCorners += Math.Max(0, cornerCount);
        }

        private static void RecordReplicationPathingPump(long started, int messages)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            var elapsed = GetReplicationPathingPerfElapsedTicks(started);
            replicationPathingPerfPumpTicks += elapsed;
            replicationPathingPerfPumpMaxTicks = Math.Max(replicationPathingPerfPumpMaxTicks, elapsed);
            replicationPathingPerfPumpMessages += Math.Max(0, messages);
            replicationPathingPerfPumpMaxMessages = Math.Max(replicationPathingPerfPumpMaxMessages, messages);
        }

        private static void RecordReplicationPathingRetryScan(
            long started,
            int inspected,
            int due,
            int pendingCount)
        {
            if (!replicationConfigPathingPerfDiagnostics)
            {
                return;
            }

            replicationPathingPerfRetryScans++;
            replicationPathingPerfRetryRowsInspected += Math.Max(0, inspected);
            replicationPathingPerfRetryRowsDue += Math.Max(0, due);
            replicationPathingPerfRetryPendingMax = Math.Max(replicationPathingPerfRetryPendingMax, pendingCount);
            replicationPathingPerfRetryTicks += GetReplicationPathingPerfElapsedTicks(started);
        }

        private static void LogReplicationPathingPerfWindow(float now)
        {
            var current = instance;
            if (ReferenceEquals(current, null) || replicationPathingPerfFrames <= 0)
            {
                return;
            }

            var elapsed = Math.Max(0.001f, now - replicationPathingPerfWindowStartRealtime);
            var avgFps = replicationPathingPerfFrames / Math.Max(0.001f, replicationPathingPerfFrameSeconds);
            var gc0 = Math.Max(0, GC.CollectionCount(0) - replicationPathingPerfGc0Start);
            var gc1 = Math.Max(0, GC.CollectionCount(1) - replicationPathingPerfGc1Start);
            var gc2 = Math.Max(0, GC.CollectionCount(2) - replicationPathingPerfGc2Start);
            var managedBytes = GC.GetTotalMemory(false);
            var managedDelta = managedBytes - replicationPathingPerfManagedBytesStart;

            current.LogReplicationInfo("Going Cooperative pathing perf window side="
                + (replicationConfigHostMode ? "host" : "client")
                + " elapsed=" + elapsed.ToString("0.###", CultureInfo.InvariantCulture)
                + " frames=" + replicationPathingPerfFrames.ToString(CultureInfo.InvariantCulture)
                + " avgFps=" + avgFps.ToString("0.0", CultureInfo.InvariantCulture)
                + " worstMs=" + replicationPathingPerfWorstFrameMs.ToString("0.0", CultureInfo.InvariantCulture)
                + " over33ms=" + replicationPathingPerfFramesOver33Ms.ToString(CultureInfo.InvariantCulture)
                + " over50ms=" + replicationPathingPerfFramesOver50Ms.ToString(CultureInfo.InvariantCulture)
                + " over100ms=" + replicationPathingPerfFramesOver100Ms.ToString(CultureInfo.InvariantCulture)
                + " snapshots=" + replicationPathingPerfSnapshots.ToString(CultureInfo.InvariantCulture)
                + " snapshotEntities=" + replicationPathingPerfSnapshotEntities.ToString(CultureInfo.InvariantCulture)
                + " snapshotMaxEntities=" + replicationPathingPerfSnapshotMaxEntities.ToString(CultureInfo.InvariantCulture)
                + " collectMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfSnapshotCollectTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " collectMaxMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfSnapshotCollectMaxTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " encodeSendMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfSnapshotEncodeSendTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " encodeSendMaxMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfSnapshotEncodeSendMaxTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " wireChars=" + replicationPathingPerfSnapshotWireCharacters.ToString(CultureInfo.InvariantCulture)
                + " identityCalls=" + replicationPathingPerfIdentityCalls.ToString(CultureInfo.InvariantCulture)
                + " identityFailures=" + replicationPathingPerfIdentityFailures.ToString(CultureInfo.InvariantCulture)
                + " identityMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfIdentityTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " semanticCalls=" + replicationPathingPerfSemanticCalls.ToString(CultureInfo.InvariantCulture)
                + " semanticRows=" + replicationPathingPerfSemanticRows.ToString(CultureInfo.InvariantCulture)
                + " semanticMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfSemanticTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " moverSamples=" + replicationPathingPerfMovingWorkers.ToString(CultureInfo.InvariantCulture)
                + "/" + replicationPathingPerfMovingNpcs.ToString(CultureInfo.InvariantCulture)
                + "/" + replicationPathingPerfMovingAnimals.ToString(CultureInfo.InvariantCulture)
                + " cornerExtractions=" + replicationPathingPerfCornerExtractions.ToString(CultureInfo.InvariantCulture)
                + " cornerFailures=" + replicationPathingPerfCornerExtractionFailures.ToString(CultureInfo.InvariantCulture)
                + " cornerMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfCornerTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " motionEvents=" + replicationPathingPerfMotionBegins.ToString(CultureInfo.InvariantCulture)
                + "/" + replicationPathingPerfMotionChanges.ToString(CultureInfo.InvariantCulture)
                + "/" + replicationPathingPerfMotionEnds.ToString(CultureInfo.InvariantCulture)
                + " motionCorners=" + replicationPathingPerfMotionCorners.ToString(CultureInfo.InvariantCulture)
                + " pumpMessages=" + replicationPathingPerfPumpMessages.ToString(CultureInfo.InvariantCulture)
                + " pumpMaxMessages=" + replicationPathingPerfPumpMaxMessages.ToString(CultureInfo.InvariantCulture)
                + " pumpMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfPumpTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " pumpMaxMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfPumpMaxTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " retryScans=" + replicationPathingPerfRetryScans.ToString(CultureInfo.InvariantCulture)
                + " retryInspected=" + replicationPathingPerfRetryRowsInspected.ToString(CultureInfo.InvariantCulture)
                + " retryDue=" + replicationPathingPerfRetryRowsDue.ToString(CultureInfo.InvariantCulture)
                + " retryPendingMax=" + replicationPathingPerfRetryPendingMax.ToString(CultureInfo.InvariantCulture)
                + " retryScanMs=" + ReplicationPathingPerfTicksToMs(replicationPathingPerfRetryTicks).ToString("0.###", CultureInfo.InvariantCulture)
                + " gc=" + gc0.ToString(CultureInfo.InvariantCulture)
                + "/" + gc1.ToString(CultureInfo.InvariantCulture)
                + "/" + gc2.ToString(CultureInfo.InvariantCulture)
                + " managedDelta=" + managedDelta.ToString(CultureInfo.InvariantCulture));
        }

        private static void StartReplicationPathingPerfWindow(float now)
        {
            ResetReplicationPathingPerfWindowCounters();
            replicationPathingPerfWindowStartRealtime = now;
            replicationPathingPerfGc0Start = GC.CollectionCount(0);
            replicationPathingPerfGc1Start = GC.CollectionCount(1);
            replicationPathingPerfGc2Start = GC.CollectionCount(2);
            replicationPathingPerfManagedBytesStart = GC.GetTotalMemory(false);
        }

        private static void ResetReplicationPathingPerfDiagnostics()
        {
            replicationPathingPerfWindowStartRealtime = 0f;
            ResetReplicationPathingPerfWindowCounters();
        }

        private static void ResetReplicationPathingPerfWindowCounters()
        {
            replicationPathingPerfFrameSeconds = 0f;
            replicationPathingPerfWorstFrameMs = 0f;
            replicationPathingPerfFrames = 0;
            replicationPathingPerfFramesOver33Ms = 0;
            replicationPathingPerfFramesOver50Ms = 0;
            replicationPathingPerfFramesOver100Ms = 0;
            replicationPathingPerfSnapshotCollectTicks = 0L;
            replicationPathingPerfSnapshotCollectMaxTicks = 0L;
            replicationPathingPerfSnapshotEncodeSendTicks = 0L;
            replicationPathingPerfSnapshotEncodeSendMaxTicks = 0L;
            replicationPathingPerfIdentityTicks = 0L;
            replicationPathingPerfSemanticTicks = 0L;
            replicationPathingPerfCornerTicks = 0L;
            replicationPathingPerfPumpTicks = 0L;
            replicationPathingPerfPumpMaxTicks = 0L;
            replicationPathingPerfRetryTicks = 0L;
            replicationPathingPerfSnapshots = 0;
            replicationPathingPerfSnapshotEntities = 0;
            replicationPathingPerfSnapshotMaxEntities = 0;
            replicationPathingPerfSnapshotWireCharacters = 0;
            replicationPathingPerfIdentityCalls = 0;
            replicationPathingPerfIdentityFailures = 0;
            replicationPathingPerfSemanticCalls = 0;
            replicationPathingPerfSemanticRows = 0;
            replicationPathingPerfMovingWorkers = 0;
            replicationPathingPerfMovingNpcs = 0;
            replicationPathingPerfMovingAnimals = 0;
            replicationPathingPerfCornerExtractions = 0;
            replicationPathingPerfCornerExtractionFailures = 0;
            replicationPathingPerfMotionBegins = 0;
            replicationPathingPerfMotionChanges = 0;
            replicationPathingPerfMotionEnds = 0;
            replicationPathingPerfMotionCorners = 0;
            replicationPathingPerfPumpMessages = 0;
            replicationPathingPerfPumpMaxMessages = 0;
            replicationPathingPerfRetryScans = 0;
            replicationPathingPerfRetryRowsInspected = 0;
            replicationPathingPerfRetryRowsDue = 0;
            replicationPathingPerfRetryPendingMax = 0;
        }
    }
}
