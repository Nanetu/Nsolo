using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    public class MenuManager : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject welcomePanel;
        [Tooltip("Full main-menu screen in the UI hierarchy. When assigned, the scene opens here " +
                 "instead of the welcome fallback — useful while wiring the new menu art.")]
        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameObject tutorialPanel;
        [Tooltip("The tutorial's START button. Hidden when the rules are opened from the pause menu, " +
                 "where 'start playing' would mean abandoning the game already in progress.")]
        [SerializeField] private GameObject tutorialStartButton;
        [Tooltip("Play opens this: vs Computer / vs Human. It sits between the welcome screen and " +
                 "the difficulty panel, which is now only reached through the vs Computer button.")]
        [SerializeField] private GameObject modePanel;
        [SerializeField] private GameObject difficultyPanel;
        [SerializeField] private GameObject profilePanel;
        [SerializeField] private GameObject pausePanel;
        [SerializeField] private GameObject gameOverPanel;

        [Header("Gameplay Chrome")]
        [Tooltip("Background art used against the AI — the one with the Undo pill painted on it.")]
        [SerializeField] private GameObject aiBackgroundPanel;
        [Tooltip("Background art used in local two-player, without the Undo pill.")]
        [SerializeField] private GameObject humanBackgroundPanel;
        [Tooltip("Background art used online. Falls back to the local two-player art if left empty.")]
        [SerializeField] private GameObject onlineBackgroundPanel;
        [Tooltip("The Undo button object in HUDRoot. Its image is transparent and the pill it sits " +
                 "on belongs to the AI background art, so it has to be switched off alongside it — " +
                 "otherwise two-player play has a live invisible button over bare background.")]
        [SerializeField] private GameObject undoButtonObject;

        [Header("Game")]
        [SerializeField] private GameController gameController;
        [SerializeField] private GameObject gameplayRoot;
        [Tooltip("Owns the online room flow. Auto-found if left empty.")]
        [SerializeField] private OnlineFlowController onlineFlow;

        [Header("Welcome")]
        [Tooltip("Greeting on the welcome screen. Refreshed from the saved profile each time it opens.")]
        [SerializeField] private TMP_Text welcomeUsernameText;

        [Header("Mode Selection")]
        [Tooltip("Selected-state overlay on the vs Computer card.")]
        [SerializeField] private GameObject vsComputerSelectionHighlight;
        [Tooltip("Selected-state overlay on the vs Human card.")]
        [SerializeField] private GameObject vsHumanSelectionHighlight;
        [Tooltip("The mode panel's CONTINUE button. Held disabled until a card is picked, exactly " +
                 "as the difficulty panel holds its own start button.")]
        [SerializeField] private Button modeContinueButton;

        [Header("Difficulty Selection")]
        [SerializeField] private GameObject easySelectionHighlight;
        [SerializeField] private GameObject mediumSelectionHighlight;
        [SerializeField] private GameObject hardSelectionHighlight;
        [SerializeField] private Button startGameButton;

        [Header("Pause — Settings")]
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private Slider sfxVolumeSlider;
        [SerializeField] private Toggle vibrationToggle;
        [SerializeField] private TMP_Text musicVolumeLabel;
        [SerializeField] private TMP_Text sfxVolumeLabel;

        [Header("Pause — Tutorial Tips")]
        [Tooltip("Optional. A Toggle for the in-game tips. Duplicating the vibration row is the " +
                 "easy way to build one — the VibrationSwitch that comes with the copy drives its " +
                 "own knob and ON/OFF caption, so only this slot needs filling. Leave empty and " +
                 "use ToggleTutorialTips or DisableTutorialTips from a plain Button instead.")]
        [SerializeField] private Toggle tutorialTipsToggle;

        [Header("Game Over Text")]
        [SerializeField] private TMP_Text gameOverTitleText;
        [SerializeField] private TMP_Text finalPlayerCapturedText;
        [SerializeField] private TMP_Text finalAiCapturedText;
        [SerializeField] private TMP_Text gameOverDifficultyText;
        [SerializeField] private TMP_Text gameOverTimeText;
        [SerializeField] private TMP_Text gameOverVictoryCountText;
        [SerializeField] private TMP_Text gameOverSummaryText;

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
            // Before anything reads a slot. Start rather than Awake because the register is built
            // after every Awake has run — see NsoloUI.Bootstrap for why it cannot be earlier.
            AdoptRebuiltUI();

            InitSettingsSliders();
            if (mainMenuPanel != null)
                ShowMainMenu();
            else
                ShowWelcome();
        }

        // ── Rebuilt screens ───────────────────────────────────────────────

        /// <summary>
        /// Screens this manager used to own that a rebuilt panel has taken over from. Kept only so
        /// they can still be switched off: once the slot points at the new screen, the old one is
        /// unreachable from here, and an unreachable full-screen panel left switched on sits over
        /// the menu eating every tap — which looks exactly like the new screen being broken.
        /// </summary>
        private readonly List<GameObject> replacedPanels = new List<GameObject>();

        /// <summary>
        /// Points every slot at whatever has been tagged for it, and keeps what was in the slot
        /// otherwise.
        ///
        /// This is what makes a rebuilt screen a drop-in. Tag the new panel with its
        /// <see cref="PanelId"/> and its parts with their <see cref="ElementId"/>s, and it takes
        /// over here — no slot to drag it into, and the screen it replaces can be deleted
        /// afterwards rather than beforehand. Nothing that has not been tagged is disturbed, so a
        /// half-finished rebuild leaves the rest of the game exactly as it was.
        /// </summary>
        private void AdoptRebuiltUI()
        {
            welcomePanel    = AdoptPanel(PanelId.Welcome,    welcomePanel);
            mainMenuPanel   = AdoptPanel(PanelId.MainMenu,   mainMenuPanel);
            tutorialPanel   = AdoptPanel(PanelId.Tutorial,   tutorialPanel);
            modePanel       = AdoptPanel(PanelId.Mode,       modePanel);
            difficultyPanel = AdoptPanel(PanelId.Difficulty, difficultyPanel);
            profilePanel    = AdoptPanel(PanelId.Profile,    profilePanel);
            pausePanel      = AdoptPanel(PanelId.Pause,      pausePanel);
            gameOverPanel   = AdoptPanel(PanelId.GameOver,   gameOverPanel);

            tutorialStartButton = Pick(NsoloUI.Object(ElementId.TutorialStart), tutorialStartButton);
            undoButtonObject    = Pick(NsoloUI.Object(ElementId.HudUndo),       undoButtonObject);

            welcomeUsernameText = Pick(NsoloUI.Label(ElementId.MainMenuUsernameLabel), welcomeUsernameText);

            vsComputerSelectionHighlight = Pick(NsoloUI.Object(ElementId.ModeVsComputerHighlight), vsComputerSelectionHighlight);
            vsHumanSelectionHighlight    = Pick(NsoloUI.Object(ElementId.ModeVsHumanHighlight),    vsHumanSelectionHighlight);
            modeContinueButton           = Pick(NsoloUI.Button(ElementId.ModeContinue),            modeContinueButton);

            easySelectionHighlight   = Pick(NsoloUI.Object(ElementId.DifficultyEasyHighlight),   easySelectionHighlight);
            mediumSelectionHighlight = Pick(NsoloUI.Object(ElementId.DifficultyMediumHighlight), mediumSelectionHighlight);
            hardSelectionHighlight   = Pick(NsoloUI.Object(ElementId.DifficultyHardHighlight),   hardSelectionHighlight);
            startGameButton          = Pick(NsoloUI.Button(ElementId.DifficultyStart),           startGameButton);

            musicVolumeSlider  = Pick(NsoloUI.Slider(ElementId.PauseMusicSlider),   musicVolumeSlider);
            sfxVolumeSlider    = Pick(NsoloUI.Slider(ElementId.PauseSfxSlider),     sfxVolumeSlider);
            vibrationToggle    = Pick(NsoloUI.Toggle(ElementId.PauseVibrationToggle), vibrationToggle);
            tutorialTipsToggle = Pick(NsoloUI.Toggle(ElementId.PauseTipsToggle),    tutorialTipsToggle);
            musicVolumeLabel   = Pick(NsoloUI.Label(ElementId.PauseMusicLabel),     musicVolumeLabel);
            sfxVolumeLabel     = Pick(NsoloUI.Label(ElementId.PauseSfxLabel),       sfxVolumeLabel);

            gameOverTitleText        = Pick(NsoloUI.Label(ElementId.GameOverTitleLabel),         gameOverTitleText);
            finalPlayerCapturedText  = Pick(NsoloUI.Label(ElementId.GameOverPlayerScoreLabel),   finalPlayerCapturedText);
            finalAiCapturedText      = Pick(NsoloUI.Label(ElementId.GameOverOpponentScoreLabel), finalAiCapturedText);
            gameOverDifficultyText   = Pick(NsoloUI.Label(ElementId.GameOverDifficultyLabel),    gameOverDifficultyText);
            gameOverTimeText         = Pick(NsoloUI.Label(ElementId.GameOverTimeLabel),          gameOverTimeText);
            gameOverVictoryCountText = Pick(NsoloUI.Label(ElementId.GameOverVictoryCountLabel),  gameOverVictoryCountText);
            gameOverSummaryText      = Pick(NsoloUI.Label(ElementId.GameOverSummaryLabel),       gameOverSummaryText);
        }

        private GameObject AdoptPanel(PanelId id, GameObject current)
        {
            GameObject rebuilt = NsoloUI.Panel(id);
            if (rebuilt == null || rebuilt == current) return current;

            if (current != null)
            {
                current.SetActive(false);
                replacedPanels.Add(current);
                Debug.Log($"MenuManager: '{rebuilt.name}' has taken over as the {id} screen, " +
                          $"replacing '{current.name}'. The old one can be deleted.", rebuilt);
            }

            return rebuilt;
        }

        /// <summary>
        /// The rebuilt reference if there is one, otherwise whatever the slot already held.
        /// Written against Unity's own null check rather than <c>??</c>, which does not see a
        /// destroyed object as null and would hand back a reference that throws on first use.
        /// </summary>
        private static T Pick<T>(T rebuilt, T current) where T : Object
            => rebuilt != null ? rebuilt : current;

        // ── Welcome Panel ─────────────────────────────────────────────────

        /// <summary>
        /// Goes to the menu root — whichever panel that currently is.
        ///
        /// Start() already prefers <see cref="mainMenuPanel"/> when one is assigned, but every way
        /// back out of the game (the pause menu, game over, and all the online exits) called this
        /// and landed on the old welcome screen instead. So the app opened on one menu and returned
        /// to a different one, which is not a preview of anything. Routing both through the same
        /// choice means assigning mainMenuPanel swaps the menu wholesale, and clearing it puts
        /// everything back on welcome.
        /// </summary>
        public void ShowWelcome()
        {
            if (mainMenuPanel != null)
            {
                ShowMainMenu();
                return;
            }

            PrepareMenuReturn();
            RefreshWelcomeUsername();
            ShowOnly(welcomePanel);
            AudioManager.StartMenuMusic();
        }

        /// <summary>
        /// Opens the authored main-menu panel. Assign <see cref="mainMenuPanel"/> to preview the
        /// new entrance animation; clear it to fall back to <see cref="ShowWelcome"/> on start.
        /// </summary>
        public void ShowMainMenu()
        {
            PrepareMenuReturn();
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
            gameController?.SetPaused(false);
            SetGameplayVisible(false);
            // Back to the standard chrome, so the menu is never sitting on two-player art.
            ApplyGameplayChrome(GameMode.VersusComputer);
        }

        /// <summary>Re-reads the saved username so a rename on the profile page shows up here.</summary>
        public void RefreshWelcomeUsername()
        {
            if (welcomeUsernameText == null) return;
            string name = ProfileManager.Instance?.Username;
            welcomeUsernameText.text = string.IsNullOrWhiteSpace(name) ? "Player" : name;
        }

        public void ShowTutorial()
        {
            tutorialOpenedFromPause = false;
            SetActive(tutorialStartButton, true);
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
            SetActive(tutorialStartButton, false);

            SetPanelActive(pausePanel, false);
            SetPanelActive(tutorialPanel, true);
            AudioManager.Click();
            Haptics.Light();
        }

        /// <summary>
        /// Leaves the tutorial for wherever it was opened from. Wire `TutorialPanel/Back` to this
        /// rather than to BackToWelcome — from the main menu it behaves exactly as it always did.
        /// </summary>
        public void CloseTutorial()
        {
            SetPanelActive(tutorialPanel, false);

            if (tutorialOpenedFromPause)
            {
                tutorialOpenedFromPause = false;
                SetActive(tutorialStartButton, true);
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
                case ModeOnline: ShowOnline(); break;
                // Nothing picked. The button is held non-interactable until something is, so this
                // is only reachable if that slot was never wired up.
                default: return;
            }
        }

        private void ApplyModeSelection(int selection)
        {
            selectedMode = selection;
            SetActive(vsComputerSelectionHighlight, selection == ModeVsComputer);
            SetActive(vsHumanSelectionHighlight,    selection == ModeVsHuman);
            NsoloUI.SetVisible(ElementId.ModeOnlineHighlight, selection == ModeOnline);
            NsoloUI.Gate(modeContinueButton, true);
            AudioManager.Click();
            Haptics.Light();
        }

        private void ResetModeSelection()
        {
            selectedMode = -1;
            SetActive(vsComputerSelectionHighlight, false);
            SetActive(vsHumanSelectionHighlight,    false);
            NsoloUI.SetVisible(ElementId.ModeOnlineHighlight, false);
            NsoloUI.Gate(modeContinueButton, false);
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
            SetActive(easySelectionHighlight,   difficulty == 0);
            SetActive(mediumSelectionHighlight, difficulty == 1);
            SetActive(hardSelectionHighlight,   difficulty == 2);
            NsoloUI.Gate(startGameButton, true);
        }

        private void ResetDifficultySelection()
        {
            selectedDifficulty = -1;
            SetActive(easySelectionHighlight,   false);
            SetActive(mediumSelectionHighlight, false);
            SetActive(hardSelectionHighlight,   false);
            NsoloUI.Gate(startGameButton, false);
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
        /// Swaps the background art for the mode being played, and takes the Undo button with it.
        /// Safe to call with nothing assigned, which is the state until the human panel is built.
        /// </summary>
        private void ApplyGameplayChrome(GameMode mode)
        {
            // Neither two-player mode has an Undo — taking a move back is not something you can do
            // to an opponent who has already seen it — but they do not share a background: online
            // has its own art, and falls back to the local one only if that slot is empty.
            bool online = mode == GameMode.Online;
            bool twoPlayer = mode != GameMode.VersusComputer;
            bool useOnlineArt = online && onlineBackgroundPanel != null;

            SetActive(onlineBackgroundPanel, useOnlineArt);
            SetActive(humanBackgroundPanel, twoPlayer && !useOnlineArt);
            SetActive(aiBackgroundPanel, !twoPlayer);
            SetActive(undoButtonObject, !twoPlayer);
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
            if (musicVolumeLabel != null)
                musicVolumeLabel.text = $"[{Mathf.RoundToInt(value * 100)}%]";
        }

        public void OnSFXVolumeChanged(float value)
        {
            AudioManager.Instance?.SetSfxVolume(value);
            if (sfxVolumeLabel != null)
                sfxVolumeLabel.text = $"[{Mathf.RoundToInt(value * 100)}%]";

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
            if (musicVolumeLabel != null)
                musicVolumeLabel.text = $"[{Mathf.RoundToInt(music * 100)}%]";
            if (sfxVolumeLabel != null)
                sfxVolumeLabel.text = $"[{Mathf.RoundToInt(sfx * 100)}%]";
        }

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

            if (gameOverTitleText != null)
                gameOverTitleText.text = hotSeat
                    ? $"PLAYER {winner} WINS"
                    : (playerWon ? "VICTORY" : "DEFEAT");
            if (finalPlayerCapturedText != null)
                finalPlayerCapturedText.text = playerCaptured.ToString();
            if (finalAiCapturedText != null)
                finalAiCapturedText.text = aiCaptured.ToString();

            if (gameOverDifficultyText != null)
            {
                if (hotSeat)
                {
                    gameOverDifficultyText.text = "2 PLAYER";
                }
                else if (online)
                {
                    gameOverDifficultyText.text = "ONLINE";
                }
                else
                {
                    int d = gameController != null ? gameController.CurrentDifficulty : -1;
                    gameOverDifficultyText.text =
                        d >= 0 && d < DifficultyNames.Length ? DifficultyNames[d] : "--";
                }
            }

            if (gameOverTimeText != null)
            {
                float seconds = gameController != null ? gameController.LastGameSeconds : 0f;
                gameOverTimeText.text = $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";
            }

            // The lifetime victory count is a record of games against the AI. Neither two-player
            // results nor online ones are filed into it, so showing it here would imply otherwise.
            if (gameOverVictoryCountText != null)
                gameOverVictoryCountText.text = hotSeat || online
                    ? "--"
                    : (ProfileManager.Instance?.GamesWon ?? 0).ToString();

            if (gameOverSummaryText != null)
            {
                // Online, the two score arguments are still player 1's and player 2's stones, so
                // they have to be read from this device's seat rather than assumed to be "mine,
                // theirs" — otherwise the joiner sees their score and their opponent's swapped.
                int mine = online && gameController.OnlineLocalPlayer == 2 ? aiCaptured : playerCaptured;
                int theirs = online && gameController.OnlineLocalPlayer == 2 ? playerCaptured : aiCaptured;

                // "OPP" rather than "Computer" or "Opponent": this label is a 200px box at font 30
                // with autosizing off, so a longer word wraps onto a second line and overflows it.
                gameOverSummaryText.text = hotSeat
                    ? $"P1: {playerCaptured} - P2: {aiCaptured}"
                    : (playerWon
                        ? $"You: {mine} - {theirs}"
                        : $"OPP: {theirs} - {mine}");
            }

            // The two figures the rebuilt game-over screen added. Written through the register
            // rather than through slots of their own — they belong to the new panel, and giving
            // this manager two more serialized fields for it would be adding to the wiring the
            // register exists to remove.
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
            if (panel == welcomePanel || panel == mainMenuPanel) return 0;
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

            SetPanelActive(welcomePanel,    false);
            SetPanelActive(mainMenuPanel,   false);
            SetPanelActive(tutorialPanel,   false);
            SetPanelActive(modePanel,       false);
            SetPanelActive(difficultyPanel, false);
            SetPanelActive(profilePanel,    false);
            SetPanelActive(pausePanel,      false);
            SetPanelActive(gameOverPanel,   false);

            // Every rebuilt screen, including ones this manager has no slot for. A tagged panel
            // left switched on in the scene is the same full-screen tap-eater as an untagged one,
            // and the whole point of the register is that a new screen needs no slot here.
            foreach (NsoloPanel panel in NsoloUI.AllPanels)
                if (panel != null) SetPanelActive(panel.gameObject, false);

            // The screens the rebuilt ones replaced. Unreachable through the slots now, and still
            // perfectly capable of covering the menu.
            foreach (GameObject panel in replacedPanels)
                if (panel != null) panel.SetActive(false);
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

        private void SetActive(GameObject obj, bool active)
        {
            if (obj != null) obj.SetActive(active);
        }

        private void SetGameplayVisible(bool visible)
        {
            if (gameplayRoot != null) gameplayRoot.SetActive(visible);

            // The board is 3D and stays in the scene behind every menu, so hiding the HUD is not
            // enough to stop it being tapped. Telling the controller outright is.
            ResolveGameController();
            gameController?.SetBoardInputEnabled(visible);
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
