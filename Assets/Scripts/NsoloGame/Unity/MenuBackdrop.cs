using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// One opaque sheet that sits behind every menu screen, so the 3D board is never visible from
    /// a menu.
    ///
    /// The board is not a menu background. It is a real object in the scene, lit and rendered
    /// continuously, and the only reason the menus normally hide it is that each one happens to
    /// carry a full-screen image of its own. That holds right up until two menus change places:
    /// <see cref="PanelTransition"/> crossfades them, so for the length of the hand-off the
    /// outgoing screen is below full alpha and the incoming one has not reached it — and the board
    /// shows through the gap between them. Going to the profile screen and back flashed the game
    /// board twice, which reads as the app losing its place rather than as a transition.
    ///
    /// Fixing it inside the transition is not possible: no crossfade between two translucent things
    /// is opaque in the middle. The fix has to be behind both of them, which is what this is. It
    /// also means the menus no longer each depend on being opaque — a panel with a transparent
    /// corner or a rounded card is now free to have one.
    ///
    /// Deliberately not a menu screen itself. It is never navigated to, never appears in
    /// <see cref="NsoloUI.AllPanels"/>, and is not something <see cref="MenuManager.ShowOnly"/> can
    /// take down by accident — it answers one question ("should the board be visible right now?")
    /// and MenuManager is the only thing that asks it.
    /// </summary>
    [DisallowMultipleComponent]
    public class MenuBackdrop : MonoBehaviour
    {
        [Tooltip("The menu artwork. Leave empty to fill with the flat colour below — which is " +
                 "already enough to hide the board, but assigning the same background the menu " +
                 "screens use makes the hand-off seamless rather than merely opaque.")]
        [SerializeField] private Sprite sprite;

        [Tooltip("Used when no sprite is assigned, and as the tint behind one that is. Must be " +
                 "fully opaque — anything less lets the board back through, which is the whole " +
                 "problem this exists to solve.")]
        [SerializeField] private Color fill = new Color(0.06f, 0.05f, 0.09f, 1f);

        [Tooltip("How long the sheet takes to appear or clear. Covers the board/menu boundary " +
                 "only: moving between two menus never touches it, because it is already up.")]
        [SerializeField] private float fadeSeconds = 0.18f;

        private static MenuBackdrop instance;

        private CanvasGroup group;
        private Image image;
        private Coroutine fade;
        private bool shown;

        /// <summary>
        /// Finds the backdrop, building one under <paramref name="canvas"/> if the scene has none.
        ///
        /// Auto-creation is here so this cannot be the component somebody forgets to add: the fault
        /// it fixes is subtle enough to live in a build for weeks, and a backdrop that only works
        /// when correctly wired would fail exactly the way the original bug did — silently, and
        /// only during a transition. A scene copy is still preferred when one exists, because that
        /// is the one carrying the artwork.
        /// </summary>
        public static MenuBackdrop Ensure(Transform canvas)
        {
            if (instance != null) return instance;

            instance = FindObjectOfType<MenuBackdrop>(includeInactive: true);
            if (instance != null) return instance;

            if (canvas == null) return null;

            var host = new GameObject("MenuBackdrop", typeof(RectTransform));
            host.transform.SetParent(canvas, worldPositionStays: false);
            instance = host.AddComponent<MenuBackdrop>();
            return instance;
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                // Same rule the other singletons here follow: a duplicate sharing an object takes
                // only the spare component with it, never the object and whatever else it carries.
                if (instance.gameObject == gameObject) Destroy(this);
                else Destroy(gameObject);
                return;
            }

            instance = this;
            Build();

            // Starts down. The first screen shown asks for it, and a backdrop that began visible
            // would cover the board for a frame when the scene opens straight into a game.
            group.alpha = 0f;
            gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void Build()
        {
            var rect = (RectTransform)transform;

            // Full-bleed regardless of aspect: anchored to the canvas corners rather than sized, so
            // it covers a tall phone and a tablet without either being a special case.
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;

            image = GetComponent<Image>();
            if (image == null) image = gameObject.AddComponent<Image>();

            image.sprite = sprite;
            image.color = sprite != null ? Color.white : fill;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;

            // Draws only. Every tap belongs to whatever is on top of it, and a full-screen image
            // that answered raycasts would swallow the menu underneath — the same failure the
            // stranded online panels used to cause.
            image.raycastTarget = false;

            group = GetComponent<CanvasGroup>();
            if (group == null) group = gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            // Behind every sibling. Panels are added to the canvas in whatever order the scene
            // holds them, so this is asserted rather than assumed.
            transform.SetAsFirstSibling();
        }

        /// <summary>
        /// Raises or clears the sheet. Repeating the current state is free, which matters because
        /// the caller asks on every screen change and most of those do not change the answer.
        /// </summary>
        public void SetShown(bool value)
        {
            if (shown == value && gameObject.activeSelf == value) return;
            shown = value;

            if (value)
            {
                gameObject.SetActive(true);

                // Re-asserted on the way up rather than only at Awake: panels are switched on and
                // off constantly and any of them can end up added after this was built, which would
                // otherwise leave the backdrop drawing over the menu it is supposed to sit behind.
                transform.SetAsFirstSibling();
            }

            if (fade != null) StopCoroutine(fade);

            if (!gameObject.activeInHierarchy)
            {
                // Nothing can animate on a disabled object, and the state still has to be right.
                group.alpha = value ? 1f : 0f;
                return;
            }

            fade = StartCoroutine(FadeTo(value ? 1f : 0f));
        }

        private IEnumerator FadeTo(float target)
        {
            float from = group.alpha;

            // Unscaled throughout, like every other piece of motion in the menus: the clock is
            // stopped for some of the screens this runs under, and a backdrop that froze with it
            // would strand the board half-visible.
            for (float t = 0f; t < 1f && fadeSeconds > 0f; t += Time.unscaledDeltaTime / fadeSeconds)
            {
                group.alpha = Mathf.Lerp(from, target, Mathf.Clamp01(t));
                yield return null;
            }

            group.alpha = target;
            fade = null;

            // Switched off once clear so it costs nothing to draw while a game is being played.
            if (target <= 0f) gameObject.SetActive(false);
        }

        // ── Static convenience ────────────────────────────────────────────
        // Mirrors AudioManager: callers say what they want and a missing backdrop is simply a game
        // that shows its board through the transitions, not a null reference.

        public static void Show(bool value) => instance?.SetShown(value);
    }
}
