using System;
using System.Collections.Generic;
using System.Globalization;

namespace GoingCooperative.Core
{
    /// <summary>
    /// Stable registry of player-originated gameplay intents understood by the
    /// host-authoritative command layer. Presentation/state-only tokens are not
    /// included here.
    /// </summary>
    public static class MultiplayerActionRegistry
    {
        public const string WireVersion = "client-actions-v1";

        private static readonly string[] ClientIntentTokens =
        {
            "kind:Pause",
            "kind:Speed",
            "kind:Dig",
            "kind:Build",
            "kind:Cut",
            "kind:RegionOrder",
            "custom:" + LockstepCommandPayloads.EquipOrderAction,
            "custom:" + LockstepCommandPayloads.ResearchActivateAction,
            "custom:" + LockstepCommandPayloads.ProductionQueueAction,
            "custom:" + LockstepCommandPayloads.ProductionQueueV2Action,
            "custom:" + LockstepCommandPayloads.ManagementPolicyAction,
            "custom:" + LockstepCommandPayloads.StoragePolicyUpdateAction,
            "custom:" + LockstepCommandPayloads.WorkerManagePresetAction,
            "custom:" + LockstepCommandPayloads.DraftStateAction,
            "custom:" + LockstepCommandPayloads.DraftMoveAction,
            "custom:" + LockstepCommandPayloads.CombatAttackAction,
            "custom:" + LockstepCommandPayloads.CombatCancelAction,
            "custom:" + LockstepCommandPayloads.GameEventOptionChosenAction,
            "custom:" + LockstepCommandPayloads.TraderTradeCommitAction,
            "custom:" + LockstepCommandPayloads.TraderTradeBasketUpdateAction,
            "custom:" + LockstepCommandPayloads.TraderTradeOpenRequestAction,
            "custom:" + LockstepCommandPayloads.PrioritisedObjectWorkV1Action,
            "medical:" + MedicalReplicationPayloads.TreatmentOrderPrefix,
            "medical:" + MedicalReplicationPayloads.StateRequestPrefix
        };

        private static readonly HashSet<string> CustomIntentActions =
            new HashSet<string>(
                new[]
                {
                    LockstepCommandPayloads.EquipOrderAction,
                    LockstepCommandPayloads.ResearchActivateAction,
                    LockstepCommandPayloads.ProductionQueueAction,
                    LockstepCommandPayloads.ProductionQueueV2Action,
                    LockstepCommandPayloads.ManagementPolicyAction,
                    LockstepCommandPayloads.StoragePolicyUpdateAction,
                    LockstepCommandPayloads.WorkerManagePresetAction,
                    LockstepCommandPayloads.DraftStateAction,
                    LockstepCommandPayloads.DraftMoveAction,
                    LockstepCommandPayloads.CombatAttackAction,
                    LockstepCommandPayloads.CombatCancelAction,
                    LockstepCommandPayloads.GameEventOptionChosenAction,
                    LockstepCommandPayloads.TraderTradeCommitAction,
                    LockstepCommandPayloads.TraderTradeBasketUpdateAction,
                    LockstepCommandPayloads.TraderTradeOpenRequestAction,
                    LockstepCommandPayloads.PrioritisedObjectWorkV1Action
                },
                StringComparer.Ordinal);

        public static IReadOnlyList<string> ClientIntents
        {
            get { return ClientIntentTokens; }
        }

        public static string ClientIntentFingerprint
        {
            get
            {
                var hash = new DeterminismHash();
                hash.Add(WireVersion);
                for (var i = 0; i < ClientIntentTokens.Length; i++)
                {
                    hash.Add(ClientIntentTokens[i]);
                }

                return WireVersion
                    + ":"
                    + hash.Value.ToString(
                        "x16",
                        CultureInfo.InvariantCulture);
            }
        }

        public static bool IsKnownCustomClientIntent(string action)
        {
            return !string.IsNullOrWhiteSpace(action)
                && CustomIntentActions.Contains(action);
        }
    }
}
