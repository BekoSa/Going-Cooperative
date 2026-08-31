using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private GameObject? multiplayerPresenceCursorRoot;
        private TextMeshProUGUI? multiplayerPresenceCursorText;
        private readonly List<GameObject> multiplayerPresencePingRoots = new List<GameObject>();
        private readonly List<TextMeshProUGUI> multiplayerPresencePingTexts = new List<TextMeshProUGUI>();
        private readonly List<GameObject> multiplayerPresenceSelectionRoots = new List<GameObject>();
        private readonly List<TextMeshProUGUI> multiplayerPresenceSelectionTexts = new List<TextMeshProUGUI>();

        private void UpdateMultiplayerPresenceGui()
        {
            if (multiplayerCanvasRoot == null)
            {
                return;
            }

            var gameplayVisible = replicationRuntimeStarted
                && !multiplayerMainMenuActive
                && (multiplayerCanvasPanel == null || !multiplayerCanvasPanel.activeSelf)
                && (multiplayerResyncOverlay == null || !multiplayerResyncOverlay.activeSelf);
            if (!gameplayVisible)
            {
                HideMultiplayerPresenceVisuals();
                return;
            }

            UpdateMultiplayerRemoteCursorGui();
            UpdateMultiplayerRemoteSelectionGui();
            UpdateMultiplayerPingGui();
        }

        private void UpdateMultiplayerRemoteCursorGui()
        {
            EnsureMultiplayerPresenceCursorGui();
            if (multiplayerPresenceCursorRoot == null)
            {
                return;
            }

            if (TryGetReplicationRemotePresenceWorldPoint(out var cursorWorld)
                && TryProjectReplicationPresenceToCanvas(cursorWorld, out var cursorPosition))
            {
                multiplayerPresenceCursorRoot.SetActive(true);
                multiplayerPresenceCursorRoot.transform.SetAsLastSibling();
                multiplayerPresenceCursorRoot.GetComponent<RectTransform>().anchoredPosition =
                    cursorPosition + new Vector2(0f, 18f);
                if (multiplayerPresenceCursorText != null)
                {
                    multiplayerPresenceCursorText.text =
                        GetReplicationRemoteDisplayName() + "\n+";
                }
            }
            else
            {
                multiplayerPresenceCursorRoot.SetActive(false);
            }
        }

        private void UpdateMultiplayerRemoteSelectionGui()
        {
            var selectedEntityIds = GetReplicationRemoteSelectedEntityIds();
            EnsureMultiplayerPresenceSelectionVisualCount(selectedEntityIds.Count);
            for (var i = 0; i < multiplayerPresenceSelectionRoots.Count; i++)
            {
                var root = multiplayerPresenceSelectionRoots[i];
                if (i >= selectedEntityIds.Count)
                {
                    root.SetActive(false);
                    continue;
                }

                if (!TryGetReplicationRemoteSelectedEntityWorldPoint(
                        selectedEntityIds[i],
                        out var world)
                    || !TryProjectReplicationPresenceToCanvas(world, out var position))
                {
                    root.SetActive(false);
                    continue;
                }

                root.SetActive(true);
                root.transform.SetAsLastSibling();
                root.GetComponent<RectTransform>().anchoredPosition = position;
                var text = multiplayerPresenceSelectionTexts[i];
                text.text = "[" + GetReplicationRemoteDisplayName() + "]";
            }
        }

        private void UpdateMultiplayerPingGui()
        {
            EnsureMultiplayerPresencePingVisualCount(ReplicationPresencePings.Count);
            var now = Time.realtimeSinceStartup;
            for (var i = 0; i < multiplayerPresencePingRoots.Count; i++)
            {
                var root = multiplayerPresencePingRoots[i];
                if (i >= ReplicationPresencePings.Count)
                {
                    root.SetActive(false);
                    continue;
                }

                var ping = ReplicationPresencePings[i];
                if (!TryProjectReplicationPresenceToCanvas(ping.WorldPosition, out var position))
                {
                    root.SetActive(false);
                    continue;
                }

                root.SetActive(true);
                root.transform.SetAsLastSibling();
                var rect = root.GetComponent<RectTransform>();
                rect.anchoredPosition = position;
                var age = Mathf.Max(0f, now - ping.CreatedRealtime);
                var alpha = Mathf.Clamp01(ping.ExpiresRealtime - now);
                var pulse = 1f + Mathf.Sin(age * 8f) * 0.12f;
                rect.localScale = new Vector3(pulse, pulse, 1f);

                var text = multiplayerPresencePingTexts[i];
                var baseColor = ping.Remote
                    ? new Color(0.28f, 0.82f, 1f, 1f)
                    : MultiplayerCanvasAccent;
                text.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
                text.text = ping.Remote ? "PING +" : "PING";
            }
        }

        private void EnsureMultiplayerPresenceCursorGui()
        {
            if (multiplayerCanvasRoot == null || multiplayerPresenceCursorRoot != null)
            {
                return;
            }

            multiplayerPresenceCursorRoot = new GameObject(
                "Remote Player Cursor",
                typeof(RectTransform));
            multiplayerPresenceCursorRoot.transform.SetParent(multiplayerCanvasRoot.transform, false);
            var rect = multiplayerPresenceCursorRoot.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(140f, 52f);

            multiplayerPresenceCursorText = CreateMultiplayerGameText(
                multiplayerPresenceCursorRoot.transform,
                "Label",
                "PLAYER\n+",
                16f,
                TextAlignmentOptions.Center,
                new Color(0.28f, 0.82f, 1f, 0.98f));
            multiplayerPresenceCursorText.fontStyle = FontStyles.Bold;
            multiplayerPresenceCursorText.raycastTarget = false;
            SetMultiplayerCanvasRect(
                multiplayerPresenceCursorText.rectTransform,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            multiplayerPresenceCursorRoot.SetActive(false);
        }

        private void EnsureMultiplayerPresenceSelectionVisualCount(int count)
        {
            if (multiplayerCanvasRoot == null)
            {
                return;
            }

            while (multiplayerPresenceSelectionRoots.Count < count)
            {
                var root = new GameObject(
                    "Remote Player Selection",
                    typeof(RectTransform));
                root.transform.SetParent(multiplayerCanvasRoot.transform, false);
                var rect = root.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(180f, 30f);

                var text = CreateMultiplayerGameText(
                    root.transform,
                    "Label",
                    "◆ OTHER PLAYER",
                    14f,
                    TextAlignmentOptions.Center,
                    new Color(0.28f, 0.82f, 1f, 0.95f));
                text.fontStyle = FontStyles.Bold;
                text.raycastTarget = false;
                SetMultiplayerCanvasRect(
                    text.rectTransform,
                    Vector2.zero,
                    Vector2.one,
                    Vector2.zero,
                    Vector2.zero);
                multiplayerPresenceSelectionRoots.Add(root);
                multiplayerPresenceSelectionTexts.Add(text);
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
                var root = new GameObject("Player Ping", typeof(RectTransform));
                root.transform.SetParent(multiplayerCanvasRoot.transform, false);
                var rect = root.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(110f, 34f);

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

        private void HideMultiplayerPresenceVisuals()
        {
            if (multiplayerPresenceCursorRoot != null)
            {
                multiplayerPresenceCursorRoot.SetActive(false);
            }

            for (var i = 0; i < multiplayerPresencePingRoots.Count; i++)
            {
                multiplayerPresencePingRoots[i].SetActive(false);
            }

            for (var i = 0; i < multiplayerPresenceSelectionRoots.Count; i++)
            {
                multiplayerPresenceSelectionRoots[i].SetActive(false);
            }
        }

        private void DestroyMultiplayerPresenceGui()
        {
            if (multiplayerPresenceCursorRoot != null)
            {
                Destroy(multiplayerPresenceCursorRoot);
            }

            for (var i = 0; i < multiplayerPresencePingRoots.Count; i++)
            {
                if (multiplayerPresencePingRoots[i] != null)
                {
                    Destroy(multiplayerPresencePingRoots[i]);
                }
            }

            for (var i = 0; i < multiplayerPresenceSelectionRoots.Count; i++)
            {
                if (multiplayerPresenceSelectionRoots[i] != null)
                {
                    Destroy(multiplayerPresenceSelectionRoots[i]);
                }
            }

            multiplayerPresenceCursorRoot = null;
            multiplayerPresenceCursorText = null;
            multiplayerPresencePingRoots.Clear();
            multiplayerPresencePingTexts.Clear();
            multiplayerPresenceSelectionRoots.Clear();
            multiplayerPresenceSelectionTexts.Clear();
        }
    }
}
