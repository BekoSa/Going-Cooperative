using System;
using System.Collections.Generic;
using System.Globalization;

namespace GoingCooperative.Core
{
    /// <summary>
    /// Stable registry for player-originated payload actions and host-only
    /// state/presentation payload actions. Every public *Action constant in
    /// LockstepCommandPayloads must be classified here and is verified by tests.
    /// </summary>
    public static class MultiplayerActionRegistry
    {
        public const string WireVersion = "client-actions-v2";

        private static readonly string[] ClientPayloadActionTokens =
        {
            LockstepCommandPayloads.SetPausedAction,
            LockstepCommandPayloads.SetSpeedIndexAction,
            LockstepCommandPayloads.SetSpeedNormalAction,
            LockstepCommandPayloads.DigVoxelAction,
            LockstepCommandPayloads.PlaceBlueprintAction,
            LockstepCommandPayloads.PlaceBlueprintBatchAction,
            LockstepCommandPayloads.CutPlantAction,
            LockstepCommandPayloads.RegionOrderAction,
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
        };

        private static readonly string[] HostPayloadActionTokens =
        {
            LockstepCommandPayloads.StoragePolicyStateAction,
            LockstepCommandPayloads.CombatOutcomeAction,
            LockstepCommandPayloads.CombatPresentationAction,
            LockstepCommandPayloads.PrioritisedObjectWorkResultV1Action
        };

        private static readonly HashSet<string> ClientPayloadActions =
            new HashSet<string>(
                ClientPayloadActionTokens,
                StringComparer.Ordinal);

        private static readonly HashSet<string> HostPayloadActions =
            new HashSet<string>(
                HostPayloadActionTokens,
                StringComparer.Ordinal);

        private static readonly HashSet<string> CustomClientIntentActions =
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

        public static IReadOnlyList<string> ClientPayloadActionsList
        {
            get { return ClientPayloadActionTokens; }
        }

        public static IReadOnlyList<string> HostPayloadActionsList
        {
            get { return HostPayloadActionTokens; }
        }

        public static string ClientIntentFingerprint
        {
            get
            {
                var hash = new DeterminismHash();
                hash.Add(WireVersion);
                for (var i = 0; i < ClientPayloadActionTokens.Length; i++)
                {
                    hash.Add(ClientPayloadActionTokens[i]);
                }

                // Medical V1 uses compact pipe-delimited payload prefixes rather
                // than LockstepCommandPayloads JSON action names.
                hash.Add(MedicalReplicationPayloads.TreatmentOrderPrefix);
                hash.Add(MedicalReplicationPayloads.StateRequestPrefix);

                return WireVersion
                    + ":"
                    + hash.Value.ToString(
                        "x16",
                        CultureInfo.InvariantCulture);
            }
        }

        public static bool IsKnownClientPayloadAction(string action)
        {
            return !string.IsNullOrWhiteSpace(action)
                && ClientPayloadActions.Contains(action);
        }

        public static bool IsKnownHostPayloadAction(string action)
        {
            return !string.IsNullOrWhiteSpace(action)
                && HostPayloadActions.Contains(action);
        }

        public static bool IsKnownCustomClientIntent(string action)
        {
            return !string.IsNullOrWhiteSpace(action)
                && CustomClientIntentActions.Contains(action);
        }
    }
}
