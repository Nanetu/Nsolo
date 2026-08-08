using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    public class MenuManager : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject welcomePanel;
        [SerializeField] private GameObject tutorialPanel;
        [SerializeField] private GameObject difficultyPanel;
        [SerializeField] private GameObject profilePanel;
        [SerializeField] private GameObject pausePanel;
        [SerializeField] private GameObject gameOverPanel;

        [Header("Game")]
        [SerializeField] private GameController gameController;
        [SerializeField] private GameObject gameplayRoot;

        [Header("Welcome")]
        [Tooltip("Greeting on the welcome screen. Refreshed from the saved profile each time it opens.")]
        [SerializeField] private TMP_Text welcomeUsernameText;

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

        private bool isPaused;
        private bool subscribedToGameOver;
        private int selectedDifficulty = -1;

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
            InitSettingsSliders();
            ShowWelcome();
        }

        // ── Welcome Panel ─────────────────────────────────────────────────

        public void ShowWelcome()
        {
            // Dropped before the timescale is reset: a tip left on screen would otherwise restore
            // whatever scale it captured and freeze the menu behind it.
            TutorialCoach.Instance?.ForceHide();
            Time.timeScale = 1f;
            isPaused = false;
            ResolveGameController();
            gameController?.SetPaused(false);
            SetGameplayVisible(false);
            RefreshWelcomeUsername();
            ShowOnly(welcomePanel);
            AudioManager.StartMenuMusic();
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
            SetGameplayVisible(false);
            ShowOnly(tutorialPanel);
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
            if (startGameButton != null) startGameButton.interactable = true;
        }

        private void ResetDifficultySelection()
        {
            selectedDifficulty = -1;
            SetActive(easySelectionHighlight,   false);
            SetActive(mediumSelectionHighlight, false);
            SetActive(hardSelectionHighlight,   false);
            if (startGameButton != null) startGameButton.interactable = false;
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

            if (gameController == null)
            {
                Debug.LogError("MenuManager: GameController not assigned.");
                return;
            }

            gameController.StartNewGame(difficulty);
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
            if (gameController == null || gameController.CurrentState == GameState.GameOver)
                return;

            isPaused = !isPaused;
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

        public void RestartGame()
        {
            TutorialCoach.Instance?.ForceHide();
            Time.timeScale = 1f;
            isPaused = false;
            HideAllPanels();
            SetGameplayVisible(true);
            gameController?.RestartGame();
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

            bool playerWon = winner == 1;

            if (gameOverTitleText != null)
                gameOverTitleText.text = playerWon ? "VICTORY" : "DEFEAT";
            if (finalPlayerCapturedText != null)
                finalPlayerCapturedText.text = playerCaptured.ToString();
            if (finalAiCapturedText != null)
                finalAiCapturedText.text = aiCaptured.ToString();

            if (gameOverDifficultyText != null)
            {
                int d = gameController != null ? gameController.CurrentDifficulty : -1;
                gameOverDifficultyText.text =
                    d >= 0 && d < DifficultyNames.Length ? DifficultyNames[d] : "--";
            }

            if (gameOverTimeText != null)
            {
                float seconds = gameController != null ? gameController.LastGameSeconds : 0f;
                gameOverTimeText.text = $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";
            }

            if (gameOverVictoryCountText != null)
                gameOverVictoryCountText.text = (ProfileManager.Instance?.GamesWon ?? 0).ToString();

            if (gameOverSummaryText != null)
                gameOverSummaryText.text = playerWon
                    ? $"You: {playerCaptured} - {aiCaptured}"
                    : $"AI: {aiCaptured} - {playerCaptured}";

            SetPanelActive(gameOverPanel, true);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void ShowOnly(GameObject panel)
        {
            HideAllPanels();
            SetPanelActive(panel, true);
        }

        private void HideAllPanels()
        {
            SetPanelActive(welcomePanel,    false);
            SetPanelActive(tutorialPanel,   false);
            SetPanelActive(difficultyPanel, false);
            SetPanelActive(profilePanel,    false);
            SetPanelActive(pausePanel,      false);
            SetPanelActive(gameOverPanel,   false);
        }

        private void SetPanelActive(GameObject panel, bool active)
        {
            if (panel != null) panel.SetActive(active);
        }

        private void SetActive(GameObject obj, bool active)
        {
            if (obj != null) obj.SetActive(active);
        }

        private void SetGameplayVisible(bool visible)
        {
            if (gameplayRoot != null) gameplayRoot.SetActive(visible);
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
