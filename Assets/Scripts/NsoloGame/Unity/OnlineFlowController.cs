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
        [Tooltip("Either player. Held disabled until an opponent has joined.")]
        [SerializeField] private Button lobbyStartButton;
        [Tooltip("Optional. Says what the lobby is waiting for.")]
        [SerializeField] private TMP_Text lobbyHintText;
        [Tooltip("Optional. Copies the room code to the clipboard. Shown only while the host is still waiting for someone to join.")]
        [SerializeField] private Button lobbyCopyCodeButton;
        [Tooltip("Optional. Opens the Android share sheet so the code can go to WhatsApp, SMS, etc. Shown only while the host is still waiting for someone to join.")]
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
        /// Why the last match ended, held while the player is still looking at the finished board.
        /// Null at every other time, which is what tells the pause button whether it has a dialog to
        /// put back up. Cleared on the way out rather than when the dialog is dismissed, because
        /// dismissing it is exactly the case that needs it kept.
        /// </summary>
        private MatchEndReason? endedReason;

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

        private void Start()
        {
            // After NsoloUI has built its register, which happens once every Awake has run.
            AdoptRebuiltUI();
            HideScreens();
        }

        /// <summary>
        /// Points the online slots at rebuilt screens where they exist, and leaves them alone
        /// otherwise. The counterpart of MenuManager.AdoptRebuiltUI — see there for why the wiring
        /// runs this way round.
        /// </summary>
        private void AdoptRebuiltUI()
        {
            GameObject rebuiltOnline = NsoloUI.Panel(PanelId.Online);
            if (rebuiltOnline != null && rebuiltOnline != onlinePanel)
            {
                if (onlinePanel != null) onlinePanel.SetActive(false);
                onlinePanel = rebuiltOnline;
            }

            GameObject rebuiltLobby = NsoloUI.Panel(PanelId.Lobby);
            if (rebuiltLobby != null && rebuiltLobby != lobbyPanel)
            {
                if (lobbyPanel != null) lobbyPanel.SetActive(false);
                lobbyPanel = rebuiltLobby;
            }

            lobbyRoomCodeText     = Pick(NsoloUI.Label(ElementId.LobbyRoomCodeLabel),      lobbyRoomCodeText);
            lobbyPlayer1NameText  = Pick(NsoloUI.Label(ElementId.LobbyPlayer1NameLabel),   lobbyPlayer1NameText);
            lobbyPlayer2NameText  = Pick(NsoloUI.Label(ElementId.LobbyPlayer2NameLabel),   lobbyPlayer2NameText);
            lobbyPlayer1StatusText = Pick(NsoloUI.Label(ElementId.LobbyPlayer1StatusLabel), lobbyPlayer1StatusText);
            lobbyPlayer2StatusText = Pick(NsoloUI.Label(ElementId.LobbyPlayer2StatusLabel), lobbyPlayer2StatusText);
            lobbyHintText         = Pick(NsoloUI.Label(ElementId.LobbyHintLabel),          lobbyHintText);

            lobbyStartButton     = Pick(NsoloUI.Button(ElementId.LobbyStart),     lobbyStartButton);
            lobbyCopyCodeButton  = Pick(NsoloUI.Button(ElementId.LobbyCopyCode),  lobbyCopyCodeButton);
            lobbyShareCodeButton = Pick(NsoloUI.Button(ElementId.LobbyShareCode), lobbyShareCodeButton);

            // NsoloElement has already bound these two if they were tagged. Binding again would
            // double the action, so only a button that arrived through a slot is bound here.
            if (NsoloUI.Button(ElementId.LobbyCopyCode) == null) Bind(lobbyCopyCodeButton, CopyRoomCode);
            if (NsoloUI.Button(ElementId.LobbyShareCode) == null) Bind(lobbyShareCodeButton, ShareRoomCode);
        }

        private static T Pick<T>(T rebuilt, T current) where T : UnityEngine.Object
            => rebuilt != null ? rebuilt : current;

        /// <summary>
        /// Hides both online screens without touching the connection. The menu calls this whenever
        /// it shows anything else, so a panel left switched on in the scene — or one stranded by an
        /// aborted flow — cannot sit invisibly over the menu swallowing every tap.
        /// </summary>
        public void HideScreens()
        {
            SetOnlineVisible(false);
            SetLobbyVisible(false);
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
            // an attempt that never came back cannot follow them in and kill both buttons — and
            // that the last match's ending cannot follow them into the next one.
            SetConnecting(false);
            endedReason = null;

            menuManager?.HideAllPanelsForOnline();
            SetOnlineVisible(true);
            SetLobbyVisible(false);
            GameModals.Instance?.HideAll();

            // Get the connection under way now, while they are still deciding which card to press.
            // Most of the delay on Create is reaching Photon, not making the room.
            Transport.Prewarm();
        }

        /// <summary>
        /// Opens the connection without opening the screen, for callers that know the player is
        /// heading online before the Create/Join panel is up. Safe to call repeatedly.
        /// </summary>
        public void PrewarmConnection()
        {
            if (transport == null) return;
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

        /// <summary>
        /// Wire to the lobby's START GAME button. Either player — see
        /// <see cref="NetworkMatch.RequestBegin"/> for why this is not the host's alone.
        /// </summary>
        public void StartGame()
        {
            if (match == null) return;

            AudioManager.Click();
            Haptics.Light();
            match.RequestBegin();
        }

        /// <summary>Whether the lobby is the screen currently up. Read by the back-button handler.</summary>
        public bool IsLobbyVisible => lobbyIntended && lobbyPanel != null && lobbyPanel.activeSelf;

        /// <summary>Whether the Create/Join screen is up.</summary>
        public bool IsOnlineVisible => onlineIntended && onlinePanel != null && onlinePanel.activeSelf;

        /// <summary>Whether we are holding a room right now — the thing leaving would destroy.</summary>
        private bool InRoom => Transport != null && !string.IsNullOrEmpty(Transport.RoomCode);

        /// <summary>
        /// Wire to the online panel's BACK and the lobby's BACK / LEAVE LOBBY.
        ///
        /// Asks first when there is a room to lose. Leaving is not a navigation step here — it
        /// destroys the room, kills a code that may already have been sent to somebody, and throws
        /// the other player out if they have arrived. The confirmation is skipped when no room is
        /// held, so BACK on the Create/Join screen stays a plain back button.
        /// </summary>
        public void LeaveOnline()
        {
            if (!InRoom || GameModals.Instance == null)
            {
                LeaveOnlineConfirmed();
                return;
            }

            GameModals.Instance.ShowDialog(
                "Leave the room?",
                "This closes the room and your code stops working.\n\nIf someone is on their way in, they won't be able to join.",
                new GameModals.Choice("STAY", null),
                new GameModals.Choice("LEAVE", LeaveOnlineConfirmed));
        }

        /// <summary>The actual leave, once it is no longer in question.</summary>
        private void LeaveOnlineConfirmed()
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
            endedReason = null;
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
            SetOnlineVisible(false);
            SetLobbyVisible(true);
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

            // A seat is ready when somebody is sitting in it. Yours always is — you are looking at
            // the screen — so the only seat that changes is the other one.
            bool p1Ready = iAmOne || opponentHere;
            bool p2Ready = !iAmOne || opponentHere;

            if (lobbyPlayer1StatusText != null) lobbyPlayer1StatusText.text = p1Ready ? "READY" : "WAITING";
            if (lobbyPlayer2StatusText != null) lobbyPlayer2StatusText.text = p2Ready ? "READY" : "WAITING";

            // Player 2's row has two bars parked on the same spot: the grey waiting one, and a gold
            // copy of player 1's. Swapping them is what makes an opponent arriving *look* like
            // something happening — a word changing from WAITING to READY is easy to miss, a row
            // turning gold is not. Both calls are no-ops until the bars are tagged, so a lobby
            // without them behaves exactly as it did.
            NsoloUI.SetVisible(ElementId.LobbyPlayer2WaitingBar, !p2Ready);
            NsoloUI.SetVisible(ElementId.LobbyPlayer2ReadyBar, p2Ready);

            // The code is the host's to hand out, and only until someone takes it up. The joiner
            // already has it — they typed it in to get here — and a room only seats two, so anyone
            // they passed it on to would arrive to find it full. Offering them COPY and SHARE was
            // offering an invitation they have no room for.
            SetButtonActive(lobbyCopyCodeButton, match.IsHost && !opponentHere);
            SetButtonActive(lobbyShareCodeButton, match.IsHost && !opponentHere);

            // Either player may start, once there is someone to start against. START GAME does not
            // begin play — it takes both devices to the arrangement screen, where each side lays out
            // its own half at its own pace and the first move waits for both. So neither player can
            // pull the other into a game they were not ready for, and the joiner is no longer stuck
            // in the lobby when the host puts their phone down.
            //
            // Hidden rather than greyed out while waiting. A disabled button still reads as
            // something you were meant to be able to press, so the first thing a player alone in a
            // lobby does is press it and wonder what is broken; with nothing there, the hint line
            // below is the only thing to read and it says exactly what the room is waiting for.
            // It appears the moment the opponent lands, which is also the clearest signal that they
            // have.
            SetButtonActive(lobbyStartButton, opponentHere);

            if (lobbyHintText != null)
                lobbyHintText.text = opponentHere
                    ? "Both players are in. Either of you can start."
                    : (match.IsHost ? "Share your room code with a friend."
                                    : "Waiting for another player to join.");
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
            if (IsLobbyVisible && reason == MatchEndReason.OpponentLeft)
            {
                // Re-read the seats on the way past. They are normally captured once and held,
                // because a seat number changing under a game in progress would be far worse than a
                // stale one — but nothing has been played yet, so that protection is not buying
                // anything here. PUN will have promoted whoever is left, and picking that up is what
                // lets a joiner whose host walked out host the next person to try the code, rather
                // than sitting in a room where neither side can run the rules.
                EnsureMatch();
                RefreshLobby();
                return;
            }

            CloseMatch();

            if (GameModals.Instance == null)
            {
                menuManager?.ShowWelcome();
                return;
            }

            endedReason = reason;
            ShowMatchEndedDialog();
        }

        /// <summary>
        /// Puts the "this match can't continue" question back up. Separate from
        /// <see cref="HandleMatchEnded"/> so the pause button can raise it again after the player
        /// dismissed it to look at the board.
        /// </summary>
        private void ShowMatchEndedDialog()
        {
            GameModals.Instance.ShowDisconnected(
                opponentLeft: endedReason == MatchEndReason.OpponentLeft,
                // Nothing to do: dismissing is the whole action. The board is still drawn underneath
                // and nothing can move it now, so this leaves them alone with it.
                onStay: null,
                onMenu: () => { endedReason = null; HideScreens(); menuManager?.ShowWelcome(); },
                // Whatever difficulty they last chose, so this is one tap rather than a trip through
                // a difficulty screen they did not ask for.
                onPlayComputer: () => { endedReason = null; HideScreens(); menuManager?.StartGame(PlayerPrefs.GetInt("AIDifficulty", 1)); },
                onPlayHuman: () => { endedReason = null; HideScreens(); menuManager?.StartHotSeatGame(); });
        }

        /// <summary>
        /// Re-raises the dialog for a match that ended and was dismissed, and reports whether there
        /// was one. Called by the pause button, which is otherwise dead once the game is over — so
        /// staying on the board to read it cannot become a screen with no way off it.
        /// </summary>
        public bool ReshowMatchEndedDialog()
        {
            if (endedReason == null || GameModals.Instance == null) return false;
            if (GameModals.Instance.IsBlocking) return false;

            ShowMatchEndedDialog();
            return true;
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

        /// <summary>
        /// Whether each online screen is *meant* to be up. Flipped the instant a screen is asked to
        /// come or go, which is earlier than the object itself changes.
        ///
        /// That gap is the whole reason these exist. These two screens used to be switched with a
        /// bare SetActive, so <c>activeSelf</c> was an honest answer; animating them out means the
        /// object stays active for the 130ms it takes to leave, and every read of "is the lobby
        /// up?" during that window would say yes about a screen the player has already left. One of
        /// those reads decides whether a departing opponent means "the room went back to waiting" or
        /// "the match is over", and getting it wrong there strands a live game.
        /// </summary>
        private bool onlineIntended;
        private bool lobbyIntended;

        private void SetOnlineVisible(bool visible)
        {
            onlineIntended = visible;
            AnimateScreen(onlinePanel, visible);
        }

        private void SetLobbyVisible(bool visible)
        {
            lobbyIntended = visible;
            AnimateScreen(lobbyPanel, visible);
        }

        /// <summary>
        /// Shows or hides an online screen the same way the menu shows its own — through the
        /// panel's transition when it has one, so these two stop being the only screens in the game
        /// that cut. A panel with no transition keeps the old instant behaviour.
        /// </summary>
        private static void AnimateScreen(GameObject panel, bool visible)
        {
            if (panel == null) return;

            var transition = panel.GetComponent<PanelTransition>();
            if (transition == null)
            {
                panel.SetActive(visible);
                return;
            }

            if (visible) transition.Show();
            else transition.Hide();
        }

        /// <summary>Both lobby buttons are optional slots, so this tolerates an empty one.</summary>
        private static void SetButtonActive(Button button, bool active)
        {
            if (button != null) button.gameObject.SetActive(active);
        }
    }
}
