using System;
using System.Collections.Generic;
using GoingCooperative.Core;
using TMPro;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private readonly Dictionary<string, GameObject>
            multiplayerPresenceCursorRoots =
                new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, TextMeshProUGUI>
            multiplayerPresenceCursorTexts =
                new Dictionary<string, TextMeshProUGUI>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<GameObject>>
            multiplayerPresenceSelectionRoots =
                new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TextMeshProUGUI>>
            multiplayerPresenceSelectionTexts =
                new Dictionary<string, List<TextMeshProUGUI>>(StringComparer.Ordinal);
        private readonly List<GameObject> multiplayerPresencePingRoots =
            new List<GameObject>();
        private readonly List<TextMeshProUGUI> multiplayerPresencePingTexts =
            new List<TextMeshProUGUI>();

        private void UpdateMultiplayerPresenceGui()
        {
            if (multiplayerCanvasRoot == null)
            {
                return;
            }

            var gameplayVisible = replicationRuntimeStarted
                && !multiplayerMainMenuActive
                && (multiplayerCanvasPanel == null
                    || !multiplayerCanvasPanel.activeSelf)
                && (multiplayerResyncOverlay == null
                    || !multiplayerResyncOverlay.activeSelf);
            if (!gameplayVisible)
            {
                HideMultiplayerPresenceVisuals();
                return;
            }

            var remoteStates = GetReplicationRemotePresenceStates();
            var visiblePeerIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < remoteStates.Count; i++)
            {
                var state = remoteStates[i];
                visiblePeerIds.Add(state.PeerId);
                UpdateMultiplayerRemoteCursorGui(state);
                UpdateMultiplayerRemoteSelectionGui(state);
            }

            HideInactiveMultiplayerRemotePeerVisuals(visiblePeerIds);
            UpdateMultiplayerPingGui();
        }

        private void UpdateMultiplayerRemoteCursorGui(
            ReplicationRemotePresenceState state)
        {
            EnsureMultiplayerPresenceCursorGui(state.PeerId);
            if (!multiplayerPresenceCursorRoots.TryGetValue(
                    state.PeerId,
                    out var root))
            {
                return;
            }

            if (TryGetReplicationRemotePresenceWorldPoint(
                    state.PeerId,
                    out var cursorWorld)
                && TryProjectReplicationPresenceToCanvas(
                    cursorWorld,
                    out var cursorPosition))
            {
                root.SetActive(true);
                root.transform.SetAsLastSibling();
                root.GetComponent<RectTransform>().anchoredPosition =
                    cursorPosition + new Vector2(0f, 18f);
                if (multiplayerPresenceCursorTexts.TryGetValue(
                        state.PeerId,
                        out var text))
                {
                    text.text =
                        GetReplicationRemoteDisplayName(state.PeerId)
                        + "\n+";
                    text.color =
                        GetMultiplayerPeerPresenceColor(state.PeerId);
                }
            }
            else
            {
                root.SetActive(false);
            }
        }

        private void UpdateMultiplayerRemoteSelectionGui(
            ReplicationRemotePresenceState state)
        {
            var selectedEntityIds =
                GetReplicationRemoteSelectedEntityIds(state.PeerId);
            EnsureMultiplayerPresenceSelectionVisualCount(
                state.PeerId,
                selectedEntityIds.Count);

            if (!multiplayerPresenceSelectionRoots.TryGetValue(
                    state.PeerId,
                    out var roots)
                || !multiplayerPresenceSelectionTexts.TryGetValue(
                    state.PeerId,
                    out var texts))
            {
                return;
            }

            for (var i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (i >= selectedEntityIds.Count)
                {
                    root.SetActive(false);
                    continue;
                }

                if (!TryGetReplicationRemoteSelectedEntityWorldPoint(
                        state.PeerId,
                        selectedEntityIds[i],
                        out var world)
                    || !TryProjectReplicationPresenceToCanvas(
                        world,
                        out var position))
                {
                    root.SetActive(false);
                    continue;
                }

                root.SetActive(true);
                root.transform.SetAsLastSibling();
                root.GetComponent<RectTransform>().anchoredPosition =
                    position;
                texts[i].text =
                    "[" + GetReplicationRemoteDisplayName(state.PeerId) + "]";
                texts[i].color =
                    GetMultiplayerPeerPresenceColor(state.PeerId);
            }
        }

        private void UpdateMultiplayerPingGui()
        {
            EnsureMultiplayerPresencePingVisualCount(
                ReplicationPresencePings.Count);
            var now = Time.realtimeSinceStartup;
            var localPeerId = GetReplicationLocalPeerId();
            for (var i = 0; i < multiplayerPresencePingRoots.Count; i++)
            {
                var root = multiplayerPresencePingRoots[i];
                if (i >= ReplicationPresencePings.Count)
                {
                    root.SetActive(false);
                    continue;
                }

                var ping = ReplicationPresencePings[i];
                if (!TryProjectReplicationPresenceToCanvas(
                        ping.WorldPosition,
                        out var position))
                {
                    root.SetActive(false);
                    continue;
                }

                root.SetActive(true);
                root.transform.SetAsLastSibling();
                var rect = root.GetComponent<RectTransform>();
                rect.anchoredPosition = position;
                var age = Mathf.Max(
                    0f,
                    now - ping.CreatedRealtime);
                var alpha = Mathf.Clamp01(
                    ping.ExpiresRealtime - now);
                var pulse =
                    1f + Mathf.Sin(age * 8f) * 0.12f;
                rect.localScale =
                    new Vector3(pulse, pulse, 1f);

                var text = multiplayerPresencePingTexts[i];
                var local = string.Equals(
                    ping.PeerId,
                    localPeerId,
                    StringComparison.Ordinal);
                var baseColor = local
                    ? MultiplayerCanvasAccent
                    : GetMultiplayerPeerPresenceColor(ping.PeerId);
                text.color = new Color(
                    baseColor.r,
                    baseColor.g,
                    baseColor.b,
                    alpha);
                text.text = local
                    ? "PING"
                    : GetReplicationRemoteDisplayName(ping.PeerId)
                        + "  PING";
            }
        }

        private void EnsureMultiplayerPresenceCursorGui(string peerId)
        {
            if (multiplayerCanvasRoot == null
                || multiplayerPresenceCursorRoots.ContainsKey(peerId))
            {
                return;
            }

            var root = new GameObject(
                "Remote Player Cursor " + peerId,
                typeof(RectTransform));
            root.transform.SetParent(
                multiplayerCanvasRoot.transform,
                false);
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin =
                rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(180f, 52f);

            var text = CreateMultiplayerGameText(
                root.transform,
                "Label",
                GetReplicationRemoteDisplayName(peerId) + "\n+",
                16f,
                TextAlignmentOptions.Center,
                GetMultiplayerPeerPresenceColor(peerId));
            text.fontStyle = FontStyles.Bold;
            text.raycastTarget = false;
            SetMultiplayerCanvasRect(
                text.rectTransform,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            root.SetActive(false);
            multiplayerPresenceCursorRoots.Add(peerId, root);
            multiplayerPresenceCursorTexts.Add(peerId, text);
        }

        private void EnsureMultiplayerPresenceSelectionVisualCount(
            string peerId,
            int count)
        {
            if (multiplayerCanvasRoot == null)
            {
                return;
            }

            if (!multiplayerPresenceSelectionRoots.TryGetValue(
                    peerId,
                    out var roots))
            {
                roots = new List<GameObject>();
                multiplayerPresenceSelectionRoots.Add(peerId, roots);
            }

            if (!multiplayerPresenceSelectionTexts.TryGetValue(
                    peerId,
                    out var texts))
            {
                texts = new List<TextMeshProUGUI>();
                multiplayerPresenceSelectionTexts.Add(peerId, texts);
            }

            while (roots.Count < count)
            {
                var root = new GameObject(
                    "Remote Player Selection "
                        + peerId
                        + " "
                        + roots.Count.ToString(),
                    typeof(RectTransform));
                root.transform.SetParent(
                    multiplayerCanvasRoot.transform,
                    false);
                var rect = root.GetComponent<RectTransform>();
                rect.anchorMin =
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(200f, 30f);

                var text = CreateMultiplayerGameText(
                    root.transform,
                    "Label",
                    "[" + GetReplicationRemoteDisplayName(peerId) + "]",
                    14f,
                    TextAlignmentOptions.Center,
                    GetMultiplayerPeerPresenceColor(peerId));
                text.fontStyle = FontStyles.Bold;
                text.raycastTarget = false;
                SetMultiplayerCanvasRect(
                    text.rectTransform,
                    Vector2.zero,
                    Vector2.one,
                    Vector2.zero,
                    Vector2.zero);
                roots.Add(root);
                texts.Add(text);
            }
        }

        private void EnsureMultiplayerPresencePingVisualCount(int count)
        {
            if (multiplayerCanvasRoot == null)
            {
                return;
            }

            while (multiplayerPresencePingRoots.Count < count)
            {
                var root = new GameObject(
                    "Player Ping",
                    typeof(RectTransform));
                root.transform.SetParent(
                    multiplayerCanvasRoot.transform,
                    false);
                var rect = root.GetComponent<RectTransform>();
                rect.anchorMin =
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(180f, 34f);

                var text = CreateMultiplayerGameText(
                    root.transform,
                    "Label",
                    "PING",
                    17f,
                    TextAlignmentOptions.Center,
                    MultiplayerCanvasAccent);
                text.fontStyle = FontStyles.Bold;
                text.raycastTarget = false;
                SetMultiplayerCanvasRect(
                    text.rectTransform,
                    Vector2.zero,
                    Vector2.one,
                    Vector2.zero,
                    Vector2.zero);
                multiplayerPresencePingRoots.Add(root);
                multiplayerPresencePingTexts.Add(text);
            }
        }

        private static Color GetMultiplayerPeerPresenceColor(string peerId)
        {
            var slot = 0;
            if (string.Equals(
                    peerId,
                    ReplicationHostPeerId,
                    StringComparison.Ordinal))
            {
                slot = 0;
            }
            else if (MultiplayerPeerIds.TryParseClientSlot(
                peerId,
                out var clientSlot))
            {
                slot = clientSlot;
            }

            switch (slot % 8)
            {
                case 0: return new Color(0.28f, 0.82f, 1f, 0.98f);
                case 1: return new Color(1f, 0.72f, 0.28f, 0.98f);
                case 2: return new Color(0.52f, 1f, 0.46f, 0.98f);
                case 3: return new Color(0.96f, 0.48f, 0.92f, 0.98f);
                case 4: return new Color(1f, 0.48f, 0.42f, 0.98f);
                case 5: return new Color(0.68f, 0.62f, 1f, 0.98f);
                case 6: return new Color(0.36f, 1f, 0.84f, 0.98f);
                default: return new Color(1f, 0.9f, 0.42f, 0.98f);
            }
        }

        private bool TryProjectReplicationPresenceToCanvas(
            Vector3 world,
            out Vector2 canvasPosition)
        {
            canvasPosition = Vector2.zero;
            if (multiplayerCanvasRoot == null)
            {
                return false;
            }

            var camera = GetReplicationPresenceCamera();
            if (camera == null)
            {
                return false;
            }

            var screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f
                || screen.x < -64f
                || screen.y < -64f
                || screen.x > Screen.width + 64f
                || screen.y > Screen.height + 64f)
            {
                return false;
            }

            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                multiplayerCanvasRoot.GetComponent<RectTransform>(),
                new Vector2(screen.x, screen.y),
                null,
                out canvasPosition);
        }

        private void HideInactiveMultiplayerRemotePeerVisuals(
            HashSet<string> activePeerIds)
        {
            foreach (var pair in multiplayerPresenceCursorRoots)
            {
                if (!activePeerIds.Contains(pair.Key))
                {
                    pair.Value.SetActive(false);
                }
            }

            foreach (var pair in multiplayerPresenceSelectionRoots)
            {
                if (activePeerIds.Contains(pair.Key))
                {
                    continue;
                }

                for (var i = 0; i < pair.Value.Count; i++)
                {
                    pair.Value[i].SetActive(false);
                }
            }
        }

        private void HideMultiplayerPresenceVisuals()
        {
            foreach (var root in multiplayerPresenceCursorRoots.Values)
            {
                root.SetActive(false);
            }

            foreach (var roots in multiplayerPresenceSelectionRoots.Values)
            {
                for (var i = 0; i < roots.Count; i++)
                {
                    roots[i].SetActive(false);
                }
            }

            for (var i = 0; i < multiplayerPresencePingRoots.Count; i++)
            {
                multiplayerPresencePingRoots[i].SetActive(false);
            }
        }

        private void DestroyMultiplayerPresenceGui()
        {
            foreach (var root in multiplayerPresenceCursorRoots.Values)
            {
                if (root != null)
                {
                    Destroy(root);
                }
            }

            foreach (var roots in multiplayerPresenceSelectionRoots.Values)
            {
                for (var i = 0; i < roots.Count; i++)
                {
                    if (roots[i] != null)
                    {
                        Destroy(roots[i]);
                    }
                }
            }

            for (var i = 0; i < multiplayerPresencePingRoots.Count; i++)
            {
                if (multiplayerPresencePingRoots[i] != null)
                {
                    Destroy(multiplayerPresencePingRoots[i]);
                }
            }

            multiplayerPresenceCursorRoots.Clear();
            multiplayerPresenceCursorTexts.Clear();
            multiplayerPresenceSelectionRoots.Clear();
            multiplayerPresenceSelectionTexts.Clear();
            multiplayerPresencePingRoots.Clear();
            multiplayerPresencePingTexts.Clear();
        }
    }
}
