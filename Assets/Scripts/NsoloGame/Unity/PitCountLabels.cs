using TMPro;
using UnityEngine;
using NsoloGame.Core;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Draws the stone count over every pit.
    ///
    /// This is not a convenience: PitStoneVisualizer caps each pile at maxVisualStonesPerPit (12),
    /// so a pit holding more than that renders fewer stones than it actually contains. Counting the
    /// board by eye is therefore not merely awkward, it is wrong on exactly the big pits that
    /// decide captures — which is unworkable in a strategy game.
    ///
    /// The labels are built in code rather than authored into the scene, so there are no 32 extra
    /// objects to place and they follow the pits wherever the board art moves.
    /// </summary>
    public class PitCountLabels : MonoBehaviour
    {
        [Header("Placement")]
        [Tooltip("Canvas the labels are parented to. Falls back to the first canvas in the scene.")]
        [SerializeField] private Canvas targetCanvas;
        [Tooltip("Screen-space nudge from the pit centre, in reference-resolution pixels.")]
        [SerializeField] private Vector2 screenOffset = new Vector2(0f, 0f);

        [Header("Appearance")]
        [Tooltip("Optional. Leave empty to inherit the HUD's font, which is PlusJakartaSans-Bold " +
                 "SDF — that is what keeps the counts matching the rest of the interface.")]
        [SerializeField] private TMP_FontAsset fontOverride;
        [SerializeField] private float fontSize = 50f;
        [SerializeField] private Color textColor = new Color(1f, 0.97f, 0.9f, 1f);
        [Tooltip("Dark outline so the number stays readable over both stones and bare wood.")]
        [SerializeField, Range(0f, 1f)] private float outlineWidth = 0.25f;
        [Tooltip("Box each number is drawn in. Needs headroom over the font size or tall glyphs clip.")]
        [SerializeField] private Vector2 labelSize = new Vector2(130f, 76f);

        [Header("Behaviour")]
        [Tooltip("Counts are on by default — the visual pile cap makes the board unreadable without them.")]
        [SerializeField] private bool visible = true;
        [Tooltip("Only pits holding MORE than this get a number. Small piles read fine as stones, " +
                 "so labelling them is just clutter.")]
        [SerializeField] private int minimumStonesToLabel = 5;

        /// <summary>PlayerPrefs key so the choice survives a restart.</summary>
        public const string PrefKey = "ShowPitCounts";

        private PitStoneVisualizer visualizer;
        private TMP_FontAsset font;
        private Camera boardCamera;
        private RectTransform canvasRect;
        private readonly TMP_Text[,] labels = new TMP_Text[4, 8];
        private bool built;

        // Kept so toggling visibility can re-evaluate the threshold without the caller having to
        // hand the board back in.
        private GameBoard lastBoard;

        public bool Visible => visible;

        private void Awake()
        {
            visible = PlayerPrefs.GetInt(PrefKey, visible ? 1 : 0) == 1;
        }

        /// <summary>
        /// Wired by UIManager, which owns the pit lookup. Kept out of Awake because the visualizer
        /// builds its hole table there and the order between the two is not guaranteed.
        /// </summary>
        public void Initialize(PitStoneVisualizer stoneVisualizer, TMP_Text fontSource)
        {
            visualizer = stoneVisualizer;

            // An explicit override wins; otherwise the counts follow whatever the HUD uses, so
            // they stay in family automatically if the interface font ever changes.
            if (fontOverride != null)
                font = fontOverride;
            else if (fontSource != null && fontSource.font != null)
                font = fontSource.font;
        }

        public void SetVisible(bool value)
        {
            visible = value;
            PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
            PlayerPrefs.Save();
            ApplyVisibility();
        }

        /// <summary>Hidden while stones are in flight, when the numbers would be a frame behind.</summary>
        public void HideDuringAnimation()
        {
            SetGroupActive(false);
        }

        public void Refresh(GameBoard board)
        {
            if (board == null || visualizer == null) return;
            if (!EnsureBuilt()) return;

            lastBoard = board;

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    TMP_Text label = labels[r, c];
                    if (label == null) continue;

                    int count = board.Get(r, c);
                    bool wanted = visible && count > minimumStonesToLabel;

                    label.enabled = wanted;
                    if (!wanted) continue;

                    label.text = count.ToString();
                    PositionLabel(label, r, c);
                }
            }
        }

        private void ApplyVisibility()
        {
            if (lastBoard != null) Refresh(lastBoard);
            else SetGroupActive(false);
        }

        private void SetGroupActive(bool active)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 8; c++)
                    if (labels[r, c] != null)
                        labels[r, c].enabled = active;
        }

        private bool EnsureBuilt()
        {
            if (built) return true;

            if (targetCanvas == null) targetCanvas = FindObjectOfType<Canvas>();
            if (targetCanvas == null)
            {
                Debug.LogWarning("PitCountLabels: no Canvas found, pit counts disabled.");
                return false;
            }

            canvasRect = targetCanvas.transform as RectTransform;

            if (font == null)
            {
                // Falling through to TMP's default means LiberationSans, which will not match the
                // rest of the UI — worth saying out loud rather than quietly looking wrong.
                font = TMP_Settings.defaultFontAsset;
                Debug.LogWarning("PitCountLabels: no font resolved from the HUD, falling back to " +
                                 "the TMP default. Assign Font Override to keep the counts in " +
                                 "PlusJakartaSans.");
            }

            var root = new GameObject("PitCountLabels", typeof(RectTransform));
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.SetParent(canvasRect, false);
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            // Behind any popup, which is added to the canvas after this.
            rootRect.SetAsFirstSibling();

            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 8; c++)
                    labels[r, c] = CreateLabel(rootRect, r, c);

            built = true;
            return true;
        }

        private TMP_Text CreateLabel(RectTransform parent, int row, int col)
        {
            var go = new GameObject($"Count_{row}_{col}", typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = labelSize;

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = fontSize;
            text.color = textColor;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.raycastTarget = false;   // must never eat a pit tap

            // Outline keeps the number legible over a pile of stones as well as bare board.
            text.outlineWidth = outlineWidth;
            text.outlineColor = new Color32(0, 0, 0, 255);

            return text;
        }

        private void PositionLabel(TMP_Text label, int row, int col)
        {
            if (boardCamera == null) boardCamera = Camera.main;
            if (boardCamera == null) return;

            Transform pit = visualizer.GetPitTransform(row, col);
            if (pit == null)
            {
                label.enabled = false;
                return;
            }

            Vector3 screen = boardCamera.WorldToScreenPoint(pit.position);

            // Overlay canvases take a null camera here; anything else needs its own.
            Camera uiCamera = targetCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : targetCanvas.worldCamera;

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, screen, uiCamera, out Vector2 local))
            {
                ((RectTransform)label.transform).anchoredPosition = local + screenOffset;
            }
        }
    }
}
