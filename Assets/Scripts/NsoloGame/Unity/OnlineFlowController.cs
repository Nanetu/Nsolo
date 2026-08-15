using NsoloGame.AI;
using NsoloGame.Core;
using NsoloGame.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Everything between tapping PLAY ONLINE and a game being under way: making or finding a room,
    /// the lobby both players wait in, and handing the finished match to <see cref="GameController"/>.
    ///
    /// It is a separate component rather than more of <see cref="MenuManager"/> because it owns the
    /// connection. Keeping it in one place means the rest of the game talks to an online match
    /// through <see cref="NetworkMatch"/> and never touches a transport — the separation that lets a
    /// persistence layer be added later without any of this moving.
    /// </summary>
    public class OnlineFlowController : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("The Photon transport. Auto-found on this object if left empty.")]
        [SerializeField] private PhotonMatchTransport transport;
        [SerializeField] private MenuManager menuManager;
        [SerializeField] private GameController gameController;

        [Header("Online panel")]
        [Tooltip("The Create Room / Join Room screen, reached from the welcome screen.")]
        [SerializeField] private GameObject onlinePanel;

        [Header("Lobby panel")]
        [Tooltip("Where both players wait. Shows the room code and who is in.")]
        [SerializeField] private GameObject lobbyPanel;
        [SerializeField] private TMP_Text lobbyRoomCodeText;
        [SerializeField] private TMP_Text lobbyPlayer1NameText;
        [SerializeField] private TMP_Text lobbyPlayer2NameText;
        [SerializeField] private TMP_Text lobbyPlayer1StatusText;
        [SerializeField] private TMP_Text lobbyPlayer2StatusText;
        [Tooltip("Host only. Held disabled until an opponent has joined.")]
        [SerializeField] private Button lobbyStartButton;
        [Tooltip("Optional. Explains why START GAME is unavailable on the joiner's screen.")]
        [SerializeField] private TMP_Text lobbyHintText;
        [Tooltip("Optional. Copies the room code to the clipboard.")]
        [SerializeField] private Button lobbyCopyCodeButton;
        [Tooltip("Optional. Opens the Android share sheet so the code can go to WhatsApp, SMS, etc.")]
        [SerializeField] private Button lobbyShareCodeButton;

        private IMatchTransport Transport => transport;
        private NetworkMatch match;
        private bool subscribed;

        /// <summary>
        /// True between tapping Create/Join and the room being ready, and used to ignore a second
        /// tap while the first is still in flight.
        ///
        /// It is cleared by a timeout as well as by the callbacks, because every way of clearing it
        /// used to depend on Photon answering. When it did not — a region it cannot reach, a network
        /// that goes nowhere — this latched on and took both buttons out with it: Create looked like
        /// it needed several presses (the early ones were being swallowed, and the room only opened
        /// when the connection finally landed), and Join was simply dead thereafter.
        /// </summary>
        private bool connecting;

        private float connectingSince;

        /// <summary>
        /// How long to wait before deciding a connection attempt is not coming back. Photon's own
        /// timeouts are around ten seconds, so this sits past them rather than pre-empting them.
        /// </summary>
        private const float ConnectTimeoutSeconds = 15f;

        private void Awake()
        {
            if (transport == null) transport = GetComponent<PhotonMatchTransport>();
            if (transport == null) transport = FindObjectOfType<PhotonMatchTransport>();
            if (menuManager == null) menuManager = FindObjectOfType<MenuManager>();
            if (gameController == null) gameController = FindObjectOfType<GameController>();

            ResolveLobbyPanel();
            HideScreens();
            Subscribe();

            // Bound here rather than in the Inspector so these two only need dropping into their
            // slots — one less piece of wiring to get wrong.
            Bind(lobbyCopyCodeButton, CopyRoomCode);
            Bind(lobbyShareCodeButton, ShareRoomCode);
        }

        private static void Bind(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button == null) return;

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        /// <summary>
        /// Takes the lobby panel to be whichever object actually holds the lobby's contents, rather
        /// than trusting the slot.
        ///
        /// Rebuilding the lobby by hand — duplicating a panel, moving the labels and buttons across,
        /// deleting the old one — leaves the slot pointing at an empty husk while the real screen is
        /// somewhere else. The symptom is silent and baffling: Create Room "does nothing", because
        /// the panel being shown has nothing in it and the panel with everything in it is never
        /// touched. The content knows where it lives, so this asks it.
        /// </summary>
        private void ResolveLobbyPanel()
        {
            if (lobbyRoomCodeText == null) return;

            GameObject owner = PanelRootOf(lobbyRoomCodeText.transform);
            if (owner == null || owner == lobbyPanel) return;

            Debug.LogWarning(
                $"OnlineFlowController: Lobby Panel was set to '{(lobbyPanel != null ? lobbyPanel.name : "none")}' " +
                $"but the lobby's contents live under '{owner.name}'. Using '{owner.name}'. " +
                "Update the slot to silence this.");

            lobbyPanel = owner;
        }

        /// <summary>Walks up to the top-level panel — the child of the Canvas that contains this.</summary>
        private static GameObject PanelRootOf(Transform t)
        {
            Canvas canvas = t.GetComponentInParent<Canvas>();
            if (canvas == null) return null;

            Transform cursor = t;
            while (cursor.parent != null && cursor.parent != canvas.transform)
                cursor = cursor.parent;

            return cursor.parent == canvas.transform ? cursor.gameObject : null;
        }

        /// <summary>
        /// Hides both online screens without touching the connection. The menu calls this whenever
        /// it shows anything else, so a panel left switched on in the scene — or one stranded by an
        /// aborted flow — cannot sit invisibly over the menu swallowing every tap.
        /// </summary>
        public void HideScreens()
        {
            SetActive(onlinePanel, false);
            SetActive(lobbyPanel, false);
        }

        private void OnDestroy() => Unsubscribe();

        private void Update()
        {
            if (!connecting) return;
            if (Time.unscaledTime - connectingSince < ConnectTimeoutSeconds) return;

            Debug.LogWarning("OnlineFlowController: no answer from Photon within " +
                             $"{ConnectTimeoutSeconds:0}s — giving up on this attempt.");
            SetConnecting(false);
            HandleConnectionFailed();
        }

        /// <summary>
        /// Single place the latch is set or cleared, so it can never be left on by a path that
        /// forgot. Also greys the cards, which is the only sign the player gets that the first tap
        /// registered and something is happening.
        /// </summary>
        private void SetConnecting(bool value)
        {
            connecting = value;
            connectingSince = Time.unscaledTime;

            SetCardsInteractable(!value);
        }

        private void SetCardsInteractable(bool on)
        {
            if (onlinePanel == null) return;

            foreach (Button b in onlinePanel.GetComponentsInChildren<Button>(true))
                b.interactable = on;
        }

        private void Subscribe()
        {
            if (subscribed || transport == null) return;

            Transport.RoomCreated += HandleRoomCreated;
            Transport.MatchReady += HandleMatchReady;
            Transport.RoomNotFound += HandleRoomNotFound;
            Transport.ConnectionFailed += HandleConnectionFailed;
            Transport.MatchEnded += HandleMatchEnded;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed || transport == null) return;

            Transport.RoomCreated -= HandleRoomCreated;
            Transport.MatchReady -= HandleMatchReady;
            Transport.RoomNotFound -= HandleRoomNotFound;
            Transport.ConnectionFailed -= HandleConnectionFailed;
            Transport.MatchEnded -= HandleMatchEnded;
            subscribed = false;
        }

        // ── Entry points (wire these to buttons) ──────────────────────────

        /// <summary>Opens the Create Room / Join Room screen. Wire to the welcome screen's PLAY ONLINE.</summary>
        public void ShowOnlinePanel()
        {
            if (transport == null)
            {
                Debug.LogError("OnlineFlowController: no PhotonMatchTransport in the scene.");
                return;
            }

            // Arriving at this screen is a fresh start, so nothing from a previous attempt is still
            // in flight as far as the player is concerned. Clearing here means a latch left on by
            // an attempt that never came back cannot follow them in and kill both buttons.
            SetConnecting(false);

            menuManager?.HideAllPanelsForOnline();
            SetActive(onlinePanel, true);
            SetActive(lobbyPanel, false);
            GameModals.Instance?.HideAll();

            // Get the connection under way now, while they are still deciding which card to press.
            // Most of the delay on Create is reaching Photon, not making the room.
            Transport.Prewarm();
        }

        /// <summary>Wire to the online panel's CREATE ROOM card.</summary>
        public void CreateRoom()
        {
            if (connecting || transport == null) return;

            SetConnecting(true);
            AudioManager.Click();
            Haptics.Light();
            Transport.CreateRoom();
        }

        /// <summary>Wire to the online panel's JOIN ROOM card.</summary>
        public void ShowJoinRoom()
        {
            // These two used to fail silently, which is indistinguishable from the button being
            // dead. Both are real states worth naming: a connection attempt still in flight, and a
            // GameModals component that never woke up.
            if (connecting)
            {
                Debug.LogWarning("OnlineFlowController: still connecting, ignoring Join Room.");
                return;
            }

            if (GameModals.Instance == null)
            {
                Debug.LogError("OnlineFlowController: no GameModals in the scene, so Join Room cannot open.");
                return;
            }

            AudioManager.Click();
            Haptics.Light();

            GameModals.Instance.ShowJoinRoom(
                onJoin: code =>
                {
                    SetConnecting(true);
                    Transport.JoinRoom(code);
                },
                onCancel: () => SetConnecting(false));
        }

        /// <summary>
        /// Copies the room code to the clipboard. Wire to a COPY button on the lobby.
        /// </summary>
        public void CopyRoomCode()
        {
            string code = Transport?.RoomCode;
            if (string.IsNullOrEmpty(code)) return;

            GUIUtility.systemCopyBuffer = code;
            AudioManager.Click();
            Haptics.Light();

            // Copying gives no other sign it worked, and a code that silently failed to copy is the
            // kind of thing you only discover once your friend cannot join.
            if (lobbyHintText != null) lobbyHintText.text = $"Code {code} copied to clipboard.";
        }

        /// <summary>
        /// Hands the code to the phone's share sheet — WhatsApp, SMS, anything installed. Wire to a
        /// SHARE button on the lobby.
        ///
        /// Falls back to copying on desktop and in the editor, where there is no share sheet, so the
        /// button does something useful while testing rather than appearing broken.
        /// </summary>
        public void ShareRoomCode()
        {
            string code = Transport?.RoomCode;
            if (string.IsNullOrEmpty(code)) return;

            string message = $"Play Nsolo with me! Room code: {code}";

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var intentClass = new AndroidJavaClass("android.content.Intent"))
                using (var intent = new AndroidJavaObject("android.content.Intent"))
                {
                    intent.Call<AndroidJavaObject>("setAction", intentClass.GetStatic<string>("ACTION_SEND"));
                    intent.Call<AndroidJavaObject>("setType", "text/plain");
                    intent.Call<AndroidJavaObject>(
                        "putExtra", intentClass.GetStatic<string>("EXTRA_TEXT"), message);

                    using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var chooser = intentClass.CallStatic<AndroidJavaObject>(
                               "createChooser", intent, "Share room code"))
                    {
                        activity.Call("startActivity", chooser);
                    }
                }

                AudioManager.Click();
                Haptics.Light();
                return;
            }
            catch (System.Exception e)
            {
                // A share sheet that will not open should not cost the player the code.
                Debug.LogWarning($"OnlineFlowController: share sheet failed ({e.Message}); copying instead.");
            }
#endif
            CopyRoomCode();
        }

        /// <summary>Wire to the lobby's START GAME button. Host only.</summary>
        public void StartGame()
        {
            if (match == null || !match.IsHost) return;

            AudioManager.Click();
            Haptics.Light();
            match.RequestBegin();
        }

        /// <summary>Wire to the online panel's BACK and the lobby's BACK / LEAVE LOBBY.</summary>
        public void LeaveOnline()
        {
            CloseAndCleanup();
            menuManager?.ShowWelcome();
        }

        /// <summary>
        /// Drops the room and clears the online screens without navigating anywhere. Separate from
        /// <see cref="LeaveOnline"/> so the menu can call it on its way back to the welcome screen
        /// without the two calling each other.
        /// </summary>
        public void CloseAndCleanup()
        {
            SetConnecting(false);
            CloseMatch();
            GameModals.Instance?.HideAll();
            HideScreens();
        }

        // ── Transport callbacks ───────────────────────────────────────────

        /// <summary>
        /// The room exists and we are alone in it. Straight to the lobby — it already shows the code
        /// and a waiting state, so a separate "here is your code" popup would be the same screen
        /// twice.
        /// </summary>
        private void HandleRoomCreated(string code)
        {
            SetConnecting(false);
            EnsureMatch();
            ShowLobby();
        }

        private void HandleMatchReady()
        {
            SetConnecting(false);
            GameModals.Instance?.CloseJoinRoom();
            EnsureMatch();
            ShowLobby();
        }

        /// <summary>
        /// Builds the match once per room. The host reaches this on creation and the joiner on
        /// arrival, and both call it again when the opponent turns up — hence the guard.
        /// </summary>
        private void EnsureMatch()
        {
            if (match != null)
            {
                // Seats can only be settled once both are in; re-assert in case this is the second
                // call, when IsHost is finally meaningful.
                match.AssignSeats(Transport.IsHost);
                return;
            }

            // A fresh engine on the same deterministic rules the local game uses. Building one here
            // rather than borrowing GameController's keeps the authority's copy of the rules
            // independent of whatever the UI happens to be doing.
            match = new NetworkMatch(Transport, new GameEngine(new SowingPath()));
            match.AssignSeats(Transport.IsHost);
            match.MatchBegun += HandleMatchBegun;
        }

        private void ShowLobby()
        {
            menuManager?.HideAllPanelsForOnline();
            SetActive(onlinePanel, false);
            SetActive(lobbyPanel, true);
            RefreshLobby();
        }

        /// <summary>
        /// Paints the lobby from whatever the transport currently knows. Called when the room is
        /// made and again when the opponent arrives, so the second player's slot fills in place.
        /// </summary>
        private void RefreshLobby()
        {
            if (match == null) return;

            bool opponentHere = Transport.IsReady;

            if (lobbyRoomCodeText != null)
                lobbyRoomCodeText.text = Transport.RoomCode ?? "------";

            // Seat 1 is always the room's creator, so the names go to fixed slots rather than
            // "me on the left" — both players then see the same board seats they will actually play.
            string mine = match.LocalPlayerName;
            string theirs = opponentHere ? match.OpponentName : "Waiting...";
            bool iAmOne = match.LocalPlayer == 1;

            if (lobbyPlayer1NameText != null) lobbyPlayer1NameText.text = iAmOne ? mine : theirs;
            if (lobbyPlayer2NameText != null) lobbyPlayer2NameText.text = iAmOne ? theirs : mine;

            string p1State = iAmOne ? "READY" : (opponentHere ? "READY" : "WAITING");
            string p2State = iAmOne ? (opponentHere ? "READY" : "WAITING") : "READY";
            if (lobbyPlayer1StatusText != null) lobbyPlayer1StatusText.text = p1State;
            if (lobbyPlayer2StatusText != null) lobbyPlayer2StatusText.text = p2State;

            // Only the host can start, and only once there is someone to start against. The joiner
            // is told why rather than being left prodding a dead button.
            if (lobbyStartButton != null)
            {
                lobbyStartButton.gameObject.SetActive(match.IsHost);
                lobbyStartButton.interactable = match.IsHost && opponentHere;
            }

            if (lobbyHintText != null)
                lobbyHintText.text = match.IsHost
                    ? (opponentHere ? "Both players are in. Start when you're ready."
                                    : "Share your room code with a friend.")
                    : "Waiting for the host to start the game.";
        }

        private void HandleMatchBegun()
        {
            HideScreens();
            menuManager?.StartOnlineGame(match);
        }

        private void HandleRoomNotFound()
        {
            SetConnecting(false);

            if (GameModals.Instance == null)
            {
                LeaveOnline();
                return;
            }

            GameModals.Instance.ShowRoomNotFound(onTryAgain: ShowJoinRoom, onMenu: LeaveOnline);
        }

        private void HandleConnectionFailed()
        {
            SetConnecting(false);

            // Distinct from a match dropping: nothing was ever under way, so this is the same dead
            // end as a bad code and gets the same dialog, which offers a retry rather than the
            // disconnect dialog's "start a local game instead".
            GameModals.Instance?.ShowRoomNotFound(onTryAgain: ShowJoinRoom, onMenu: LeaveOnline);
        }

        private void HandleMatchEnded(MatchEndReason reason)
        {
            SetConnecting(false);
            if (match == null) return;

            // Losing the opponent while still in the lobby is not a lost match — it is a room that
            // went back to waiting. Only tear down once a game was actually under way.
            if (lobbyPanel != null && lobbyPanel.activeSelf && reason == MatchEndReason.OpponentLeft)
            {
                RefreshLobby();
                return;
            }

            CloseMatch();

            if (GameModals.Instance == null)
            {
                menuManager?.ShowWelcome();
                return;
            }

            GameModals.Instance.ShowDisconnected(
                opponentLeft: reason == MatchEndReason.OpponentLeft,
                onMenu: () => { HideScreens(); menuManager?.ShowWelcome(); },
                // Whatever difficulty they last chose, so this is one tap rather than a trip through
                // a difficulty screen they did not ask for.
                onPlayComputer: () => { HideScreens(); menuManager?.StartGame(PlayerPrefs.GetInt("AIDifficulty", 1)); },
                onPlayHuman: () => { HideScreens(); menuManager?.StartHotSeatGame(); });
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Tears the current match down: unhooks the game from it, unhooks it from the transport,
        /// and leaves the room. Safe to call when there is no match.
        /// </summary>
        private void CloseMatch()
        {
            gameController?.DetachOnlineMatch();

            if (match != null)
            {
                match.MatchBegun -= HandleMatchBegun;
                match.Detach();
                match = null;
            }

            Transport?.Leave();
        }

        private static void SetActive(GameObject obj, bool active)
        {
            if (obj != null) obj.SetActive(active);
        }
    }
}
