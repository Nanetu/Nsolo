using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// One modal's moving parts. A plain serializable class rather than a MonoBehaviour on purpose:
    /// a script living on a panel that starts inactive never runs Awake, which is the trap that
    /// silently disabled the handover card during hot-seat (see docs/hotseat-wiring.md). Owning
    /// these from a component on GameSystems keeps every modal driven by something always awake.
    ///
    /// The motion matches <see cref="PassDeviceModal"/> and <see cref="TutorialCoach"/> — backdrop,
    /// card, CanvasGroup, unscaled time, slight pop on entry — so these read as the same family of
    /// popups rather than a new one.
    /// </summary>
    [Serializable]
    public class ModalPanel
    {
        [Tooltip("Root of the popup. Toggled on and off as it comes and goes.")]
        public GameObject panelRoot;
        [Tooltip("Full-screen dimmer behind the card. This is what stops taps reaching the board.")]
        public GameObject backdrop;
        [Tooltip("The card itself. Scaled up slightly on entry, like the tutorial tips.")]
        public RectTransform card;
        public CanvasGroup canvasGroup;

        public bool IsVisible => panelRoot != null && panelRoot.activeSelf;

        /// <summary>Whether this modal is wired up at all. An unwired modal is skipped, not crashed on.</summary>
        public bool Exists => panelRoot != null;

        public void ShowImmediate()
        {
            if (panelRoot == null) return;

            if (backdrop != null) backdrop.SetActive(true);
            panelRoot.SetActive(true);
            if (canvasGroup != null) canvasGroup.alpha = 1f;
            if (card != null) card.localScale = Vector3.one;
        }

        public void HideImmediate()
        {
            if (panelRoot == null) return;

            panelRoot.SetActive(false);
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            if (card != null) card.localScale = Vector3.one;
        }

        public IEnumerator FadeIn(float seconds, float popScale)
        {
            if (panelRoot == null) yield break;

            if (backdrop != null) backdrop.SetActive(true);
            panelRoot.SetActive(true);

            yield return Fade(0f, 1f, seconds, popScale, 1f);
        }

        public IEnumerator FadeOut(float seconds, float popScale)
        {
            if (panelRoot == null) yield break;

            yield return Fade(1f, 0f, seconds, 1f, popScale);
            HideImmediate();
        }

        private IEnumerator Fade(float fromAlpha, float toAlpha, float seconds, float fromScale, float toScale)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += seconds > 0f ? Time.unscaledDeltaTime / seconds : 1f;
                float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

                if (canvasGroup != null) canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, eased);
                if (card != null) card.localScale = Vector3.one * Mathf.Lerp(fromScale, toScale, eased);

                yield return null;
            }

            if (canvasGroup != null) canvasGroup.alpha = toAlpha;
            if (card != null) card.localScale = Vector3.one * toScale;
        }
    }

    /// <summary>
    /// Every popup the game raises, driven from two panels rather than five.
    ///
    /// Join Room keeps its own panel because it holds an input field. Everything else — room not
    /// found, connection lost, forfeit — is the same shape: a title, a body and up to three
    /// buttons. Those share one <see cref="dialog"/> panel, the way TutorialCoach drives every tip
    /// from a single card. Room creation has no popup at all: the lobby screen already shows the
    /// code and the waiting state, so a modal doing the same job was just clutter.
    ///
    /// Every slot is optional: an unwired modal logs and degrades rather than deadlocking whatever
    /// asked for it, matching how the rest of the project's optional UI behaves.
    ///
    /// Button click handlers are attached at runtime rather than in the Inspector. The buttons here
    /// answer questions ("try again with which code?", "confirm what?") whose answers only exist at
    /// the moment the modal is raised, so a fixed Inspector target could not express them.
    /// </summary>
    public class GameModals : MonoBehaviour
    {
        [Header("Join Room")]
        [SerializeField] private ModalPanel joinRoom;
        [SerializeField] private TMP_InputField joinCodeInput;
        [SerializeField] private Button joinConfirmButton;
        [SerializeField] private Button joinCancelButton;
        [Tooltip("Optional. Shown while the join is in flight.")]
        [SerializeField] private TMP_Text joinStatusText;

        [Header("Dialog (shared by Room Not Found, Disconnected and Forfeit)")]
        [Tooltip("One panel reused for every question the game asks. Same idea as TutorialCoach " +
                 "driving every tip from a single card — these three differ only in their words and " +
                 "how many buttons they need, so they do not each need a panel in the hierarchy.")]
        [SerializeField] private ModalPanel dialog;
        [SerializeField] private TMP_Text dialogTitleText;
        [SerializeField] private TMP_Text dialogBodyText;
        [Tooltip("Left-hand / primary button. Always used.")]
        [SerializeField] private Button dialogButtonA;
        [SerializeField] private TMP_Text dialogButtonALabel;
        [Tooltip("Right-hand button. Hidden when a dialog needs only one.")]
        [SerializeField] private Button dialogButtonB;
        [SerializeField] private TMP_Text dialogButtonBLabel;
        [Tooltip("Third button, used only by the disconnect dialog.")]
        [SerializeField] private Button dialogButtonC;
        [SerializeField] private TMP_Text dialogButtonCLabel;
        [Tooltip("Fourth button. Only the disconnect dialog asks this many questions at once.")]
        [SerializeField] private Button dialogButtonD;
        [SerializeField] private TMP_Text dialogButtonDLabel;

        [Header("Motion")]
        [SerializeField] private float fadeInSeconds = 0.18f;
        [SerializeField] private float fadeOutSeconds = 0.14f;
        [SerializeField] private float popScale = 0.92f;

        private static GameModals instance;
        public static GameModals Instance => instance;

        /// <summary>Whether any modal is up, and therefore whether board input should be ignored.</summary>
        public bool IsBlocking => joinRoom.IsVisible || dialog.IsVisible;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                // Same duplicate handling as TutorialCoach, AudioManager and PassDeviceModal: only
                // the spare component goes when two share an object, never the whole GameObject.
                if (instance.gameObject == gameObject) Destroy(this);
                else Destroy(gameObject);
                return;
            }
            instance = this;

            HideAll();
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        public void HideAll()
        {
            joinRoom.HideImmediate();
            dialog.HideImmediate();
        }

        // ── Join Room ─────────────────────────────────────────────────────

        /// <summary>
        /// Asks for a code. <paramref name="onJoin"/> receives the normalised six characters.
        /// </summary>
        public void ShowJoinRoom(Action<string> onJoin, Action onCancel)
        {
            if (!Warn(joinRoom, "Join Room")) return;

            if (joinCodeInput != null)
            {
                joinCodeInput.characterLimit = Net.RoomCode.Length;
                joinCodeInput.text = string.Empty;
                joinCodeInput.onValueChanged.RemoveAllListeners();
                joinCodeInput.onValueChanged.AddListener(_ => RefreshJoinButton());
            }

            if (joinStatusText != null) joinStatusText.text = string.Empty;

            Rebind(joinConfirmButton, () =>
            {
                string code = Net.RoomCode.Normalize(joinCodeInput != null ? joinCodeInput.text : string.Empty);
                if (!Net.RoomCode.IsWellFormed(code)) return;

                if (joinStatusText != null) joinStatusText.text = "Joining...";
                if (joinConfirmButton != null) joinConfirmButton.interactable = false;

                onJoin?.Invoke(code);
            });

            Rebind(joinCancelButton, () =>
            {
                Hide(joinRoom);
                onCancel?.Invoke();
            });

            RefreshJoinButton();
            Show(joinRoom);

            if (joinCodeInput != null) joinCodeInput.ActivateInputField();
        }

        /// <summary>Join succeeded — take the modal down so the lobby can come up.</summary>
        public void CloseJoinRoom() => Hide(joinRoom);

        private void RefreshJoinButton()
        {
            if (joinConfirmButton == null) return;

            string code = Net.RoomCode.Normalize(joinCodeInput != null ? joinCodeInput.text : string.Empty);
            joinConfirmButton.interactable = Net.RoomCode.IsWellFormed(code);
        }

        // ── The shared dialog ─────────────────────────────────────────────

        /// <summary>One button on the dialog: its words and what it does.</summary>
        public readonly struct Choice
        {
            public readonly string Label;
            public readonly Action Action;

            public Choice(string label, Action action)
            {
                Label = label;
                Action = action;
            }
        }

        /// <summary>
        /// Raises the shared dialog with a title, a body and one to three choices.
        ///
        /// Every question the online game asks goes through here. They differ only in wording and
        /// button count, so giving each its own panel would put three near-identical objects in the
        /// hierarchy for no gain — the same reasoning that has TutorialCoach drive every tip from
        /// one card.
        /// </summary>
        public void ShowDialog(string title, string body, params Choice[] choices)
        {
            if (!Warn(dialog, "Dialog"))
            {
                // Nothing to ask with. The first choice is always the safe one, so take it rather
                // than leaving the player facing a question that never appeared.
                if (choices.Length > 0) choices[0].Action?.Invoke();
                return;
            }

            if (dialogTitleText != null) dialogTitleText.text = title;
            if (dialogBodyText != null) dialogBodyText.text = body;

            Bind(0, choices, dialogButtonA, dialogButtonALabel);
            Bind(1, choices, dialogButtonB, dialogButtonBLabel);
            Bind(2, choices, dialogButtonC, dialogButtonCLabel);
            Bind(3, choices, dialogButtonD, dialogButtonDLabel);

            Show(dialog);
        }

        /// <summary>
        /// Where each button sits, indexed by how many the dialog is showing.
        ///
        /// Positions come from here rather than from wherever the scene left them because the count
        /// varies from one to four and the card has nothing painted on it to align to. A fixed
        /// position per slot left the three-button disconnect dialog reading out of order — the
        /// third choice sat above the first two — and gave a fourth nowhere to go.
        /// </summary>
        private static readonly Vector2[][] ButtonLayouts =
        {
            new Vector2[0],
            new[] { new Vector2(0f, -180f) },
            new[] { new Vector2(-165f, -180f), new Vector2(165f, -180f) },
            new[] { new Vector2(-165f, -60f), new Vector2(165f, -60f), new Vector2(0f, -180f) },
            new[] { new Vector2(-165f, -60f), new Vector2(165f, -60f),
                    new Vector2(-165f, -180f), new Vector2(165f, -180f) },
        };

        private void Bind(int index, Choice[] choices, Button button, TMP_Text label)
        {
            if (button == null) return;

            if (index >= choices.Length)
            {
                button.gameObject.SetActive(false);
                return;
            }

            Choice c = choices[index];
            button.gameObject.SetActive(true);
            if (label != null) label.text = c.Label;

            if (choices.Length < ButtonLayouts.Length &&
                button.transform is RectTransform rect)
                rect.anchoredPosition = ButtonLayouts[choices.Length][index];

            Rebind(button, () =>
            {
                Hide(dialog);
                c.Action?.Invoke();
            });
        }

        /// <summary>
        /// The code did not get them into a game. Deliberately does not speculate about why — a
        /// wrong code and an expired one look identical from here, and guessing wrong is worse than
        /// not guessing.
        /// </summary>
        public void ShowRoomNotFound(Action onTryAgain, Action onMenu)
        {
            // The join modal closes first: this replaces it rather than stacking on top, so there
            // is only ever one thing to answer.
            Hide(joinRoom);

            ShowDialog(
                "Room Not Found",
                "We couldn't find that room.\n\nCheck the code and try again. Rooms also close when the other player leaves.",
                new Choice("TRY AGAIN", onTryAgain),
                new Choice("MAIN MENU", onMenu));
        }

        /// <summary>
        /// Photon never answered. Separate from <see cref="ShowRoomNotFound"/> because the two are
        /// different failures and telling them apart matters: a room that cannot be found is about
        /// the code, and a connection that never came up is not. Saying "room not found" to somebody
        /// who was creating a room sends them looking for a code they were never given.
        ///
        /// <paramref name="onRetry"/> repeats whatever they were doing, rather than always landing
        /// in the join screen.
        /// </summary>
        public void ShowConnectionFailed(bool wasCreating, Action onRetry, Action onMenu)
        {
            Hide(joinRoom);

            ShowDialog(
                "No Connection",
                wasCreating
                    ? "We couldn't reach the game service, so the room was not created.\n\nCheck your internet connection and try again."
                    : "We couldn't reach the game service.\n\nCheck your internet connection and try again.",
                new Choice("TRY AGAIN", onRetry),
                new Choice("MAIN MENU", onMenu));
        }

        /// <summary>
        /// The match is over because the connection is. Offers a way out rather than a retry: there
        /// is no saved state to come back to in this pass, so reconnecting to the same game is not
        /// something that could work.
        ///
        /// The first choice is to stay where they are. The board is still on screen underneath —
        /// GameController stops the game without clearing it — and being marched off a position you
        /// were in the middle of reading, because somebody else quit, is its own small insult. The
        /// other three remain one tap away afterwards: the pause button re-raises this dialog, since
        /// with the game over it has nothing else to do.
        /// </summary>
        public void ShowDisconnected(bool opponentLeft, Action onStay, Action onMenu, Action onPlayComputer, Action onPlayHuman)
        {
            // Plain language, and specific about which of the two happened, because they call for
            // different feelings — one is bad luck, the other is the opponent leaving.
            ShowDialog(
                opponentLeft ? "Opponent Left" : "Connection Lost",
                opponentLeft
                    ? "Your opponent has left the game.\n\nThis match can't continue, but you can stay and look at the final position."
                    : "The connection was lost.\n\nThis match can't continue, but you can stay and look at the final position.",
                new Choice("STAY ON BOARD", onStay),
                new Choice("MAIN MENU", onMenu),
                new Choice("VS COMPUTER", onPlayComputer),
                new Choice("VS HUMAN", onPlayHuman));
        }

        /// <summary>
        /// Are you sure? Replaces the two-press arming the bottom pill used to do in local
        /// two-player, so conceding asks the same question in every mode.
        /// </summary>
        public void ShowForfeitConfirm(Action onConfirm, Action onCancel = null)
        {
            if (!dialog.Exists)
            {
                // Refusing to concede is the safe failure: a player who cannot forfeit is
                // inconvenienced, one who forfeits by accident is not. So this deliberately does
                // not fall through to the first choice the way ShowDialog otherwise would.
                Debug.LogWarning("GameModals: no dialog panel wired, so forfeit is unavailable.");
                onCancel?.Invoke();
                return;
            }

            ShowDialog(
                "Forfeit?",
                "Are you sure you want to forfeit?\n\nThis ends the game and your opponent wins.",
                new Choice("FORFEIT", onConfirm),
                new Choice("CANCEL", onCancel));
        }

        /// <summary>
        /// What the Android back button does while a modal is up.
        ///
        /// Join Room cancels, because backing out of a text field is exactly what back means there
        /// and its cancel path is already safe. Everything else swallows the press: the shared
        /// dialog asks questions whose answers are not interchangeable — the first choice is STAY
        /// on one and FORFEIT on another — so there is no "dismiss" that is right in general, and
        /// guessing would eventually forfeit somebody's match with a stray tap.
        /// </summary>
        public void BackRequested()
        {
            if (joinRoom.IsVisible && joinCancelButton != null)
            {
                joinCancelButton.onClick.Invoke();
                return;
            }

            // Consumed deliberately.
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private bool Warn(ModalPanel panel, string name)
        {
            if (panel.Exists) return true;

            Debug.LogWarning($"GameModals: no panel wired for '{name}' — skipping it.");
            return false;
        }

        private void Show(ModalPanel panel)
        {
            StartCoroutine(panel.FadeIn(fadeInSeconds, popScale));
            Haptics.Light();
        }

        private void Hide(ModalPanel panel)
        {
            if (!panel.Exists || !panel.IsVisible) return;
            StartCoroutine(panel.FadeOut(fadeOutSeconds, popScale));
        }

        /// <summary>
        /// Points a button at a fresh callback. Listeners are cleared first because these modals are
        /// raised many times a session with different answers each time, and AddListener stacks —
        /// without the clear, the second forfeit would fire the first one's handler as well.
        /// </summary>
        private static void Rebind(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button == null) return;

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
            button.interactable = true;
        }
    }
}
