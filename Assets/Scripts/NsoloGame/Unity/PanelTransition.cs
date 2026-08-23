using System.Collections;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Carries a panel on and off screen so screens hand over to each other, instead of one
    /// blinking out and the next blinking in.
    ///
    /// The problem this exists to solve is a gap, not a cut. Panels were switched with
    /// <c>SetActive</c>: the outgoing screen vanished in a single frame and the incoming one then
    /// faded up from nothing, so for a beat there was no screen at all, and the eye catches that
    /// hole. Overlapping the two — the old one still leaving while the new one is already arriving
    /// — is most of what "this feels smooth" actually is.
    ///
    /// The rest is timing. Leaving is quicker than arriving and eases in; arriving is slower and
    /// eases out. Equal times in both directions read as mechanical, because nothing physical
    /// starts and stops at the same rate. The travel is deliberately small: a screen that slides a
    /// long way reads as the whole app lurching sideways rather than one card replacing another.
    ///
    /// Direction comes from <see cref="NavDirection"/>, which MenuManager sets from the relative
    /// depth of the two screens. Going deeper moves one way and coming back moves the other, which
    /// tells a player where they are in a hierarchy without any text saying so.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class PanelTransition : MonoBehaviour
    {
        [Tooltip("How long a panel takes to arrive.")]
        [SerializeField] private float enterSeconds = 0.22f;

        [Tooltip("How long a panel takes to leave. Shorter than arriving on purpose — the screen " +
                 "being left is no longer the subject, and lingering on it makes navigation sticky.")]
        [SerializeField] private float exitSeconds = 0.13f;

        [Tooltip("How far the panel travels, in canvas units. Small: this is a hand-off between " +
                 "cards, not a camera move.")]
        [SerializeField] private float travel = 34f;

        /// <summary>
        /// Which way the next transition moves: +1 going deeper, -1 coming back. Static because it
        /// describes the navigation rather than any one panel, and the two panels in a hand-off
        /// have to agree on it or they would slide against each other.
        /// </summary>
        public static int NavDirection = 1;

        private RectTransform rect;
        private CanvasGroup group;
        private Vector2 home;
        private bool cached;
        private Coroutine motion;

        /// <summary>
        /// Set by <see cref="Show"/> just before it activates the object, so the entrance it is
        /// about to run is not doubled by the one <see cref="OnEnable"/> starts for panels switched
        /// on directly with SetActive.
        /// </summary>
        private bool showDriven;

        private void Awake() => Cache();

        private void Cache()
        {
            if (cached) return;

            rect = (RectTransform)transform;

            // Added rather than required: these panels were authored by hand and most have no
            // CanvasGroup, and demanding one would mean editing every panel to add a component
            // whose only job is to let this fade.
            group = GetComponent<CanvasGroup>();
            if (group == null) group = gameObject.AddComponent<CanvasGroup>();

            home = rect.anchoredPosition;
            cached = true;
        }

        private void OnEnable()
        {
            // OnEnable can beat Awake when a panel starts the scene switched off and is switched on
            // in the same frame something else wakes, so the cache is asserted here too.
            Cache();

            if (showDriven)
            {
                showDriven = false;
                return;
            }

            Play(Enter());
        }

        private void OnDisable()
        {
            // Whatever was in flight is gone with the object. Leaving the panel parked at a partial
            // alpha or offset would show up the next time it appeared.
            motion = null;
            if (!cached) return;

            group.alpha = 1f;
            group.blocksRaycasts = true;
            group.interactable = true;
            rect.anchoredPosition = home;
        }

        /// <summary>Brings the panel on screen.</summary>
        public void Show()
        {
            Cache();
            StopMotion();

            if (!gameObject.activeSelf)
            {
                showDriven = true;
                gameObject.SetActive(true);
            }

            Play(Enter());
        }

        /// <summary>
        /// Takes the panel off screen, leaving it visible while it goes.
        ///
        /// Input is cut at once rather than when the animation ends: for every purpose except
        /// drawing, the panel is gone the moment it is asked to leave, so a tap during the hand-off
        /// cannot reach a screen the player has already navigated away from.
        /// </summary>
        public void Hide()
        {
            Cache();

            if (!gameObject.activeSelf) return;

            group.blocksRaycasts = false;
            group.interactable = false;

            StopMotion();
            Play(Exit());
        }

        /// <summary>Off screen now, no animation. For teardown, where motion would be noise.</summary>
        public void HideImmediate()
        {
            Cache();
            StopMotion();

            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        private IEnumerator Enter()
        {
            float from = NavDirection >= 0 ? travel : -travel;

            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
            rect.anchoredPosition = home + new Vector2(from, 0f);

            float t = 0f;
            while (t < 1f)
            {
                t += enterSeconds > 0f ? Time.unscaledDeltaTime / enterSeconds : 1f;

                // Ease out: quickest at the start, settling as it lands. An arriving screen should
                // look like it is coming to rest.
                float e = EaseOut(Mathf.Clamp01(t));

                group.alpha = e;
                rect.anchoredPosition = home + new Vector2(Mathf.Lerp(from, 0f, e), 0f);
                yield return null;
            }

            group.alpha = 1f;
            rect.anchoredPosition = home;
            group.blocksRaycasts = true;
            group.interactable = true;
            motion = null;
        }

        private IEnumerator Exit()
        {
            float to = NavDirection >= 0 ? -travel : travel;
            float startAlpha = group.alpha;

            float t = 0f;
            while (t < 1f)
            {
                t += exitSeconds > 0f ? Time.unscaledDeltaTime / exitSeconds : 1f;

                // Ease in: drifts, then goes. The mirror of the entrance, so a screen leaves the
                // way the next one arrives.
                float e = EaseIn(Mathf.Clamp01(t));

                group.alpha = Mathf.Lerp(startAlpha, 0f, e);
                rect.anchoredPosition = home + new Vector2(Mathf.Lerp(0f, to, e), 0f);
                yield return null;
            }

            motion = null;

            // Restores alpha, raycasts and position by way of OnDisable.
            gameObject.SetActive(false);
        }

        private void Play(IEnumerator routine)
        {
            if (!gameObject.activeInHierarchy) return;
            motion = StartCoroutine(routine);
        }

        private void StopMotion()
        {
            if (motion == null) return;

            StopCoroutine(motion);
            motion = null;
        }

        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        private static float EaseIn(float t) => t * t;
    }
}
