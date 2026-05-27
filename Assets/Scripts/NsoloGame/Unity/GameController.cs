using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using NsoloGame.Core;
using NsoloGame.AI;

namespace NsoloGame.Unity
{
    public enum GameState
    {
        Initialising,
        HumanTurn,
        ValidatingMove,
        ApplyingMove,
        AiThinking,
        GameOver
    }

    /// <summary>
    /// MonoBehaviour game controller for Nsolo.
    /// Manages game flow and state transitions.
    /// Runs AI on a background thread and dispatches results back to main thread.
    /// </summary>
    public class GameController : MonoBehaviour
    {
        [SerializeField] private UIManager uiManager;
        [SerializeField] private bool autoStartOnSceneLoad = false;
        [SerializeField] private bool enableBreadcrumbLogs = false;
        
        private GameState gameState;
        private GameBoard gameBoard;
        private GameEngine gameEngine;
        private AIAgent aiAgent;
        private SowingPath sowingPath;
        private int humanPlayer = 1;
        private int aiPlayer = 2;
        private Difficulty aiDifficulty;

        private Move pendingMove;
        private Task<Move> aiMoveTask;
        private CancellationTokenSource aiMoveCancellation;
        private Move aiMove;
        private bool isPaused;

        public event System.Action<int, int, int> GameOver;
        public GameState CurrentState => gameState;

        private void Awake()
        {
            Log("Awake()");

            if (uiManager == null)
            {
                uiManager = FindObjectOfType<UIManager>();
                Log(uiManager == null
                    ? "No UIManager found in scene during Awake()."
                    : $"Auto-found UIManager: {uiManager.name}.");
            }
        }

        private void Start()
        {
            Log("Start()");
            GameBoard.InitializeZobrist();
            
            // Initialize game systems
            sowingPath = new SowingPath();
            gameEngine = new GameEngine(sowingPath);
            
            // Load AI difficulty from PlayerPrefs (default to Medium)
            int difficultyInt = PlayerPrefs.GetInt("AIDifficulty", 1);
            aiDifficulty = (Difficulty)difficultyInt;
            
            var evaluationFunction = new EvaluationFunction(gameEngine);
            aiAgent = new AIAgent(gameEngine, evaluationFunction, aiDifficulty);
            Log($"Game systems initialized. AI difficulty={aiDifficulty}.");

            gameState = GameState.Initialising;

            if (autoStartOnSceneLoad)
            {
                InitializeGame();
            }
        }

        public void StartNewGame(int difficultyInt)
        {
            Log($"StartNewGame(difficultyInt={difficultyInt})");
            SetAIDifficulty(difficultyInt);
            InitializeGame();
        }

        private void InitializeGame()
        {
            Log("InitializeGame()");
            CancelAiThinking();
            StopAllCoroutines();
            isPaused = false;
            gameBoard = new GameBoard();
            gameState = GameState.HumanTurn;

            if (uiManager == null)
            {
                Debug.LogError("GameController: Cannot initialize visuals because UIManager is not assigned.");
                return;
            }

            Log($"Starting board created. Total pit stones={CountBoardStones(gameBoard)}. Hole_1_11={gameBoard.Get(1, 11)}, Hole_2_0={gameBoard.Get(2, 0)}.");
            uiManager.UpdateDisplay(gameBoard, gameBoard.CapturedP1, gameBoard.CapturedP2);
            uiManager.ShowStatus("Your turn");
            uiManager.ShowMessage("Select one of your highlighted pits", 2.5f);
            
            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            Log($"Initial legal moves={legalMoves.Count}.");
            uiManager.HighlightLegalMoves(legalMoves);
        }

        private void Update()
        {
            switch (gameState)
            {
                case GameState.AiThinking:
                    HandleAiThinkingState();
                    break;
            }
        }

        /// <summary>
        /// Called by UIManager when a hole is touched.
        /// </summary>
        public void OnHoleTouched(int row, int col)
        {
            Log($"OnHoleTouched(row={row}, col={col}) while state={gameState}.");

            if (isPaused || gameState != GameState.HumanTurn)
                return;

            gameState = GameState.ValidatingMove;
            ValidateAndApplyMove(row, col);
        }

        private void ValidateAndApplyMove(int row, int col)
        {
            Log($"ValidateAndApplyMove(row={row}, col={col})");

            // Check if move is legal
            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            Move selectedMove = null;

            foreach (var move in legalMoves)
            {
                if (move.Row == row && move.Col == col)
                {
                    selectedMove = move;
                    break;
                }
            }

            if (selectedMove == null)
            {
                Log("Move rejected as illegal.");
                // Illegal move - flash red and stay in HumanTurn
                uiManager.FlashIllegalMove(row, col);
                uiManager.ShowStatus("Your turn");
                gameState = GameState.HumanTurn;
                return;
            }

            // Valid move - apply it
            Log("Move accepted. Entering ApplyingMove.");
            gameState = GameState.ApplyingMove;
            pendingMove = selectedMove;
            StartCoroutine(ApplyHumanMoveCoroutine());
        }

        private IEnumerator ApplyHumanMoveCoroutine()
        {
            Log($"HandleApplyingMoveState() applying human move row={pendingMove.Row}, col={pendingMove.Col}.");
            GameBoard startingBoard = gameBoard.Clone();
            MoveResult moveResult = gameEngine.ApplyMoveWithResult(gameBoard, pendingMove, humanPlayer);
            Log($"Human move applied. Total pit stones={CountBoardStones(gameBoard)}, captured P1={gameBoard.CapturedP1}, captured P2={gameBoard.CapturedP2}.");
            uiManager.ShowStatus("Sowing");
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, "You");

            // Check if game is over
            (bool isOver, int winner) = gameEngine.IsTerminal(gameBoard, aiPlayer);
            if (isOver)
            {
                FinishGame(winner);
                yield break;
            }

            // Transition to AI thinking
            gameState = GameState.AiThinking;
            uiManager.ShowStatus("AI thinking");
            StartAIMove();
        }

        private void StartAIMove()
        {
            Log("StartAIMove()");
            CancelAiThinking();

            // Run AI on background thread
            GameBoard aiBoardSnapshot = gameBoard.Clone();
            aiMoveCancellation = new CancellationTokenSource();
            CancellationToken token = aiMoveCancellation.Token;
            aiMoveTask = Task.Run(() => aiAgent.SelectMove(aiBoardSnapshot, aiPlayer, token), token);
        }

        private void HandleAiThinkingState()
        {
            if (isPaused)
                return;

            if (aiMoveTask == null || !aiMoveTask.IsCompleted)
                return;

            if (aiMoveTask.IsCanceled || aiMoveTask.IsFaulted)
            {
                aiMoveTask = null;
                return;
            }

            aiMove = aiMoveTask.Result;
            aiMoveTask = null;
            aiMoveCancellation = null;

            if (aiMove != null)
            {
                gameState = GameState.ApplyingMove;
                StartCoroutine(ApplyAiMoveCoroutine(aiMove));
            }
            else
            {
                Debug.LogWarning("GameController: AI returned no move.");
            }
        }

        private IEnumerator ApplyAiMoveCoroutine(Move move)
        {
            Log($"AI move selected row={move.Row}, col={move.Col}.");
            GameBoard startingBoard = gameBoard.Clone();
            MoveResult moveResult = gameEngine.ApplyMoveWithResult(gameBoard, move, aiPlayer);
            Log($"AI move applied. Total pit stones={CountBoardStones(moveResult.Board)}, captured P1={moveResult.Board.CapturedP1}, captured P2={moveResult.Board.CapturedP2}.");
            uiManager.ShowStatus("AI sowing");
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, "AI");

            // Check if game is over
            (bool isOver, int winner) = gameEngine.IsTerminal(gameBoard, humanPlayer);
            if (isOver)
            {
                FinishGame(winner);
                yield break;
            }

            gameState = GameState.HumanTurn;
            uiManager.ShowStatus("Your turn");
            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            Log($"Returned to HumanTurn. Legal moves={legalMoves.Count}.");
            uiManager.HighlightLegalMoves(legalMoves);
        }

        public void RestartGame()
        {
            Log("RestartGame()");
            gameState = GameState.Initialising;
            InitializeGame();
        }

        public void SetPaused(bool paused)
        {
            Log($"SetPaused({paused}) while state={gameState}.");
            isPaused = paused;

            if (paused)
            {
                if (gameState == GameState.AiThinking)
                {
                    CancelAiThinking();
                }
                return;
            }

            if (gameState == GameState.AiThinking && aiMoveTask == null)
            {
                StartAIMove();
            }
        }

        public void SetAIDifficulty(int difficultyInt)
        {
            aiDifficulty = (Difficulty)difficultyInt;
            PlayerPrefs.SetInt("AIDifficulty", difficultyInt);
            PlayerPrefs.Save();
            
            var evaluationFunction = new EvaluationFunction(gameEngine);
            aiAgent = new AIAgent(gameEngine, evaluationFunction, aiDifficulty);
        }

        private void ShowMoveFeedback(MoveResult moveResult, string actor)
        {
            string movedFrom = $"{actor} moved from row {moveResult.Move.Row}, column {moveResult.Move.Col}";
            if (moveResult.CapturedStones > 0)
            {
                uiManager.ShowMessage($"{movedFrom}. Captured {moveResult.CapturedStones} stones.");
            }
            else
            {
                uiManager.ShowMessage(movedFrom + ".");
            }
        }

        private void FinishGame(int winner)
        {
            Log($"FinishGame(winner={winner})");
            CancelAiThinking();
            gameState = GameState.GameOver;

            int capturedP1 = gameBoard == null ? 0 : gameBoard.CapturedP1;
            int capturedP2 = gameBoard == null ? 0 : gameBoard.CapturedP2;

            if (uiManager != null)
            {
                uiManager.ShowGameOver(winner);
            }

            GameOver?.Invoke(winner, capturedP1, capturedP2);
        }

        private void CancelAiThinking()
        {
            if (aiMoveCancellation != null)
            {
                aiMoveCancellation.Cancel();
                aiMoveCancellation.Dispose();
                aiMoveCancellation = null;
            }

            aiMoveTask = null;
            aiMove = null;
        }

        private int CountBoardStones(GameBoard board)
        {
            int total = 0;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    total += board.Get(r, c);
                }
            }

            return total;
        }

        private void Log(string message)
        {
            if (enableBreadcrumbLogs)
            {
                Debug.Log($"[GameController] {message}", this);
            }
        }
    }
}
