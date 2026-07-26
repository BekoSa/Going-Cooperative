using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationMedicalWoundStateDeltaKind = "MedicalWoundStateV1";
        private const float ReplicationMedicalRosterRefreshSeconds = 1f;
        private const float ReplicationMedicalFlushSeconds = 0.25f;
        private const float ReplicationMedicalCheckpointSeconds = 15f;
        private const float ReplicationMedicalPanelRequestDebounceSeconds = 10f;
        private const int ReplicationMedicalMaxSendsPerFlush = 8;

        private sealed class ReplicationMedicalStatsSubscription
        {
            public object Stats = null!;
            public object Owner = null!;
            public string EntityId = string.Empty;
            public readonly List<Tuple<EventInfo, Delegate>> EventHandlers = new List<Tuple<EventInfo, Delegate>>();
            public bool Seen;
        }

        private static readonly Dictionary<object, ReplicationMedicalStatsSubscription> ReplicationMedicalSubscriptions =
            new Dictionary<object, ReplicationMedicalStatsSubscription>(ReferenceObjectComparer.Instance);
        private static readonly HashSet<object> ReplicationMedicalDirtyStats =
            new HashSet<object>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, long> ReplicationMedicalHostRevisionByEntityId =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ReplicationMedicalHostSignatureByEntityId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationMedicalClientRevisionByEntityId =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationMedicalLastPanelRequestByEntityId =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationMedicalHostLastStateRequestByEntityId =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<WeakReference> ReplicationMedicalHealthPanels = new List<WeakReference>();
        private static readonly List<object> ReplicationMedicalDirtyScratch = new List<object>();
        private static readonly List<object> ReplicationMedicalStaleSubscriptionScratch = new List<object>();
        private static float replicationMedicalNextRosterRefreshRealtime;
        private static float replicationMedicalNextFlushRealtime;
        private static float replicationMedicalNextCheckpointRealtime;
        private static float replicationMedicalNextDiagnosticsRealtime;
        private static int replicationMedicalApplyDepth;
        private static int replicationMedicalPanelRefreshDepth;
        private static long replicationMedicalRequestSequence;
        private static long replicationMedicalStatesSent;
        private static long replicationMedicalStatesApplied;
        private static long replicationMedicalOrdersSent;
        private static long replicationMedicalOrdersApplied;
        private static long replicationMedicalNativeEvents;
        private static long replicationMedicalTickMarks;

        private void TryInstallReplicationMedicalV1Hooks(Harmony harmony)
        {
            if (!replicationConfigMedicalReplicationV1) return;

            var count = 0;
            if (replicationConfigMedicalWoundStateV1)
            {
                count += PatchReplicationMedicalMethod(
                    harmony,
                    "NSMedieval.StatsSystem.WoundUtils",
                    "Tick",
                    new[] { MedicalType("NSMedieval.StatsSystem.StatsInstance"), MedicalType("NSMedieval.StatsSystem.WoundEffectorInfo") },
                    nameof(ReplicationMedicalWoundTickPrefix),
                    nameof(ReplicationMedicalWoundTickPostfix));
                count += PatchReplicationMedicalMethod(
                    harmony,
                    "NSMedieval.State.CreatureBase",
                    "TendWounds",
                    new[] { typeof(float) },
                    null,
                    nameof(ReplicationMedicalTendWoundsPostfix));
                count += PatchReplicationMedicalMethod(
                    harmony,
                    "NSMedieval.State.CreatureBase",
                    "set_IsReceivingWoundTreatman",
                    new[] { typeof(bool) },
                    null,
                    nameof(ReplicationMedicalTreatmentStatePostfix));
                count += PatchReplicationMedicalMethod(
                    harmony,
                    "NSMedieval.State.CreatureBase",
                    "set_CanReceiveWoundTreatment",
                    new[] { typeof(bool) },
                    null,
                    nameof(ReplicationMedicalTreatmentStatePostfix));
            }

            if (replicationConfigMedicalTreatmentOrdersV1)
            {
                count += PatchReplicationMedicalMethod(harmony, "NSMedieval.AdditionalMenuItems.PrioritiseTendWoundsMenuItem", "OnClickCallback", Type.EmptyTypes, nameof(ReplicationMedicalOrderPrefix), null);
                count += PatchReplicationMedicalMethod(harmony, "NSMedieval.AdditionalMenuItems.PrioritiseSelfTendWoundsMenuItem", "OnClickCallback", Type.EmptyTypes, nameof(ReplicationMedicalOrderPrefix), null);
                count += PatchReplicationMedicalMethod(harmony, "NSMedieval.AdditionalMenuItems.PrioritiseAnimalTendWoundsMenuItem", "OnClickCallback", Type.EmptyTypes, nameof(ReplicationMedicalOrderPrefix), null);
            }

            if (replicationConfigMedicalPanelRefreshV1)
            {
                count += PatchReplicationMedicalMethod(harmony, "NSMedieval.UI.WorkerHealthExtraPanel", "SetupTabPanel", Type.EmptyTypes, null, nameof(ReplicationMedicalHealthPanelPostfix));
                count += PatchReplicationMedicalMethod(harmony, "NSMedieval.UI.AnimalHealthExtraPanel", "SetupTabPanel", Type.EmptyTypes, null, nameof(ReplicationMedicalHealthPanelPostfix));
                count += PatchReplicationMedicalMethod(harmony, "NSMedieval.UI.EnemyHealthExtraPanel", "SetupTabPanel", Type.EmptyTypes, null, nameof(ReplicationMedicalHealthPanelPostfix));
            }

            LogReplicationInfo("Going Cooperative medical-v1 hooks=" + count.ToString(CultureInfo.InvariantCulture)
                + " woundState=" + replicationConfigMedicalWoundStateV1
                + " orders=" + replicationConfigMedicalTreatmentOrdersV1
                + " presentation=" + replicationConfigMedicalTreatmentPresentationV1
                + " panelRefresh=" + replicationConfigMedicalPanelRefreshV1
                + " clientTickSuppression=" + replicationConfigMedicalClientWoundTickSuppressionV1);
        }

        private int PatchReplicationMedicalMethod(
            Harmony harmony,
            string typeName,
            string methodName,
            Type[] parameterTypes,
            string? prefixName,
            string? postfixName)
        {
            try
            {
                if (Array.IndexOf(parameterTypes, typeof(object)) >= 0)
                {
                    LogReplicationWarning("Going Cooperative medical-v1 patch parameter missing " + typeName + "." + methodName);
                    return 0;
                }
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.Method(type, methodName, parameterTypes);
                if (method == null)
                {
                    LogReplicationWarning("Going Cooperative medical-v1 patch method missing " + typeName + "." + methodName);
                    return 0;
                }
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var prefix = prefixName == null ? null : new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(prefixName, flags));
                var postfix = postfixName == null ? null : new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(postfixName, flags));
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative medical-v1 patch failed " + typeName + "." + methodName + " " + FormatReflectionExceptionDetail(ex));
                return 0;
            }
        }

        private static Type MedicalType(string name)
        {
            return AccessTools.TypeByName(name) ?? typeof(object);
        }

        private static bool ReplicationMedicalWoundTickPrefix()
        {
            return !replicationConfigMedicalReplicationV1
                || !replicationConfigMedicalWoundStateV1
                || replicationConfigHostMode
                || !replicationConfigMedicalClientWoundTickSuppressionV1
                || replicationMedicalApplyDepth > 0;
        }

        private static void ReplicationMedicalWoundTickPostfix(object __0)
        {
            if (!MedicalHostStateCaptureEnabled() || __0 == null || replicationMedicalApplyDepth > 0) return;
            replicationMedicalTickMarks++;
            ReplicationMedicalDirtyStats.Add(__0);
        }

        private static void ReplicationMedicalTendWoundsPostfix(object __instance)
        {
            if (!MedicalHostStateCaptureEnabled() || __instance == null || replicationMedicalApplyDepth > 0) return;
            if (TryGetReplicationMedicalStats(__instance, out var stats) && stats != null)
            {
                ReplicationMedicalDirtyStats.Add(stats);
                if (replicationConfigMedicalDiagnostics)
                {
                    instance?.LogReplicationInfo("Going Cooperative medical-v1 treatment committed entity=" + GetReplicationMedicalEntityLabel(__instance));
                }
            }
        }

        private static void ReplicationMedicalTreatmentStatePostfix(object __instance)
        {
            if (!MedicalHostStateCaptureEnabled() || __instance == null || replicationMedicalApplyDepth > 0) return;
            if (TryGetReplicationMedicalStats(__instance, out var stats) && stats != null) ReplicationMedicalDirtyStats.Add(stats);
        }

        private static bool ReplicationMedicalOrderPrefix(object __instance, MethodBase __originalMethod)
        {
            if (!replicationConfigMedicalReplicationV1
                || !replicationConfigMedicalTreatmentOrdersV1
                || replicationMedicalApplyDepth > 0
                || replicationConfigHostMode) return true;

            if (!ShouldSendReplicationLocalCommandIntent())
            {
                if (replicationConfigMedicalDiagnostics)
                    instance?.LogReplicationWarning("Going Cooperative medical-v1 order rejected client-not-ready method=" + __originalMethod.DeclaringType?.Name);
                return false;
            }

            if (!TryBuildReplicationMedicalOrder(__instance, __originalMethod, out var payload, out var detail))
            {
                instance?.LogReplicationWarning("Going Cooperative medical-v1 order capture failed " + detail);
                return false;
            }

            SendReplicationManagementIntent(payload, "medical-order");
            replicationMedicalOrdersSent++;
            if (replicationConfigMedicalDiagnostics)
                instance?.LogReplicationInfo("Going Cooperative medical-v1 order sent " + detail);
            return false;
        }

        private static bool TryBuildReplicationMedicalOrder(
            object menuItem,
            MethodBase originalMethod,
            out string payload,
            out string detail)
        {
            payload = string.Empty;
            detail = string.Empty;
            var getSelectedWorker = AccessTools.Method(menuItem.GetType(), "GetSelectedWorker", Type.EmptyTypes);
            var ownerProperty = AccessTools.Property(menuItem.GetType(), "Owner");
            var doctor = getSelectedWorker?.Invoke(menuItem, null);
            var menuOwner = ownerProperty?.GetValue(menuItem, null);
            var getAsTarget = menuOwner == null ? null : AccessTools.Method(menuOwner.GetType(), "GetAsTarget", Type.EmptyTypes);
            var patient = getAsTarget?.Invoke(menuOwner, null);
            if (doctor == null || patient == null
                || !TryGetReplicationAgentOwnerEntityId(doctor, out var doctorId, out var doctorDetail)
                || !TryGetReplicationAgentOwnerEntityId(patient, out var patientId, out var patientDetail))
            {
                detail = "medical-order-context-missing doctor=" + (doctor == null ? "missing" : "unresolved")
                    + " patient=" + (patient == null ? "missing" : "unresolved");
                return false;
            }

            var declaringName = originalMethod.DeclaringType?.Name ?? string.Empty;
            var kind = declaringName.IndexOf("Self", StringComparison.OrdinalIgnoreCase) >= 0
                ? "self"
                : declaringName.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0 ? "animal" : "worker";
            var requestId = "medical-" + (++replicationMedicalRequestSequence).ToString(CultureInfo.InvariantCulture);
            payload = MedicalReplicationPayloads.CreateTreatmentOrder(kind, doctorId, patientId, requestId);
            detail = "kind=" + kind + " doctor=" + doctorId + " patient=" + patientId + " " + doctorDetail + " " + patientDetail;
            return true;
        }

        private static void ReplicationMedicalHealthPanelPostfix(object __instance)
        {
            if (!replicationConfigMedicalReplicationV1 || !replicationConfigMedicalPanelRefreshV1 || __instance == null) return;
            RegisterReplicationMedicalHealthPanel(__instance);
            if (replicationMedicalPanelRefreshDepth > 0 || replicationConfigHostMode || !replicationConfigMedicalWoundStateV1) return;

            var creature = AccessTools.Property(__instance.GetType(), "CreatureBase")?.GetValue(__instance, null);
            if (creature == null || !TryGetReplicationAgentOwnerEntityId(creature, out var entityId, out _)) return;
            var now = Time.realtimeSinceStartup;
            if (ReplicationMedicalLastPanelRequestByEntityId.TryGetValue(entityId, out var last) && now - last < ReplicationMedicalPanelRequestDebounceSeconds) return;
            ReplicationMedicalLastPanelRequestByEntityId[entityId] = now;
            if (!ShouldSendReplicationLocalCommandIntent()) return;

            var requestId = "medical-panel-" + (++replicationMedicalRequestSequence).ToString(CultureInfo.InvariantCulture);
            SendReplicationManagementIntent(MedicalReplicationPayloads.CreateStateRequest(entityId, requestId), "medical-panel-request");
        }

        private static void RegisterReplicationMedicalHealthPanel(object panel)
        {
            for (var i = ReplicationMedicalHealthPanels.Count - 1; i >= 0; i--)
            {
                var existing = ReplicationMedicalHealthPanels[i].Target;
                if (existing == null) ReplicationMedicalHealthPanels.RemoveAt(i);
                else if (ReferenceEquals(existing, panel)) return;
            }
            ReplicationMedicalHealthPanels.Add(new WeakReference(panel));
        }

        private static bool MedicalHostStateCaptureEnabled()
        {
            return replicationConfigMedicalReplicationV1
                && replicationConfigMedicalWoundStateV1
                && replicationConfigHostMode
                && replicationRuntimeStarted;
        }

        private static void UpdateReplicationMedicalV1()
        {
            if (!replicationConfigMedicalReplicationV1 || !replicationRuntimeStarted) return;
            LogReplicationMedicalDiagnosticsIfDue();
            if (!replicationConfigHostMode || !replicationConfigMedicalWoundStateV1 || !replicationRemoteHelloReceived) return;

            var now = Time.realtimeSinceStartup;
            if (now >= replicationMedicalNextRosterRefreshRealtime)
            {
                replicationMedicalNextRosterRefreshRealtime = now + ReplicationMedicalRosterRefreshSeconds;
                RefreshReplicationMedicalSubscriptions();
            }
            if (now >= replicationMedicalNextCheckpointRealtime)
            {
                replicationMedicalNextCheckpointRealtime = now + ReplicationMedicalCheckpointSeconds;
                foreach (var subscription in ReplicationMedicalSubscriptions.Values)
                    SendReplicationMedicalWoundState(subscription, checkpoint: true);
            }
            if (now < replicationMedicalNextFlushRealtime || ReplicationMedicalDirtyStats.Count == 0) return;
            replicationMedicalNextFlushRealtime = now + ReplicationMedicalFlushSeconds;

            ReplicationMedicalDirtyScratch.Clear();
            foreach (var stats in ReplicationMedicalDirtyStats)
            {
                ReplicationMedicalDirtyScratch.Add(stats);
                if (ReplicationMedicalDirtyScratch.Count >= ReplicationMedicalMaxSendsPerFlush) break;
            }
            for (var i = 0; i < ReplicationMedicalDirtyScratch.Count; i++)
            {
                var stats = ReplicationMedicalDirtyScratch[i];
                ReplicationMedicalDirtyStats.Remove(stats);
                if (ReplicationMedicalSubscriptions.TryGetValue(stats, out var subscription))
                    SendReplicationMedicalWoundState(subscription, checkpoint: false);
            }
            ReplicationMedicalDirtyScratch.Clear();
        }

        private static void LogReplicationMedicalDiagnosticsIfDue()
        {
            if (!replicationConfigMedicalDiagnostics || Time.realtimeSinceStartup < replicationMedicalNextDiagnosticsRealtime) return;
            replicationMedicalNextDiagnosticsRealtime = Time.realtimeSinceStartup + 10f;
            instance?.LogReplicationInfo("Going Cooperative medical-v1 status mode="
                + (replicationConfigHostMode ? "host" : "client")
                + " subscriptions=" + ReplicationMedicalSubscriptions.Count.ToString(CultureInfo.InvariantCulture)
                + " dirty=" + ReplicationMedicalDirtyStats.Count.ToString(CultureInfo.InvariantCulture)
                + " statesSent=" + replicationMedicalStatesSent.ToString(CultureInfo.InvariantCulture)
                + " statesApplied=" + replicationMedicalStatesApplied.ToString(CultureInfo.InvariantCulture)
                + " ordersSent=" + replicationMedicalOrdersSent.ToString(CultureInfo.InvariantCulture)
                + " ordersApplied=" + replicationMedicalOrdersApplied.ToString(CultureInfo.InvariantCulture)
                + " nativeEvents=" + replicationMedicalNativeEvents.ToString(CultureInfo.InvariantCulture)
                + " tickMarks=" + replicationMedicalTickMarks.ToString(CultureInfo.InvariantCulture));
        }

        private static void RefreshReplicationMedicalSubscriptions()
        {
            foreach (var subscription in ReplicationMedicalSubscriptions.Values) subscription.Seen = false;
            var views = FindReplicationAnimatedAgentViews();
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null
                    || !TryResolveReplicationAgentOwnerFromView(view, out var owner, out _)
                    || owner == null
                    || !TryGetReplicationAgentOwnerEntityId(owner, out var entityId, out _)
                    || !TryGetReplicationMedicalStats(owner, out var stats)
                    || stats == null) continue;

                if (ReplicationMedicalSubscriptions.TryGetValue(stats, out var existing))
                {
                    existing.Seen = true;
                    existing.Owner = owner;
                    existing.EntityId = entityId;
                    continue;
                }

                var created = new ReplicationMedicalStatsSubscription
                {
                    Stats = stats,
                    Owner = owner,
                    EntityId = entityId,
                    Seen = true
                };
                SubscribeReplicationMedicalStatsEvents(created);
                ReplicationMedicalSubscriptions.Add(stats, created);
                ReplicationMedicalDirtyStats.Add(stats);
            }

            ReplicationMedicalStaleSubscriptionScratch.Clear();
            foreach (var pair in ReplicationMedicalSubscriptions)
            {
                if (!pair.Value.Seen || IsReplicationMedicalDisposed(pair.Value.Owner)) ReplicationMedicalStaleSubscriptionScratch.Add(pair.Key);
            }
            for (var i = 0; i < ReplicationMedicalStaleSubscriptionScratch.Count; i++)
            {
                var stats = ReplicationMedicalStaleSubscriptionScratch[i];
                if (ReplicationMedicalSubscriptions.TryGetValue(stats, out var subscription)) UnsubscribeReplicationMedicalStatsEvents(subscription);
                ReplicationMedicalSubscriptions.Remove(stats);
                ReplicationMedicalDirtyStats.Remove(stats);
            }
            ReplicationMedicalStaleSubscriptionScratch.Clear();
        }

        private static bool IsReplicationMedicalDisposed(object owner)
        {
            return TryReadReplicationBooleanState(owner, "HasDisposed", out var disposed) && disposed;
        }

        private static void SubscribeReplicationMedicalStatsEvents(ReplicationMedicalStatsSubscription subscription)
        {
            var eventNames = new[] { "OnEffectorStartEvent", "OnEffectorStackEvent", "OnEffectorEndEvent" };
            for (var i = 0; i < eventNames.Length; i++)
            {
                try
                {
                    var eventInfo = subscription.Stats.GetType().GetEvent(eventNames[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var invoke = eventInfo?.EventHandlerType?.GetMethod("Invoke");
                    var parameters = invoke?.GetParameters();
                    if (eventInfo == null || eventInfo.EventHandlerType == null || parameters == null || parameters.Length != 1) continue;
                    var eventParameter = Expression.Parameter(parameters[0].ParameterType, "effector");
                    var callback = typeof(GoingCooperativePlugin).GetMethod(nameof(ReplicationMedicalNativeEffectorEvent), BindingFlags.Static | BindingFlags.NonPublic);
                    if (callback == null) continue;
                    var body = Expression.Call(callback, Expression.Constant(subscription.Stats, typeof(object)));
                    var handler = Expression.Lambda(eventInfo.EventHandlerType, body, eventParameter).Compile();
                    eventInfo.AddEventHandler(subscription.Stats, handler);
                    subscription.EventHandlers.Add(Tuple.Create(eventInfo, handler));
                }
                catch (Exception ex)
                {
                    instance?.LogReplicationWarning("Going Cooperative medical-v1 native event subscription failed event=" + eventNames[i] + " " + FormatReflectionExceptionDetail(ex));
                }
            }
        }

        private static void UnsubscribeReplicationMedicalStatsEvents(ReplicationMedicalStatsSubscription subscription)
        {
            for (var i = 0; i < subscription.EventHandlers.Count; i++)
            {
                try { subscription.EventHandlers[i].Item1.RemoveEventHandler(subscription.Stats, subscription.EventHandlers[i].Item2); }
                catch { }
            }
            subscription.EventHandlers.Clear();
        }

        private static void ReplicationMedicalNativeEffectorEvent(object stats)
        {
            if (!MedicalHostStateCaptureEnabled() || replicationMedicalApplyDepth > 0 || stats == null) return;
            replicationMedicalNativeEvents++;
            ReplicationMedicalDirtyStats.Add(stats);
        }

        private static void SendReplicationMedicalWoundState(ReplicationMedicalStatsSubscription subscription, bool checkpoint, bool force = false)
        {
            if (!TryCollectReplicationMedicalWounds(subscription.Stats, out var wounds, out var detail))
            {
                if (replicationConfigMedicalDiagnostics)
                    instance?.LogReplicationWarning("Going Cooperative medical-v1 collect failed entityId=" + subscription.EntityId + " " + detail);
                return;
            }

            var receivingTreatment = replicationConfigMedicalTreatmentPresentationV1
                && TryReadReplicationBooleanState(subscription.Owner, "IsReceivingWoundTreatman", out var receiving)
                && receiving;
            var canReceiveTreatment = TryReadReplicationBooleanState(subscription.Owner, "CanReceiveWoundTreatment", out var canReceive)
                && canReceive;
            var signature = BuildReplicationMedicalStateSignature(wounds, receivingTreatment, canReceiveTreatment);
            var hasPrevious = ReplicationMedicalHostSignatureByEntityId.TryGetValue(subscription.EntityId, out var previous);
            if (!force && wounds.Count == 0 && !receivingTreatment && !canReceiveTreatment && !hasPrevious) return;
            if (!force && !checkpoint && hasPrevious && string.Equals(previous, signature, StringComparison.Ordinal)) return;
            var revision = ReplicationMedicalHostRevisionByEntityId.TryGetValue(subscription.EntityId, out var currentRevision)
                ? currentRevision + 1L : 1L;
            ReplicationMedicalHostRevisionByEntityId[subscription.EntityId] = revision;
            var payload = MedicalReplicationPayloads.CreateWoundState(
                new MedicalWoundState(subscription.EntityId, revision, checkpoint, receivingTreatment, canReceiveTreatment, wounds));
            var uniqueId = TryParseReplicationEntityNumericId(subscription.EntityId, out var parsedId) ? parsedId : 0L;
            instance?.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationMedicalWoundStateDeltaKind,
                uniqueId,
                string.Empty,
                0, 0, 0,
                payload));
            if (wounds.Count == 0 && !receivingTreatment && !canReceiveTreatment)
                ReplicationMedicalHostSignatureByEntityId.Remove(subscription.EntityId);
            else
                ReplicationMedicalHostSignatureByEntityId[subscription.EntityId] = signature;
            replicationMedicalStatesSent++;
            if (replicationConfigMedicalDiagnostics)
            {
                instance?.LogReplicationInfo("Going Cooperative medical-v1 state sent entityId=" + subscription.EntityId
                    + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                    + " checkpoint=" + checkpoint
                    + " wounds=" + wounds.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string BuildReplicationMedicalStateSignature(IReadOnlyList<MedicalWoundRecord> wounds, bool receivingTreatment, bool canReceiveTreatment)
        {
            var builder = new System.Text.StringBuilder(64 + (wounds.Count * 64));
            builder.Append(receivingTreatment ? '1' : '0').Append(canReceiveTreatment ? '1' : '0');
            for (var i = 0; i < wounds.Count; i++)
            {
                var wound = wounds[i];
                builder.Append('|').Append(wound.Name)
                    .Append(':').Append(wound.StartTime.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(wound.StackCount.ToString(CultureInfo.InvariantCulture))
                    // A tenth of a severity point is below the visible precision of the
                    // health panel while avoiding one reliable delta per wound tick.
                    .Append(':').Append(Math.Round(wound.CurrentSeverity * 10f).ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(wound.NeedsTending ? '1' : '0')
                    .Append(':').Append(wound.NeedsRest ? '1' : '0')
                    .Append(':').Append(wound.LastTendTime.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(Math.Round(wound.LastTendQuality * 1000f).ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static bool TryCollectReplicationMedicalWounds(object stats, out List<MedicalWoundRecord> wounds, out string detail)
        {
            wounds = new List<MedicalWoundRecord>();
            if (!TryGetReplicationMedicalActiveEffectors(stats, out var active))
            {
                detail = "active-effectors-missing";
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < active.Count; i++)
            {
                var effector = active[i];
                if (effector == null || !TryReadInstanceMemberValue(effector, "WoundInfo", out var woundInfo) || woundInfo == null) continue;
                var name = ReadReplicationMedicalString(effector, "Name", "name");
                if (name.Length == 0 || !names.Add(name))
                {
                    detail = "duplicate-or-empty-wound-name name=" + name;
                    return false;
                }

                wounds.Add(new MedicalWoundRecord(
                    name,
                    ReadReplicationMedicalLong(effector, "StartTime", "startTime"),
                    ReadReplicationMedicalInt(effector, "StackCount", "stackCount"),
                    ReadReplicationMedicalFloat(effector, "DurationUnmodified", "duration"),
                    ReadReplicationMedicalFloat(effector, "DurationModifier", "durationModifier"),
                    ReadReplicationMedicalFloat(woundInfo, "CurrentSeverity", "currentSeverity"),
                    ReadReplicationMedicalFloat(woundInfo, "MinSeverity", "minSeverity"),
                    ReadReplicationMedicalBool(woundInfo, "NeedTend", "needTend"),
                    ReadReplicationMedicalBool(woundInfo, "NeedRest", "needRest"),
                    ReadReplicationMedicalLong(woundInfo, "LastTickTime", "lastTickTime"),
                    ReadReplicationMedicalLong(woundInfo, "LastTendTime", "lastTendTime"),
                    ReadReplicationMedicalFloat(woundInfo, "LastTendQuality", "lastTendQuality"),
                    ReadReplicationMedicalString(effector, "CauseCreatureName", "causeCreatureName"),
                    ReadReplicationMedicalInt(effector, "CauseCreatureBodyType", "causeCreatureBodyType"),
                    ReadReplicationMedicalString(effector, "CauseHumanoidPerkId", "causeHumanoidPerkId")));
            }

            detail = "ok wounds=" + wounds.Count.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryGetReplicationMedicalActiveEffectors(object stats, out IList active)
        {
            active = Array.Empty<object>();
            var method = AccessTools.Method(stats.GetType(), "GetActiveEffectors", Type.EmptyTypes);
            var value = method?.Invoke(stats, null);
            if (value is IList list)
            {
                active = list;
                return true;
            }
            return false;
        }

        private static bool TryApplyReplicationMedicalWorldDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigMedicalReplicationV1 || !replicationConfigMedicalWoundStateV1)
            {
                detail = "medical-v1-wound-state-disabled";
                return false;
            }
            if (!MedicalReplicationPayloads.TryReadWoundState(delta.Detail, out var state) || state == null)
            {
                detail = "medical-v1-state-payload-invalid";
                return false;
            }
            if (ReplicationMedicalClientRevisionByEntityId.TryGetValue(state.EntityId, out var appliedRevision)
                && state.Revision <= appliedRevision)
            {
                detail = "ok medical-v1-stale entityId=" + state.EntityId
                    + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                    + " applied=" + appliedRevision.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (!TryFindReplicationMedicalOwnerByEntityId(state.EntityId, out var owner, out var ownerDetail)
                || owner == null
                || !TryGetReplicationMedicalStats(owner, out var stats)
                || stats == null)
            {
                detail = "medical-v1-owner-missing entityId=" + state.EntityId + " " + ownerDetail;
                return false;
            }

            replicationMedicalApplyDepth++;
            applyingRuntimeCommandDepth++;
            BeginReplicationRegionOrderStateCaptureSuppression();
            try
            {
                if (!TryReconcileReplicationMedicalWounds(stats, state.Wounds, out var reconcileDetail))
                {
                    detail = "medical-v1-reconcile-failed " + reconcileDetail;
                    return false;
                }
                if (replicationConfigMedicalTreatmentPresentationV1)
                    SetReplicationMedicalMember(owner, "IsReceivingWoundTreatman", "isReceivingWoundTreatman", state.ReceivingTreatment);
                SetReplicationMedicalMember(owner, "CanReceiveWoundTreatment", "canReceiveWoundTreatment", state.CanReceiveTreatment);
                SetReplicationMedicalMember(owner, null, "isWounded", state.Wounds.Count > 0);
                AccessTools.Method(owner.GetType(), "HandleBloodLoss", Type.EmptyTypes)?.Invoke(owner, null);
                AccessTools.Method(stats.GetType(), "Update", Type.EmptyTypes)?.Invoke(stats, null);
                ReplicationMedicalClientRevisionByEntityId[state.EntityId] = state.Revision;
                replicationMedicalStatesApplied++;
                RefreshReplicationMedicalHealthPanels(owner);
                detail = "ok medical-v1-state entityId=" + state.EntityId
                    + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                    + " wounds=" + state.Wounds.Count.ToString(CultureInfo.InvariantCulture)
                    + " " + reconcileDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "medical-v1-apply-exception " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                EndReplicationRegionOrderStateCaptureSuppression();
                applyingRuntimeCommandDepth--;
                replicationMedicalApplyDepth--;
            }
        }

        private static bool TryReconcileReplicationMedicalWounds(object stats, IReadOnlyList<MedicalWoundRecord> authoritative, out string detail)
        {
            if (!TryGetReplicationMedicalActiveEffectors(stats, out var active))
            {
                detail = "active-effectors-missing";
                return false;
            }

            var wanted = new Dictionary<string, MedicalWoundRecord>(StringComparer.Ordinal);
            for (var i = 0; i < authoritative.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(authoritative[i].Name) || wanted.ContainsKey(authoritative[i].Name))
                {
                    detail = "authoritative-duplicate name=" + authoritative[i].Name;
                    return false;
                }
                wanted.Add(authoritative[i].Name, authoritative[i]);
            }

            var removed = 0;
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var current = active[i];
                if (current == null || !TryReadInstanceMemberValue(current, "WoundInfo", out var wound) || wound == null) continue;
                var name = ReadReplicationMedicalString(current, "Name", "name");
                if (wanted.ContainsKey(name)) continue;
                EndReplicationMedicalWoundAtIndex(stats, active, i, current);
                removed++;
            }

            var added = 0;
            var updated = 0;
            for (var i = 0; i < authoritative.Count; i++)
            {
                var record = authoritative[i];
                var index = FindReplicationMedicalWoundIndex(active, record.Name);
                if (index < 0)
                {
                    var start = AccessTools.Method(stats.GetType(), "StartEffector", new[] { typeof(string), typeof(float), typeof(bool), typeof(int), typeof(string) });
                    if (start == null)
                    {
                        detail = "start-effector-method-missing name=" + record.Name;
                        return false;
                    }
                    var started = Convert.ToBoolean(start.Invoke(stats, new object[] { record.Name, record.DurationModifier, true, 0, record.CausePerkId }), CultureInfo.InvariantCulture);
                    index = FindReplicationMedicalWoundIndex(active, record.Name);
                    if (!started || index < 0)
                    {
                        detail = "start-effector-rejected name=" + record.Name;
                        return false;
                    }
                    added++;
                }

                var boxedEffector = active[index];
                if (boxedEffector == null || !TryReadInstanceMemberValue(boxedEffector, "WoundInfo", out var woundInfo) || woundInfo == null)
                {
                    detail = "wound-info-missing name=" + record.Name;
                    return false;
                }
                SetReplicationMedicalMember(boxedEffector, null, "name", record.Name);
                SetReplicationMedicalMember(boxedEffector, null, "startTime", record.StartTime);
                SetReplicationMedicalMember(boxedEffector, null, "stackCount", record.StackCount);
                SetReplicationMedicalMember(boxedEffector, null, "duration", record.Duration);
                SetReplicationMedicalMember(boxedEffector, null, "durationModifier", record.DurationModifier);
                SetReplicationMedicalMember(boxedEffector, null, "causeCreatureName", record.CauseCreatureName);
                SetReplicationMedicalMember(boxedEffector, null, "causeCreatureBodyType", record.CauseBodyType);
                SetReplicationMedicalMember(boxedEffector, null, "causeHumanoidPerkId", record.CausePerkId);
                SetReplicationMedicalMember(woundInfo, "CurrentSeverity", "currentSeverity", record.CurrentSeverity);
                SetReplicationMedicalMember(woundInfo, null, "minSeverity", record.MinimumSeverity);
                SetReplicationMedicalMember(woundInfo, "NeedTend", "needTend", record.NeedsTending);
                SetReplicationMedicalMember(woundInfo, "NeedRest", "needRest", record.NeedsRest);
                SetReplicationMedicalMember(woundInfo, "LastTickTime", "lastTickTime", record.LastTickTime);
                SetReplicationMedicalMember(woundInfo, null, "lastTendTime", record.LastTendTime);
                SetReplicationMedicalMember(woundInfo, null, "lastTendQuality", record.LastTendQuality);
                active[index] = boxedEffector;
                updated++;
            }

            detail = "added=" + added.ToString(CultureInfo.InvariantCulture)
                + " updated=" + updated.ToString(CultureInfo.InvariantCulture)
                + " removed=" + removed.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static void EndReplicationMedicalWoundAtIndex(object stats, IList active, int index, object effector)
        {
            // The game's indexed EndEffector refuses finite-duration effectors before
            // their natural expiry. An authoritative removal must still project now,
            // so run the blueprint instance End contracts and remove the exact row.
            TryReadInstanceMemberValue(effector, "Blueprint", out var blueprint);
            active.RemoveAt(index);
            if (blueprint != null
                && TryReadInstanceMemberValue(blueprint, "Instances", out var instances)
                && instances is IEnumerable enumerable)
            {
                foreach (var effectInstance in enumerable)
                {
                    if (effectInstance == null) continue;
                    var end = FindReplicationMedicalCompatibleMethod(effectInstance.GetType(), "End", stats);
                    end?.Invoke(effectInstance, new[] { stats });
                }
            }
        }

        private static int FindReplicationMedicalWoundIndex(IList active, string name)
        {
            for (var i = 0; i < active.Count; i++)
            {
                var candidate = active[i];
                if (candidate != null
                    && TryReadInstanceMemberValue(candidate, "WoundInfo", out var wound)
                    && wound != null
                    && string.Equals(ReadReplicationMedicalString(candidate, "Name", "name"), name, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private static void RefreshReplicationMedicalHealthPanels(object owner)
        {
            if (!replicationConfigMedicalPanelRefreshV1 || ReplicationMedicalHealthPanels.Count == 0) return;
            replicationMedicalPanelRefreshDepth++;
            try
            {
                for (var i = ReplicationMedicalHealthPanels.Count - 1; i >= 0; i--)
                {
                    var panel = ReplicationMedicalHealthPanels[i].Target;
                    if (panel == null)
                    {
                        ReplicationMedicalHealthPanels.RemoveAt(i);
                        continue;
                    }
                    if (panel is Behaviour behaviour && (!behaviour.isActiveAndEnabled || !behaviour.gameObject.activeInHierarchy)) continue;
                    var creature = AccessTools.Property(panel.GetType(), "CreatureBase")?.GetValue(panel, null);
                    if (!ReferenceEquals(creature, owner)) continue;
                    AccessTools.Method(panel.GetType(), "UpdateTabPanel", Type.EmptyTypes)?.Invoke(panel, null);
                }
            }
            catch (Exception ex)
            {
                if (replicationConfigMedicalDiagnostics)
                    instance?.LogReplicationWarning("Going Cooperative medical-v1 panel refresh failed " + FormatReflectionExceptionDetail(ex));
            }
            finally
            {
                replicationMedicalPanelRefreshDepth--;
            }
        }

        private static bool TryApplyReplicationMedicalTreatmentOrder(
            string kind,
            string doctorEntityId,
            string patientEntityId,
            string requestId,
            out string detail)
        {
            if (!replicationConfigMedicalReplicationV1 || !replicationConfigMedicalTreatmentOrdersV1 || !replicationConfigHostMode)
            {
                detail = "medical-v1-orders-disabled-or-not-host";
                return false;
            }
            if (kind != "worker" && kind != "animal" && kind != "self")
            {
                detail = "medical-v1-order-kind-invalid kind=" + kind;
                return false;
            }
            var doctorDetail = string.Empty;
            var patientDetail = string.Empty;
            if (!TryFindReplicationAgentOwnerByEntityId(doctorEntityId, out var doctor, out doctorDetail) || doctor == null
                || !TryFindReplicationAgentOwnerByEntityId(patientEntityId, out var patient, out patientDetail) || patient == null)
            {
                detail = "medical-v1-order-entity-missing doctor=" + doctorDetail + " patient=" + patientDetail;
                return false;
            }
            if (kind == "self" && !ReferenceEquals(doctor, patient))
            {
                detail = "medical-v1-self-target-mismatch";
                return false;
            }
            if (!IsReplicationMedicalHumanoid(doctor)
                || (kind == "worker" && !IsReplicationMedicalHumanoid(patient))
                || (kind == "animal" && !IsReplicationMedicalAnimal(patient))
                || IsReplicationMedicalDeadOrDisposed(doctor)
                || IsReplicationMedicalDeadOrDisposed(patient)
                || !TryGetReplicationMedicalStats(patient, out var patientStats)
                || patientStats == null
                || !HasReplicationMedicalUntendedWound(patient, patientStats))
            {
                detail = "medical-v1-order-validation-failed kind=" + kind;
                return false;
            }

            try
            {
                if (kind == "worker")
                {
                    var currentGoalName = ReadReplicationMedicalCurrentGoalName(patient);
                    if (!string.Equals(currentGoalName, "FaintGoal", StringComparison.Ordinal))
                    {
                        SetReplicationMedicalMember(patient, "CanReceiveWoundTreatment", "canReceiveWoundTreatment", true);
                        if (!TryForceReplicationMedicalGoal(patient, "PatientGoal", doctor, out var patientGoalDetail))
                        {
                            detail = "medical-v1-patient-goal-failed " + patientGoalDetail;
                            return false;
                        }
                    }
                    if (!TryForceReplicationMedicalGoal(doctor, "TendWoundsGoal", patient, out var doctorGoalDetail))
                    {
                        detail = "medical-v1-doctor-goal-failed " + doctorGoalDetail;
                        return false;
                    }
                }
                else if (kind == "animal")
                {
                    SetReplicationMedicalMember(patient, "CanReceiveWoundTreatment", "canReceiveWoundTreatment", true);
                    if (!TryForceReplicationMedicalGoal(doctor, "TendWoundsGoal", patient, out var animalGoalDetail))
                    {
                        detail = "medical-v1-animal-goal-failed " + animalGoalDetail;
                        return false;
                    }
                }
                else if (!TryForceReplicationMedicalGoal(doctor, "SelfTendWoundsGoal", patient, out var selfGoalDetail))
                {
                    detail = "medical-v1-self-goal-failed " + selfGoalDetail;
                    return false;
                }

                ReplicationMedicalDirtyStats.Add(patientStats);
                replicationMedicalOrdersApplied++;
                detail = "ok medical-v1-order requestId=" + requestId + " kind=" + kind
                    + " doctor=" + doctorEntityId + " patient=" + patientEntityId;
                return true;
            }
            catch (Exception ex)
            {
                detail = "medical-v1-order-exception " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryForceReplicationMedicalGoal(object worker, string goalName, object target, out string detail)
        {
            if (!TryReadInstanceMemberValue(worker, "GoapAgent", out var goapAgent) || goapAgent == null)
            {
                detail = "worker-goap-agent-missing";
                return false;
            }
            var workerGoapType = AccessTools.TypeByName("NSMedieval.Goap.WorkerGoapAgent");
            if (workerGoapType == null || !workerGoapType.IsInstanceOfType(goapAgent))
            {
                detail = "worker-goap-agent-type-invalid";
                return false;
            }

            var reservationType = AccessTools.TypeByName("NSMedieval.Manager.ReservationManager");
            var reservation = reservationType == null ? null : ResolveReplicationUnityManagerInstance(reservationType);
            if (reservation == null)
            {
                detail = "reservation-manager-missing";
                return false;
            }
            var setPreferred = FindReplicationMedicalCompatibleMethod(reservation.GetType(), "SetPreferredReservable", worker, target);
            var reserve = FindReplicationMedicalCompatibleMethod(reservation.GetType(), "TryToExclusiveReservation", target, worker, 1f);
            if (setPreferred == null || reserve == null)
            {
                detail = "reservation-method-missing";
                return false;
            }
            setPreferred.Invoke(reservation, new[] { worker, target });
            var reserved = Convert.ToBoolean(reserve.Invoke(reservation, new object[] { target, worker, 1f }), CultureInfo.InvariantCulture);
            AccessTools.Method(goapAgent.GetType(), "Abort", Type.EmptyTypes)?.Invoke(goapAgent, null);
            var force = AccessTools.Method(workerGoapType, "ForceNextGoalExclusive", new[] { typeof(string) });
            var goal = force?.Invoke(goapAgent, new object[] { goalName });
            detail = goal == null
                ? "force-goal-returned-null goal=" + goalName + " reserved=" + reserved
                : "ok goal=" + goalName + " reserved=" + reserved;
            return goal != null;
        }

        private static MethodInfo? FindReplicationMedicalCompatibleMethod(Type type, string name, params object[] arguments)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal)) continue;
                var parameters = methods[i].GetParameters();
                if (parameters.Length != arguments.Length) continue;
                var compatible = true;
                for (var p = 0; p < parameters.Length; p++)
                {
                    if (arguments[p] != null && !parameters[p].ParameterType.IsInstanceOfType(arguments[p]))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (compatible) return methods[i];
            }
            return null;
        }

        private static bool HasReplicationMedicalUntendedWound(object patient, object stats)
        {
            var native = AccessTools.Method(patient.GetType(), "HasUntendendWounds", Type.EmptyTypes);
            if (native != null)
            {
                try { return Convert.ToBoolean(native.Invoke(patient, null), CultureInfo.InvariantCulture); }
                catch { }
            }
            if (!TryGetReplicationMedicalActiveEffectors(stats, out var active)) return false;
            for (var i = 0; i < active.Count; i++)
            {
                var effector = active[i];
                if (effector != null
                    && TryReadInstanceMemberValue(effector, "WoundInfo", out var wound)
                    && wound != null
                    && ReadReplicationMedicalBool(wound, "NeedTend", "needTend")) return true;
            }
            return false;
        }

        private static bool IsReplicationMedicalHumanoid(object value)
        {
            var type = AccessTools.TypeByName("NSMedieval.State.HumanoidInstance");
            return type != null && type.IsInstanceOfType(value);
        }

        private static bool IsReplicationMedicalAnimal(object value)
        {
            var type = AccessTools.TypeByName("NSMedieval.State.AnimalInstance");
            return type != null && type.IsInstanceOfType(value);
        }

        private static bool IsReplicationMedicalDeadOrDisposed(object value)
        {
            return (TryReadReplicationBooleanState(value, "HasDied", out var dead) && dead)
                || (TryReadReplicationBooleanState(value, "HasDisposed", out var disposed) && disposed);
        }

        private static string ReadReplicationMedicalCurrentGoalName(object creature)
        {
            if (TryReadInstanceMemberValue(creature, "GoapAgent", out var agent)
                && agent != null
                && TryReadInstanceMemberValue(agent, "CurrentGoalName", out var value)) return value as string ?? string.Empty;
            return string.Empty;
        }

        private static bool TryApplyReplicationMedicalStateRequest(string entityId, string requestId, out string detail)
        {
            if (!MedicalHostStateCaptureEnabled())
            {
                detail = "medical-v1-state-request-disabled";
                return false;
            }
            var now = Time.realtimeSinceStartup;
            if (ReplicationMedicalHostLastStateRequestByEntityId.TryGetValue(entityId, out var lastRequest)
                && now - lastRequest < 0.5f)
            {
                detail = "ok medical-v1-state-request-rate-limited requestId=" + requestId + " entityId=" + entityId;
                return true;
            }
            ReplicationMedicalHostLastStateRequestByEntityId[entityId] = now;
            if (!TryFindReplicationMedicalOwnerByEntityId(entityId, out var owner, out var ownerDetail)
                || owner == null
                || !TryGetReplicationMedicalStats(owner, out var stats)
                || stats == null)
            {
                detail = "medical-v1-state-request-owner-missing " + ownerDetail;
                return false;
            }
            if (!ReplicationMedicalSubscriptions.TryGetValue(stats, out var subscription))
            {
                subscription = new ReplicationMedicalStatsSubscription { Stats = stats, Owner = owner, EntityId = entityId, Seen = true };
                SubscribeReplicationMedicalStatsEvents(subscription);
                ReplicationMedicalSubscriptions[stats] = subscription;
            }
            SendReplicationMedicalWoundState(subscription, checkpoint: true, force: true);
            detail = "ok medical-v1-state-request requestId=" + requestId + " entityId=" + entityId;
            return true;
        }

        private static bool TryGetReplicationMedicalStats(object owner, out object? stats)
        {
            stats = null;
            return (TryReadInstanceMemberValue(owner, "Stats", out stats) && stats != null)
                || (TryReadInstanceMemberValue(owner, "stats", out stats) && stats != null);
        }

        private static bool TryFindReplicationMedicalOwnerByEntityId(string entityId, out object? owner, out string detail)
        {
            if (TryFindReplicationAgentOwnerByEntityId(entityId, out owner, out detail) && owner != null) return true;
            var primaryDetail = detail;
            var views = FindReplicationAnimatedAgentViews();
            var scanned = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null
                    || view is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy
                    || !TryGetReplicationViewEntityId(view, out var candidateId)
                    || !string.Equals(candidateId, entityId, StringComparison.Ordinal)) continue;
                scanned++;
                if (TryResolveReplicationAgentOwnerFromView(view, out owner, out var ownerDetail) && owner != null)
                {
                    detail = "medical-fallback-owner scanned=" + scanned.ToString(CultureInfo.InvariantCulture) + " " + ownerDetail;
                    return true;
                }
            }
            owner = null;
            detail = "medical-owner-not-found scanned=" + scanned.ToString(CultureInfo.InvariantCulture) + " primary=" + primaryDetail;
            return false;
        }

        private static string GetReplicationMedicalEntityLabel(object owner)
        {
            return TryGetReplicationAgentOwnerEntityId(owner, out var entityId, out _) ? entityId : "unresolved";
        }

        private static string ReadReplicationMedicalString(object owner, string propertyName, string fieldName)
        {
            if (TryReadInstanceMemberValue(owner, propertyName, out var value) && value != null) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (TryReadInstanceMemberValue(owner, fieldName, out value) && value != null) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return string.Empty;
        }

        private static int ReadReplicationMedicalInt(object owner, string propertyName, string fieldName)
        {
            if (TryReadInstanceMemberValue(owner, propertyName, out var value) && value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (TryReadInstanceMemberValue(owner, fieldName, out value) && value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return 0;
        }

        private static long ReadReplicationMedicalLong(object owner, string propertyName, string fieldName)
        {
            if (TryReadInstanceMemberValue(owner, propertyName, out var value) && value != null) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (TryReadInstanceMemberValue(owner, fieldName, out value) && value != null) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return 0L;
        }

        private static float ReadReplicationMedicalFloat(object owner, string propertyName, string fieldName)
        {
            if (TryReadInstanceMemberValue(owner, propertyName, out var value) && value != null) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (TryReadInstanceMemberValue(owner, fieldName, out value) && value != null) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return 0f;
        }

        private static bool ReadReplicationMedicalBool(object owner, string propertyName, string fieldName)
        {
            if (TryReadInstanceMemberValue(owner, propertyName, out var value) && value != null) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (TryReadInstanceMemberValue(owner, fieldName, out value) && value != null) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            return false;
        }

        private static bool SetReplicationMedicalMember(object owner, string? propertyName, string fieldName, object value)
        {
            var type = owner.GetType();
            try
            {
                if (!string.IsNullOrEmpty(propertyName))
                {
                    var property = AccessTools.Property(type, propertyName);
                    if (property?.CanWrite == true)
                    {
                        property.SetValue(owner, ConvertReplicationMedicalValue(value, property.PropertyType), null);
                        return true;
                    }
                }
                var field = AccessTools.Field(type, fieldName);
                if (field != null)
                {
                    field.SetValue(owner, ConvertReplicationMedicalValue(value, field.FieldType));
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (replicationConfigMedicalDiagnostics)
                    instance?.LogReplicationWarning("Going Cooperative medical-v1 member set failed member=" + (propertyName ?? fieldName) + " " + FormatReflectionExceptionDetail(ex));
            }
            return false;
        }

        private static object ConvertReplicationMedicalValue(object value, Type targetType)
        {
            if (targetType.IsInstanceOfType(value)) return value;
            if (targetType.IsEnum) return Enum.ToObject(targetType, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static void ResetReplicationMedicalV1State()
        {
            foreach (var subscription in ReplicationMedicalSubscriptions.Values) UnsubscribeReplicationMedicalStatsEvents(subscription);
            ReplicationMedicalSubscriptions.Clear();
            ReplicationMedicalDirtyStats.Clear();
            ReplicationMedicalHostRevisionByEntityId.Clear();
            ReplicationMedicalHostSignatureByEntityId.Clear();
            ReplicationMedicalClientRevisionByEntityId.Clear();
            ReplicationMedicalLastPanelRequestByEntityId.Clear();
            ReplicationMedicalHostLastStateRequestByEntityId.Clear();
            ReplicationMedicalHealthPanels.Clear();
            ReplicationMedicalDirtyScratch.Clear();
            ReplicationMedicalStaleSubscriptionScratch.Clear();
            replicationMedicalNextRosterRefreshRealtime = 0f;
            replicationMedicalNextFlushRealtime = 0f;
            replicationMedicalNextCheckpointRealtime = 0f;
            replicationMedicalNextDiagnosticsRealtime = 0f;
            replicationMedicalApplyDepth = 0;
            replicationMedicalPanelRefreshDepth = 0;
            replicationMedicalRequestSequence = 0L;
            replicationMedicalStatesSent = 0L;
            replicationMedicalStatesApplied = 0L;
            replicationMedicalOrdersSent = 0L;
            replicationMedicalOrdersApplied = 0L;
            replicationMedicalNativeEvents = 0L;
            replicationMedicalTickMarks = 0L;
        }
    }
}
