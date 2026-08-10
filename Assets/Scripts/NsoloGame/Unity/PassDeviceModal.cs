using System.Collections;
using TMPro;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// The handover card in hot-seat play: "Pass the device — tap to continue". It goes up before
    /// the board turns round, so the incoming player has a moment to take the phone and look at it
    /// before their own rows swing into place, rather than being handed a board mid-rotation.
    ///
    /// Built to the same shape as <see cref="TutorialCoach"/> — backdrop, card, CanvasGroup fade,
    /// everything on unscaled time — so it behaves like the popups already in the game.
    ///
    /// It freezes the game clock while it is up, which is deliberate: the seconds spent passing a
    /// phone across a table are not play time and should not land on the timer.
    /// </summary>
    public class PassDeviceModal : MonoBehaviour
    {
        [Header("Panel")]
        [Tooltip("Root of the popup. Toggled on and off as it comes and goes.")]
        [SerializeField] private GameObject panelRoot;
        [Tooltip("Full-screen dimmer behind the card. This is what stops taps reaching the board.")]
        [SerializeField] private GameObject backdrop;
        [Tooltip("The card itself. Scaled up slightly on entry, like the tutorial tips.")]
        [SerializeField] private RectTransform card;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text bodyText;

        [Header("Motion")]
        [SerializeField] private float fadeInSeconds = 0.18f;
        [SerializeField] private float fadeOutSeconds = 0.14f;
        [SerializeField] private float popScale = 0.92f;

        [Header("Behaviour")]
        [Tooltip("Taps are ignored for this long after the card appears, so the tap that ended the " +
                 "previous player's turn cannot immediately dismiss it.")]
        [SerializeField] private float inputGraceSeconds = 0.35f;

        private static PassDeviceModal instance;
        public static PassDeviceModal Instance => instance;

        private float timeScaleBefore = 1f;
        private bool frozeTime;
        private bool showing;

        /// <summary>Whether the card is up, and therefore whether board input should be ignored.</summary>
        public bool IsBlocking => showing;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                // Matches the duplicate handling in TutorialCoach and AudioManager: only the spare
                // component goes when two share an object, never the whole GameObject.
                if (instance.gameObject == gameObject) Destroy(this);
                else Destroy(gameObject);
                return;
            }
            instance = this;

            HideImmediate();
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        /// <summary>
        /// Shows the card and waits for a tap. Yield on it from a coroutine — it returns once the
        /// player has tapped and the card has faded back out.
        /// </summary>
        /// <param name="incomingPlayerName">Who is being handed the device, e.g. "Player 2".</param>
        public IEnumerator ShowAndWait(string incomingPlayerName)
        {
            if (panelRoot == null)
            {
                // Nothing wired up yet. Better to let play continue without the handover card than
                // to deadlock the turn waiting on a tap for a card that is not on screen.
                Debug.LogWarning("PassDeviceModal: no panel assigned, skipping the handover card.");
                yield break;
            }

            showing = true;

            if (titleText != null) titleText.text = "Pass the Device";
            if (bodyText != null)
                bodyText.text = $"{incomingPlayerName}, it is your turn.\n\nTap anywhere to continue.";

            if (backdrop != null) backdrop.SetActive(true);
            panelRoot.SetActive(true);

            if (!frozeTime)
            {
                timeScaleBefore = Time.timeScale;
                Time.timeScale = 0f;
                frozeTime = true;
            }

            Haptics.Pulse(HapticStrength.Medium);

            yield return Fade(0f, 1f, fadeInSeconds, popScale, 1f);

            // Grace period first, then wait for a fresh tap. Without this the tap that completed
            // the outgoing player's move can still be down on the frame the card appears.
            float grace = inputGraceSeconds;
            while (grace > 0f)
            {
                grace -= Time.unscaledDeltaTime;
                yield return null;
            }

            while (!TapWentDown())
                yield return null;

            Haptics.Light();
            AudioManager.Click();

            yield return Fade(1f, 0f, fadeOutSeconds, 1f, popScale);

            HideImmediate();
        }

        /// <summary>
        /// Tears the card down without waiting for a tap — for leaving to the menu or restarting
        /// mid-handover, where the game it belonged to no longer exists.
        /// </summary>
        public void ForceHide()
        {
            StopAllCoroutines();
            HideImmediate();
        }

        /// <summary>
        /// Polls raw input rather than going through the EventSystem, because the backdrop sits
        /// over everything and would swallow the taps. Same reasoning as TutorialCoach.
        /// </summary>
        private static bool TapWentDown()
        {
            if (Input.touchCount > 0)
                return Input.GetTouch(0).phase == TouchPhase.Began;
            return Input.GetMouseButtonDown(0);
        }

        private IEnumerator Fade(float fromAlpha, float toAlpha, float seconds, float fromScale, float toScale)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += seconds > 0f ? Time.unscaledDeltaTime / seconds : 1f;
                float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

                if (canvasGroup != null)
                    canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, eased);
                if (card != null)
                    card.localScale = Vector3.one * Mathf.Lerp(fromScale, toScale, eased);

                yield return null;
            }

            if (canvasGroup != null) canvasGroup.alpha = toAlpha;
            if (card != null) card.localScale = Vector3.one * toScale;
        }

        private void HideImmediate()
        {
            showing = false;

            if (frozeTime)
            {
                Time.timeScale = timeScaleBefore;
                frozeTime = false;
            }

            if (panelRoot != null) panelRoot.SetActive(false);
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            if (card != null) card.localScale = Vector3.one;
        }
    }
}
