using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Makes a menu button visibly answer a touch.
    ///
    /// Unity's own press feedback is a colour tint on the button's graphic, and a graphic at zero
    /// alpha tinted any colour is still invisible — so the old menus, which were transparent Buttons
    /// over pills painted into the background, took presses and gave nothing back. The feedback is
    /// drawn instead: a white layer fades up under the finger and the button scales down a little
    /// beneath it.
    ///
    /// The layer takes the shape of whatever the button looks like, and works it out on its own:
    /// <list type="number">
    /// <item>A button with a picture of its own glows as a silhouette of that picture. The sprite
    /// supplies only its shape and the colour is the glow's, so an oval exported from Figma as a
    /// square with transparent padding lights up as the oval — the padding stays transparent.</item>
    /// <item>An invisible button with a picture inside it — an icon on a transparent hitbox — glows
    /// as a silhouette of that picture.</item>
    /// <item>An invisible button with nothing inside, over art painted into the background, glows as
    /// a rounded rectangle the size of the button, since there is no picture to follow.</item>
    /// </list>
    ///
    /// The layer is built in <see cref="Awake"/> and never saved. It carries no information of its
    /// own, so there is nothing in the scene to maintain and nothing to fit by hand.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class UIPressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        [Tooltip("Rounded shape for a button with no picture to glow in the shape of — an " +
                 "invisible hitbox over art painted into the background. Filled in automatically, " +
                 "and ignored on any button that has a picture.")]
        [SerializeField] private Sprite shape;

        [Tooltip("What colour the button lights up. White reads as the artwork brightening, " +
                 "whatever colour the artwork is. Set it to a colour to tint the press instead.")]
        [SerializeField] private Color glowColor = Color.white;

        [Tooltip("How bright the layer gets under a finger on a normal-sized button. Small on " +
                 "purpose: this reads as the button lighting up, not as a white shape appearing on " +
                 "top of it. Smaller buttons — back arrows, close crosses, icons — are brightened " +
                 "automatically, up to 2.5x, because a small glow at this strength goes unnoticed.")]
        [SerializeField, Range(0f, 1f)] private float pressAlpha = 0.14f;

        [Tooltip("How far the button shrinks while held.")]
        [SerializeField, Range(0.8f, 1f)] private float pressedScale = 0.97f;

        [Tooltip("How long the press and release take. Long enough to be seen, short enough that " +
                 "the button never feels like it is lagging behind the finger.")]
        [SerializeField] private float seconds = 0.09f;

        [Tooltip("Optional slow shimmer while idle, for the one button on a screen that is the " +
                 "obvious next step. Zero on everything else — a menu where everything pulses is " +
                 "as flat as one where nothing does.")]
        [SerializeField, Range(0f, 0.2f)] private float idleAlpha;

        [SerializeField] private float idleSeconds = 2.6f;

        /// <summary>Name of the layer this builds.</summary>
        public const string LayerName = "PressLayer";

        /// <summary>
        /// On-screen area, in canvas units, of a normal pill button — about 485 x 150. A glow this
        /// size or larger is drawn at <see cref="pressAlpha"/>.
        /// </summary>
        private const float FullSizeArea = 72000f;

        /// <summary>The most a small button's glow is brightened by, however small it is.</summary>
        private const float MaxSmallBoost = 2.5f;

        private static Material silhouette;
        private static bool silhouetteMissing;

        private RectTransform rect;
        private Vector3 homeScale = Vector3.one;
        private Button button;
        private Image layer;
        private Coroutine motion;
        private bool held;

        /// <summary>The picture the glow is a silhouette of, or null when it is using <see cref="shape"/>.</summary>
        private Image artwork;

        private bool Available => button == null || button.interactable;

        private void Awake()
        {
            rect = (RectTransform)transform;

            // The authored scale, not an assumed 1. Elements exported from Figma are often
            // sized by scaling a small rect rather than by setting its size, and on those a
            // component that resets to Vector3.one does not 'restore' anything — it shrinks
            // the element to a quarter of itself the moment its panel opens.
            homeScale = transform.localScale;
            button = GetComponent<Button>();

            // PressLayers used to be kept in the scene and fitted by hand. Any still sitting under
            // this button are left over from that, and would otherwise draw as a second glow.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name == LayerName) Destroy(child.gameObject);
            }

            var layerObject = new GameObject(LayerName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            layerObject.transform.SetParent(rect, false);
            layer = layerObject.GetComponent<Image>();

            // Never let the decoration eat the tap it is decorating.
            layer.raycastTarget = false;

            Refresh();
        }

        /// <summary>
        /// Decides what the glow is the shape of and fits the layer to it. Run when the button first
        /// appears and every time its panel reopens, so a picture swapped or a button resized in the
        /// editor is picked up without a restart.
        /// </summary>
        private void Refresh()
        {
            if (layer == null) return;

            artwork = FindArtwork();
            Material material = artwork != null ? SilhouetteMaterial() : null;
            if (material == null) artwork = null;

            if (artwork != null)
            {
                layer.material = material;
                CopyArtwork();
            }
            else
            {
                layer.material = null;
                layer.sprite = shape;

                // The rounded shapes carry a 9-slice border; a sprite without one cannot be sliced.
                layer.type = shape != null && shape.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
                layer.preserveAspect = false;
                FitToHitbox();
            }

            SetAlpha(0f);
        }

        /// <summary>
        /// The picture the button is seen as: its own Image when that is visible, otherwise the
        /// largest visible Image directly inside it, otherwise none.
        /// </summary>
        private Image FindArtwork()
        {
            Image own = GetComponent<Image>();
            if (IsVisible(own)) return own;

            Image best = null;
            float bestArea = 0f;

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child.name == LayerName || !child.gameObject.activeSelf) continue;

                Image image = child.GetComponent<Image>();
                if (!IsVisible(image)) continue;

                var childRect = (RectTransform)child;
                Vector2 size = childRect.rect.size;
                float area = Mathf.Abs(size.x * childRect.localScale.x * size.y * childRect.localScale.y);

                if (best == null || area > bestArea)
                {
                    best = image;
                    bestArea = area;
                }
            }

            return best;
        }

        private static bool IsVisible(Image image) =>
            image != null && image.enabled && image.sprite != null && image.color.a > 0.01f;

        /// <summary>
        /// Lays the layer exactly over the artwork and draws it the same way — same sprite, same
        /// slicing, same aspect rule — so the silhouette is the picture's own outline to the pixel.
        /// </summary>
        private void CopyArtwork()
        {
            layer.sprite = artwork.sprite;
            layer.type = artwork.type;
            layer.preserveAspect = artwork.preserveAspect;
            layer.fillCenter = artwork.fillCenter;
            layer.fillMethod = artwork.fillMethod;
            layer.fillAmount = artwork.fillAmount;
            layer.fillClockwise = artwork.fillClockwise;
            layer.fillOrigin = artwork.fillOrigin;
            layer.useSpriteMesh = artwork.useSpriteMesh;
            layer.pixelsPerUnitMultiplier = artwork.pixelsPerUnitMultiplier;

            var layerRect = (RectTransform)layer.transform;

            if (artwork.gameObject == gameObject)
            {
                // The button's own picture. A child stretched over the whole button sits exactly on
                // it, scale included — which is what a silhouette wants, unlike the rounded fallback
                // below, which has to undo the scale to keep its corners round.
                layerRect.anchorMin = Vector2.zero;
                layerRect.anchorMax = Vector2.one;
                layerRect.pivot = rect.pivot;
                layerRect.offsetMin = Vector2.zero;
                layerRect.offsetMax = Vector2.zero;
                layerRect.localRotation = Quaternion.identity;
                layerRect.localScale = Vector3.one;

                // Over the picture, under everything the button carries — its label, an icon.
                layerRect.SetAsFirstSibling();
                return;
            }

            // A picture inside the button. The layer is its sibling, so copying its placement puts
            // one exactly on top of the other.
            var art = (RectTransform)artwork.transform;
            layerRect.anchorMin = art.anchorMin;
            layerRect.anchorMax = art.anchorMax;
            layerRect.pivot = art.pivot;
            layerRect.anchoredPosition = art.anchoredPosition;
            layerRect.sizeDelta = art.sizeDelta;
            layerRect.localRotation = art.localRotation;
            layerRect.localScale = art.localScale;

            // Directly above the picture, so anything drawn over it stays over the glow as well.
            int target = art.GetSiblingIndex();
            if (layerRect.GetSiblingIndex() > target) target++;
            layerRect.SetSiblingIndex(target);
        }

        /// <summary>
        /// Sizes the rounded fallback to the button's on-screen rect and undoes the button's own
        /// scale.
        ///
        /// Stretching it over the button would put it in the button's scaled space. Buttons exported
        /// from Figma keep a uniform 160x30 rect and get their real size from a non-uniform
        /// localScale — around 4x across and 6x down — so the 9-slice corners would be stretched by
        /// those same factors into lopsided ovals. Given the button's effective size and the inverse
        /// of its scale instead, the two cancel to the same on-screen rectangle and the corners are
        /// drawn in unscaled units. The press animation still scales the button, and the layer
        /// rides along with it.
        /// </summary>
        private void FitToHitbox()
        {
            var layerRect = (RectTransform)layer.transform;

            float sx = Mathf.Approximately(homeScale.x, 0f) ? 1f : homeScale.x;
            float sy = Mathf.Approximately(homeScale.y, 0f) ? 1f : homeScale.y;
            Vector2 size = rect.rect.size;

            layerRect.anchorMin = layerRect.anchorMax = new Vector2(0.5f, 0.5f);
            layerRect.pivot = new Vector2(0.5f, 0.5f);
            layerRect.anchoredPosition = Vector2.zero;
            layerRect.sizeDelta = new Vector2(Mathf.Abs(size.x * sx), Mathf.Abs(size.y * sy));
            layerRect.localRotation = Quaternion.identity;
            layerRect.localScale = new Vector3(1f / sx, 1f / sy, 1f);

            // Drawn first so it sits behind any label the button owns.
            layerRect.SetAsFirstSibling();
        }

        /// <summary>
        /// One shared material for every silhouette glow. Null — with a single console line — if the
        /// shader is missing, in which case buttons fall back to the rounded glow rather than drawing
        /// a faint copy of their own picture over itself, which would not show at all.
        /// </summary>
        private static Material SilhouetteMaterial()
        {
            if (silhouette != null || silhouetteMissing) return silhouette;

            Shader shader = Resources.Load<Shader>("NsoloUISilhouette");
            if (shader == null || !shader.isSupported)
            {
                silhouetteMissing = true;
                Debug.LogWarning("UIPressFeedback: the NsoloUISilhouette shader is missing, so buttons " +
                                 "glow as rounded rectangles instead of in the shape of their pictures.");
                return null;
            }

            silhouette = new Material(shader) { name = "NsoloUISilhouette", hideFlags = HideFlags.HideAndDontSave };
            return silhouette;
        }

        private void OnEnable()
        {
            held = false;
            rect.localScale = homeScale;

            Refresh();

            if (idleAlpha > 0f) motion = StartCoroutine(Idle());
        }

        private void OnDisable()
        {
            // Panels are switched off wholesale, which kills the coroutine wherever it happened to
            // be. Reset so the button is not left dimmed or shrunk the next time it appears.
            if (motion != null) StopCoroutine(motion);
            motion = null;
            held = false;
            rect.localScale = homeScale;
            SetAlpha(0f);
        }

        /// <summary>
        /// Adds press feedback to a button that has none, with the rounded shape to fall back on if
        /// it turns out to have no picture of its own.
        ///
        /// Used by <see cref="NsoloElement"/> so a rebuilt button answers a touch without anybody
        /// remembering to add this component. Returns whatever is already there rather than
        /// stacking a second copy.
        /// </summary>
        public static UIPressFeedback Attach(GameObject target, Sprite shapeSprite)
        {
            if (target == null) return null;

            UIPressFeedback existing = target.GetComponent<UIPressFeedback>();
            if (existing != null) return existing;

            UIPressFeedback added = target.AddComponent<UIPressFeedback>();
            added.SetShape(shapeSprite);
            return added;
        }

        /// <summary>
        /// Sets the fallback shape, before or after Awake has built the layer.
        ///
        /// Both orders happen: adding this to a panel that is switched off — which every menu panel
        /// is — defers Awake until the panel first opens, so the field is what gets read; adding it
        /// to something already on screen runs Awake inside AddComponent, and by then the layer
        /// exists and has to be refreshed directly.
        /// </summary>
        public void SetShape(Sprite value)
        {
            shape = value;
            if (layer != null) Refresh();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!Available) return;

            // A picture can change while its screen is up — a card swapping to its selected art —
            // and the glow should be the shape of what is under the finger now.
            if (artwork != null && layer.sprite != artwork.sprite) CopyArtwork();

            held = true;
            Play(Mathf.Min(1f, pressAlpha * SizeBoost()), pressedScale);
        }

        /// <summary>
        /// How much brighter this glow is drawn than a full-size button's.
        ///
        /// A small lit shape at the same strength as a big one is easy to miss — the back arrows and
        /// the quit icon barely seemed to react — so the boost grows as the glow shrinks: the square
        /// root of how many times smaller its area is, so a glow half as wide and half as tall is
        /// drawn twice as bright, capped at <see cref="MaxSmallBoost"/>. Measured at the moment of
        /// the press rather than once, so a panel still sliding or scaling in when the button was
        /// built cannot skew it.
        /// </summary>
        private float SizeBoost()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return 1f;

            // In canvas units rather than pixels, so the answer is the same on every phone: dividing
            // out the root canvas's scale cancels whatever the Canvas Scaler chose for this screen.
            float canvasScale = canvas.rootCanvas.transform.lossyScale.x;
            if (Mathf.Approximately(canvasScale, 0f)) return 1f;

            var layerRect = (RectTransform)layer.transform;
            Vector2 size = layerRect.rect.size;
            Vector3 scale = layerRect.lossyScale;
            float area = Mathf.Abs(size.x * scale.x * size.y * scale.y) / (canvasScale * canvasScale);
            if (area <= 0f) return 1f;

            return Mathf.Clamp(Mathf.Sqrt(FullSizeArea / area), 1f, MaxSmallBoost);
        }

        public void OnPointerUp(PointerEventData eventData) => Release();

        // A finger that slides off a button has changed its mind, and the button should look like
        // it noticed. Without this it stays lit until the next press.
        public void OnPointerExit(PointerEventData eventData) => Release();

        private void Release()
        {
            if (!held) return;

            held = false;
            Play(0f, 1f);
        }

        private void Play(float toAlpha, float toScale)
        {
            if (motion != null) StopCoroutine(motion);
            motion = StartCoroutine(To(toAlpha, toScale));
        }

        private IEnumerator To(float toAlpha, float toScale)
        {
            float fromAlpha = layer != null ? layer.color.a : 0f;
            float fromScale = Mathf.Approximately(homeScale.x, 0f) ? 1f : rect.localScale.x / homeScale.x;

            float t = 0f;
            while (t < 1f)
            {
                // Unscaled: the pause menu runs at timeScale zero, and a pause button that only
                // animates while the game is running would be the one place this is needed most.
                t += seconds > 0f ? Time.unscaledDeltaTime / seconds : 1f;
                float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

                SetAlpha(Mathf.Lerp(fromAlpha, toAlpha, eased));
                rect.localScale = homeScale * Mathf.Lerp(fromScale, toScale, eased);
                yield return null;
            }

            SetAlpha(toAlpha);
            rect.localScale = homeScale * toScale;
            motion = null;

            if (!held && idleAlpha > 0f && toAlpha == 0f) motion = StartCoroutine(Idle());
        }

        private IEnumerator Idle()
        {
            while (!held)
            {
                float phase = idleSeconds > 0f ? Time.unscaledTime / idleSeconds : 0f;
                // Sine mapped to 0..1 so the shimmer breathes rather than blinking.
                SetAlpha(idleAlpha * (0.5f + 0.5f * Mathf.Sin(phase * Mathf.PI * 2f)));
                yield return null;
            }
        }

        private void SetAlpha(float a)
        {
            if (layer == null) return;

            Color c = glowColor;
            c.a = a;
            layer.color = c;
        }
    }
}
