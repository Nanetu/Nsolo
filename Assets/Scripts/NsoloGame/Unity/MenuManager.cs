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
        }

        private void OnDisable()
        {
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
            Time.timeScale = 1f;
            isPaused = false;
            ResolveGameController();
            gameController?.SetPaused(false);
            SetGameplayVisible(false);
            RefreshWelcomeUsername();
            ShowOnly(welcomePanel);
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
        /// Hook for the pre-game formation phase's Ready button (wire this up in the scene).
        /// </summary>
        public void OnReadyButtonPressed()
        {
            ResolveGameController();
            gameController?.ConfirmFormationReady();
        }

        public void TogglePause()
        {
            if (gameController == null || gameController.CurrentState == GameState.GameOver)
                return;

            isPaused = !isPaused;
            Time.timeScale = isPaused ? 0f : 1f;
            gameController.SetPaused(isPaused);
            SetPanelActive(pausePanel, isPaused);
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
            float music = PlayerPrefs.GetFloat("MusicVolume", 0.7f);
            float sfx   = PlayerPrefs.GetFloat("SFXVolume",   0.85f);
            bool vib    = PlayerPrefs.GetInt("Vibration", 1) == 1;

            AudioListener.volume = music;

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

            UpdateSettingsLabels(music, sfx);
        }

        public void OnMusicVolumeChanged(float value)
        {
            AudioListener.volume = value;
            PlayerPrefs.SetFloat("MusicVolume", value);
            PlayerPrefs.Save();
            if (musicVolumeLabel != null)
                musicVolumeLabel.text = $"[{Mathf.RoundToInt(value * 100)}%]";
        }

        public void OnSFXVolumeChanged(float value)
        {
            PlayerPrefs.SetFloat("SFXVolume", value);
            PlayerPrefs.Save();
            if (sfxVolumeLabel != null)
                sfxVolumeLabel.text = $"[{Mathf.RoundToInt(value * 100)}%]";
            SFXVolumeChanged?.Invoke(value);
        }

        public void OnVibrationToggled(bool value)
        {
            PlayerPrefs.SetInt("Vibration", value ? 1 : 0);
            PlayerPrefs.Save();
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
