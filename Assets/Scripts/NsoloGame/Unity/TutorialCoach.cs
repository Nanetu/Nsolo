using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>Every contextual tip the coach can show. Names are used to build PlayerPrefs keys,
    /// so renaming one resets it for existing players.</summary>
    public enum TutorialTip
    {
        ArrangeStones,
        OpponentRows,
        YourPits,
        NeedTwoStones,
        HintButton,
        UndoButton,
        PauseButton,
        FirstCapture,
        FirstRelay,
        CapturedByAi,
        SkipAnimation,
    }

    /// <summary>
    /// Short, once-only popups that explain the game while it is being played, so first-timers do
    /// not have to read the tutorial page first.
    ///
    /// Two shapes share one panel: a <b>modal</b> tip dims the screen, blocks input and waits for
    /// the Got It button, and a <b>toast</b> tip appears briefly over the board and fades itself
    /// out without interrupting. Which one a tip uses is part of its definition below.
    ///
    /// Everything runs on unscaled time — the pause tip fires while Time.timeScale is 0.
    /// </summary>
    public class TutorialCoach : MonoBehaviour
    {
        [Header("Panel")]
        [Tooltip("Root of the whole tip popup. Toggled on and off as tips come and go.")]
        [SerializeField] private GameObject panelRoot;
        [Tooltip("Full-screen dimmer behind the card. Enabled only for modal tips — it is what stops board taps getting through.")]
        [SerializeField] private GameObject backdrop;
        [Tooltip("The card itself. Scaled up slightly on entry.")]
        [SerializeField] private RectTransform card;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text bodyText;
        [Tooltip("Got It button. Hidden for toast tips, which dismiss themselves.")]
        [SerializeField] private GameObject dismissButton;

        [Header("Motion")]
        [SerializeField] private float fadeInSeconds = 0.18f;
        [SerializeField] private float fadeOutSeconds = 0.14f;
        [SerializeField] private float popScale = 0.92f;

        [Header("Behaviour")]
        [Tooltip("Tips queued behind the one on screen. Anything past this is dropped rather than backing up.")]
        [SerializeField] private int maxQueued = 3;

        /// <summary>
        /// What the setting starts as before the player has ever touched it: on, so a first-timer
        /// gets taught without having to find a switch first. Deliberately a constant rather than a
        /// serialized field — it is a product decision, and a serialized copy in the scene would
        /// quietly outrank whatever this file says.
        /// </summary>
        private const bool TipsOnByDefault = true;

        // Live state, seeded in Awake from the saved pref — or from the default above on a fresh
        // install. Deliberately not the serialized field itself, so writing it cannot dirty the scene.
        private bool tipsEnabled;

        /// <summary>PlayerPrefs key holding the master on/off switch.</summary>
        public const string EnabledPrefKey = "TutorialTipsEnabled";

        /// <summary>
        /// Raised whenever the setting changes, including when the coach switches itself off after
        /// the last tip. The pause menu listens so its control cannot sit there showing ON while
        /// the coach has already stood down.
        /// </summary>
        public static event System.Action TipsSettingChanged;

        private static readonly Dictionary<TutorialTip, TipDefinition> Tips = BuildTips();

        private struct PendingTip
        {
            public TutorialTip Tip;
            public System.Action OnDismissed;
        }

        private readonly Queue<PendingTip> queued = new Queue<PendingTip>();

        private static TutorialCoach instance;
        public static TutorialCoach Instance => instance;

        private bool showing;
        private TutorialTip current;
        private Coroutine routine;
        private System.Action currentOnDismissed;

        // Modal tips freeze the game so the sowing animation and the clock do not run on behind
        // the card. The previous scale is restored rather than assuming 1, because the pause tip
        // fires while the pause menu already has time stopped.
        private float timeScaleBeforeTip = 1f;
        private bool frozeTime;

        // Double-tap dismissal state.
        private const float DoubleTapSeconds = 0.45f;
        private bool awaitingDoubleTap;
        private float lastTapTime = -1f;

        /// <summary>Whether a modal tip is on screen, and therefore whether input should be ignored.</summary>
        public bool IsBlocking { get; private set; }

        // ── Tip copy ──────────────────────────────────────────────────────

        private struct TipDefinition
        {
            public string Title;
            public string Body;
            public bool Modal;
            /// <summary>
            /// Dismissed by double-tapping the screen instead of pressing Got It. Used for the tip
            /// that teaches the double-tap gesture, so the player learns it by performing it.
            /// </summary>
            public bool DismissByDoubleTap;
            /// <summary>Seconds a toast stays up. Ignored for modal tips.</summary>
            public float Seconds;
            /// <summary>How many times this tip may ever be shown. Most are once; the "wrong side"
            /// nudge repeats a few times because one showing rarely sticks.</summary>
            public int MaxShows;
        }

        private static Dictionary<TutorialTip, TipDefinition> BuildTips()
        {
            return new Dictionary<TutorialTip, TipDefinition>
            {
                // The first thing a new player ever sees, so it is also where the way out is
                // advertised — the tips are on by default and nothing else mentions them.
                [TutorialTip.ArrangeStones] = new TipDefinition
                {
                    Title = "Arrange Your Stones",
                    Body = "Tap one of your pits to lift its stones, then tap pits to drop them one by one. Press START when you are happy.\n\n" +
                           "<size=80%>Instructions can be switched on and off in the pause menu.</size>",
                    Modal = true,
                    MaxShows = 1,
                },
                [TutorialTip.OpponentRows] = new TipDefinition
                {
                    Title = "Not Your Side",
                    Body = "The top two rows belong to your opponent. Yours are the bottom two.",
                    Modal = false,
                    Seconds = 2.4f,
                    MaxShows = 3,
                },
                // The win condition rides along here rather than getting its own popup — four
                // modals inside the first minute is more than a new player will read.
                [TutorialTip.YourPits] = new TipDefinition
                {
                    Title = "These Are Yours",
                    Body = "The bottom two rows are your pits. Tap a glowing one to sow its stones.\n\n" +
                           "You win when your opponent has no pit left holding 2 or more stones.",
                    Modal = true,
                    MaxShows = 1,
                },
                [TutorialTip.NeedTwoStones] = new TipDefinition
                {
                    Title = "Need Two Stones",
                    Body = "A pit must hold at least 2 stones before you can play it.",
                    Modal = false,
                    Seconds = 2.2f,
                    MaxShows = 2,
                },
                [TutorialTip.HintButton] = new TipDefinition
                {
                    Title = "Hint",
                    Body = "Asks the AI for the strongest move and flashes that pit. Try thinking it through first.",
                    Modal = true,
                    MaxShows = 1,
                },
                [TutorialTip.UndoButton] = new TipDefinition
                {
                    Title = "Undo",
                    Body = "Takes back your last move and the AI's reply. Only available on your turn.",
                    Modal = true,
                    MaxShows = 1,
                },
                [TutorialTip.PauseButton] = new TipDefinition
                {
                    Title = "Paused",
                    Body = "Music, sound and vibration live here. Resume when you are ready.",
                    Modal = true,
                    MaxShows = 1,
                },
                [TutorialTip.FirstCapture] = new TipDefinition
                {
                    Title = "Capture!",
                    Body = "Landing in your inner row opposite two filled enemy pits takes the stones from both.",
                    Modal = true,
                    MaxShows = 1,
                },
                [TutorialTip.FirstRelay] = new TipDefinition
                {
                    Title = "Relay Sowing",
                    Body = "Landing in a pit that already had stones picks them all up and keeps sowing.",
                    Modal = true,
                    MaxShows = 1,
                },
                [TutorialTip.CapturedByAi] = new TipDefinition
                {
                    Title = "You Lost Stones",
                    Body = "The AI captured from your side. Keep an eye on your inner row.",
                    Modal = false,
                    Seconds = 2.6f,
                    MaxShows = 1,
                },
                // Dismissed by the very gesture it describes, so the player has already done it
                // once by the time the tip goes away.
                [TutorialTip.SkipAnimation] = new TipDefinition
                {
                    Title = "Skip Ahead",
                    Body = "Long relays take a while to play out. Double tap anywhere to jump " +
                           "straight to the finished board.\n\nDouble tap now to try it.",
                    Modal = true,
                    DismissByDoubleTap = true,
                    MaxShows = 1,
                },
            };
        }

        // ── Lifecycle ─────────────────────────────────────────────────────

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                // Only the spare component goes when the duplicate shares an object — see the
                // matching note in AudioManager. Destroying the GameObject takes both down.
                if (instance.gameObject == gameObject) Destroy(this);
                else Destroy(gameObject);
                return;
            }
            instance = this;

            tipsEnabled = PlayerPrefs.GetInt(EnabledPrefKey, TipsOnByDefault ? 1 : 0) == 1;

            // Catches an install that worked through the whole set in an earlier session: without
            // this the switch would sit on ON for ever while nothing could possibly appear.
            AutoDisableIfExhausted();

            HidePanelImmediate();
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Requests a tip. Safe to call from anywhere and at any time — it silently does nothing
        /// when tips are off, when the coach is absent from the scene, or when this tip has
        /// already been shown its allotted number of times.
        /// </summary>
        /// <summary>
        /// <paramref name="onDismissed"/> runs only if the tip is actually shown and then closed.
        /// A suppressed tip — already seen, tips switched off, no coach in the scene — runs
        /// nothing. Callers relying on it must treat it as a bonus action, never as a
        /// continuation: the skip-animation tip uses it to perform the skip the player just
        /// learned, and firing that on a suppressed tip would silently skip every later relay.
        /// </summary>
        public static void Show(TutorialTip tip, System.Action onDismissed = null)
        {
            if (instance == null) return;
            instance.ShowTip(tip, onDismissed);
        }

        public void ShowTip(TutorialTip tip, System.Action onDismissed = null)
        {
            if (!tipsEnabled || !Tips.ContainsKey(tip)) return;
            if (ShowCount(tip) >= Tips[tip].MaxShows) return;
            if (IsQueued(tip) || (showing && current == tip)) return;

            if (showing)
            {
                if (queued.Count < maxQueued)
                    queued.Enqueue(new PendingTip { Tip = tip, OnDismissed = onDismissed });
                return;
            }

            StartTip(tip, onDismissed);
        }

        private bool IsQueued(TutorialTip tip)
        {
            foreach (PendingTip pending in queued)
                if (pending.Tip == tip) return true;
            return false;
        }

        /// <summary>Wire this to the popup's Got It button.</summary>
        public void Dismiss()
        {
            if (!showing) return;
            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(DismissRoutine());
        }

        /// <summary>Makes every tip eligible again. Handy for a "Replay tutorial" settings button.</summary>
        public void ResetAllTips()
        {
            foreach (TutorialTip tip in Tips.Keys)
                PlayerPrefs.DeleteKey(PrefKeyFor(tip));
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Master switch, for the pause-menu control.
        ///
        /// Switching tips <b>on</b> also replays them from the start. Every tip is once-only, so a
        /// player who has already worked through the set would otherwise flip the switch and watch
        /// absolutely nothing happen — the control would look broken. Turning them on is an
        /// explicit request to see them, so that is what it does.
        /// </summary>
        public void SetTipsEnabled(bool value)
        {
            bool turningOn = value && !tipsEnabled;

            tipsEnabled = value;
            PlayerPrefs.SetInt(EnabledPrefKey, value ? 1 : 0);
            PlayerPrefs.Save();

            if (turningOn) ResetAllTips();
            if (!value && showing) Dismiss();

            TipsSettingChanged?.Invoke();
        }

        public bool TipsEnabled => tipsEnabled;

        // ── Internals ─────────────────────────────────────────────────────

        private static string PrefKeyFor(TutorialTip tip) => $"Tip_{tip}";

        private static int ShowCount(TutorialTip tip) => PlayerPrefs.GetInt(PrefKeyFor(tip), 0);

        private static void RecordShown(TutorialTip tip)
        {
            PlayerPrefs.SetInt(PrefKeyFor(tip), ShowCount(tip) + 1);
            PlayerPrefs.Save();
        }

        /// <summary>True once every tip has been shown as often as it ever will be.</summary>
        private static bool AllTipsExhausted()
        {
            foreach (var entry in Tips)
                if (ShowCount(entry.Key) < entry.Value.MaxShows) return false;
            return true;
        }

        /// <summary>
        /// Switches the setting off once the whole set has been used up, so the control stops
        /// claiming tips are coming when none can. This is the auto half of the arrangement: on by
        /// itself for the first play, off by itself when there is nothing left, and manual in both
        /// directions after that — where switching it back on replays the set.
        ///
        /// Unlike <see cref="SetTipsEnabled"/> this neither replays anything nor closes whatever is
        /// on screen, so it is safe to call while a tip is still being read.
        /// </summary>
        private void AutoDisableIfExhausted()
        {
            if (!tipsEnabled || !AllTipsExhausted()) return;

            tipsEnabled = false;
            PlayerPrefs.SetInt(EnabledPrefKey, 0);
            PlayerPrefs.Save();
            TipsSettingChanged?.Invoke();
        }

        private void StartTip(TutorialTip tip, System.Action onDismissed = null)
        {
            TipDefinition def = Tips[tip];

            showing = true;
            current = tip;
            currentOnDismissed = onDismissed;
            IsBlocking = def.Modal;
            RecordShown(tip);

            // Stop the world for modals. Everything on scaled time freezes with it — the sowing
            // animation, the AI's think timer and the game clock — so a tip fired mid-move does
            // not have the board carrying on behind it. This coach runs on unscaled time.
            if (def.Modal && !frozeTime)
            {
                timeScaleBeforeTip = Time.timeScale;
                Time.timeScale = 0f;
                frozeTime = true;
            }

            awaitingDoubleTap = def.Modal && def.DismissByDoubleTap;
            lastTapTime = -1f;

            if (titleText != null) titleText.text = def.Title;
            if (bodyText != null) bodyText.text = def.Body;
            if (backdrop != null) backdrop.SetActive(def.Modal);
            if (dismissButton != null) dismissButton.SetActive(def.Modal && !def.DismissByDoubleTap);
            if (panelRoot != null) panelRoot.SetActive(true);

            // A modal interrupts, so it gets a firmer pulse than a toast that slides past.
            Haptics.Pulse(def.Modal ? HapticStrength.Medium : HapticStrength.Light);

            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(ShowRoutine(def));
        }

        private IEnumerator ShowRoutine(TipDefinition def)
        {
            yield return Fade(0f, 1f, fadeInSeconds, popScale, 1f);

            if (def.Modal) yield break;   // waits for Dismiss() from the button

            float remaining = def.Seconds;
            while (remaining > 0f)
            {
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }

            routine = StartCoroutine(DismissRoutine());
        }

        private IEnumerator DismissRoutine()
        {
            yield return Fade(1f, 0f, fadeOutSeconds, 1f, popScale);

            System.Action callback = currentOnDismissed;
            HidePanelImmediate();

            // Fired before the next queued tip starts, so a follow-up action (like performing the
            // skip the player just learned) happens against an unfrozen board.
            callback?.Invoke();

            if (queued.Count > 0)
            {
                PendingTip next = queued.Dequeue();
                StartTip(next.Tip, next.OnDismissed);
                yield break;
            }

            // Screen clear and nothing waiting — if that was the last tip the player will ever be
            // shown, the switch stops advertising them.
            AutoDisableIfExhausted();
        }

        private void Update()
        {
            if (!awaitingDoubleTap || !showing) return;
            if (!TapWentDown()) return;

            float now = Time.unscaledTime;
            if (lastTapTime > 0f && now - lastTapTime <= DoubleTapSeconds)
            {
                awaitingDoubleTap = false;
                Haptics.Light();
                Dismiss();
                return;
            }

            lastTapTime = now;
        }

        /// <summary>
        /// Polls raw input rather than going through the EventSystem, because the tip's own
        /// backdrop sits over everything and would swallow the taps.
        /// </summary>
        private static bool TapWentDown()
        {
            if (Input.touchCount > 0)
                return Input.GetTouch(0).phase == TouchPhase.Began;
            return Input.GetMouseButtonDown(0);
        }

        /// <summary>
        /// Tears down whatever is on screen without running callbacks — for leaving to the menu or
        /// restarting, where a queued tip about the abandoned game is no longer wanted.
        /// </summary>
        public void ForceHide()
        {
            if (routine != null) StopCoroutine(routine);
            queued.Clear();
            currentOnDismissed = null;
            HidePanelImmediate();
        }

        private IEnumerator Fade(float fromAlpha, float toAlpha, float seconds, float fromScale, float toScale)
        {
            float t = 0f;
            while (t < 1f)
            {
                // Unscaled throughout: tips fire while the game is paused.
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

        private void HidePanelImmediate()
        {
            showing = false;
            IsBlocking = false;
            routine = null;
            currentOnDismissed = null;
            awaitingDoubleTap = false;
            lastTapTime = -1f;

            if (frozeTime)
            {
                Time.timeScale = timeScaleBeforeTip;
                frozeTime = false;
            }

            if (panelRoot != null) panelRoot.SetActive(false);
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            if (card != null) card.localScale = Vector3.one;
        }
    }
}
