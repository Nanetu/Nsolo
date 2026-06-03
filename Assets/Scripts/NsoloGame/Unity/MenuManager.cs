using TMPro;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Owns the top-level UI panel flow for menu, tutorial, difficulty, pause, and game-over screens.
    /// Button OnClick events can call the public methods on this component directly.
    /// </summary>
    public class MenuManager : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject welcomePanel;
        [SerializeField] private GameObject tutorialPanel;
        [SerializeField] private GameObject difficultyPanel;
        [SerializeField] private GameObject pausePanel;
        [SerializeField] private GameObject gameOverPanel;

        [Header("Game")]
        [SerializeField] private GameController gameController;
        [SerializeField] private GameObject gameplayRoot;

        [Header("Game Over Text")]
        [SerializeField] private TMP_Text gameOverTitleText;
        [SerializeField] private TMP_Text finalPlayerCapturedText;
        [SerializeField] private TMP_Text finalAiCapturedText;

        private bool isPaused;
        private bool subscribedToGameOver;

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
            ShowWelcome();
        }

        public void ShowWelcome()
        {
            Time.timeScale = 1f;
            isPaused = false;
            ResolveGameController();
            if (gameController != null)
            {
                gameController.SetPaused(false);
            }

            SetGameplayVisible(false);
            ShowOnly(welcomePanel);
        }

        public void ShowTutorial()
        {
            SetGameplayVisible(false);
            ShowOnly(tutorialPanel);
        }

        public void ShowDifficulty()
        {
            SetGameplayVisible(false);
            ShowOnly(difficultyPanel);
        }

        public void BackToWelcome()
        {
            ShowWelcome();
        }

        public void StartEasyGame()
        {
            StartGame(0);
        }

        public void StartMediumGame()
        {
            StartGame(1);
        }

        public void StartHardGame()
        {
            StartGame(2);
        }

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
                Debug.LogError("MenuManager: Cannot start game because GameController is not assigned.");
                return;
            }

            gameController.StartNewGame(difficulty);
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
            if (!isPaused)
                return;

            isPaused = false;
            Time.timeScale = 1f;
            if (gameController != null)
            {
                gameController.SetPaused(false);
            }
            SetPanelActive(pausePanel, false);
        }

        public void RestartGame()
        {
            Time.timeScale = 1f;
            isPaused = false;
            HideAllPanels();
            SetGameplayVisible(true);

            if (gameController != null)
            {
                gameController.RestartGame();
            }
        }

        public void MainMenu()
        {
            ShowWelcome();
        }

        private void HandleGameOver(int winner, int playerCaptured, int aiCaptured)
        {
            Time.timeScale = 1f;
            isPaused = false;
            SetPanelActive(pausePanel, false);
            SetGameplayVisible(true);

            if (gameOverTitleText != null)
            {
                gameOverTitleText.text = winner == 1 ? "VICTORY" : "DEFEAT";
            }

            if (finalPlayerCapturedText != null)
            {
                finalPlayerCapturedText.text = playerCaptured.ToString();
            }

            if (finalAiCapturedText != null)
            {
                finalAiCapturedText.text = aiCaptured.ToString();
            }

            SetPanelActive(gameOverPanel, true);
        }

        private void ShowOnly(GameObject panel)
        {
            HideAllPanels();
            SetPanelActive(panel, true);
        }

        private void HideAllPanels()
        {
            SetPanelActive(welcomePanel, false);
            SetPanelActive(tutorialPanel, false);
            SetPanelActive(difficultyPanel, false);
            SetPanelActive(pausePanel, false);
            SetPanelActive(gameOverPanel, false);
        }

        private void SetPanelActive(GameObject panel, bool active)
        {
            if (panel != null)
            {
                panel.SetActive(active);
            }
        }

        private void SetGameplayVisible(bool visible)
        {
            if (gameplayRoot != null)
            {
                gameplayRoot.SetActive(visible);
            }
        }

        private void ResolveGameController()
        {
            if (gameController != null)
                return;

            gameController = FindObjectOfType<GameController>();
            if (gameController != null)
                return;

            GameController[] controllers = Resources.FindObjectsOfTypeAll<GameController>();
            if (controllers.Length > 0)
            {
                gameController = controllers[0];
            }
        }

        private void SubscribeToGameOver()
        {
            if (gameController == null || subscribedToGameOver)
                return;

            gameController.GameOver += HandleGameOver;
            subscribedToGameOver = true;
        }
    }
}
