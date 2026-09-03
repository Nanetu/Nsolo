using System.Collections.Generic;
using TMPro;
using UnityEngine;

using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Which screen a <see cref="NsoloPanel"/> is. This is the whole of the wiring between a
    /// rebuilt screen and the code that drives it: tag the panel with its id and the controllers
    /// find it, instead of somebody remembering to drag it into the right Inspector slot.
    ///
    /// The numbers are written out on purpose. A serialized enum stores the number, not the name,
    /// so inserting a value in the middle of a plain enum would silently repoint every field
    /// already saved in the scene — a panel tagged Pause would come back as GameOver.
    /// </summary>
    public enum PanelId
    {
        None = 0,
        [InspectorName("Welcome (old first screen)")] Welcome = 1,
        [InspectorName("Main menu")] MainMenu = 2,
        [InspectorName("Mode - vs Computer / vs Human")] Mode = 3,
        [InspectorName("Difficulty")] Difficulty = 4,
        [InspectorName("Profile")] Profile = 5,
        [InspectorName("Online - Create / Join")] Online = 6,
        [InspectorName("Lobby")] Lobby = 7,
        [InspectorName("Pause")] Pause = 8,
        [InspectorName("Game over")] GameOver = 9,
        [InspectorName("Tutorial / How to play")] Tutorial = 10,
    }

    /// <summary>
    /// What one thing inside a panel is — a button's job, a label's job, or an object something
    /// else needs to switch on and off. Put a <see cref="NsoloElement"/> on it, pick the entry,
    /// and it wires itself: no onClick, no Inspector slot, no controller reference.
    ///
    /// Numbers are grouped a hundred to a screen and, as with <see cref="PanelId"/>, are explicit
    /// so that adding entries later cannot disturb wiring already saved in the scene.
    /// </summary>
    public enum ElementId
    {
        None = 0,

        // ── Works on any screen ───────────────────────────────────────────
        [InspectorName("BACK (any screen - works out where back is)")] Back = 1,

        // ── Main menu ─────────────────────────────────────────────────────
        [InspectorName("Main menu/PLAY")] MainMenuPlay = 100,

        /// <summary>
        /// Goes straight to Create/Join. It began as a main-menu button and now lives on the mode
        /// screen as a third card, so the name is historical — the id is kept rather than renumbered
        /// because the number is what the scene stores. Prefer <see cref="ModeOnline"/> on a card
        /// that should light up and wait for CONTINUE.
        /// </summary>
        [InspectorName("Play online (jumps straight in)")] MainMenuPlayOnline = 101,
        [InspectorName("Main menu/PROFILE")] MainMenuProfile = 102,
        [InspectorName("Main menu/HOW TO PLAY")] MainMenuHowToPlay = 103,
        [InspectorName("Main menu/QUIT")] MainMenuQuit = 104,
        [InspectorName("Main menu/Label - player name")] MainMenuUsernameLabel = 105,

        // ── Mode ──────────────────────────────────────────────────────────
        [InspectorName("Mode/Card - vs COMPUTER")] ModeVsComputer = 200,
        [InspectorName("Mode/Card - vs HUMAN")] ModeVsHuman = 201,
        [InspectorName("Mode/CONTINUE")] ModeContinue = 202,
        [InspectorName("Mode/Tick on the vs COMPUTER card")] ModeVsComputerHighlight = 203,
        [InspectorName("Mode/Tick on the vs HUMAN card")] ModeVsHumanHighlight = 204,
        [InspectorName("Mode/Card - PLAY ONLINE")] ModeOnline = 205,
        [InspectorName("Mode/Tick on the PLAY ONLINE card")] ModeOnlineHighlight = 206,

        // ── Difficulty ────────────────────────────────────────────────────
        [InspectorName("Difficulty/Card - EASY")] DifficultyEasy = 300,
        [InspectorName("Difficulty/Card - MEDIUM")] DifficultyMedium = 301,
        [InspectorName("Difficulty/Card - HARD")] DifficultyHard = 302,
        [InspectorName("Difficulty/START GAME")] DifficultyStart = 303,
        [InspectorName("Difficulty/Tick on the EASY card")] DifficultyEasyHighlight = 304,
        [InspectorName("Difficulty/Tick on the MEDIUM card")] DifficultyMediumHighlight = 305,
        [InspectorName("Difficulty/Tick on the HARD card")] DifficultyHardHighlight = 306,

        // ── Profile ───────────────────────────────────────────────────────
        [InspectorName("Profile/Label - username")] ProfileUsernameLabel = 400,
        [InspectorName("Profile/Label - games played")] ProfileGamesPlayedLabel = 401,
        [InspectorName("Profile/Label - games won")] ProfileGamesWonLabel = 402,
        [InspectorName("Profile/Label - win rate")] ProfileWinRateLabel = 403,
        [InspectorName("Profile/Label - fastest win")] ProfileShortestWinLabel = 404,
        [InspectorName("Profile/Label - easy wins")] ProfileEasyWinsLabel = 405,
        [InspectorName("Profile/Label - hard wins")] ProfileHardWinsLabel = 406,
        [InspectorName("Profile/Label - breakdown")] ProfileBreakdownLabel = 407,
        [InspectorName("Profile/EDIT NAME")] ProfileEditUsername = 408,
        [InspectorName("Profile/Avatar - the picture itself")] ProfileAvatarIcon = 409,
        [InspectorName("Profile/Avatar - the initial letter")] ProfileAvatarMonogram = 410,
        [InspectorName("Profile/Avatar - tap to change")] ProfileAvatarButton = 411,
        [InspectorName("Profile/Avatar picker - CLOSE")] ProfileCloseAvatarPicker = 412,
        [InspectorName("Profile/Name box (input field)")] ProfileUsernameInput = 413,
        [InspectorName("Profile/Stat - matches played")] ProfileMatchesLabel = 414,
        [InspectorName("Profile/Stat - best streak")] ProfileBestStreakLabel = 415,
        [InspectorName("Profile/Stat - total playtime")] ProfileTotalPlaytimeLabel = 416,
        [InspectorName("Profile/Stat - stones captured")] ProfileStonesCapturedLabel = 417,
        [InspectorName("Profile/Stat - longest relay")] ProfileLongestRelayLabel = 418,
        [InspectorName("Profile/Breakdown - easy")] ProfileBreakdownEasyLabel = 419,
        [InspectorName("Profile/Breakdown - medium")] ProfileBreakdownMediumLabel = 420,
        [InspectorName("Profile/Breakdown - hard")] ProfileBreakdownHardLabel = 421,

        // ── Online: Create / Join ─────────────────────────────────────────
        [InspectorName("Online/CREATE ROOM")] OnlineCreateRoom = 500,
        [InspectorName("Online/JOIN ROOM")] OnlineJoinRoom = 501,
        [InspectorName("Online/LEAVE (asks first)")] OnlineLeave = 502,

        // ── Lobby ─────────────────────────────────────────────────────────
        [InspectorName("Lobby/Label - room code")] LobbyRoomCodeLabel = 600,
        [InspectorName("Lobby/Label - player 1 name")] LobbyPlayer1NameLabel = 601,
        [InspectorName("Lobby/Label - player 2 name")] LobbyPlayer2NameLabel = 602,
        [InspectorName("Lobby/Label - player 1 status")] LobbyPlayer1StatusLabel = 603,
        [InspectorName("Lobby/Label - player 2 status")] LobbyPlayer2StatusLabel = 604,
        [InspectorName("Lobby/Label - waiting hint")] LobbyHintLabel = 605,
        [InspectorName("Lobby/START GAME")] LobbyStart = 606,
        [InspectorName("Lobby/COPY CODE")] LobbyCopyCode = 607,
        [InspectorName("Lobby/SHARE CODE")] LobbyShareCode = 608,
        [InspectorName("Lobby/LEAVE (asks first)")] LobbyLeave = 609,
        [InspectorName("Lobby/Player 2 grey WAITING bar")] LobbyPlayer2WaitingBar = 610,
        [InspectorName("Lobby/Player 2 gold READY bar")] LobbyPlayer2ReadyBar = 611,

        // ── Pause ─────────────────────────────────────────────────────────
        [InspectorName("Pause/RESUME")] PauseResume = 700,
        [InspectorName("Pause/RESTART")] PauseRestart = 701,
        [InspectorName("Pause/MAIN MENU")] PauseMainMenu = 702,
        [InspectorName("Pause/HOW TO PLAY")] PauseHowToPlay = 703,
        [InspectorName("Pause/Slider - music volume")] PauseMusicSlider = 704,
        [InspectorName("Pause/Slider - sound volume")] PauseSfxSlider = 705,
        [InspectorName("Pause/Label - music percent")] PauseMusicLabel = 706,
        [InspectorName("Pause/Label - sound percent")] PauseSfxLabel = 707,
        [InspectorName("Pause/Toggle - vibration")] PauseVibrationToggle = 708,
        [InspectorName("Pause/Toggle - in-game tips")] PauseTipsToggle = 709,
        [InspectorName("Pause/RESET TIPS")] PauseResetTips = 710,

        // ── Game over ─────────────────────────────────────────────────────
        [InspectorName("Game over/Label - VICTORY or DEFEAT")] GameOverTitleLabel = 800,
        [InspectorName("Game over/Label - your stones")] GameOverPlayerScoreLabel = 801,
        [InspectorName("Game over/Label - opponent stones")] GameOverOpponentScoreLabel = 802,
        [InspectorName("Game over/Label - difficulty")] GameOverDifficultyLabel = 803,
        [InspectorName("Game over/Label - time taken")] GameOverTimeLabel = 804,
        [InspectorName("Game over/Label - total wins")] GameOverVictoryCountLabel = 805,
        [InspectorName("Game over/Label - score summary")] GameOverSummaryLabel = 806,
        [InspectorName("Game over/PLAY AGAIN")] GameOverPlayAgain = 807,
        [InspectorName("Game over/MAIN MENU")] GameOverMainMenu = 808,
        [InspectorName("Game over/Label - stones captured")] GameOverCapturesLabel = 809,
        [InspectorName("Game over/Label - longest relay")] GameOverRelayLabel = 810,

        // ── Tutorial ──────────────────────────────────────────────────────
        [InspectorName("Tutorial/START PLAYING")] TutorialStart = 900,
        [InspectorName("Tutorial/CLOSE")] TutorialClose = 901,

        // ── On the board, during a game ───────────────────────────────────
        [InspectorName("In game/PAUSE")] HudPause = 1000,
        [InspectorName("In game/UNDO")] HudUndo = 1001,
        [InspectorName("In game/HINT")] HudHint = 1002,
        [InspectorName("In game/Main action button")] HudAction = 1003,
        [InspectorName("In game/CONFIRM formation")] HudConfirmFormation = 1004,

        /// <summary>
        /// The two captions above the score boxes. They used to be painted into the background art
        /// — one image per mode, each with COMPUTER or PLAYER 1 already lettered on it — so nothing
        /// ever wrote them. The rebuilt HUD makes them real labels, which means the mode has to say
        /// what they read. Left box is the opponent's, right box is this device's.
        /// </summary>
        [InspectorName("In game/Label - opponent name (left box)")] HudOpponentNameLabel = 1005,
        [InspectorName("In game/Label - your name (right box)")] HudPlayerNameLabel = 1006,

        /// <summary>
        /// FORFEIT, as a button of its own sharing the action pill's spot.
        ///
        /// One button relabelling itself covered all three jobs, but it could only ever wear one
        /// look, and START, HINT and FORFEIT do not want the same one. Tagging separate buttons
        /// splits them by state: <see cref="HudAction"/> while arranging, then <see cref="HudHint"/>
        /// against the computer or this one in the two-player modes. Only ever one on screen.
        ///
        /// Both optional. Tag neither and <see cref="HudAction"/> keeps doing all three jobs by
        /// changing its label, exactly as before.
        /// </summary>
        [InspectorName("In game/FORFEIT (two-player modes)")] HudForfeit = 1007,
    }

    /// <summary>
    /// The register every rebuilt screen reports into, and the one place that knows what each
    /// <see cref="ElementId"/> actually does.
    ///
    /// Why this exists: every panel used to reach the code through a serialized field on
    /// <see cref="MenuManager"/> plus an onClick entry per button. Rebuilding a screen therefore
    /// meant redoing that wiring by hand, and a missed slot is invisible — the button simply does
    /// nothing, with no error, until somebody taps it. This inverts it. A panel says what it is,
    /// an element says what it is for, and the binding is done in code where it can be read,
    /// reviewed, and reported on.
    ///
    /// The old serialized fields are untouched and still work. A registered panel simply wins over
    /// one dragged into a slot, so a rebuilt screen takes over the moment it is tagged, and the
    /// screen it replaces can be deleted afterwards rather than beforehand.
    /// </summary>
    public static class NsoloUI
    {
        private static readonly Dictionary<PanelId, NsoloPanel> panelsById = new Dictionary<PanelId, NsoloPanel>();
        private static readonly Dictionary<ElementId, List<NsoloElement>> elementsById =
            new Dictionary<ElementId, List<NsoloElement>>();
        private static readonly List<NsoloPanel> allPanels = new List<NsoloPanel>();

        private static bool scanned;
        private static int lastRescanFrame = -1;

        private static MenuManager menu;
        private static GameController game;
        private static OnlineFlowController online;

        /// <summary>Every tagged panel found, in scan order. Used to hide screens wholesale.</summary>
        public static IReadOnlyList<NsoloPanel> AllPanels
        {
            get { EnsureScanned(); return allPanels; }
        }

        /// <summary>True once anything at all has been tagged, so callers can keep the old path.</summary>
        public static bool HasAnyPanels
        {
            get { EnsureScanned(); return allPanels.Count > 0; }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────

        /// <summary>
        /// Clears the register before a play session starts. Static state survives leaving play
        /// mode when Domain Reload is switched off, and a register still holding last session's
        /// destroyed objects is worse than an empty one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            panelsById.Clear();
            elementsById.Clear();
            allPanels.Clear();
            scanned = false;
            lastRescanFrame = -1;
            menu = null;
            game = null;
            online = null;
        }

        /// <summary>
        /// Finds and binds everything once the scene is loaded.
        ///
        /// This cannot be done from the components' own Awake. Menu panels sit in the scene
        /// switched off, and a disabled GameObject never runs Awake — so a panel would only
        /// register once something had already shown it, and nothing could show it until it had
        /// registered. Scanning from outside, with the include-inactive flag, is the way out of
        /// that circle. AfterSceneLoad runs after every Awake and before the first Start, so the
        /// controllers exist to bind to and <see cref="UIClickSound"/> has not yet added its own
        /// listeners.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => Rescan();

        private static void EnsureScanned()
        {
            if (!scanned) Rescan();
        }

        /// <summary>
        /// Walks the scene for tagged panels and elements and binds what it finds. Safe to call
        /// again — binding is idempotent, and objects destroyed since the last pass are dropped.
        /// </summary>
        public static void Rescan()
        {
            panelsById.Clear();
            elementsById.Clear();
            allPanels.Clear();
            scanned = true;
            lastRescanFrame = Time.frameCount;

            menu = null;
            game = null;
            online = null;

            // FindObjectsOfTypeAll rather than a walk of the active scene's roots, for two reasons:
            // it returns objects that are switched off, which every menu panel is; and it reaches
            // the DontDestroyOnLoad scene, where GameSystems and anything parented under it live.
            // The cost is that it also returns prefab assets loaded in memory, which have no scene
            // — that is what the scene check below excludes.
            foreach (NsoloPanel panel in Resources.FindObjectsOfTypeAll<NsoloPanel>())
                if (InScene(panel)) RegisterPanel(panel);

            foreach (NsoloElement element in Resources.FindObjectsOfTypeAll<NsoloElement>())
                if (InScene(element)) RegisterElement(element);

            foreach (List<NsoloElement> list in elementsById.Values)
                foreach (NsoloElement element in list)
                    element.Bind();

            if (allPanels.Count > 0 || elementsById.Count > 0)
                Debug.Log($"NsoloUI: {allPanels.Count} tagged panel(s), {CountElements()} tagged element(s) bound.");
        }

        /// <summary>
        /// Whether this is a live object in a scene rather than a prefab asset sitting in memory.
        /// A prefab's GameObject has no scene, which is the cheapest way to tell them apart — and
        /// registering one would mean the register handing out a reference to something that is not
        /// in the game.
        /// </summary>
        private static bool InScene(UnityEngine.Component component)
            => component != null && component.gameObject.scene.IsValid();

        private static int CountElements()
        {
            int n = 0;
            foreach (List<NsoloElement> list in elementsById.Values) n += list.Count;
            return n;
        }

        private static void RegisterPanel(NsoloPanel panel)
        {
            if (panel == null || panel.Id == PanelId.None) return;

            allPanels.Add(panel);

            if (panelsById.TryGetValue(panel.Id, out NsoloPanel existing) && existing != null)
            {
                Debug.LogWarning(
                    $"NsoloUI: two panels are both tagged '{panel.Id}' — '{existing.name}' and " +
                    $"'{panel.name}'. Using '{existing.name}'; set the other one's Id to None or " +
                    "delete it, or the wrong screen will open.", panel);
                return;
            }

            panelsById[panel.Id] = panel;
            panel.Prepare();
        }

        private static void RegisterElement(NsoloElement element)
        {
            if (element == null || element.Id == ElementId.None) return;

            if (!elementsById.TryGetValue(element.Id, out List<NsoloElement> list))
            {
                list = new List<NsoloElement>();
                elementsById[element.Id] = list;
            }

            // Several buttons sharing one action is ordinary — every screen has a BACK. Several
            // objects sharing a *label*, or a *thing to switch on and off*, is not: only one of
            // them would ever be written to, and which one is an accident of hierarchy order.
            if (list.Count > 0 && !IsAction(element.Id))
            {
                Debug.LogWarning(
                    $"NsoloUI: '{element.Id}' is tagged on more than one object — '{list[0].name}' " +
                    $"and '{element.name}'. Only '{list[0].name}' will be used.", element);
            }

            list.Add(element);
        }

        // ── Lookups ───────────────────────────────────────────────────────

        /// <summary>The tagged screen for an id, or null if nothing has claimed it.</summary>
        public static GameObject Panel(PanelId id)
        {
            EnsureScanned();

            if (panelsById.TryGetValue(id, out NsoloPanel panel) && panel != null)
                return panel.gameObject;

            // A panel may have been created or retagged since the last pass. Rescan at most once a
            // frame, so a genuinely absent panel cannot turn this into a per-frame scene walk.
            if (lastRescanFrame != Time.frameCount)
            {
                Rescan();
                if (panelsById.TryGetValue(id, out panel) && panel != null) return panel.gameObject;
            }

            return null;
        }

        /// <summary>The first object tagged with an id, or null.</summary>
        public static GameObject Object(ElementId id)
        {
            NsoloElement element = First(id);
            return element != null ? element.gameObject : null;
        }

        public static TMP_Text Label(ElementId id) => Component<TMP_Text>(id);
        public static Button Button(ElementId id) => Component<Button>(id);
        public static Slider Slider(ElementId id) => Component<Slider>(id);
        public static Toggle Toggle(ElementId id) => Component<Toggle>(id);
        public static Image Image(ElementId id) => Component<Image>(id);
        public static TMP_InputField InputField(ElementId id) => Component<TMP_InputField>(id);

        private static T Component<T>(ElementId id) where T : Component
        {
            NsoloElement element = First(id);
            return element != null ? element.GetComponent<T>() : null;
        }

        private static NsoloElement First(ElementId id)
        {
            EnsureScanned();

            if (elementsById.TryGetValue(id, out List<NsoloElement> list))
                foreach (NsoloElement element in list)
                    if (element != null) return element;

            return null;
        }

        /// <summary>
        /// Child object name that, when present under a tagged label, receives the figure on its
        /// own so it can be positioned and styled independently of the heading above it.
        /// </summary>
        public const string ValueChildName = "Value";

        /// <summary>Writes into a tagged label if one exists. Does nothing if none does.</summary>
        public static void SetText(ElementId id, string text)
        {
            TMP_Text label = Label(id);
            if (label != null) label.text = text;
        }

        /// <summary>
        /// Writes a value into a tagged label, keeping whatever heading it was authored with.
        ///
        /// A label with no heading — the game-over lines, whose captions are painted into the
        /// artwork — gets the value on its own, so one method covers both kinds. The separator is
        /// the caller's because it is a layout decision: a stat card stacks its number under its
        /// heading, a breakdown row reads across on one line.
        /// </summary>
        public static void SetValue(ElementId id, string value, string separator = "\n")
        {
            NsoloElement element = First(id);
            if (element == null) return;

            // A child called Value takes the figure on its own, leaving the heading untouched above
            // it. Appending both to one label meant the number could never be centred, sized or
            // coloured apart from its caption — they were one string, so there was nothing in the
            // scene to select. Adding the child is the whole opt-in; without one, nothing changes.
            Transform own = element.transform.Find(ValueChildName);
            if (own != null)
            {
                TMP_Text valueLabel = own.GetComponent<TMP_Text>();
                if (valueLabel != null)
                {
                    valueLabel.text = value;
                    return;
                }
            }

            TMP_Text label = element.GetComponent<TMP_Text>();
            if (label == null) return;

            label.text = string.IsNullOrEmpty(element.Caption)
                ? value
                : element.Caption + separator + value;
        }

        /// <summary>Switches a tagged object on or off if one exists.</summary>
        public static void SetVisible(ElementId id, bool visible)
        {
            GameObject go = Object(id);
            if (go != null) go.SetActive(visible);
        }

        /// <summary>Greys a tagged button out if one exists.</summary>
        public static void SetInteractable(ElementId id, bool value)
        {
            Button button = Button(id);
            if (button != null) Gate(button, value);
        }

        /// <summary>
        /// Switches a button on or off and makes that visible.
        ///
        /// The three buttons the game holds back until a choice has been made all go through here,
        /// because on real artwork <c>interactable = false</c> on its own changes nothing a player
        /// can see — the button looks live, does nothing, and reads as broken rather than as
        /// waiting. See <see cref="UIDisabledDim"/>.
        /// </summary>
        public static void Gate(Button button, bool enabled)
        {
            if (button == null) return;

            button.interactable = enabled;

            var dim = button.GetComponent<UIDisabledDim>();
            if (dim == null)
            {
                // Nothing to put back and nothing yet to dim: leave the button untouched rather
                // than adding a component to every button that is merely switched on.
                if (enabled) return;

                dim = button.gameObject.AddComponent<UIDisabledDim>();
            }

            dim.SetDimmed(!enabled);
        }

        // ── Controllers ───────────────────────────────────────────────────

        internal static MenuManager Menu
        {
            get
            {
                if (menu == null) menu = UnityEngine.Object.FindObjectOfType<MenuManager>();
                return menu;
            }
        }

        internal static GameController Game
        {
            get
            {
                if (game == null) game = UnityEngine.Object.FindObjectOfType<GameController>();
                return game;
            }
        }

        internal static OnlineFlowController Online
        {
            get
            {
                if (online == null) online = UnityEngine.Object.FindObjectOfType<OnlineFlowController>();
                return online;
            }
        }

        // ── What each id means ────────────────────────────────────────────

        /// <summary>
        /// Whether an id names something to *do* — the ids that hook a Button's onClick. The rest
        /// name something to read from or write to, and are looked up rather than bound.
        /// </summary>
        public static bool IsAction(ElementId id)
        {
            switch (id)
            {
                case ElementId.Back:
                case ElementId.MainMenuPlay:
                case ElementId.MainMenuPlayOnline:
                case ElementId.MainMenuProfile:
                case ElementId.MainMenuHowToPlay:
                case ElementId.MainMenuQuit:
                case ElementId.ModeVsComputer:
                case ElementId.ModeVsHuman:
                case ElementId.ModeOnline:
                case ElementId.ModeContinue:
                case ElementId.DifficultyEasy:
                case ElementId.DifficultyMedium:
                case ElementId.DifficultyHard:
                case ElementId.DifficultyStart:
                case ElementId.ProfileEditUsername:
                case ElementId.ProfileAvatarButton:
                case ElementId.ProfileCloseAvatarPicker:
                case ElementId.OnlineCreateRoom:
                case ElementId.OnlineJoinRoom:
                case ElementId.OnlineLeave:
                case ElementId.LobbyStart:
                case ElementId.LobbyCopyCode:
                case ElementId.LobbyShareCode:
                case ElementId.LobbyLeave:
                case ElementId.PauseResume:
                case ElementId.PauseRestart:
                case ElementId.PauseMainMenu:
                case ElementId.PauseHowToPlay:
                case ElementId.PauseResetTips:
                case ElementId.GameOverPlayAgain:
                case ElementId.GameOverMainMenu:
                case ElementId.TutorialStart:
                case ElementId.TutorialClose:
                case ElementId.HudPause:
                case ElementId.HudUndo:
                case ElementId.HudHint:
                case ElementId.HudAction:
                case ElementId.HudForfeit:
                case ElementId.HudConfirmFormation:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The method behind an action id, or null if the id names something else — or if the
        /// controller that owns it is missing from the scene, which is worth knowing about and so
        /// is reported by the caller rather than swallowed here.
        /// </summary>
        internal static System.Action ActionFor(ElementId id)
        {
            MenuManager m = Menu;
            GameController g = Game;
            OnlineFlowController o = Online;

            switch (id)
            {
                case ElementId.Back:                     return m == null ? null : (System.Action)m.OnScreenBackPressed;

                case ElementId.MainMenuPlay:             return m == null ? null : (System.Action)m.ShowMode;
                case ElementId.MainMenuPlayOnline:       return m == null ? null : (System.Action)m.ShowOnline;
                case ElementId.MainMenuProfile:          return m == null ? null : (System.Action)m.ShowProfile;
                case ElementId.MainMenuHowToPlay:        return m == null ? null : (System.Action)m.ShowTutorial;
                case ElementId.MainMenuQuit:             return m == null ? null : (System.Action)m.QuitGame;

                case ElementId.ModeVsComputer:           return m == null ? null : (System.Action)m.SelectVsComputer;
                case ElementId.ModeVsHuman:              return m == null ? null : (System.Action)m.SelectVsHuman;
                case ElementId.ModeOnline:               return m == null ? null : (System.Action)m.SelectOnline;
                case ElementId.ModeContinue:             return m == null ? null : (System.Action)m.ContinueFromMode;

                case ElementId.DifficultyEasy:           return m == null ? null : (System.Action)m.SelectEasy;
                case ElementId.DifficultyMedium:         return m == null ? null : (System.Action)m.SelectMedium;
                case ElementId.DifficultyHard:           return m == null ? null : (System.Action)m.SelectHard;
                case ElementId.DifficultyStart:          return m == null ? null : (System.Action)m.StartSelectedGame;

                case ElementId.ProfileEditUsername:      return () => ProfileManager.Instance?.BeginEditUsername();
                case ElementId.ProfileAvatarButton:      return () => ProfileManager.Instance?.OpenAvatarPicker();
                case ElementId.ProfileCloseAvatarPicker: return () => ProfileManager.Instance?.CloseAvatarPicker();

                case ElementId.OnlineCreateRoom:         return o == null ? null : (System.Action)o.CreateRoom;
                case ElementId.OnlineJoinRoom:           return o == null ? null : (System.Action)o.ShowJoinRoom;
                case ElementId.OnlineLeave:              return o == null ? null : (System.Action)o.LeaveOnline;

                case ElementId.LobbyStart:               return o == null ? null : (System.Action)o.StartGame;
                case ElementId.LobbyCopyCode:            return o == null ? null : (System.Action)o.CopyRoomCode;
                case ElementId.LobbyShareCode:           return o == null ? null : (System.Action)o.ShareRoomCode;
                case ElementId.LobbyLeave:               return o == null ? null : (System.Action)o.LeaveOnline;

                case ElementId.PauseResume:              return m == null ? null : (System.Action)m.ResumeGame;
                case ElementId.PauseRestart:             return m == null ? null : (System.Action)m.RestartGame;
                case ElementId.PauseMainMenu:            return m == null ? null : (System.Action)m.MainMenu;
                case ElementId.PauseHowToPlay:           return m == null ? null : (System.Action)m.ShowTutorialFromPause;
                case ElementId.PauseResetTips:           return m == null ? null : (System.Action)m.ResetTutorialTips;

                case ElementId.GameOverPlayAgain:        return m == null ? null : (System.Action)m.RestartGame;
                case ElementId.GameOverMainMenu:         return m == null ? null : (System.Action)m.MainMenu;

                case ElementId.TutorialStart:            return m == null ? null : (System.Action)m.ShowMode;
                case ElementId.TutorialClose:            return m == null ? null : (System.Action)m.CloseTutorial;

                case ElementId.HudPause:                 return m == null ? null : (System.Action)m.TogglePause;
                case ElementId.HudUndo:                  return g == null ? null : (System.Action)g.UndoLastMove;
                case ElementId.HudHint:                  return g == null ? null : (System.Action)g.RequestHint;
                case ElementId.HudAction:                return m == null ? null : (System.Action)m.OnActionButtonPressed;
                case ElementId.HudForfeit:               return m == null ? null : (System.Action)m.OnActionButtonPressed;
                case ElementId.HudConfirmFormation:      return g == null ? null : (System.Action)g.ConfirmFormationReady;

                default: return null;
            }
        }

        /// <summary>Which controller an action id needs, for a readable message when it is absent.</summary>
        internal static string OwnerOf(ElementId id)
        {
            switch (id)
            {
                case ElementId.HudUndo:
                case ElementId.HudHint:
                case ElementId.HudConfirmFormation:
                    return "GameController";
                case ElementId.OnlineCreateRoom:
                case ElementId.OnlineJoinRoom:
                case ElementId.OnlineLeave:
                case ElementId.LobbyStart:
                case ElementId.LobbyCopyCode:
                case ElementId.LobbyShareCode:
                case ElementId.LobbyLeave:
                    return "OnlineFlowController";
                default:
                    return "MenuManager";
            }
        }
    }
}
