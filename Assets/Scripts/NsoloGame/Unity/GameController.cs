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
        PreGameFormation,
        HumanTurn,
        ValidatingMove,
        ApplyingMove,
        AiThinking,
        GameOver
    }

    public class GameController : MonoBehaviour
    {
        [SerializeField] private UIManager uiManager;
        [SerializeField] private bool autoStartOnSceneLoad = false;
        [SerializeField] private bool enableBreadcrumbLogs = false;
        [Tooltip("Logs every capture/relay/turn-end decision from GameEngine to the Console. Off by default since AI search calls this thousands of times.")]
        [SerializeField] private bool debugLandingOutcomes = false;

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
        private bool systemsInitialized;
        private float aiThinkStartTime;

        // Undo
        private Stack<GameBoard> boardHistory = new Stack<GameBoard>();

        // Hint
        private Task<Move> hintTask;
        private CancellationTokenSource hintCancellation;

        // Timer
        private float gameStartTime;

        // Pre-game formation phase
        private int formationHeld;
        private int formationHeldFromRow;
        private int formationHeldFromCol;
        private System.Random formationRandom = new System.Random();

        public event System.Action<int, int, int> GameOver;
        public GameState CurrentState => gameState;
        public int CurrentDifficulty => (int)aiDifficulty;

        /// <summary>
        /// Wall-clock length of the game that just finished. Captured once in FinishGame so the
        /// game-over panel keeps showing the final time instead of a clock that keeps ticking.
        /// </summary>
        public float LastGameSeconds { get; private set; }

        private void Awake()
        {
            Log("Awake()");
            if (uiManager == null)
            {
                uiManager = FindObjectOfType<UIManager>();
                Log(uiManager == null ? "No UIManager found." : $"Auto-found UIManager: {uiManager.name}.");
            }
            InitializeSystems();
        }

        private void Start()
        {
            Log("Start()");
            InitializeSystems();
            if (autoStartOnSceneLoad)
                InitializeGame();
        }

        private void InitializeSystems()
        {
            if (systemsInitialized) return;

            GameBoard.InitializeZobrist();
            sowingPath = new SowingPath();
            gameEngine = new GameEngine(sowingPath);
            GameEngine.DebugLandingOutcomes = debugLandingOutcomes;

            int difficultyInt = PlayerPrefs.GetInt("AIDifficulty", 1);
            aiDifficulty = (Difficulty)difficultyInt;

            var eval = new EvaluationFunction(gameEngine);
            aiAgent = new AIAgent(gameEngine, eval, aiDifficulty);
            Log($"Systems initialised. Difficulty={aiDifficulty}.");

            gameState = GameState.Initialising;
            systemsInitialized = true;
        }

        public void StartNewGame(int difficultyInt)
        {
            Log($"StartNewGame({difficultyInt})");
            InitializeSystems();
            SetAIDifficulty(difficultyInt);
            InitializeGame();
        }

        private void InitializeGame()
        {
            Log("InitializeGame()");
            CancelAiThinking();
            CancelHintSearch();
            StopAllCoroutines();
            boardHistory.Clear();
            isPaused = false;
            gameBoard = new GameBoard();
            gameBoard.CurrentPlayer = GetStartingPlayerForDifficulty(aiDifficulty);
            gameStartTime = Time.time;
            formationHeld = 0;

            if (uiManager == null)
            {
                Debug.LogError("GameController: UIManager not assigned.");
                return;
            }

            GenerateAIFormation();

            gameState = GameState.PreGameFormation;
            uiManager.UpdateDisplay(gameBoard);
            uiManager.ShowStatus("Arrange");
            uiManager.ResetGameTimer();
            uiManager.ClearLastMove();
            uiManager.SetUndoInteractable(false);
            uiManager.ClearHighlights();
        }

        // ── Pre-Game Formation Phase ─────────────────────────────────────────

        /// <summary>
        /// Randomly redistribute the AI's own 32 stones across its 16 pits, lightly
        /// biased toward the inner row, so the AI doesn't enter play with the
        /// passive uniform 2-per-pit shape.
        /// </summary>
        private void GenerateAIFormation()
        {
            int[] aiRows = aiPlayer == 1 ? new[] { 0, 1 } : new[] { 2, 3 };
            int innerRow = aiPlayer == 1 ? 1 : 2;
            int outerRow = aiPlayer == 1 ? 0 : 3;
            int cols = GameBoard.Cols;
            int totalStones = cols * aiRows.Length * 2; // 32

            foreach (int r in aiRows)
                for (int c = 0; c < cols; c++)
                    gameBoard.Set(r, c, 0);

            for (int i = 0; i < totalStones; i++)
            {
                int c = formationRandom.Next(cols);
                int r = formationRandom.NextDouble() < 0.6 ? innerRow : outerRow;
                gameBoard.Set(r, c, gameBoard.Get(r, c) + 1);
            }
        }

        public void OnFormationPitTouched(int row, int col)
        {
            if (gameState != GameState.PreGameFormation) return;
            if (!IsHumanRow(row)) return;

            if (formationHeld == 0)
            {
                int stones = gameBoard.Get(row, col);
                if (stones <= 0) return;

                gameBoard.Set(row, col, 0);
                formationHeld = stones;
                formationHeldFromRow = row;
                formationHeldFromCol = col;
            }
            else
            {
                gameBoard.Set(row, col, gameBoard.Get(row, col) + 1);
                formationHeld--;
            }

            uiManager.UpdateDisplay(gameBoard);
        }

        public void ConfirmFormationReady()
        {
            if (gameState != GameState.PreGameFormation) return;
            if (formationHeld != 0)
            {
                uiManager.ShowLastMove($"Place your remaining {formationHeld} stones first");
                return;
            }

            gameState = gameBoard.CurrentPlayer == humanPlayer ? GameState.HumanTurn : GameState.AiThinking;

            // The clock starts the moment the player commits their formation, whichever side moves
            // first — arranging stones shouldn't count against the game time.
            uiManager.StartTurnTimer();

            if (gameState == GameState.HumanTurn)
            {
                uiManager.ShowStatus("Your turn");
                uiManager.ShowLastMove("Select one of your highlighted pits");
                List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
                uiManager.HighlightLegalMoves(legalMoves);
            }
            else
            {
                uiManager.ShowStatus("Thinking...");
                uiManager.ClearHighlights();
                StartAIMove();
            }
        }

        private bool IsHumanRow(int row)
        {
            return humanPlayer == 1 ? (row == 0 || row == 1) : (row == 2 || row == 3);
        }

        private int GetStartingPlayerForDifficulty(Difficulty difficulty)
        {
            int defaultStarter = humanPlayer;
            return PlayerPrefs.GetInt($"LastLoser_Diff{(int)difficulty}", defaultStarter);
        }

        private void Update()
        {
            if (gameState == GameState.AiThinking)
                HandleAiThinkingState();

            HandleHintResult();
        }

        public void OnHoleTouched(int row, int col)
        {
            Log($"OnHoleTouched({row},{col}) state={gameState}");
            if (isPaused) return;

            if (gameState == GameState.PreGameFormation)
            {
                OnFormationPitTouched(row, col);
                return;
            }

            if (gameState != GameState.HumanTurn) return;

            gameState = GameState.ValidatingMove;
            ValidateAndApplyMove(row, col);
        }

        private void ValidateAndApplyMove(int row, int col)
        {
            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            Move selected = null;
            foreach (var m in legalMoves)
                if (m.Row == row && m.Col == col) { selected = m; break; }

            if (selected == null)
            {
                uiManager.FlashIllegalMove(row, col);
                uiManager.ShowStatus("Your turn");
                gameState = GameState.HumanTurn;
                return;
            }

            gameState = GameState.ApplyingMove;
            pendingMove = selected;
            StartCoroutine(ApplyHumanMoveCoroutine());
        }

        private IEnumerator ApplyHumanMoveCoroutine()
        {
            // Save state for undo before applying. The button is re-enabled once the human's
            // turn actually resumes (after the AI replies), not during this animation.
            boardHistory.Push(gameBoard.Clone());

            CancelHintSearch();

            GameBoard startingBoard = gameBoard.Clone();
            MoveResult moveResult = ApplyMoveWithLandingTrace(pendingMove, humanPlayer);
            uiManager.ShowStatus("Sowing");
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, "You");

            if (TryEndGameAfterMove(humanPlayer, aiPlayer)) yield break;

            gameState = GameState.AiThinking;
            uiManager.ShowStatus("Thinking...");
            uiManager.SetUndoInteractable(false);
            StartAIMove();
        }

        // Minimum time (seconds) the AI appears to "think" before moving, so play doesn't feel instant.
        private float MinAiThinkSeconds => aiDifficulty switch
        {
            Difficulty.Easy => 1.6f,
            Difficulty.Medium => 1.2f,
            _ => 0.8f,
        };

        private void StartAIMove()
        {
            CancelAiThinking();
            GameBoard snapshot = gameBoard.Clone();
            aiMoveCancellation = new CancellationTokenSource();
            CancellationToken token = aiMoveCancellation.Token;
            aiThinkStartTime = Time.time;
            aiMoveTask = Task.Run(() => aiAgent.SelectMove(snapshot, aiPlayer, token), token);
        }

        private void HandleAiThinkingState()
        {
            if (isPaused || aiMoveTask == null || !aiMoveTask.IsCompleted) return;
            if (Time.time - aiThinkStartTime < MinAiThinkSeconds) return;
            if (aiMoveTask.IsCanceled || aiMoveTask.IsFaulted) { aiMoveTask = null; return; }

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
                Debug.LogWarning("GameController: AI returned no move. Human wins.");
                FinishGame(humanPlayer);
            }
        }

        private IEnumerator ApplyAiMoveCoroutine(Move move)
        {
            GameBoard startingBoard = gameBoard.Clone();
            MoveResult moveResult = ApplyMoveWithLandingTrace(move, aiPlayer);
            uiManager.ShowStatus("AI sowing");
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, "AI");

            if (TryEndGameAfterMove(aiPlayer, humanPlayer)) yield break;

            gameState = GameState.HumanTurn;
            uiManager.ShowStatus("Your turn");
            uiManager.StartTurnTimer();
            // Undo is only allowed during the human's turn, so (re)enable it here rather than
            // during the move animation. This reverts the human's last move and the AI's reply.
            uiManager.SetUndoInteractable(boardHistory.Count > 0);
            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            uiManager.HighlightLegalMoves(legalMoves);
        }

        // ── Undo ─────────────────────────────────────────────────────────

        public void UndoLastMove()
        {
            if (gameState != GameState.HumanTurn || boardHistory.Count == 0) return;

            CancelHintSearch();
            gameBoard = boardHistory.Pop();
            uiManager.SetUndoInteractable(boardHistory.Count > 0);

            uiManager.UpdateDisplay(gameBoard);
            uiManager.ShowStatus("Your turn");
            uiManager.ShowLastMove("Move undone");
            uiManager.StartTurnTimer();

            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            uiManager.HighlightLegalMoves(legalMoves);
        }

        // ── Hint ─────────────────────────────────────────────────────────

        public void RequestHint()
        {
            if (gameState != GameState.HumanTurn || gameBoard == null) return;

            CancelHintSearch();
            uiManager.ShowLastMove("Finding a hint...");
            GameBoard snapshot = gameBoard.Clone();
            hintCancellation = new CancellationTokenSource();
            CancellationToken token = hintCancellation.Token;
            hintTask = Task.Run(() => aiAgent.GetHintMove(snapshot, humanPlayer, token), token);
        }

        private void HandleHintResult()
        {
            if (hintTask == null || !hintTask.IsCompleted) return;
            if (hintTask.IsCanceled || hintTask.IsFaulted) { hintTask = null; return; }

            Move hint = hintTask.Result;
            hintTask = null;
            hintCancellation = null;

            if (hint != null && gameState == GameState.HumanTurn)
            {
                uiManager.ShowLastMove($"Hint: play Hole {hint.Col + 1}");
                uiManager.FlashHintPit(hint.Row, hint.Col);
            }
            else if (gameState == GameState.HumanTurn)
            {
                uiManager.ShowLastMove("No hint available");
            }
        }

        private void CancelHintSearch()
        {
            hintCancellation?.Cancel();
            hintCancellation?.Dispose();
            hintCancellation = null;
            hintTask = null;
        }

        // ── Pause / Restart ───────────────────────────────────────────────

        public void RestartGame()
        {
            Log("RestartGame()");
            InitializeSystems();
            gameState = GameState.Initialising;
            InitializeGame();
        }

        public void SetPaused(bool paused)
        {
            Log($"SetPaused({paused})");
            isPaused = paused;

            if (paused)
            {
                CancelAiThinking();
                return;
            }

            if (gameState == GameState.AiThinking && aiMoveTask == null)
                StartAIMove();
        }

        public void SetAIDifficulty(int difficultyInt)
        {
            InitializeSystems();
            aiDifficulty = (Difficulty)difficultyInt;
            PlayerPrefs.SetInt("AIDifficulty", difficultyInt);
            PlayerPrefs.Save();
            var eval = new EvaluationFunction(gameEngine);
            aiAgent = new AIAgent(gameEngine, eval, aiDifficulty);
        }

        // ── Internal ──────────────────────────────────────────────────────

        private void ShowMoveFeedback(MoveResult moveResult, string actor)
        {
            string info = moveResult.CapturedStones > 0
                ? $"{actor}: Hole {moveResult.Move.Col + 1} → Captured {moveResult.CapturedStones}"
                : $"{actor}: Hole {moveResult.Move.Col + 1}";
            uiManager.ShowLastMove(info);
        }

        /// <summary>
        /// Applies a real, on-board move. Landing-outcome console logging is governed solely by the
        /// serialized <see cref="debugLandingOutcomes"/> toggle (applied once in InitializeSystems),
        /// so it stays off by default. The old per-move force-enable here was a bug: GameEngine's
        /// flag is static and the hint/AI searches run on background threads, so forcing it true
        /// bled into their thousands of ApplyMoveWithResult calls and flooded the Console.
        /// </summary>
        private MoveResult ApplyMoveWithLandingTrace(Move move, int player)
        {
            return gameEngine.ApplyMoveWithResult(gameBoard, move, player);
        }

        /// <summary>
        /// Checks for game-over immediately after a move resolves, without waiting for the
        /// opponent to take a (possibly pointless) turn first. Each player only ever sows
        /// within their own two rows, and the opponent can only ever zero out their pits via
        /// capture — never add stones — so once the player who just moved is left with no
        /// legal moves of their own, that outcome is already final and doesn't need the
        /// opponent to play it out to confirm.
        /// </summary>
        private bool TryEndGameAfterMove(int moverPlayer, int opponentPlayer)
        {
            if (gameEngine.GetLegalMoves(gameBoard, opponentPlayer).Count == 0)
            {
                FinishGame(moverPlayer);
                return true;
            }

            if (gameEngine.GetLegalMoves(gameBoard, moverPlayer).Count == 0)
            {
                FinishGame(opponentPlayer);
                return true;
            }

            return false;
        }

        private void FinishGame(int winner)
        {
            Log($"FinishGame(winner={winner})");
            CancelAiThinking();
            CancelHintSearch();
            gameState = GameState.GameOver;

            // Score is just each player's live pit total — there's no separate captured pile.
            int p1Stones = gameBoard != null ? gameEngine.GetPlayerStones(gameBoard, 1) : 0;
            int p2Stones = gameBoard != null ? gameEngine.GetPlayerStones(gameBoard, 2) : 0;

            float elapsed = Time.time - gameStartTime;
            LastGameSeconds = elapsed;
            bool humanWon = winner == humanPlayer;
            ProfileManager.Instance?.RecordGameResult((int)aiDifficulty, humanWon, elapsed);

            int loser = winner == humanPlayer ? aiPlayer : humanPlayer;
            PlayerPrefs.SetInt($"LastLoser_Diff{(int)aiDifficulty}", loser);
            PlayerPrefs.Save();

            uiManager?.ShowGameOver(winner);
            GameOver?.Invoke(winner, p1Stones, p2Stones);
        }

        private void CancelAiThinking()
        {
            aiMoveCancellation?.Cancel();
            aiMoveCancellation?.Dispose();
            aiMoveCancellation = null;
            aiMoveTask = null;
            aiMove = null;
        }

        private void Log(string msg)
        {
            if (enableBreadcrumbLogs)
                Debug.Log($"[GameController] {msg}", this);
        }
    }
}
