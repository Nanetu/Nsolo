using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Makes a menu button visibly answer a touch.
    ///
    /// The menus are full-screen artwork with transparent Buttons positioned over the pills that
    /// are painted into it. Unity's own press feedback is a colour tint on the button's graphic —
    /// and a graphic at zero alpha tinted any colour is still invisible, so before this the menus
    /// took presses and gave nothing back at all. That, more than the artwork, is why they felt
    /// dead: the board moves when you touch it and the menus did not.
    ///
    /// So the feedback is drawn rather than tinted. A white "state layer" sits over the painted
    /// pill and fades up under the finger, and the button scales down a little beneath it. Both are
    /// the shape of the pill because the layer uses a 9-sliced rounded sprite stretched to the
    /// button's own rect — which is exactly why the hitboxes had to be fitted to the art first.
    ///
    /// The layer is built in <see cref="Awake"/> rather than kept in the scene. It carries no
    /// information — it is one white quad per button, identical every time, and twenty-odd of them
    /// in the hierarchy would be noise to scroll past rather than anything worth editing. What is
    /// worth editing is here as fields.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class UIPressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        [Tooltip("9-sliced rounded sprite for the state layer. UI_PillFill for pills, " +
                 "UI_RoundRectFill for cards. Left empty, the button still scales but does not glow.")]
        [SerializeField] private Sprite shape;

        [Tooltip("How bright the layer gets under a finger. Small on purpose: this reads as the " +
                 "painted pill lighting up, not as a white box appearing on top of it.")]
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

        private RectTransform rect;
        private Vector3 homeScale = Vector3.one;
        private Button button;
        private Image layer;
        private Coroutine motion;
        private bool held;

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

            var go = new GameObject("PressLayer", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var layerRect = (RectTransform)go.transform;
            layerRect.SetParent(rect, false);

            layer = go.GetComponent<Image>();
            layer.sprite = shape;
            layer.type = Image.Type.Sliced;
            layer.color = new Color(1f, 1f, 1f, 0f);

            // Never let the decoration eat the tap it is decorating.
            layer.raycastTarget = false;

            // Drawn first so it sits behind any label the button owns.
            layerRect.SetAsFirstSibling();

            FitLayer();
        }

        /// <summary>
        /// Sizes the layer to the button's on-screen rect and undoes the button's own scale.
        ///
        /// Stretching to the parent with anchors would be simpler, and was what this did first, but
        /// it puts the layer in the parent's scaled space. Buttons exported from Figma keep a
        /// uniform 160x30 rect and get their real size from a non-uniform localScale — around 4x
        /// across and 6x down — so a stretched layer has its 9-slice corners stretched by those
        /// same factors. The corner radius is the one thing the sliced sprite exists to hold
        /// constant, and it came out smeared into lopsided ovals that visibly overhang the artwork.
        ///
        /// So the layer is given the button's *effective* size and the inverse of its scale, which
        /// cancel to the same on-screen rectangle while leaving the border to be drawn in unscaled
        /// units. The press animation still scales the button, and the layer still rides along with
        /// it, because that multiplies on top of what is set here.
        /// </summary>
        private void FitLayer()
        {
            if (layer == null) return;

            var layerRect = (RectTransform)layer.transform;

            float sx = Mathf.Approximately(homeScale.x, 0f) ? 1f : homeScale.x;
            float sy = Mathf.Approximately(homeScale.y, 0f) ? 1f : homeScale.y;

            Vector2 size = rect.rect.size;

            layerRect.anchorMin = layerRect.anchorMax = new Vector2(0.5f, 0.5f);
            layerRect.pivot = new Vector2(0.5f, 0.5f);
            layerRect.anchoredPosition = Vector2.zero;
            layerRect.sizeDelta = new Vector2(size.x * Mathf.Abs(sx), size.y * Mathf.Abs(sy));
            layerRect.localScale = new Vector3(1f / sx, 1f / sy, 1f);
        }

        private void OnEnable()
        {
            held = false;
            rect.localScale = homeScale;
            SetAlpha(0f);

            // Re-fitted rather than only built once, so resizing a button in the editor is picked
            // up the next time its panel opens instead of needing a restart.
            FitLayer();

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
        /// Adds press feedback to a button that has none, and gives it a shape to glow in.
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
        /// Sets the shape, before or after Awake has built the layer.
        ///
        /// Both orders happen: adding this to a panel that is switched off — which every menu panel
        /// is — defers Awake until the panel first opens, so the field is what gets read; adding it
        /// to something already on screen runs Awake inside AddComponent, and by then the layer
        /// exists and has to be corrected directly.
        /// </summary>
        public void SetShape(Sprite value)
        {
            shape = value;
            if (layer != null) layer.sprite = value;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!Available) return;

            held = true;
            Play(pressAlpha, pressedScale);
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

            Color c = layer.color;
            c.a = a;
            layer.color = c;
        }
    }
}
