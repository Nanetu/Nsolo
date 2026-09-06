using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    public class MenuManager : MonoBehaviour
    {
        [Header("Game")]
        [SerializeField] private GameController gameController;
        [Tooltip("The in-game HUD. Switched off whenever a menu is up.")]
        [SerializeField] private GameObject gameplayRoot;
        [Tooltip("Owns the online room flow. Auto-found if left empty.")]
        [SerializeField] private OnlineFlowController onlineFlow;

        // ── Screens ───────────────────────────────────────────────────────
        // Deliberately not serialized. Every one of these used to be an Inspector slot, and every
        // one of those slots still pointed at the screen the rebuild replaced — WelcomePanel,
        // ModePanel, DifficultyPanel, ProfilePanel, PausePanel, GameOverPanel, TutorialPanel. The
        // game only looked right because a startup pass quietly swapped each slot for the tagged
        // panel behind the Inspector's back, which meant the scene said one thing and the running
        // game did another, and a slot nobody had thought to re-drag was indistinguishable from one
        // that had been. They are resolved from the register now: the panel says which screen it is
        // (see NsoloPanel), and that is the only place the answer lives.
        private GameObject mainMenuPanel;
        private GameObject tutorialPanel;
        private GameObject modePanel;
        private GameObject difficultyPanel;
        private GameObject profilePanel;
        private GameObject pausePanel;
        private GameObject gameOverPanel;

        // ── Pause settings controls ───────────────────────────────────────
        // Held rather than looked up each time because listeners are attached to them once. Every
        // other control this manager drives is written through NsoloUI at the point of use, which
        // needs nothing kept at all.
        private Slider musicVolumeSlider;
        private Slider sfxVolumeSlider;
        private Toggle vibrationToggle;
        private Toggle tutorialTipsToggle;

        private static readonly string[] DifficultyNames = { "EASY", "MEDIUM", "HARD" };

        // Fired whenever the SFX slider changes so PitStoneVisualizer can update its volume
        public static event System.Action<float> SFXVolumeChanged;

        private const int ModeVsComputer = 0;
        private const int ModeVsHuman = 1;

        /// <summary>
        /// The mode screen's third card. Online used to be a button on the main menu, which sat
        /// oddly beside PLAY — both promised a game, and only one of them said which kind. It is a
        /// card here instead, so the screen that asks "who are you playing?" answers it completely.
        ///
        /// It is the one card that does not start a game on CONTINUE: there is no game until a room
        /// exists and somebody else is in it, so it opens Create/Join rather than a board.
        /// </summary>
        private const int ModeOnline = 2;

        private bool isPaused;
        private bool subscribedToGameOver;

        /// <summary>Whether the tutorial was opened over a game, and so has a game to go back to.</summary>
        private bool tutorialOpenedFromPause;
        private int selectedDifficulty = -1;
        private int selectedMode = -1;

        private void Awake()
        {
            ResolveGameController();
        }

        private void OnEnable()
        {
            ResolveGameController();
            SubscribeToGameOver();
            TutorialCoach.TipsSettingChanged += RefreshTutorialToggle;
        }

        private void OnDisable()
        {
            TutorialCoach.TipsSettingChanged -= RefreshTutorialToggle;

            if (gameController != null && subscribedToGameOver)
            {
                gameController.GameOver -= HandleGameOver;
                subscribedToGameOver = false;
            }
        }

        private void Start()
        {
            // Before anything reads a screen. Start rather than Awake because the register is built
            // after every Awake has run — see NsoloUI.Bootstrap for why it cannot be earlier.
            ResolveScreens();

            InitSettingsSliders();
            ShowMainMenu();
        }

        // ── Screens ───────────────────────────────────────────────────────

        /// <summary>
        /// Looks every screen up by what it is.
        ///
        /// This replaces the older arrangement, where each screen was an Inspector slot that a
        /// startup pass then overwrote with the tagged panel if it found one. That was scaffolding
        /// for a rebuild happening one screen at a time: it let a new panel take over without the
        /// old one being deleted first. The rebuild is done — every screen is tagged — and the
        /// scaffolding had become the problem, because the slots still held the old panels and
        /// nothing in the Inspector said they were being ignored.
        ///
        /// A screen that has not been tagged is reported rather than silently absent. The failure
        /// it causes otherwise is a menu button that opens nothing, which reads as the button being
        /// broken.
        /// </summary>
        private void ResolveScreens()
        {
            mainMenuPanel   = RequireScreen(PanelId.MainMenu);
            tutorialPanel   = RequireScreen(PanelId.Tutorial);
            modePanel       = RequireScreen(PanelId.Mode);
            difficultyPanel = RequireScreen(PanelId.Difficulty);
            profilePanel    = RequireScreen(PanelId.Profile);
            pausePanel      = RequireScreen(PanelId.Pause);
            gameOverPanel   = RequireScreen(PanelId.GameOver);

            musicVolumeSlider  = NsoloUI.Slider(ElementId.PauseMusicSlider);
            sfxVolumeSlider    = NsoloUI.Slider(ElementId.PauseSfxSlider);
            vibrationToggle    = NsoloUI.Toggle(ElementId.PauseVibrationToggle);
            tutorialTipsToggle = NsoloUI.Toggle(ElementId.PauseTipsToggle);

            // Built under the same canvas the menus live on, so it shares their sorting order and
            // needs no camera or layer of its own. The main menu is used to find that canvas rather
            // than a slot, for the reason the screens above are: a slot here would be one more
            // thing that can point at the wrong object without saying so.
            MenuBackdrop.Ensure(mainMenuPanel != null ? mainMenuPanel.transform.parent : null);
        }

        private static GameObject RequireScreen(PanelId id)
        {
            GameObject panel = NsoloUI.Panel(id);
            if (panel == null)
                Debug.LogError($"MenuManager: no panel in the scene is tagged '{id}'. That screen " +
                               "cannot open. Add an NsoloPanel to it and pick the id.");
            return panel;
        }

        // ── Main menu ─────────────────────────────────────────────────────

        /// <summary>
        /// Goes to the menu root.
        ///
        /// Kept as its own name because half the game's BACK buttons were wired to it by hand and
        /// a UnityEvent remembers the method by name — removing it would break those silently. It
        /// and <see cref="ShowMainMenu"/> have been the same screen since the welcome screen was
        /// replaced.
        /// </summary>
        public void ShowWelcome() => ShowMainMenu();

        /// <summary>Opens the main menu, whatever the player was doing before.</summary>
        public void ShowMainMenu()
        {
            PrepareMenuReturn();
            RefreshWelcomeUsername();
            ShowOnly(mainMenuPanel);
            AudioManager.StartMenuMusic();
        }

        private void PrepareMenuReturn()
        {
            // Dropped before the timescale is reset: a tip left on screen would otherwise restore
            // whatever scale it captured and freeze the menu behind it.
            TutorialCoach.Instance?.ForceHide();
            // Leaving mid-handover would otherwise strand the card on screen with the clock frozen.
            PassDeviceModal.Instance?.ForceHide();

            // Leaving an online game has to actually leave the room, or the opponent is left staring
            // at a board waiting for a move from somebody who has gone back to the menu.
            ResolveOnlineFlow();
            onlineFlow?.CloseAndCleanup();

            Time.timeScale = 1f;
            isPaused = false;
            ResolveGameController();

            // Ends the game rather than resuming it. This used to be SetPaused(false), which told a
            // game the player had just walked out on to carry on running: the sowing coroutine kept
            // stepping behind the menu and kept playing stone sounds, which is what "I can still
            // hear the game from the main menu" was. There is no way back to this game from here —
            // every route out of the menu starts a new one — so stopping it is the honest call.
            gameController?.AbandonGame();

            SetGameplayVisible(false);
            // Back to the standard chrome, so the menu is never sitting on two-player art.
            ApplyGameplayChrome(GameMode.VersusComputer);
        }

        /// <summary>Re-reads the saved username so a rename on the profile page shows up here.</summary>
        public void RefreshWelcomeUsername()
        {
            string name = ProfileManager.Instance?.Username;
            NsoloUI.SetText(ElementId.MainMenuUsernameLabel,
                string.IsNullOrWhiteSpace(name) ? "Player" : name);
        }

        public void ShowTutorial()
        {
            tutorialOpenedFromPause = false;
            NsoloUI.SetVisible(ElementId.TutorialStart, true);
            SetGameplayVisible(false);
            ShowOnly(tutorialPanel);
        }

        /// <summary>
        /// Opens the rules from the pause menu, over the game rather than instead of it.
        ///
        /// <see cref="ShowTutorial"/> cannot be used here. It hides the gameplay root and calls
        /// ShowOnly, which takes the pause panel down with everything else, and the tutorial's Back
        /// button goes to the welcome screen — so a player who tapped it mid-game would lose that
        /// game without ever being asked. This keeps the board loaded and the clock frozen
        /// underneath, and remembers where to go back to.
        /// </summary>
        public void ShowTutorialFromPause()
        {
            tutorialOpenedFromPause = true;

            // START sends the player to the mode panel to begin a game. Offered to somebody who is
            // already in one, it is a second way to lose it by accident — so it is not offered.
            NsoloUI.SetVisible(ElementId.TutorialStart, false);

            SetPanelActive(pausePanel, false);
            SetPanelActive(tutorialPanel, true);
            AudioManager.Click();
            Haptics.Light();
        }

        /// <summary>
        /// Leaves the tutorial for wherever it was opened from. This is what
        /// <see cref="ElementId.TutorialClose"/> binds to, rather than the plain BACK — from the
        /// main menu it behaves exactly as BACK would, and over a paused game it does not.
        /// </summary>
        public void CloseTutorial()
        {
            SetPanelActive(tutorialPanel, false);

            if (tutorialOpenedFromPause)
            {
                tutorialOpenedFromPause = false;
                NsoloUI.SetVisible(ElementId.TutorialStart, true);
                SetPanelActive(pausePanel, true);
                return;
            }

            ShowWelcome();
        }

        public void ShowProfile()
        {
            ProfileManager.Instance?.RefreshProfileUI();
            SetGameplayVisible(false);
            ShowOnly(profilePanel);
        }

        /// <summary>
        /// Closes the app. Wire this to the welcome screen's Quit button. Application.Quit is a
        /// no-op inside the editor, so play mode is stopped explicitly to keep the button testable.
        /// </summary>
        public void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ── Mode Panel ────────────────────────────────────────────────────

        /// <summary>
        /// Wire the welcome screen's Play button here. It used to go straight to the difficulty
        /// panel, which is now one step further in behind "vs Computer".
        /// </summary>
        public void ShowMode()
        {
            SetGameplayVisible(false);
            ShowOnly(modePanel);
            ResetModeSelection();
        }

        /// <summary>Mode panel: the vs Computer card. Selects, it does not navigate.</summary>
        public void SelectVsComputer() => ApplyModeSelection(ModeVsComputer);

        /// <summary>Mode panel: the vs Human card.</summary>
        public void SelectVsHuman() => ApplyModeSelection(ModeVsHuman);

        /// <summary>Mode panel: the Play Online card. Selects, it does not connect.</summary>
        public void SelectOnline()
        {
            // Online is closed until the player has finished one game offline. Somebody meeting
            // Nsolo for the first time in a room with a stranger learns it at that stranger's
            // expense — and the in-game coach is switched off online precisely so it cannot stop
            // the clock, so there is nothing there to learn from either. One game against the
            // computer, and this never appears again.
            if (!OnlineUnlocked())
            {
                ExplainOnlineLocked();
                return;
            }

            ApplyModeSelection(ModeOnline);

            // Start reaching Photon a screen early. Picking the card is the first moment we know
            // the player is heading online, and getting to a lobby is around two seconds of the
            // roughly three that Create Room takes — so spending it while they are still looking
            // for CONTINUE is two seconds they never see. Prewarm is idempotent and returns at once
            // if a connection is already up, so tapping the card repeatedly costs nothing.
            //
            // Still nothing for a player who never comes here: no connection is opened until this
            // card is chosen.
            ResolveOnlineFlow();
            onlineFlow?.PrewarmConnection();
        }

        /// <summary>
        /// Mode panel: CONTINUE. vs Computer goes on to pick a difficulty; vs Human has no
        /// difficulty to pick, so it starts the game directly.
        /// </summary>
        public void ContinueFromMode()
        {
            switch (selectedMode)
            {
                case ModeVsComputer: ShowDifficulty(); break;
                case ModeVsHuman: StartHotSeatGame(); break;
                // Checked again rather than trusted: the card cannot be selected while online is
                // locked, but CONTINUE is a separate button and this is the one that commits.
                case ModeOnline when OnlineUnlocked(): ShowOnline(); break;
                case ModeOnline: ExplainOnlineLocked(); break;
                // Nothing picked. The button is held non-interactable until something is, so this
                // is only reachable if that slot was never wired up.
                default: return;
            }
        }

        /// <summary>
        /// Whether online play is available yet. See ProfileManager.HasPlayedOffline for the
        /// reasoning; with no profile loaded this stays open rather than locking a player out on
        /// the strength of a component that failed to wake up.
        /// </summary>
        private static bool OnlineUnlocked()
        {
            ProfileManager profile = ProfileManager.Instance;
            return profile == null || profile.HasPlayedOffline;
        }

        /// <summary>
        /// Says why online is not available, and offers the thing that opens it.
        ///
        /// A dialog rather than a greyed-out card. A dimmed control that will not respond reads as
        /// broken — the player cannot tell a rule from a bug — and the rule here is easy to agree
        /// with once it is stated, so it is stated.
        /// </summary>
        private void ExplainOnlineLocked()
        {
            AudioManager.Click();
            Haptics.Light();

            if (GameModals.Instance == null)
            {
                // No dialog in the scene to explain with. Sending them to the difficulty screen
                // unannounced would be worse than the card simply not taking, so it does not take.
                Debug.LogWarning("MenuManager: online is locked and there is no GameModals in the " +
                                 "scene to say so, so the card does nothing.");
                return;
            }

            GameModals.Instance.ShowDialog(
                "Play one game first",
                "Online matches are against a real person, and they'll be waiting on your moves."
                + "\n\nFinish one game against the computer and online opens up.",
                new GameModals.Choice("NOT NOW", null),
                new GameModals.Choice("PLAY THE COMPUTER", () =>
                {
                    ApplyModeSelection(ModeVsComputer);
                    ShowDifficulty();
                }));
        }

        private void ApplyModeSelection(int selection)
        {
            selectedMode = selection;
            NsoloUI.SetVisible(ElementId.ModeVsComputerHighlight, selection == ModeVsComputer);
            NsoloUI.SetVisible(ElementId.ModeVsHumanHighlight,    selection == ModeVsHuman);
            NsoloUI.SetVisible(ElementId.ModeOnlineHighlight,     selection == ModeOnline);
            NsoloUI.SetInteractable(ElementId.ModeContinue, true);
            AudioManager.Click();
            Haptics.Light();
        }

        private void ResetModeSelection()
        {
            selectedMode = -1;
            NsoloUI.SetVisible(ElementId.ModeVsComputerHighlight, false);
            NsoloUI.SetVisible(ElementId.ModeVsHumanHighlight,    false);
            NsoloUI.SetVisible(ElementId.ModeOnlineHighlight,     false);
            NsoloUI.SetInteractable(ElementId.ModeContinue, false);
        }

        // ── Online ────────────────────────────────────────────────────────

        /// <summary>
        /// Opens the Create Room / Join Room screen. Wired straight to the welcome screen's Play
        /// Online button.
        ///
        /// Online deliberately does not sit behind the mode panel with the other two. That panel
        /// exists to pick who you are playing and then commit — a shape that fits vs Computer and
        /// vs Human because both start a game the moment you confirm. Online cannot: there is no
        /// game until a room exists and somebody else is in it, so it goes to a screen of its own
        /// rather than pretending to be a third card in a chooser it does not behave like.
        /// </summary>
        public void ShowOnline()
        {
            SetGameplayVisible(false);
            ResolveOnlineFlow();

            if (onlineFlow == null)
            {
                Debug.LogError("MenuManager: no OnlineFlowController in the scene.");
                return;
            }

            onlineFlow.ShowOnlinePanel();
        }

        /// <summary>
        /// Called by <see cref="OnlineFlowController"/> once a room is full and a match exists.
        /// The match is handed straight to the game — the menu does not keep a reference to it.
        /// </summary>
        public void StartOnlineGame(NsoloGame.Net.NetworkMatch match)
        {
            Time.timeScale = 1f;
            isPaused = false;
            SetGameplayVisible(true);
            ResolveGameController();
            SubscribeToGameOver();
            HideAllPanels();
            ApplyGameplayChrome(GameMode.Online);

            if (gameController == null)
            {
                Debug.LogError("MenuManager: GameController not assigned.");
                return;
            }

            gameController.StartNewOnlineGame(match);
        }

        /// <summary>
        /// The same handover as <see cref="StartOnlineGame"/>, for a match this device is walking
        /// back into rather than beginning. Separate only because the controller has two entry
        /// points and this one must not deal a new board — everything the menu itself has to do is
        /// identical, since a rejoined game needs the gameplay view and the online chrome exactly
        /// as a fresh one does.
        /// </summary>
        public void ResumeOnlineGame(NsoloGame.Net.NetworkMatch match)
        {
            Time.timeScale = 1f;
            isPaused = false;
            SetGameplayVisible(true);
            ResolveGameController();
            SubscribeToGameOver();
            HideAllPanels();
            ApplyGameplayChrome(GameMode.Online);

            if (gameController == null)
            {
                Debug.LogError("MenuManager: GameController not assigned.");
                return;
            }

            gameController.ResumeOnlineGame(match);
        }

        /// <summary>
        /// Clears the menu panels without touching the online screens, which
        /// <see cref="OnlineFlowController"/> owns and toggles itself.
        /// </summary>
        public void HideAllPanelsForOnline()
        {
            HideAllPanels();
            SetGameplayVisible(false);
        }

        private void ResolveOnlineFlow()
        {
            if (onlineFlow == null) onlineFlow = FindObjectOfType<OnlineFlowController>();
        }

        // ── Difficulty Panel ──────────────────────────────────────────────

        public void ShowDifficulty()
        {
            SetGameplayVisible(false);
            ShowOnly(difficultyPanel);
            ResetDifficultySelection();
        }

        public void SelectEasy()   => ApplyDifficultySelection(0);
        public void SelectMedium() => ApplyDifficultySelection(1);
        public void SelectHard()   => ApplyDifficultySelection(2);

        private void ApplyDifficultySelection(int difficulty)
        {
            selectedDifficulty = difficulty;
            NsoloUI.SetVisible(ElementId.DifficultyEasyHighlight,   difficulty == 0);
            NsoloUI.SetVisible(ElementId.DifficultyMediumHighlight, difficulty == 1);
            NsoloUI.SetVisible(ElementId.DifficultyHardHighlight,   difficulty == 2);
            NsoloUI.SetInteractable(ElementId.DifficultyStart, true);
        }

        private void ResetDifficultySelection()
        {
            selectedDifficulty = -1;
            NsoloUI.SetVisible(ElementId.DifficultyEasyHighlight,   false);
            NsoloUI.SetVisible(ElementId.DifficultyMediumHighlight, false);
            NsoloUI.SetVisible(ElementId.DifficultyHardHighlight,   false);
            NsoloUI.SetInteractable(ElementId.DifficultyStart, false);
        }

        public void StartSelectedGame()
        {
            if (selectedDifficulty < 0) return;
            StartGame(selectedDifficulty);
        }

        public void BackToWelcome() => ShowWelcome();

        // ── Gameplay ──────────────────────────────────────────────────────

        public void StartGame(int difficulty)
        {
            Time.timeScale = 1f;
            isPaused = false;
            SetGameplayVisible(true);
            ResolveGameController();
            SubscribeToGameOver();
            HideAllPanels();
            ApplyGameplayChrome(GameMode.VersusComputer);

            if (gameController == null)
            {
                Debug.LogError("MenuManager: GameController not assigned.");
                return;
            }

            gameController.StartNewGame(difficulty);
        }

        /// <summary>Starts a local two-player game on the human background, with no Undo.</summary>
        public void StartHotSeatGame()
        {
            Time.timeScale = 1f;
            isPaused = false;
            SetGameplayVisible(true);
            ResolveGameController();
            SubscribeToGameOver();
            HideAllPanels();
            ApplyGameplayChrome(GameMode.VersusHuman);

            if (gameController == null)
            {
                Debug.LogError("MenuManager: GameController not assigned.");
                return;
            }

            gameController.StartNewHotSeatGame();
        }

        /// <summary>
        /// Sets up the board chrome for the mode being played, which now means one thing: whether
        /// there is an Undo button.
        ///
        /// There used to be three background images here, one per mode, and this swapped between
        /// them. That made sense while each background had its own captions and its own Undo pill
        /// painted into the artwork — the picture *was* the HUD, so a different mode needed a
        /// different picture. It stopped making sense when the HUD became real objects drawn over
        /// one background: the rebuilt art is the only background the new HUD is laid out against,
        /// so switching to either of the older pictures put the new HUD on top of a layout it was
        /// never measured for. Both two-player modes did exactly that, which is why they were the
        /// ones that looked wrong.
        ///
        /// The two old backgrounds are still in the scene and are simply never touched.
        ///
        /// Undo survives the change because it is a rule, not a picture: you cannot take a move
        /// back from an opponent who has already seen it, so neither two-player mode has one.
        /// </summary>
        private void ApplyGameplayChrome(GameMode mode)
        {
            NsoloUI.SetVisible(ElementId.HudUndo, mode == GameMode.VersusComputer);

            // The in-game coach is held back online. A modal tip stops the clock, and stopping the
            // clock on one of two phones is how a game ends up with two different elapsed times —
            // see TutorialCoach.Suppressed. This is the one call every game start passes through,
            // including the trip back to the menu, so it is where the mode is known.
            TutorialCoach.SetSuppressed(mode == GameMode.Online);
        }

        /// <summary>
        /// Hook for the bottom pill, which is START during the formation phase and HINT afterwards.
        /// GameController owns the decision since it is the one tracking the phase.
        /// </summary>
        public void OnActionButtonPressed()
        {
            ResolveGameController();
            gameController?.OnActionButtonPressed();
        }

        public void TogglePause()
        {
            if (gameController == null) return;

            if (gameController.CurrentState == GameState.GameOver)
            {
                // Normally there is nothing to pause once the game is over, and the button does
                // nothing. The exception is an online match the opponent walked out of: the player
                // may have dismissed the dialog to read the final position, and this is the only
                // control still on that screen — so it hands the dialog back rather than leaving
                // them stranded on a board they cannot leave.
                ResolveOnlineFlow();
                if (onlineFlow != null && onlineFlow.ReshowMatchEndedDialog())
                {
                    AudioManager.Click();
                    Haptics.Light();
                }

                return;
            }

            isPaused = !isPaused;

            // Online play deliberately does not stop the clock. There is nothing to pause on the
            // other device, so freezing time here would only stop this one drawing — moves would
            // keep arriving and queue up unseen behind the panel, and the game would lurch when it
            // came back. The panel still opens for the settings, and GameController still ignores
            // taps while it is up; the game simply carries on underneath it.
            if (gameController.CurrentMode != GameMode.Online)
                Time.timeScale = isPaused ? 0f : 1f;

            gameController.SetPaused(isPaused);
            SetPanelActive(pausePanel, isPaused);

            // The coach can switch itself off mid-game once the last tip has been seen, and this
            // is the first moment the control is visible again — so it re-reads the setting rather
            // than showing whatever state it was left in.
            if (isPaused) RefreshTutorialToggle();

            AudioManager.Click();
            Haptics.Light();
            if (isPaused) TutorialCoach.Show(TutorialTip.PauseButton);
        }

        public void ResumeGame()
        {
            if (!isPaused) return;
            isPaused = false;
            Time.timeScale = 1f;
            gameController?.SetPaused(false);
            SetPanelActive(pausePanel, false);
        }

        /// <summary>
        /// Replays the game that was just played — same mode, same difficulty — without going back
        /// through the menus. Wired to Restart on both the pause and the game-over panel.
        ///
        /// The game-over button used to open the difficulty panel instead, which is a vs-Computer
        /// screen: finishing a two-player game and pressing Restart put the player in front of a
        /// difficulty list they had no use for, two taps away from the two-player game they asked
        /// for. GameController already holds the mode and difficulty, so replaying is a matter of
        /// asking it to set the same game up again.
        /// </summary>
        public void RestartGame()
        {
            TutorialCoach.Instance?.ForceHide();
            Time.timeScale = 1f;
            isPaused = false;
            HideAllPanels();
            SetGameplayVisible(true);
            ResolveGameController();
            SubscribeToGameOver();

            if (gameController == null)
            {
                Debug.LogError("MenuManager: GameController not assigned.");
                return;
            }

            // There is no rematch yet: replaying an online game means agreeing with the opponent to
            // play another, which needs a message and a screen that do not exist in this pass.
            // Restarting into online mode with no live match would deal a board nobody is playing
            // on, so this goes back to the menu instead — where Play Online is two taps away.
            if (gameController.CurrentMode == GameMode.Online)
            {
                ShowWelcome();
                return;
            }

            // Restarting from the game-over panel can be the first thing to run after a mode
            // change, so the background art and the Undo pill are re-applied rather than assumed
            // to be left over from the game that just ended.
            ApplyGameplayChrome(gameController.CurrentMode);
            gameController.RestartGame();
        }

        public void MainMenu() => ShowWelcome();

        // ── Pause Settings ────────────────────────────────────────────────

        private void InitSettingsSliders()
        {
            float music = PlayerPrefs.GetFloat(AudioManager.MusicVolumeKey, 0.7f);
            float sfx   = PlayerPrefs.GetFloat(AudioManager.SfxVolumeKey,   0.85f);
            bool vib    = Haptics.Enabled;

            // AudioListener.volume is deliberately left alone. It is a master volume, so driving
            // it from the music slider also quietened every sound effect — AudioManager now gives
            // music and SFX a source each, and applies the saved levels itself on Awake.

            if (musicVolumeSlider != null)
            {
                musicVolumeSlider.value = music;
                musicVolumeSlider.onValueChanged.AddListener(OnMusicVolumeChanged);
            }
            if (sfxVolumeSlider != null)
            {
                sfxVolumeSlider.value = sfx;
                sfxVolumeSlider.onValueChanged.AddListener(OnSFXVolumeChanged);
            }
            if (vibrationToggle != null)
            {
                vibrationToggle.isOn = vib;
                vibrationToggle.onValueChanged.AddListener(OnVibrationToggled);
            }
            if (tutorialTipsToggle != null)
            {
                tutorialTipsToggle.isOn = TutorialCoach.Instance?.TipsEnabled ?? false;
                tutorialTipsToggle.onValueChanged.AddListener(OnTutorialTipsToggled);
            }

            UpdateSettingsLabels(music, sfx);
            RefreshTutorialToggle();
        }

        public void OnMusicVolumeChanged(float value)
        {
            AudioManager.Instance?.SetMusicVolume(value);
            NsoloUI.SetText(ElementId.PauseMusicLabel, Percent(value));
        }

        public void OnSFXVolumeChanged(float value)
        {
            AudioManager.Instance?.SetSfxVolume(value);
            NsoloUI.SetText(ElementId.PauseSfxLabel, Percent(value));

            // Board audio lives on its own AudioSource inside PitStoneVisualizer, which listens
            // for this rather than being reachable from here.
            SFXVolumeChanged?.Invoke(value);
        }

        /// <summary>
        /// Routed through Haptics rather than writing the pref directly, so its cached copy of the
        /// setting cannot go stale and keep buzzing after the switch is turned off.
        /// </summary>
        public void OnVibrationToggled(bool value)
        {
            Haptics.SetEnabled(value);
        }

        /// <summary>Optional hook for a "Show tips again" button in the pause settings.</summary>
        public void ResetTutorialTips()
        {
            TutorialCoach.Instance?.ResetAllTips();
        }

        // ── Tutorial tips setting ─────────────────────────────────────────
        // Tips are on by default and stay on until the player turns them off. Three shapes are
        // offered so the pause-menu control can be whatever suits the art: a Toggle (wire to
        // OnTutorialTipsToggled), a plain Button that flips it (ToggleTutorialTips), or a button
        // that only ever switches them off (DisableTutorialTips).

        /// <summary>Wire to a Toggle's On Value Changed, like the vibration switch.</summary>
        public void OnTutorialTipsToggled(bool value)
        {
            TutorialCoach.Instance?.SetTipsEnabled(value);
            RefreshTutorialToggle();
            Haptics.Light();
        }

        /// <summary>Wire to a plain Button to flip tips on and off.</summary>
        public void ToggleTutorialTips()
        {
            TutorialCoach coach = TutorialCoach.Instance;
            if (coach == null) return;
            OnTutorialTipsToggled(!coach.TipsEnabled);
        }

        /// <summary>Wire to a one-way "Disable tutorial" button.</summary>
        public void DisableTutorialTips()
        {
            OnTutorialTipsToggled(false);
        }

        /// <summary>
        /// Keeps the optional Toggle in step with the saved setting. Safe to call with nothing
        /// assigned, which is the case until the pause menu gets its control.
        ///
        /// The ON/OFF caption is deliberately not handled here: a switch built by duplicating the
        /// vibration row carries its own VibrationSwitch, which listens to the same Toggle and
        /// already moves the knob and rewrites the caption. Setting the text from here as well
        /// would just be two things fighting over one label.
        /// </summary>
        private void RefreshTutorialToggle()
        {
            if (tutorialTipsToggle == null) return;

            bool on = TutorialCoach.Instance?.TipsEnabled ?? false;
            if (tutorialTipsToggle.isOn == on) return;

            // Set without firing our own listener, or flipping it would recurse back through here.
            // VibrationSwitch keeps its listener attached, so the knob still animates.
            tutorialTipsToggle.onValueChanged.RemoveListener(OnTutorialTipsToggled);
            tutorialTipsToggle.isOn = on;
            tutorialTipsToggle.onValueChanged.AddListener(OnTutorialTipsToggled);
        }

        private void UpdateSettingsLabels(float music, float sfx)
        {
            NsoloUI.SetText(ElementId.PauseMusicLabel, Percent(music));
            NsoloUI.SetText(ElementId.PauseSfxLabel, Percent(sfx));
        }

        /// <summary>A slider's 0..1 value as the pause menu writes it.</summary>
        private static string Percent(float value) => $"[{Mathf.RoundToInt(value * 100)}%]";

        // ── Game Over ─────────────────────────────────────────────────────

        private void HandleGameOver(int winner, int playerCaptured, int aiCaptured)
        {
            Time.timeScale = 1f;
            isPaused = false;
            SetPanelActive(pausePanel, false);
            SetGameplayVisible(true);

            GameMode currentMode = gameController != null ? gameController.CurrentMode : GameMode.VersusComputer;
            bool hotSeat = currentMode == GameMode.VersusHuman;
            bool online = currentMode == GameMode.Online;

            // Who "you" is differs by mode. Against the computer it is player 1; online it is
            // whichever seat this device was given, which is player 2 for whoever joined; in
            // hot-seat it is nobody, because both sides are people in the same room.
            bool playerWon = online
                ? winner == gameController.OnlineLocalPlayer
                : winner == 1;

            // Every line on this screen goes through the register. The old panel's labels used to
            // be here as slots of their own, and since the two panels have different labels for
            // different figures, half of what this method wrote went to a screen nobody could see.
            NsoloUI.SetText(ElementId.GameOverTitleLabel, hotSeat
                ? $"PLAYER {winner} WINS"
                : (playerWon ? "VICTORY" : "DEFEAT"));

            NsoloUI.SetValue(ElementId.GameOverPlayerScoreLabel, playerCaptured.ToString());
            NsoloUI.SetValue(ElementId.GameOverOpponentScoreLabel, aiCaptured.ToString());

            string difficultyLine;
            if (hotSeat)
            {
                difficultyLine = "2 PLAYER";
            }
            else if (online)
            {
                difficultyLine = "ONLINE";
            }
            else
            {
                int d = gameController != null ? gameController.CurrentDifficulty : -1;
                difficultyLine = d >= 0 && d < DifficultyNames.Length ? DifficultyNames[d] : "--";
            }
            NsoloUI.SetValue(ElementId.GameOverDifficultyLabel, difficultyLine);

            float seconds = gameController != null ? gameController.LastGameSeconds : 0f;
            NsoloUI.SetValue(ElementId.GameOverTimeLabel,
                $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}");

            // The lifetime victory count is a record of games against the AI. Neither two-player
            // results nor online ones are filed into it, so showing it here would imply otherwise.
            NsoloUI.SetValue(ElementId.GameOverVictoryCountLabel, hotSeat || online
                ? "--"
                : (ProfileManager.Instance?.GamesWon ?? 0).ToString());

            {
                // Online, the two score arguments are still player 1's and player 2's stones, so
                // they have to be read from this device's seat rather than assumed to be "mine,
                // theirs" — otherwise the joiner sees their score and their opponent's swapped.
                int mine = online && gameController.OnlineLocalPlayer == 2 ? aiCaptured : playerCaptured;
                int theirs = online && gameController.OnlineLocalPlayer == 2 ? playerCaptured : aiCaptured;

                // "OPP" rather than "Computer" or "Opponent": this label is a 200px box at font 30
                // with autosizing off, so a longer word wraps onto a second line and overflows it.
                NsoloUI.SetValue(ElementId.GameOverSummaryLabel, hotSeat
                    ? $"P1: {playerCaptured} - P2: {aiCaptured}"
                    : (playerWon
                        ? $"You: {mine} - {theirs}"
                        : $"OPP: {theirs} - {mine}"));
            }

            NsoloUI.SetValue(ElementId.GameOverCapturesLabel,
                gameController != null ? gameController.LastGameCaptures.ToString() : "0");
            NsoloUI.SetValue(ElementId.GameOverRelayLabel,
                gameController != null ? gameController.LastGameLongestRelay.ToString() : "0");

            SetPanelActive(gameOverPanel, true);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// The screen currently being shown, so a transition can tell which way the player is
        /// travelling. Only ShowOnly maintains it: the panels that appear over a game rather than
        /// instead of one — pause, game over — do not move the player through the menu.
        /// </summary>
        private GameObject currentPanel;

        /// <summary>
        /// How deep a screen sits in the menu. Only the relative order matters — it is what tells a
        /// transition whether the player is going further in or coming back out, so both panels in
        /// a hand-off move the same way and the direction carries meaning.
        /// </summary>
        /// <summary>
        /// What the Android back button means on whatever is currently on screen.
        ///
        /// Ordered most-specific first, and built around one rule: <b>back never destroys a room.</b>
        /// In the lobby it backgrounds the app rather than leaving, so a player can go and paste
        /// their code somewhere and come back to it. Only the on-screen BACK leaves, and it asks.
        /// </summary>
        public void HandleBackPressed()
        {
            // A modal that is up owns the press — it decides whether back means anything.
            if (GameModals.Instance != null && GameModals.Instance.IsBlocking)
            {
                GameModals.Instance.BackRequested();
                return;
            }

            ResolveOnlineFlow();

            // The lobby. Never leaves, never asks — this is the "step out and share the code" case.
            if (onlineFlow != null && onlineFlow.IsLobbyVisible)
            {
                AndroidBackButton.SendAppToBackground();
                return;
            }

            // Create/Join holds no room yet unless a create is in flight, so LeaveOnline is safe
            // here: it asks only if there is something to lose.
            if (onlineFlow != null && onlineFlow.IsOnlineVisible)
            {
                onlineFlow.LeaveOnline();
                return;
            }

            // Mid-game, back pauses rather than navigating. Quitting a match is a decision, not a
            // reflex, and the pause panel is where that decision already lives.
            if (gameplayRoot != null && gameplayRoot.activeSelf)
            {
                if (isPaused) ResumeGame();
                else TogglePause();
                return;
            }

            // A submenu steps back to the menu root.
            if (DepthOf(currentPanel) > 0)
            {
                ShowWelcome();
                return;
            }

            // Already at the root. Background rather than quit: the player can get back in one tap,
            // and nothing is lost if they meant it as "hide this for a second".
            AndroidBackButton.SendAppToBackground();
        }

        /// <summary>
        /// What an on-screen BACK button means, wherever it is. Every BACK in the game can be
        /// wired to this one method, which is why <see cref="ElementId.Back"/> is a single entry
        /// rather than one per screen.
        ///
        /// Deliberately not <see cref="HandleBackPressed"/>. That one answers the Android system
        /// button, where the rule is that back must never destroy a room — in the lobby it
        /// backgrounds the app instead of leaving, and mid-game it pauses. A BACK the player can
        /// see and chose to press is the opposite case: it is the control that *does* leave, and
        /// leaving asks first.
        /// </summary>
        public void OnScreenBackPressed()
        {
            // A dialog that is up owns the press, exactly as it does for the system button.
            if (GameModals.Instance != null && GameModals.Instance.IsBlocking)
            {
                GameModals.Instance.BackRequested();
                return;
            }

            ResolveOnlineFlow();

            // Both online screens hold, or may hold, a room. LeaveOnline is the one path that ends
            // it properly, and it puts up a confirm when there is something to lose.
            if (onlineFlow != null && (onlineFlow.IsLobbyVisible || onlineFlow.IsOnlineVisible))
            {
                onlineFlow.LeaveOnline();
                return;
            }

            // The rules, which may be open over a paused game. CloseTutorial is the only thing that
            // knows to put the pause screen back rather than dropping the player at the menu.
            if (IsShowing(tutorialPanel))
            {
                CloseTutorial();
                return;
            }

            if (IsShowing(pausePanel))
            {
                ResumeGame();
                return;
            }

            if (IsShowing(gameOverPanel))
            {
                MainMenu();
                return;
            }

            // Difficulty is the one screen two steps in: back from it is the mode picker, not the
            // menu root. Everything else at depth one goes home.
            if (IsShowing(difficultyPanel))
            {
                ShowMode();
                return;
            }

            if (DepthOf(currentPanel) > 0)
            {
                ShowWelcome();
                return;
            }

            // On the board with no panel up. Nothing to go back to that would not mean abandoning
            // the game, so this offers the screen where that choice already lives.
            if (gameplayRoot != null && gameplayRoot.activeSelf)
            {
                if (!isPaused) TogglePause();
                return;
            }

            ShowWelcome();
        }

        private static bool IsShowing(GameObject panel) => panel != null && panel.activeSelf;

        private int DepthOf(GameObject panel)
        {
            if (panel == null) return 0;
            if (panel == mainMenuPanel) return 0;
            if (panel == difficultyPanel) return 2;

            // Everything else — mode, profile, tutorial, and the panels that sit over a game — is
            // one step in from the menu root.
            return 1;
        }

        private void ShowOnly(GameObject panel)
        {
            // Set before anything moves, because the outgoing panel reads it on its way out and the
            // incoming one on its way in; they have to agree or they slide against each other.
            PanelTransition.NavDirection = DepthOf(panel) >= DepthOf(currentPanel) ? 1 : -1;
            currentPanel = panel;

            HideAllPanels();
            SetPanelActive(panel, true);
        }

        private void HideAllPanels()
        {
            // The online screens are owned by OnlineFlowController, but they still have to go down
            // whenever the menu shows something else. Left out of this, a panel switched on in the
            // scene — or stranded by an aborted flow — sits full-screen over the menu and eats every
            // tap, which looks exactly like the buttons underneath being broken.
            ResolveOnlineFlow();
            onlineFlow?.HideScreens();

            // Every tagged screen, without naming any of them. A panel left switched on in the
            // scene — or stranded by an aborted flow — sits full-screen over the menu and eats
            // every tap, which looks exactly like the buttons underneath being broken. Listing the
            // screens here as well would only be a second list to forget to add to.
            foreach (NsoloPanel panel in NsoloUI.AllPanels)
                if (panel != null) SetPanelActive(panel.gameObject, false);
        }

        private void SetPanelActive(GameObject panel, bool active)
        {
            if (panel == null) return;

            // Preferred, and what every menu panel carries: hiding is animated rather than
            // instant, so the outgoing screen is still on its way out while the incoming one is
            // already arriving. That overlap is the whole point — it is the frame with no screen at
            // all that made switching panels feel like a jump cut.
            PanelTransition transition = panel.GetComponent<PanelTransition>();
            if (transition != null)
            {
                if (active) transition.Show();
                else transition.Hide();
                return;
            }

            // A panel carrying only the older UIPanelTransition still works through it, and one
            // with neither keeps the original immediate behaviour.
            UIPanelTransition legacy = panel.GetComponent<UIPanelTransition>();
            if (legacy == null)
            {
                panel.SetActive(active);
                return;
            }

            if (active) legacy.Open();
            else legacy.CloseImmediate();
        }

        private void SetGameplayVisible(bool visible)
        {
            if (gameplayRoot != null) gameplayRoot.SetActive(visible);

            // The board is 3D and stays in the scene behind every menu, so hiding the HUD is not
            // enough to stop it being tapped. Telling the controller outright is.
            ResolveGameController();
            gameController?.SetBoardInputEnabled(visible);

            // And not enough to stop it being seen, either. The menus only looked opaque because
            // each carries a full-screen image; the moment two of them crossfade, the board shows
            // through the gap. This is the one question that has the same answer as "is the HUD
            // hidden?", which is why it is asked here rather than at every call site: the screens
            // that legitimately sit over a live game — pause, game over, the rules opened from
            // pause — go up through SetPanelActive and never come through here at all, so the board
            // stays visible behind them exactly as it should.
            MenuBackdrop.Show(!visible);
        }

        private void ResolveGameController()
        {
            if (gameController != null) return;
            gameController = FindObjectOfType<GameController>();
            if (gameController != null) return;
            var all = Resources.FindObjectsOfTypeAll<GameController>();
            if (all.Length > 0) gameController = all[0];
        }

        private void SubscribeToGameOver()
        {
            if (gameController == null || subscribedToGameOver) return;
            gameController.GameOver += HandleGameOver;
            subscribedToGameOver = true;
        }
    }
}
