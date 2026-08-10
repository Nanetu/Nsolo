using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using NsoloGame.Core;
using NsoloGame.AI;
using NsoloGame.Players;

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
        /// <summary>Hot-seat: waiting for whichever player is currently holding the device.</summary>
        HotSeatTurn,
        /// <summary>Hot-seat: handover card is up, or the board is turning round. Input is dead.</summary>
        HotSeatHandover,
        GameOver
    }

    /// <summary>
    /// Who the two seats belong to. VersusComputer is the original single-player game and its code
    /// path is untouched by hot-seat; VersusHuman runs the agent-driven loop further down.
    /// </summary>
    public enum GameMode
    {
        VersusComputer,
        VersusHuman
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

        // ── Hot-seat (local two-player) ──────────────────────────────────────
        // Everything below is inert in VersusComputer mode.

        [Header("Hot-seat")]
        [Tooltip("Turns the board round between hot-seat turns. Auto-found if left empty.")]
        [SerializeField] private BoardFlipper boardFlipper;

        private GameMode mode = GameMode.VersusComputer;

        /// <summary>
        /// The two seats, indexed by player number so [1] and [2] are the players and [0] is
        /// unused. Typed as the interface rather than as LocalHumanAgent on purpose: the loop below
        /// works with whatever is sitting in a seat, so putting an <see cref="AIPlayerAgent"/> or a
        /// future network agent in one is an assignment, not a rewrite.
        /// </summary>
        private readonly IPlayerAgent[] hotSeatAgents = new IPlayerAgent[3];

        private int hotSeatCurrentPlayer = 1;
        private Task<Move> hotSeatMoveTask;
        private CancellationTokenSource hotSeatMoveCancellation;

        /// <summary>Which player is laying out their stones during the shared formation phase.</summary>
        private int arrangingPlayer = 1;

        /// <summary>
        /// Set by the first press of FORFEIT and cleared by anything else. The button sits exactly
        /// where HINT does in single-player, so a single tap ending the game outright is a mis-tap
        /// waiting to happen — the second press is what actually concedes.
        /// </summary>
        private bool forfeitArmed;

        private Move pendingMove;

        // Whose move the stone animator is currently playing, so HandleSowingSegment knows whether
        // a capture is something the player did or something done to them.
        private bool currentMoverIsHuman;

        // How many sowing segments the move in flight has, used to hold the skip tip back until a
        // chain is actually long enough that skipping it saves anything.
        private int currentMoveSegmentCount;

        // The coroutine playing the current move, held so undo can cut the AI's reply short.
        private Coroutine activeMoveRoutine;

        /// <summary>Segments a chain needs before the skip gesture is worth interrupting to teach.</summary>
        private const int SkipTipMinimumSegments = 3;
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

        // The bottom pill is one button wearing two hats in single-player and a third in hot-seat
        // — see OnActionButtonPressed.
        private const string ActionLabelStart = "START";
        private const string ActionLabelHint = "HINT";
        private const string ActionLabelForfeit = "FORFEIT";

        public event System.Action<int, int, int> GameOver;
        public GameState CurrentState => gameState;
        public int CurrentDifficulty => (int)aiDifficulty;
        public GameMode CurrentMode => mode;

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

            // Subscribed here rather than in Awake so PitStoneVisualizer has built its animator.
            uiManager?.SetSowingSegmentListener(HandleSowingSegment);

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
            mode = GameMode.VersusComputer;
            InitializeSystems();
            SetAIDifficulty(difficultyInt);
            InitializeGame();
        }

        /// <summary>
        /// Entry point for local two-player. No difficulty is chosen because no AI plays — the menu
        /// goes straight from the mode panel into the game.
        /// </summary>
        public void StartNewHotSeatGame()
        {
            Log("StartNewHotSeatGame()");
            mode = GameMode.VersusHuman;
            InitializeSystems();

            hotSeatAgents[1] = new LocalHumanAgent("Player 1");
            hotSeatAgents[2] = new LocalHumanAgent("Player 2");

            InitializeGame();
        }

        private void InitializeGame()
        {
            Log("InitializeGame()");
            CancelAiThinking();
            CancelHintSearch();
            CancelHotSeatTurn();
            StopAllCoroutines();
            PassDeviceModal.Instance?.ForceHide();
            boardHistory.Clear();
            isPaused = false;
            forfeitArmed = false;
            gameBoard = new GameBoard();
            gameStartTime = Time.time;
            formationHeld = 0;

            bool hotSeat = mode == GameMode.VersusHuman;

            // Hot-seat always opens with player 1, whose seat is also the camera's home position.
            // The AI game keeps its "loser of the last game starts" rule.
            gameBoard.CurrentPlayer = hotSeat ? 1 : GetStartingPlayerForDifficulty(aiDifficulty);
            hotSeatCurrentPlayer = gameBoard.CurrentPlayer;
            arrangingPlayer = 1;

            if (uiManager == null)
            {
                Debug.LogError("GameController: UIManager not assigned.");
                return;
            }

            // Unconditional, and before either branch: the camera is shared by both modes, so a
            // two-player game abandoned on player 2's side would otherwise hand its upside-down
            // view to the next game against the computer. Snapping rather than animating because
            // there is nothing to narrate — this is a board being set up, not a device changing
            // hands — and it also puts right a flip left half-played by the StopAllCoroutines above.
            ResolveBoardFlipper();
            boardFlipper?.ResetToBase();

            if (hotSeat)
            {
                // Both players lay out their own stones, so neither side is generated.
                //
                // Seats are filled here rather than only in StartNewHotSeatGame because a restart
                // re-enters through this method alone: without it, replaying a two-player game
                // would find empty chairs if the agents were ever cleared.
                if (hotSeatAgents[1] == null) hotSeatAgents[1] = new LocalHumanAgent("Player 1");
                if (hotSeatAgents[2] == null) hotSeatAgents[2] = new LocalHumanAgent("Player 2");
            }
            else
            {
                GenerateAIFormation();
            }

            gameState = GameState.PreGameFormation;
            uiManager.UpdateDisplay(gameBoard);
            uiManager.ShowStatus(hotSeat ? "Player 1: Arrange" : "Arrange");
            uiManager.ResetGameTimer();
            uiManager.ClearLastMove();
            uiManager.SetUndoInteractable(false);
            uiManager.ClearHighlights();
            RefreshActionButton();

            // Arranging happens in silence — the music waits for START.
            AudioManager.Silence();
            TutorialCoach.Show(TutorialTip.ArrangeStones);
        }

        // ── Action Button (START / HINT) ─────────────────────────────────────

        /// <summary>
        /// The single pill at the bottom of the board. There is only one button because the
        /// background art only has room for one: it commits the opening formation while the
        /// player is still arranging, and asks for a hint from then on.
        /// </summary>
        public void OnActionButtonPressed()
        {
            if (gameState == GameState.PreGameFormation)
            {
                ConfirmFormationReady();
                return;
            }

            // Same pill, third job: there is no AI to ask for a hint in a two-player game, so the
            // button concedes instead.
            if (mode == GameMode.VersusHuman)
            {
                RequestForfeit();
                return;
            }

            RequestHint();
        }

        /// <summary>
        /// Points the pill at whichever job the current state calls for. Called from every place
        /// that moves the game between states, next to the matching ShowStatus call.
        /// </summary>
        private void RefreshActionButton()
        {
            bool arranging = gameState == GameState.PreGameFormation;

            if (mode == GameMode.VersusHuman)
            {
                string label = arranging
                    ? ActionLabelStart
                    : (forfeitArmed ? "CONFIRM?" : ActionLabelForfeit);

                uiManager?.SetActionButton(
                    label,
                    arranging || gameState == GameState.HotSeatTurn);
                return;
            }

            uiManager?.SetActionButton(
                arranging ? ActionLabelStart : ActionLabelHint,
                arranging || gameState == GameState.HumanTurn);
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
            if (!IsRowOwnedByActivePlayer(row)) return;

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

            if (mode == GameMode.VersusHuman)
            {
                ConfirmHotSeatFormation();
                return;
            }

            gameState = gameBoard.CurrentPlayer == humanPlayer ? GameState.HumanTurn : GameState.AiThinking;

            // The clock starts the moment the player commits their formation, whichever side moves
            // first — arranging stones shouldn't count against the game time.
            uiManager.StartTurnTimer();

            // Committing the formation is the point the game actually starts, so this is where
            // the music comes in — not back at difficulty selection.
            AudioManager.StartGameMusic();

            // From here on the pill is the hint button.
            RefreshActionButton();

            if (gameState == GameState.HumanTurn)
            {
                uiManager.ShowStatus("Your turn");
                uiManager.ShowLastMove("Select one of your highlighted pits");
                List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
                uiManager.HighlightLegalMoves(legalMoves);
                TutorialCoach.Show(TutorialTip.YourPits);
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
            return OwnsRow(humanPlayer, row);
        }

        /// <summary>Player 1 owns the bottom two rows, player 2 the top two.</summary>
        private static bool OwnsRow(int player, int row)
        {
            return player == 1 ? (row == 0 || row == 1) : (row == 2 || row == 3);
        }

        /// <summary>
        /// Whether the row belongs to whoever the board is currently waiting on. In single-player
        /// that is always the human; in hot-seat it follows the seat that is holding the device,
        /// which is what makes the same tap handler work for both players.
        /// </summary>
        private bool IsRowOwnedByActivePlayer(int row)
        {
            if (mode != GameMode.VersusHuman) return IsHumanRow(row);

            int active = gameState == GameState.PreGameFormation ? arrangingPlayer : hotSeatCurrentPlayer;
            return OwnsRow(active, row);
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

            if (gameState == GameState.HotSeatTurn)
                HandleHotSeatMoveResult();

            HandleHintResult();
        }

        public void OnHoleTouched(int row, int col)
        {
            Log($"OnHoleTouched({row},{col}) state={gameState}");
            if (isPaused) return;

            // A modal tip is waiting on its Got It button — the board stays inert until it goes.
            if (TutorialCoach.Instance != null && TutorialCoach.Instance.IsBlocking) return;

            // Hot-seat: dead while the handover card is up and while the board is turning round, so
            // a tap meant for the card cannot fall through and select a pit the incoming player has
            // not properly seen yet.
            if (gameState == GameState.HotSeatHandover) return;
            if (PassDeviceModal.Instance != null && PassDeviceModal.Instance.IsBlocking) return;
            if (boardFlipper != null && boardFlipper.IsFlipping) return;

            // Reaching across to the far side is the classic first-timer mistake, so it earns an
            // explanation rather than silence — while arranging and during play alike.
            if (!IsRowOwnedByActivePlayer(row) &&
                (gameState == GameState.PreGameFormation || gameState == GameState.HumanTurn ||
                 gameState == GameState.HotSeatTurn))
            {
                uiManager.ShowLastMove(mode == GameMode.VersusHuman
                    ? "Those rows belong to the other player"
                    : "Those rows belong to your opponent");
                TutorialCoach.Show(TutorialTip.OpponentRows);
                Haptics.Light();
                return;
            }

            if (gameState == GameState.PreGameFormation)
            {
                OnFormationPitTouched(row, col);
                return;
            }

            if (gameState == GameState.HotSeatTurn)
            {
                SubmitHotSeatMove(row, col);
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
                AudioManager.Illegal();
                Haptics.Light();
                TutorialCoach.Show(TutorialTip.NeedTwoStones);
                return;
            }

            gameState = GameState.ApplyingMove;
            pendingMove = selected;
            activeMoveRoutine = StartCoroutine(ApplyHumanMoveCoroutine());
        }

        private IEnumerator ApplyHumanMoveCoroutine()
        {
            // Save state for undo before applying. The button is re-enabled once the human's
            // turn actually resumes (after the AI replies), not during this animation.
            boardHistory.Push(gameBoard.Clone());

            // Off while the player's own stones are in the air; it comes back the moment the AI
            // takes over, and stays live right through the AI's reply.
            uiManager.SetUndoInteractable(false);

            CancelHintSearch();

            currentMoverIsHuman = true;
            GameBoard startingBoard = gameBoard.Clone();
            MoveResult moveResult = ApplyMoveWithLandingTrace(pendingMove, humanPlayer);
            currentMoveSegmentCount = moveResult.SowingSegments?.Count ?? 0;
            uiManager.ShowStatus("Sowing");
            RefreshActionButton();
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, "You");

            if (TryEndGameAfterMove(humanPlayer, aiPlayer)) yield break;

            gameState = GameState.AiThinking;
            uiManager.ShowStatus("Thinking...");
            uiManager.SetUndoInteractable(boardHistory.Count > 0);
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
                activeMoveRoutine = StartCoroutine(ApplyAiMoveCoroutine(aiMove));
            }
            else
            {
                Debug.LogWarning("GameController: AI returned no move. Human wins.");
                FinishGame(humanPlayer);
            }
        }

        private IEnumerator ApplyAiMoveCoroutine(Move move)
        {
            currentMoverIsHuman = false;
            GameBoard startingBoard = gameBoard.Clone();
            MoveResult moveResult = ApplyMoveWithLandingTrace(move, aiPlayer);
            currentMoveSegmentCount = moveResult.SowingSegments?.Count ?? 0;
            uiManager.ShowStatus("Computer sowing");
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, "Computer");

            if (TryEndGameAfterMove(aiPlayer, humanPlayer)) yield break;

            activeMoveRoutine = null;
            gameState = GameState.HumanTurn;
            uiManager.ShowStatus("Your turn");
            uiManager.StartTurnTimer();
            // Undo is only allowed during the human's turn, so (re)enable it here rather than
            // during the move animation. This reverts the human's last move and the AI's reply.
            uiManager.SetUndoInteractable(boardHistory.Count > 0);
            RefreshActionButton();
            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            uiManager.HighlightLegalMoves(legalMoves);
        }

        // ── Hot-seat (local two-player) ──────────────────────────────────
        //
        // The path above hardcodes "human moves, then the AI replies" — two methods that each name
        // the other as their successor. This one holds no such assumption: it asks whichever agent
        // owns the current seat for a move, plays it, hands the device over, and asks the other. It
        // never learns which kind of agent answered, which is the point. Putting a network agent in
        // one of the two seats is the only change an online mode would need here.

        private void ConfirmHotSeatFormation()
        {
            AudioManager.Click();
            Haptics.Light();

            formationHeld = 0;
            gameState = GameState.HotSeatHandover;
            RefreshActionButton();

            if (arrangingPlayer == 1)
            {
                // Player 1 is happy with their side; the device goes across so player 2 can lay
                // out their own rather than inheriting a randomised one.
                StartCoroutine(HandOverToPlayer(2, resumeFormation: true));
                return;
            }

            // Both sides are set, so this is where the game actually begins — the clock and the
            // music start here, exactly as they do when the single-player formation is committed.
            uiManager.StartTurnTimer();
            AudioManager.StartGameMusic();

            StartCoroutine(HandOverToPlayer(1, resumeFormation: false));
        }

        /// <summary>
        /// Shows the handover card, turns the board round, and then gives the incoming player
        /// either the formation phase or their turn.
        /// </summary>
        private IEnumerator HandOverToPlayer(int player, bool resumeFormation)
        {
            gameState = GameState.HotSeatHandover;
            uiManager.ClearHighlights();
            uiManager.SetUndoInteractable(false);
            uiManager.ShowStatus("Pass the device");

            string name = NameOf(player);

            // Card first, then the rotation: the incoming player takes the phone while it still
            // shows a static board, and watches their own rows swing into place.
            if (PassDeviceModal.Instance != null)
                yield return PassDeviceModal.Instance.ShowAndWait(name);

            ResolveBoardFlipper();
            if (boardFlipper != null)
                yield return boardFlipper.Flip();

            // Pit counts are screen-space labels placed from WorldToScreenPoint, so they are still
            // sitting where the old camera put them until the board is redrawn.
            uiManager.UpdateDisplay(gameBoard);

            if (resumeFormation)
            {
                arrangingPlayer = player;
                gameState = GameState.PreGameFormation;
                uiManager.ShowStatus($"{name}: Arrange");
                uiManager.ShowLastMove("Arrange your stones, then press START");
                RefreshActionButton();
                TutorialCoach.Show(TutorialTip.ArrangeStones);
                yield break;
            }

            BeginHotSeatTurn(player);
        }

        private void BeginHotSeatTurn(int player)
        {
            hotSeatCurrentPlayer = player;
            gameBoard.CurrentPlayer = player;
            forfeitArmed = false;
            gameState = GameState.HotSeatTurn;

            IPlayerAgent agent = hotSeatAgents[player];
            uiManager.ShowStatus($"{NameOf(player)}'s turn");
            uiManager.StartTurnTimer();
            RefreshActionButton();

            // Only an agent that plays by tapping gets the board lit up for it. An agent that
            // thinks for itself — the AI today, a remote player later — leaves the board plain.
            if (agent != null && agent.RequiresBoardInput)
            {
                uiManager.HighlightLegalMoves(gameEngine.GetLegalMoves(gameBoard, player));
                uiManager.ShowLastMove("Select one of your highlighted pits");
            }

            RequestHotSeatMove(player);
        }

        private void RequestHotSeatMove(int player)
        {
            CancelHotSeatTurn();

            IPlayerAgent agent = hotSeatAgents[player];
            if (agent == null)
            {
                Debug.LogError($"GameController: no agent seated for player {player}.");
                return;
            }

            hotSeatMoveCancellation = new CancellationTokenSource();
            hotSeatMoveTask = agent.RequestMove(
                gameBoard.Clone(), player, hotSeatMoveCancellation.Token);
        }

        /// <summary>
        /// Feeds a tap to the current seat's agent. Legality is settled here, before the agent sees
        /// it, which is the same order single-player uses — the agent's job is to name a move, not
        /// to know the rules.
        /// </summary>
        private void SubmitHotSeatMove(int row, int col)
        {
            // Only an agent that takes board input can be answered by a tap. Anything else in the
            // seat resolves its own move and this is simply not the way it arrives.
            LocalHumanAgent agent = hotSeatAgents[hotSeatCurrentPlayer] as LocalHumanAgent;
            if (agent == null || !agent.IsAwaitingInput) return;

            if (forfeitArmed)
            {
                forfeitArmed = false;
                RefreshActionButton();
            }

            Move selected = null;
            foreach (Move m in gameEngine.GetLegalMoves(gameBoard, hotSeatCurrentPlayer))
                if (m.Row == row && m.Col == col) { selected = m; break; }

            if (selected == null)
            {
                uiManager.FlashIllegalMove(row, col);
                AudioManager.Illegal();
                Haptics.Light();
                TutorialCoach.Show(TutorialTip.NeedTwoStones);
                return;
            }

            agent.SubmitMove(selected);
        }

        private void HandleHotSeatMoveResult()
        {
            if (isPaused || hotSeatMoveTask == null || !hotSeatMoveTask.IsCompleted) return;
            if (hotSeatMoveTask.IsCanceled || hotSeatMoveTask.IsFaulted)
            {
                hotSeatMoveTask = null;
                return;
            }

            Move move = hotSeatMoveTask.Result;
            hotSeatMoveTask = null;

            if (move == null)
            {
                Debug.LogWarning($"GameController: player {hotSeatCurrentPlayer} produced no move.");
                FinishGame(Opponent(hotSeatCurrentPlayer));
                return;
            }

            gameState = GameState.ApplyingMove;
            activeMoveRoutine = StartCoroutine(ApplyHotSeatMoveCoroutine(move, hotSeatCurrentPlayer));
        }

        private IEnumerator ApplyHotSeatMoveCoroutine(Move move, int player)
        {
            int opponent = Opponent(player);

            // Both movers are people here, so captures are always worded as something a player did.
            currentMoverIsHuman = true;

            uiManager.ClearHighlights();

            GameBoard startingBoard = gameBoard.Clone();
            MoveResult moveResult = ApplyMoveWithLandingTrace(move, player);
            currentMoveSegmentCount = moveResult.SowingSegments?.Count ?? 0;

            uiManager.ShowStatus("Sowing");
            RefreshActionButton();
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, NameOf(player));

            activeMoveRoutine = null;

            if (TryEndGameAfterMove(player, opponent)) yield break;

            yield return HandOverToPlayer(opponent, resumeFormation: false);
        }

        /// <summary>
        /// Conceding. The first press arms it and relabels the pill; the second actually resigns.
        /// This button sits exactly where HINT does in single-player, and one stray tap ending the
        /// game outright is too easy to do by accident — delete the armed branch for a single-press
        /// forfeit.
        /// </summary>
        private void RequestForfeit()
        {
            if (gameState != GameState.HotSeatTurn) return;

            if (!forfeitArmed)
            {
                forfeitArmed = true;
                RefreshActionButton();
                uiManager.ShowLastMove("Press again to concede this game");
                Haptics.Light();
                AudioManager.Click();
                return;
            }

            forfeitArmed = false;
            int conceding = hotSeatCurrentPlayer;

            Log($"Forfeit by player {conceding}.");
            CancelHotSeatTurn();
            uiManager.ShowLastMove($"{NameOf(conceding)} forfeited");
            FinishGame(Opponent(conceding));
        }

        private void CancelHotSeatTurn()
        {
            hotSeatMoveCancellation?.Cancel();
            hotSeatMoveCancellation?.Dispose();
            hotSeatMoveCancellation = null;
            hotSeatMoveTask = null;

            (hotSeatAgents[1] as LocalHumanAgent)?.Abandon();
            (hotSeatAgents[2] as LocalHumanAgent)?.Abandon();
        }

        private string NameOf(int player)
        {
            return hotSeatAgents[player]?.DisplayName ?? $"Player {player}";
        }

        private static int Opponent(int player) => player == 1 ? 2 : 1;

        private void ResolveBoardFlipper()
        {
            if (boardFlipper == null) boardFlipper = FindObjectOfType<BoardFlipper>();
        }

        // ── Undo ─────────────────────────────────────────────────────────

        /// <summary>
        /// Undo is allowed on the player's own turn and, deliberately, right through the AI's reply
        /// — while it is thinking and while its stones are still in the air. Waiting out a long
        /// relay before being allowed to take back a move the player already regrets is just a
        /// penalty for the AI being slow.
        ///
        /// This is safe because the history entry was pushed *before* the player's move, so
        /// rewinding to it produces the same board whether or not the AI has replied yet. The
        /// reply simply never happened.
        /// </summary>
        private bool CanUndoNow()
        {
            switch (gameState)
            {
                case GameState.HumanTurn:
                    return true;
                case GameState.AiThinking:
                    return true;
                // Only once the player's own sowing has resolved — interrupting their own move
                // mid-flight would be rewinding something they are still watching happen.
                case GameState.ApplyingMove:
                    return !currentMoverIsHuman;
                default:
                    return false;
            }
        }

        public void UndoLastMove()
        {
            if (boardHistory.Count == 0 || !CanUndoNow()) return;

            TutorialCoach.Show(TutorialTip.UndoButton);
            Haptics.Light();
            AudioManager.Click();

            bool interruptedAi = gameState != GameState.HumanTurn;

            if (interruptedAi)
            {
                // Cut the AI off: stop the search, stop the coroutine driving its move, and clear
                // the animator's flags by hand since its own cleanup will never run.
                CancelAiThinking();
                if (activeMoveRoutine != null)
                {
                    StopCoroutine(activeMoveRoutine);
                    activeMoveRoutine = null;
                }
                uiManager.CancelMoveAnimation();
                gameState = GameState.HumanTurn;
            }

            CancelHintSearch();
            gameBoard = boardHistory.Pop();
            uiManager.SetUndoInteractable(boardHistory.Count > 0);

            uiManager.UpdateDisplay(gameBoard);
            uiManager.ShowStatus("Your turn");
            uiManager.ShowLastMove(interruptedAi
                ? "Move undone — computer's reply cancelled"
                : "Move undone");
            uiManager.StartTurnTimer();

            List<Move> legalMoves = gameEngine.GetLegalMoves(gameBoard, humanPlayer);
            uiManager.HighlightLegalMoves(legalMoves);
        }

        // ── Hint ─────────────────────────────────────────────────────────

        public void RequestHint()
        {
            if (gameState != GameState.HumanTurn || gameBoard == null) return;

            TutorialCoach.Show(TutorialTip.HintButton);
            Haptics.Light();
            AudioManager.Click();

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

        /// <summary>
        /// Called by the stone animator as each sowing segment begins, which is the moment the
        /// mechanic it demonstrates actually happens — a tip raised here freezes the board
        /// mid-move, so the player is looking at the relay or capture while reading about it.
        ///
        /// Segment 0 is the opening sow and teaches nothing. After that, a segment carrying
        /// ExtraSources is a capture (stones scooped from the opponent's two pits), and anything
        /// else is a relay.
        /// </summary>
        private void HandleSowingSegment(SowingSegment segment, int index)
        {
            if (segment == null || index == 0) return;

            bool byHuman = currentMoverIsHuman;

            if (segment.ExtraSources != null && segment.ExtraSources.Count > 0)
            {
                AudioManager.Capture();
                Haptics.Medium();
                TutorialCoach.Show(byHuman ? TutorialTip.FirstCapture : TutorialTip.CapturedByAi);
                return;
            }

            if (!byHuman) return;

            TutorialCoach.Show(TutorialTip.FirstRelay);

            // Queued behind the relay explanation, and only on a chain long enough to be worth
            // escaping. Dismissing it with a double tap also performs the skip, so the gesture the
            // player just learned does exactly what it said it would.
            if (currentMoveSegmentCount >= SkipTipMinimumSegments)
                TutorialCoach.Show(TutorialTip.SkipAnimation, onDismissed: () => uiManager?.TrySkipMoveAnimation());
        }

        /// <summary>
        /// Hook for a double tap on the board. Returns true when a move was actually fast-forwarded,
        /// so UIManager knows the gesture was consumed and should not also select a pit.
        /// </summary>
        public bool TrySkipAnimation()
        {
            if (uiManager == null || !uiManager.TrySkipMoveAnimation()) return false;
            Haptics.Light();
            return true;
        }

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
            CancelHotSeatTurn();
            forfeitArmed = false;
            gameState = GameState.GameOver;
            RefreshActionButton();

            // Score is just each player's live pit total — there's no separate captured pile.
            int p1Stones = gameBoard != null ? gameEngine.GetPlayerStones(gameBoard, 1) : 0;
            int p2Stones = gameBoard != null ? gameEngine.GetPlayerStones(gameBoard, 2) : 0;

            float elapsed = Time.time - gameStartTime;
            LastGameSeconds = elapsed;
            bool humanWon = winner == humanPlayer;
            bool hotSeat = mode == GameMode.VersusHuman;

            // Hot-seat results are deliberately not recorded. The whole stats system is keyed by AI
            // difficulty — win rate, per-difficulty records, the "loser starts next" rule — and
            // filing two-player games under a difficulty nobody played would make those numbers
            // mean nothing. Tracking them properly needs its own counters, which is future work.
            if (!hotSeat)
            {
                ProfileManager.Instance?.RecordGameResult((int)aiDifficulty, humanWon, elapsed);
            }

            AudioManager.Silence();
            // Somebody in the room won a hot-seat game, so it always gets the victory sting.
            AudioManager.GameOver(hotSeat || humanWon);
            Haptics.Heavy();

            if (!hotSeat)
            {
                int loser = winner == humanPlayer ? aiPlayer : humanPlayer;
                PlayerPrefs.SetInt($"LastLoser_Diff{(int)aiDifficulty}", loser);
                PlayerPrefs.Save();
            }

            // Started before the panel goes up so the turn is already under way behind it.
            if (hotSeat) StartCoroutine(ReturnBoardToDefaultView());

            uiManager?.ShowGameOver(winner, hotSeat);
            GameOver?.Invoke(winner, p1Stones, p2Stones);
        }

        /// <summary>
        /// Turns the board back to player 1's side once a hot-seat game is decided.
        ///
        /// A handover leaves the camera on whichever seat played last, and the result panel is the
        /// point at which the far side's view stops being useful — nobody is going to take another
        /// turn from it. Doing it here rather than only when the next game starts means the board
        /// is never left sitting upside down between games, whichever way the player leaves the
        /// panel: restart, main menu, or straight into a game against the computer.
        ///
        /// It plays the same half turn a handover does rather than snapping, so the board reads as
        /// being set back down between the two players rather than jumping orientation.
        /// </summary>
        private IEnumerator ReturnBoardToDefaultView()
        {
            ResolveBoardFlipper();
            if (boardFlipper == null || !boardFlipper.IsFlipped) yield break;

            yield return boardFlipper.Flip();

            // Pit counts are screen-space labels placed with WorldToScreenPoint, so they are still
            // where the old camera put them until the board is redrawn.
            uiManager?.UpdateDisplay(gameBoard);
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
