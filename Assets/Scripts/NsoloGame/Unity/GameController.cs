using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using NsoloGame.Core;
using NsoloGame.AI;
using NsoloGame.Net;
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
        /// <summary>Online: this player's turn. Taps are accepted and sent as move requests.</summary>
        OnlineLocalTurn,
        /// <summary>Online: the opponent's turn. The board is live but inert.</summary>
        OnlineOpponentTurn,
        /// <summary>
        /// Online: nothing to do but wait on the network — for the opponent's formation, or for the
        /// host's answer to a move already sent. Input is dead.
        /// </summary>
        OnlineWaiting,
        GameOver
    }

    /// <summary>
    /// Who the two seats belong to. VersusComputer is the original single-player game and its code
    /// path is untouched by hot-seat; VersusHuman runs the agent-driven loop further down, and
    /// Online runs a third loop that takes its moves from the network rather than computing them.
    /// </summary>
    public enum GameMode
    {
        VersusComputer,
        VersusHuman,
        Online
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

        /// <summary>
        /// Which seat opens the current two-player game. Not always player 1 — see
        /// <see cref="NextHotSeatStarter"/>.
        /// </summary>
        private int hotSeatStarter = 1;

        /// <summary>
        /// Who opened the last two-player game, so the next one can open with the other seat.
        ///
        /// Kept in PlayerPrefs rather than a field because two players passing a phone back and
        /// forth are one session in every sense except the app's: they put it down between games,
        /// and an in-memory flag would reset and hand player 1 the opening move again.
        /// </summary>
        private const string HotSeatLastStarterKey = "HotSeatLastStarter";

        /// <summary>Which player is laying out their stones during the shared formation phase.</summary>
        private int arrangingPlayer = 1;

        // ── Online ───────────────────────────────────────────────────────
        // Inert in both local modes.

        /// <summary>
        /// The live match, or null when not playing online. This is the only thing here that knows
        /// anything about the network, and it is deliberately an object this class is handed rather
        /// than one it builds — GameController does not connect, join, or send; it reacts to what
        /// the match tells it and asks the match for what it wants.
        /// </summary>
        private NetworkMatch networkMatch;

        /// <summary>Which seat this device plays online. 1 for the room's creator, 2 for the joiner.</summary>
        private int onlineLocalPlayer = 1;

        /// <summary>
        /// Authoritative results waiting their turn on screen. See <see cref="HandleMoveApplied"/> —
        /// the board is settled long before the stones finish moving, so these have to be shown one
        /// after another rather than as they arrive.
        /// </summary>
        private readonly Queue<MoveResult> onlineResults = new Queue<MoveResult>();

        private bool playingOnlineMove;

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

        /// <summary>
        /// An online match this controller gave up on, rather than one the transport reported as
        /// over.
        ///
        /// There is exactly one of those: a reconnect that got as far as the room and then never
        /// received a position (see <see cref="CheckResyncDeadline"/>). The transport is perfectly
        /// happy in that case — we are connected, in a room, and nothing has failed as far as it can
        /// tell — so its MatchEnded never fires, and without this the closing dialog that normally
        /// follows the end of an online game would never be raised. The player would be left on a
        /// dead board with a pause button that had nothing to say.
        /// </summary>
        public event System.Action<Net.MatchEndReason> OnlineMatchAbandoned;

        public GameState CurrentState => gameState;
        public int CurrentDifficulty => (int)aiDifficulty;
        public GameMode CurrentMode => mode;

        /// <summary>
        /// Which seat this device is playing online. Meaningless in the local modes. Exposed so the
        /// result panel can work out whether "you" won, which is not the same as player 1 winning.
        /// </summary>
        public int OnlineLocalPlayer => onlineLocalPlayer;

        /// <summary>
        /// Wall-clock length of the game that just finished. Captured once in FinishGame so the
        /// game-over panel keeps showing the final time instead of a clock that keeps ticking.
        /// </summary>
        public float LastGameSeconds { get; private set; }

        /// <summary>
        /// Stones this device's player took over the finished game, and their longest relay in it.
        ///
        /// Both are per-player rather than per-game totals. A relay is the run of laps a single
        /// move turns into when the last stone keeps landing in an occupied hole, and it is the one
        /// number in Nsolo a player actually brags about — so it has to be *theirs*, not whichever
        /// side happened to manage it. Captures likewise: the board carries no separate captured
        /// pile, so this is the only place the figure survives the game that produced it.
        /// </summary>
        public int LastGameCaptures { get; private set; }
        public int LastGameLongestRelay { get; private set; }

        // Indexed by player number, so seat 1 and seat 2 are counted apart and the read-outs above
        // can pick whichever seat this device was playing. Index 0 is unused.
        private readonly int[] capturesThisGame = new int[3];
        private readonly int[] longestRelayThisGame = new int[3];

        /// <summary>Which seat this device is playing: its own online seat, or the human's.</summary>
        private int LocalSeat => mode == GameMode.Online ? onlineLocalPlayer : humanPlayer;

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
            onlineResults.Clear();
            playingOnlineMove = false;
            GameModals.Instance?.HideAll();
            gameBoard = new GameBoard();
            gameStartTime = Time.time;
            formationHeld = 0;

            System.Array.Clear(capturesThisGame, 0, capturesThisGame.Length);
            System.Array.Clear(longestRelayThisGame, 0, longestRelayThisGame.Length);

            bool hotSeat = mode == GameMode.VersusHuman;
            bool online = mode == GameMode.Online;

            // Hot-seat alternates the opening move between the two seats — see
            // NextHotSeatStarter for why that is worth doing rather than always opening with
            // player 1. The AI game keeps its "loser of the last game starts" rule. Online has
            // neither: the host draws for it and says so in the start packet, so there is nothing
            // to guess here.
            hotSeatStarter = hotSeat ? NextHotSeatStarter() : 1;
            gameBoard.CurrentPlayer = online ? 1
                                    : hotSeat ? hotSeatStarter
                                    : GetStartingPlayerForDifficulty(aiDifficulty);
            hotSeatCurrentPlayer = gameBoard.CurrentPlayer;

            // Online, each device arranges its own side and only its own side.
            arrangingPlayer = online ? onlineLocalPlayer : 1;

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
            else if (online)
            {
                // The opponent's side is theirs to arrange, on their own device. Until their
                // formation arrives, the far rows just show the default two-per-pit board — nothing
                // is generated for them, and nothing that appears there before the match starts is
                // real.
                StartCoroutine(OrientBoardForLocalSeat());
            }
            else
            {
                GenerateAIFormation();
            }

            // Each background captions its two score boxes differently, so which seat belongs on
            // which side is a property of the mode, not a constant. Set before the first draw.
            if (online) uiManager.SetScoreSides(Opponent(onlineLocalPlayer), onlineLocalPlayer);
            else if (hotSeat) uiManager.SetScoreSides(1, 2);
            else uiManager.SetScoreSides(aiPlayer, humanPlayer);

            // The left box is the opponent's. Every mode captions it now that the rebuilt HUD
            // draws those captions as text rather than baking them into the background art.
            uiManager.SetSeatNames(mode, online ? networkMatch?.OpponentName : null);

            gameState = GameState.PreGameFormation;
            uiManager.UpdateDisplay(gameBoard);
            uiManager.ShowStatus(online ? "Arrange your side" : (hotSeat ? "Player 1: Arrange" : "Arrange"));
            uiManager.ResetGameTimer();
            uiManager.ClearLastMove();
            uiManager.SetUndoInteractable(false);
            uiManager.ClearHighlights();
            RefreshActionButton();

            // The menu loop carries on through the arrangement phase, and the game loop takes over
            // at START. Arranging used to be silent — a deliberate choice that read as a bug from
            // the player's seat: the music cuts out the moment the board appears, and the first
            // thing the game does after they pick a mode is go quiet on them. Holding the menu
            // track here keeps a continuous bed under the whole pre-game, and makes committing the
            // formation the moment the score changes rather than the moment sound returns.
            //
            // Costs nothing when arriving from a menu, which is every path here: PlayMusic ignores
            // a request for the track already running, so this is a no-op rather than a restart.
            AudioManager.StartMenuMusic();
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

            // Same pill, third job: there is no AI to ask for a hint against another person, so the
            // button concedes instead — in both two-player modes.
            if (mode == GameMode.VersusHuman || mode == GameMode.Online)
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

            if (mode == GameMode.Online)
            {
                // Live on your own turn only. Conceding while the opponent's move is still being
                // animated would land a forfeit in the middle of their turn resolving — and online
                // it would race the host, which is the one place the result has to be unambiguous.
                uiManager?.SetActionButton(
                    arranging ? ActionLabelStart : ActionLabelForfeit,
                    arranging || gameState == GameState.OnlineLocalTurn,
                    arranging ? HudActionRole.Start : HudActionRole.Forfeit);
                return;
            }

            if (mode == GameMode.VersusHuman)
            {
                uiManager?.SetActionButton(
                    arranging ? ActionLabelStart : ActionLabelForfeit,
                    arranging || gameState == GameState.HotSeatTurn,
                    arranging ? HudActionRole.Start : HudActionRole.Forfeit);
                return;
            }

            uiManager?.SetActionButton(
                arranging ? ActionLabelStart : ActionLabelHint,
                arranging || gameState == GameState.HumanTurn,
                arranging ? HudActionRole.Start : HudActionRole.Hint);
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

            if (mode == GameMode.Online)
            {
                ConfirmOnlineFormation();
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
            // Online, the answer never changes: this device only ever plays its own seat, whether it
            // is arranging or moving.
            if (mode == GameMode.Online) return OwnsRow(onlineLocalPlayer, row);

            if (mode != GameMode.VersusHuman) return IsHumanRow(row);

            int active = gameState == GameState.PreGameFormation ? arrangingPlayer : hotSeatCurrentPlayer;
            return OwnsRow(active, row);
        }

        private int GetStartingPlayerForDifficulty(Difficulty difficulty)
        {
            int defaultStarter = humanPlayer;
            return PlayerPrefs.GetInt($"LastLoser_Diff{(int)difficulty}", defaultStarter);
        }

        /// <summary>
        /// Hands the opening move to whichever seat did not have it last time, and records the
        /// choice for the game after that.
        ///
        /// Moving first is worth a great deal in Nsolo, and more than it looks. From the standard
        /// two-per-pit board every one of the sixteen legal opening moves captures — there is no
        /// quiet first move to play — and it takes between four and twelve stones off the other
        /// side before they have touched the board. Self-play puts the opener's win rate at 57%
        /// between two players choosing at random and above 80% once both are choosing well.
        ///
        /// So a fixed opener is not a small unfairness in a two-player game, it is most of the
        /// result. The other two modes had already dealt with this in their own way — the AI hands
        /// the opening to whoever lost last, and an online host draws for it — and hot-seat was
        /// the one left always opening with player 1, which is to say always with whoever happened
        /// to be holding the phone when the game was set up.
        ///
        /// Alternating rather than drawing lots, because these two are sitting together across a
        /// series: taking turns is both fairer over a session and visibly fair, which a coin they
        /// cannot see is not.
        /// </summary>
        private int NextHotSeatStarter()
        {
            // Defaults to 2 so that the first game ever played opens, after the flip, with player 1.
            int last = PlayerPrefs.GetInt(HotSeatLastStarterKey, 2);
            int starter = last == 1 ? 2 : 1;

            PlayerPrefs.SetInt(HotSeatLastStarterKey, starter);
            PlayerPrefs.Save();

            return starter;
        }

        private void Update()
        {
            if (gameState == GameState.AiThinking)
                HandleAiThinkingState();

            if (gameState == GameState.HotSeatTurn)
                HandleHotSeatMoveResult();

            HandleHintResult();

            if (Interrupted) TickInterruptionCountdown();
            if (resyncDeadline > 0f) CheckResyncDeadline();
        }

        /// <summary>
        /// Whether taps on the 3D board count. Driven by <see cref="MenuManager"/>, which switches it
        /// off whenever the gameplay view is hidden.
        ///
        /// The board is 3D geometry with colliders, not part of the Canvas, so a full-screen menu
        /// does not cover it in any sense the physics raycast understands — PitClickHandler already
        /// checks the EventSystem, but that only holds while every panel keeps its raycast target
        /// switched on. This is the belt to that pair of braces: while a menu is up, board taps are
        /// not merely blocked, they are not being accepted at all.
        /// </summary>
        private bool boardInputEnabled = true;

        public void SetBoardInputEnabled(bool enabled)
        {
            if (boardInputEnabled == enabled) return;

            boardInputEnabled = enabled;
            Log($"SetBoardInputEnabled({enabled})");
        }

        public void OnHoleTouched(int row, int col)
        {
            // Logged after the guard, not before it. Logging first made every rejected tap look like
            // a tap that had landed, which is worrying to read and completely misleading.
            if (!boardInputEnabled) return;

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

            // A modal is up — the room code, a forfeit question, a lost connection. The board waits.
            if (GameModals.Instance != null && GameModals.Instance.IsBlocking) return;

            // Nothing to do but wait on the network, so a tap should not be mistaken for a move.
            if (gameState == GameState.OnlineWaiting || gameState == GameState.OnlineOpponentTurn)
            {
                if (gameState == GameState.OnlineOpponentTurn)
                    uiManager.ShowLastMove("Wait for your opponent to move");
                return;
            }

            // Reaching across to the far side is the classic first-timer mistake, so it earns an
            // explanation rather than silence — while arranging and during play alike.
            if (!IsRowOwnedByActivePlayer(row) &&
                (gameState == GameState.PreGameFormation || gameState == GameState.HumanTurn ||
                 gameState == GameState.HotSeatTurn || gameState == GameState.OnlineLocalTurn))
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

            if (gameState == GameState.OnlineLocalTurn)
            {
                SubmitOnlineMove(row, col);
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
            TrackMoveStats(moveResult);
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
            TrackMoveStats(moveResult);
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

            // Player 2 has just finished arranging, so when the opening move is theirs the phone
            // and the camera are already pointing the right way. Handing over would show a
            // pass-the-device card to somebody already holding it and flip the board off their
            // side and straight back.
            if (hotSeatStarter == 2)
            {
                BeginHotSeatTurn(2);
                return;
            }

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

        /// <summary>
        /// Plays one already-computed move: animates the stones, adopts the resulting board, and
        /// says what happened.
        ///
        /// It takes a <see cref="MoveResult"/> and has no idea where it came from — the local engine
        /// in hot-seat, the host's authoritative packet online. That is the whole point of it being
        /// its own method. The animation was already driven off a computed result rather than off
        /// the tap that caused it, so making online work needed no change to how stones move; it
        /// only needed this step to stop assuming it was the one who computed the result.
        ///
        /// The caller owns what happens next — ending the game, handing the device over, asking the
        /// next player — because that differs per mode and this does not.
        /// </summary>
        private IEnumerator PlayMoveResult(MoveResult moveResult, string actorName, string sowingStatus)
        {
            // Both movers are people in every mode that reaches here, so captures are always worded
            // as something a player did rather than something the computer did to them.
            currentMoverIsHuman = true;

            uiManager.ClearHighlights();

            GameBoard startingBoard = gameBoard.Clone();
            currentMoveSegmentCount = moveResult.SowingSegments?.Count ?? 0;
            TrackMoveStats(moveResult);

            uiManager.ShowStatus(sowingStatus);
            RefreshActionButton();
            yield return uiManager.PlayMoveAnimation(startingBoard, moveResult);
            gameBoard = moveResult.Board;
            ShowMoveFeedback(moveResult, actorName);
        }

        private IEnumerator ApplyHotSeatMoveCoroutine(Move move, int player)
        {
            int opponent = Opponent(player);
            MoveResult moveResult = ApplyMoveWithLandingTrace(move, player);

            yield return PlayMoveResult(moveResult, NameOf(player), "Sowing");

            activeMoveRoutine = null;

            if (TryEndGameAfterMove(player, opponent)) yield break;

            yield return HandOverToPlayer(opponent, resumeFormation: false);
        }

        /// <summary>
        /// Conceding, in either two-player mode.
        ///
        /// This used to arm on the first press and resign on the second, because the pill sits
        /// exactly where HINT does in single-player and one stray tap ending a game outright is too
        /// easy to do by accident. The question is now asked properly, in a modal, which says what
        /// conceding actually costs instead of relabelling a button to "CONFIRM?" — and asks it the
        /// same way whether the opponent is across the table or across the country.
        /// </summary>
        private void RequestForfeit()
        {
            bool online = mode == GameMode.Online;
            if (gameState != (online ? GameState.OnlineLocalTurn : GameState.HotSeatTurn)) return;

            AudioManager.Click();
            Haptics.Light();

            int conceding = online ? onlineLocalPlayer : hotSeatCurrentPlayer;

            if (GameModals.Instance == null)
            {
                // Nothing wired up to ask with. Refusing to concede is the safe failure: a player
                // who cannot forfeit is inconvenienced, one who forfeits by accident is not.
                Debug.LogWarning("GameController: no GameModals in the scene, so forfeit is unavailable.");
                uiManager.ShowLastMove("Forfeit is unavailable");
                return;
            }

            GameModals.Instance.ShowForfeitConfirm(
                onConfirm: () => ConfirmForfeit(conceding),
                onCancel: () => RefreshActionButton());
        }

        private void ConfirmForfeit(int conceding)
        {
            Log($"Forfeit by player {conceding}.");

            if (mode == GameMode.Online)
            {
                // Told to the opponent before the local game ends, so they learn why their screen
                // just stopped waiting for a move rather than watching it time out.
                networkMatch?.RequestForfeit();
                return;
            }

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

        // ── Online ───────────────────────────────────────────────────────
        //
        // The third turn loop. It differs from the other two in one way that matters: it never
        // computes a move result for display. Every move that reaches the screen — the opponent's
        // and this player's alike — arrives as an authoritative result from the match and is played
        // through PlayMoveResult, the same method hot-seat uses. A tap does not move a stone here;
        // it sends a request, and the answer moves the stone.

        /// <summary>
        /// Entry point for an online game. The caller has already built the match and seated it —
        /// this class does not connect to anything.
        /// </summary>
        public void StartNewOnlineGame(NetworkMatch match)
        {
            Log($"StartNewOnlineGame(localPlayer={match?.LocalPlayer}, host={match?.IsHost})");

            if (match == null)
            {
                Debug.LogError("GameController: StartNewOnlineGame called with no match.");
                return;
            }

            DetachOnlineMatch();

            mode = GameMode.Online;
            InitializeSystems();

            networkMatch = match;
            onlineLocalPlayer = match.LocalPlayer;

            networkMatch.MatchStarted += HandleMatchStarted;
            networkMatch.MoveApplied += HandleMoveApplied;
            networkMatch.Forfeited += HandleForfeited;
            networkMatch.MatchEnded += HandleMatchEnded;
            networkMatch.Desynced += HandleDesynced;
            networkMatch.MatchInterrupted += HandleMatchInterrupted;
            networkMatch.MatchResumed += HandleMatchResumed;
            networkMatch.MatchResynced += HandleMatchResynced;

            InitializeGame();
        }

        /// <summary>
        /// Walks back into an online game that is already under way, on a device that has no memory
        /// of it — the app was killed and relaunched inside the five minutes its seat is held.
        ///
        /// Not <see cref="StartNewOnlineGame"/> with a flag, because almost everything that method
        /// does is wrong here. It deals a fresh board, opens the arrangement phase and waits for two
        /// formations; this game's stones were arranged some minutes ago and its position exists on
        /// the other device. So the setup that is still relevant — mode, systems, subscriptions, the
        /// camera on the right side of the board — is done here, and the board itself is left blank
        /// until the position arrives.
        ///
        /// What comes next is the path a reconnecting client already takes: nothing is drawn, the
        /// caption says we are catching up, and <see cref="resyncDeadline"/> is armed so a position
        /// that never comes ends the match instead of leaving a player on a board that says
        /// "catching up" forever. <see cref="HandleMatchResynced"/> takes it from there.
        /// </summary>
        public void ResumeOnlineGame(NetworkMatch match)
        {
            Log($"ResumeOnlineGame(localPlayer={match?.LocalPlayer})");

            if (match == null)
            {
                Debug.LogError("GameController: ResumeOnlineGame called with no match.");
                return;
            }

            DetachOnlineMatch();

            mode = GameMode.Online;
            InitializeSystems();

            networkMatch = match;
            onlineLocalPlayer = match.LocalPlayer;

            networkMatch.MatchStarted += HandleMatchStarted;
            networkMatch.MoveApplied += HandleMoveApplied;
            networkMatch.Forfeited += HandleForfeited;
            networkMatch.MatchEnded += HandleMatchEnded;
            networkMatch.Desynced += HandleDesynced;
            networkMatch.MatchInterrupted += HandleMatchInterrupted;
            networkMatch.MatchResumed += HandleMatchResumed;
            networkMatch.MatchResynced += HandleMatchResynced;

            // The same clearing InitializeGame does, minus everything that deals a new game.
            CancelAiThinking();
            CancelHintSearch();
            CancelHotSeatTurn();
            StopAllCoroutines();
            PassDeviceModal.Instance?.ForceHide();
            boardHistory.Clear();
            isPaused = false;
            onlineResults.Clear();
            playingOnlineMove = false;
            formationHeld = 0;
            gameStartTime = Time.time;

            System.Array.Clear(capturesThisGame, 0, capturesThisGame.Length);
            System.Array.Clear(longestRelayThisGame, 0, longestRelayThisGame.Length);

            // Deliberately an empty board rather than the two-per-pit default. A returning player is
            // about to be handed the real position, and showing them a plausible-looking opening
            // for the round trip in between would be showing them a game that is not theirs — the
            // one thing worse than showing them nothing.
            gameBoard = new GameBoard();
            for (int r = 0; r < GameBoard.Rows; r++)
                for (int c = 0; c < GameBoard.Cols; c++)
                    gameBoard.Set(r, c, 0);

            arrangingPlayer = onlineLocalPlayer;

            if (uiManager == null)
            {
                Debug.LogError("GameController: UIManager not assigned.");
                return;
            }

            ResolveBoardFlipper();
            boardFlipper?.ResetToBase();
            StartCoroutine(OrientBoardForLocalSeat());

            uiManager.SetScoreSides(Opponent(onlineLocalPlayer), onlineLocalPlayer);
            uiManager.SetSeatNames(mode, networkMatch.OpponentName);

            gameState = GameState.OnlineWaiting;
            uiManager.UpdateDisplay(gameBoard);
            uiManager.ResetGameTimer();
            uiManager.SetUndoInteractable(false);
            uiManager.ClearHighlights();
            RefreshActionButton();

            uiManager.ShowStatus("Rejoining");
            uiManager.ShowLastMove("Catching up with the game...");
            resyncDeadline = Time.realtimeSinceStartup + ResyncTimeoutSeconds;

            AudioManager.StartGameMusic();
        }

        /// <summary>
        /// Unsubscribes from the current match. Called before seating another one and when leaving
        /// an online game, so a finished match cannot keep driving the board.
        /// </summary>
        public void DetachOnlineMatch()
        {
            if (networkMatch == null) return;

            networkMatch.MatchStarted -= HandleMatchStarted;
            networkMatch.MoveApplied -= HandleMoveApplied;
            networkMatch.Forfeited -= HandleForfeited;
            networkMatch.MatchEnded -= HandleMatchEnded;
            networkMatch.Desynced -= HandleDesynced;
            networkMatch.MatchInterrupted -= HandleMatchInterrupted;
            networkMatch.MatchResumed -= HandleMatchResumed;
            networkMatch.MatchResynced -= HandleMatchResynced;

            networkMatch = null;

            // The countdown belongs to the match that just went away. Left running, it would tick
            // down over whatever the player did next and end by announcing that a game they are no
            // longer in has expired.
            ClearInterruption();
        }

        /// <summary>
        /// Turns the board round for the joiner, once, before they arrange their stones.
        ///
        /// Player 2's pits are the top two rows, and the camera's home position looks at the board
        /// from player 1's side. Rather than mirroring board coordinates at the network boundary —
        /// which would mean two coordinate systems and a conversion to get wrong — the joiner's
        /// camera is simply parked on their own side, exactly as hot-seat does between turns. Board
        /// coordinates then mean the same thing on both devices for the whole match.
        /// </summary>
        private IEnumerator OrientBoardForLocalSeat()
        {
            if (onlineLocalPlayer != 2) yield break;

            ResolveBoardFlipper();
            if (boardFlipper == null) yield break;

            yield return boardFlipper.Flip();
            uiManager.UpdateDisplay(gameBoard);
        }

        /// <summary>
        /// Commits this player's opening formation and waits for the other one.
        ///
        /// Both players arrange at the same time rather than taking turns — there is no device to
        /// pass, so making one of them watch the other lay out sixteen pits would be dead time for
        /// no reason. Whoever finishes first waits.
        /// </summary>
        private void ConfirmOnlineFormation()
        {
            AudioManager.Click();
            Haptics.Light();

            formationHeld = 0;
            gameState = GameState.OnlineWaiting;

            uiManager.ShowStatus("Waiting");
            uiManager.ShowLastMove("Waiting for your opponent to finish arranging...");
            uiManager.ClearHighlights();
            RefreshActionButton();

            networkMatch?.SubmitLocalFormation(CollectLocalFormation());
        }

        /// <summary>Reads this player's sixteen pits out of the board, in the order the protocol expects.</summary>
        private int[] CollectLocalFormation()
        {
            int firstRow = onlineLocalPlayer == 1 ? 0 : 2;
            var cells = new int[16];

            for (int i = 0; i < cells.Length; i++)
                cells[i] = gameBoard.Get(firstRow + i / GameBoard.Cols, i % GameBoard.Cols);

            return cells;
        }

        private void HandleMatchStarted(GameBoard board, int firstPlayer)
        {
            Log($"Online match started. First player = {firstPlayer}.");

            gameBoard = board.Clone();

            // Re-asserted here as well as at setup: the nickname can still be settling when the
            // board is first drawn, and this is the last moment before play where it is free.
            uiManager.SetSeatNames(GameMode.Online, networkMatch?.OpponentName);

            uiManager.UpdateDisplay(gameBoard);

            // Same moment the local modes start their clock and their music: the point at which the
            // board is set and play actually begins.
            uiManager.StartTurnTimer();
            AudioManager.StartGameMusic();

            BeginOnlineTurn(firstPlayer);
        }

        private void BeginOnlineTurn(int player)
        {
            gameBoard.CurrentPlayer = player;

            bool mine = player == onlineLocalPlayer;
            gameState = mine ? GameState.OnlineLocalTurn : GameState.OnlineOpponentTurn;

            string opponent = networkMatch?.OpponentName ?? "Opponent";

            uiManager.ShowStatus(mine ? "Your turn" : $"{opponent}'s turn");
            uiManager.StartTurnTimer();
            RefreshActionButton();

            if (mine)
            {
                uiManager.HighlightLegalMoves(gameEngine.GetLegalMoves(gameBoard, player));
                uiManager.ShowLastMove("Select one of your highlighted pits");
                TutorialCoach.Show(TutorialTip.YourPits);
                return;
            }

            uiManager.ClearHighlights();
            uiManager.ShowLastMove($"Waiting for {opponent} to move...");
        }

        /// <summary>
        /// A tap during this player's own online turn.
        ///
        /// The legality check here is a local pre-filter and nothing more. It exists so an obviously
        /// impossible tap — an empty pit, a single stone, the opponent's row — gets its usual
        /// immediate flash and buzz instead of a round trip's worth of silence. It is not what makes
        /// the move legal: the host checks again against the real board and is free to disagree.
        /// Nothing on this screen moves until it answers.
        /// </summary>
        private void SubmitOnlineMove(int row, int col)
        {
            Move selected = null;
            foreach (Move m in gameEngine.GetLegalMoves(gameBoard, onlineLocalPlayer))
                if (m.Row == row && m.Col == col) { selected = m; break; }

            if (selected == null)
            {
                uiManager.FlashIllegalMove(row, col);
                AudioManager.Illegal();
                Haptics.Light();
                TutorialCoach.Show(TutorialTip.NeedTwoStones);
                return;
            }

            gameState = GameState.OnlineWaiting;
            uiManager.ClearHighlights();
            uiManager.ShowStatus("Sending");
            RefreshActionButton();

            networkMatch?.RequestMove(selected);
        }

        /// <summary>
        /// An authoritative move, from either player. This is the only thing that moves stones in an
        /// online game.
        ///
        /// Results are queued rather than played the moment they arrive. The board state they
        /// describe is already settled by the time this is called, but the animation that shows it
        /// takes seconds, and a long relay chain takes longer still — so an opponent moving promptly
        /// can land their result while the previous one is still sowing. Playing them as they
        /// arrived would run two animations over one board.
        /// </summary>
        private void HandleMoveApplied(MoveResult result)
        {
            if (mode != GameMode.Online || result == null) return;

            onlineResults.Enqueue(result);
            PumpOnlineResults();
        }

        private void PumpOnlineResults()
        {
            if (playingOnlineMove || onlineResults.Count == 0) return;

            playingOnlineMove = true;
            activeMoveRoutine = StartCoroutine(PlayOnlineMoveCoroutine(onlineResults.Dequeue()));
        }

        private IEnumerator PlayOnlineMoveCoroutine(MoveResult result)
        {
            int player = result.Player;
            int opponent = Opponent(player);
            bool mine = player == onlineLocalPlayer;

            yield return PlayMoveResult(
                result,
                OnlineNameOf(player),
                mine ? "Sowing" : "Opponent sowing");

            activeMoveRoutine = null;
            playingOnlineMove = false;

            if (TryEndGameAfterMove(player, opponent)) yield break;

            // Anything that arrived while this was playing goes next, and the turn is only handed
            // over once the queue has actually drained — otherwise the board would invite a move
            // while a move it has not shown yet is still waiting.
            if (onlineResults.Count > 0)
            {
                PumpOnlineResults();
                yield break;
            }

            BeginOnlineTurn(opponent);
        }

        private void HandleForfeited(int conceding)
        {
            if (mode != GameMode.Online) return;

            Log($"Online forfeit by player {conceding}.");
            uiManager.ShowLastMove(conceding == onlineLocalPlayer
                ? "You forfeited"
                : "Your opponent forfeited");

            FinishGame(Opponent(conceding));
        }

        /// <summary>
        /// The two boards disagreed, which should be impossible. <c>NetworkMatch</c> has already
        /// adopted the host's state; this redraws to match it so the player is at least looking at
        /// the real game rather than a divergent one.
        /// </summary>
        private void HandleDesynced()
        {
            if (mode != GameMode.Online || networkMatch?.Board == null) return;

            Debug.LogError("GameController: board desync — redrawing from the host's state.");
            gameBoard = networkMatch.Board.Clone();
            uiManager.UpdateDisplay(gameBoard);
            uiManager.ShowLastMove("Re-synced with your opponent");
        }

        // ── Interruptions (a dropped connection being waited out) ─────────

        /// <summary>
        /// When the current grace period runs out, on <see cref="Time.realtimeSinceStartup"/>, or
        /// zero when nothing is being waited for. Real time, matching the transport's own clock, so
        /// the number on screen is the number actually being counted.
        /// </summary>
        private float interruptionEndsAt;

        /// <summary>Whose connection went, which decides which sentence the player is shown.</summary>
        private bool interruptionIsLocal;

        /// <summary>The last whole second put on screen, so the caption is rewritten once a second.</summary>
        private int lastCountdownSecond = -1;

        /// <summary>
        /// When to give up on the host's position after reconnecting, or zero when not waiting.
        ///
        /// A client that is back on the network but has not been told where the game got to cannot
        /// be allowed to sit there indefinitely. The host may have dropped in the same moment, or
        /// left while we were away — in which case nothing is coming, and without a deadline the
        /// player is left on a board that says "catching up" and never stops saying it. That is the
        /// same class of failure as a latched flag freezing a match, and it gets the same treatment.
        /// </summary>
        private float resyncDeadline;

        /// <summary>
        /// How long the host has to answer a resume request. Generous next to a round trip, because
        /// the connection has just been re-established and the first packets over it are the
        /// slowest — but far short of the rejoin window, since by this point we are connected and a
        /// silent host means something is actually wrong.
        /// </summary>
        private const float ResyncTimeoutSeconds = 12f;

        private bool Interrupted => interruptionEndsAt > 0f;

        /// <summary>
        /// Somebody's connection dropped. The match is not over — the seat is held for a few minutes
        /// — so this stops play and says what is happening rather than tearing anything down.
        /// </summary>
        private void HandleMatchInterrupted(MatchInterruption interruption)
        {
            if (mode != GameMode.Online || gameState == GameState.GameOver) return;

            Log($"Online match interrupted (local={interruption.Local}), holding for {interruption.GraceSeconds:0}s.");

            interruptionIsLocal = interruption.Local;
            interruptionEndsAt = Time.realtimeSinceStartup + interruption.GraceSeconds;
            lastCountdownSecond = -1;

            // Arranging is left alone deliberately. Laying out your own half needs nobody else, and
            // nothing is sent until START — so a player who was mid-formation can carry on with it
            // while the connection sorts itself out, and finds their work still there either way.
            // Freezing them would waste the wait and lose the arrangement if it ended badly.
            if (gameState != GameState.PreGameFormation)
            {
                gameState = GameState.OnlineWaiting;
                uiManager.ClearHighlights();
                RefreshActionButton();
            }

            // The notification sting rather than the illegal-move one. Nobody did anything wrong.
            AudioManager.Popup();
            Haptics.Medium();
            uiManager.ShowStatus(interruptionIsLocal ? "Reconnecting" : "Opponent lost connection");
        }

        /// <summary>
        /// The connection is back. The host can carry straight on; a client has to be told where the
        /// game got to before it can, because it has no way of knowing what it missed.
        /// </summary>
        private void HandleMatchResumed()
        {
            if (mode != GameMode.Online || !Interrupted) return;

            Log("Online match resumed.");
            ClearInterruption();

            if (gameState == GameState.PreGameFormation)
            {
                // Never stopped arranging, so there is nothing to restart.
                uiManager.ShowStatus("Arrange your side");
                return;
            }

            if (networkMatch == null) return;

            if (networkMatch.IsHost)
            {
                // The authority's board never went anywhere.
                uiManager.ShowLastMove("Reconnected");
                BeginOnlineTurn(networkMatch.CurrentPlayer);
                return;
            }

            // Held inert until the host's position arrives. Showing a turn now would mean guessing
            // from a board that is potentially several moves stale, and a highlighted pit the
            // player is invited to tap is the worst possible thing to be wrong about.
            uiManager.ShowStatus("Reconnected");
            uiManager.ShowLastMove("Catching up with the game...");
            resyncDeadline = Time.realtimeSinceStartup + ResyncTimeoutSeconds;
        }

        /// <summary>
        /// The host's position, adopted after a reconnect. Client only — see
        /// <see cref="NetworkMatch.MatchResynced"/>.
        /// </summary>
        private void HandleMatchResynced(GameBoard board, int currentPlayer)
        {
            if (mode != GameMode.Online || board == null) return;
            if (gameState == GameState.GameOver) return;

            Log($"Online match re-synced; it is player {currentPlayer}'s turn.");
            resyncDeadline = 0f;

            gameBoard = board.Clone();
            uiManager.UpdateDisplay(gameBoard);
            uiManager.ShowLastMove("Back in the game");

            BeginOnlineTurn(currentPlayer);
        }

        private void ClearInterruption()
        {
            interruptionEndsAt = 0f;
            interruptionIsLocal = false;
            lastCountdownSecond = -1;
            resyncDeadline = 0f;
        }

        /// <summary>
        /// Gives up on a host that reconnected us and then said nothing. Reported as the opponent
        /// having left, which by any measure that matters to the player is what has happened.
        /// </summary>
        private void CheckResyncDeadline()
        {
            if (Time.realtimeSinceStartup < resyncDeadline) return;

            resyncDeadline = 0f;
            Debug.LogWarning($"GameController: no position from the host within {ResyncTimeoutSeconds:0}s of reconnecting.");
            EndOnlineMatch(MatchEndReason.OpponentLeft);

            // Announced as well as applied. Nothing else knows this happened — the transport has
            // seen no failure — so this is the only chance to put the closing dialog up.
            OnlineMatchAbandoned?.Invoke(MatchEndReason.OpponentLeft);
        }

        /// <summary>
        /// Puts the remaining time on the board, once a second.
        ///
        /// A silent wait is indistinguishable from a frozen game, which is the thing a player does
        /// worst with: they quit. A number going down says the game knows what is happening and that
        /// there is a point at which it will stop — and it also tells them how long they have to
        /// decide whether to wait, which is a decision they are entitled to make.
        ///
        /// Only the caption is driven here. The deadline itself belongs to the transport, which owns
        /// the room and is the only thing that can actually end the match when it passes.
        /// </summary>
        private void TickInterruptionCountdown()
        {
            float remaining = interruptionEndsAt - Time.realtimeSinceStartup;
            if (remaining < 0f) remaining = 0f;

            int whole = Mathf.CeilToInt(remaining);
            if (whole == lastCountdownSecond) return;
            lastCountdownSecond = whole;

            string clock = $"{whole / 60}:{whole % 60:00}";
            uiManager.ShowLastMove(interruptionIsLocal
                ? $"Trying to reconnect... {clock}"
                : $"Waiting for your opponent... {clock}");
        }

        private void HandleMatchEnded(MatchEndReason reason) => EndOnlineMatch(reason);

        /// <summary>
        /// Stops an online game that cannot continue, and says so on the board.
        ///
        /// Public because the order this used to run in was wrong in a way that froze the game.
        /// The transport raises its ending to two listeners: this controller, through
        /// <see cref="NetworkMatch"/>, and <see cref="OnlineFlowController"/>, which put the
        /// "opponent left" dialog up. The flow controller was subscribed first, and the first thing
        /// it did was tear the match down — which unhooked this controller from the very event it
        /// was waiting for. So the dialog appeared over a board that had never been told anything:
        /// still in the opponent's turn, still answering every tap with "wait for your opponent to
        /// move", with no opponent and no way out but force-quitting. Dismissing the dialog to look
        /// at the final position was therefore the one thing a player must not do.
        ///
        /// The flow controller calls this before it cleans up now, so the board is always told
        /// first. The subscription is kept as well, for any path that ends a match without going
        /// through that controller, and the two are safe to double up: a game already over is left
        /// exactly as it is.
        /// </summary>
        public void EndOnlineMatch(MatchEndReason reason)
        {
            if (mode != GameMode.Online) return;

            // Already finished — somebody won, or somebody forfeited, or this is the second of the
            // two reports. Whichever it is, the result on screen is the true one and stands.
            if (gameState == GameState.GameOver) return;

            Log($"Online match ended: {reason}.");

            // Everything waiting on the network stops here, before the modal goes up — a coroutine
            // still animating a move would otherwise carry on behind it.
            if (activeMoveRoutine != null)
            {
                StopCoroutine(activeMoveRoutine);
                activeMoveRoutine = null;
            }
            onlineResults.Clear();
            playingOnlineMove = false;
            uiManager.CancelMoveAnimation();
            uiManager.ClearHighlights();

            gameState = GameState.GameOver;
            RefreshActionButton();
            AudioManager.Silence();

            // The wait is over and it ended badly. Stopped before the closing message goes up, or
            // the next tick would overwrite it with a countdown to something that has already
            // happened.
            ClearInterruption();

            // Said on the board as well as in the dialog, because the dialog can be dismissed: a
            // player who stays to read the final position would otherwise be looking at a board
            // still captioned with whatever the last move was, as though it were their turn.
            uiManager.ShowStatus("Match ended");
            uiManager.ShowLastMove(reason == MatchEndReason.OpponentLeft
                ? "Your opponent left. This is the final position."
                : "Connection lost. This is the final position.");

            // The modal itself belongs to OnlineFlowController, which hears about this from the
            // transport directly. This method's job is only to stop the game that was in progress —
            // it does not know what panels exist and should not.
        }

        /// <summary>
        /// How a seat is named in the move feedback line. The local player is always "You" — their
        /// own username adds nothing when they are the one reading it — while the opponent is named,
        /// since that is the only place their name appears once play starts.
        /// </summary>
        private string OnlineNameOf(int player) =>
            player == onlineLocalPlayer ? "You" : (networkMatch?.OpponentName ?? "Opponent");

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
                ? "Move undone. Computer's reply cancelled"
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

        /// <summary>
        /// Puts the running game down for good, because the player has left it for the menu.
        ///
        /// Leaving used to go through <c>SetPaused(false)</c>, which is the opposite instruction:
        /// it *resumes*. Everything the game had in flight carried on behind the menu — the sowing
        /// coroutine kept stepping and kept playing stone sounds, the AI kept searching, and a hint
        /// task kept running — which is why the board could still be heard from the main menu.
        /// Pausing instead would have been just as wrong: a paused game is one you are coming back
        /// to, and nothing here ever comes back. The game is over; this is what says so.
        ///
        /// Deliberately leaves the board as it stands rather than clearing it. Nothing is looking at
        /// it — the menu is up and <see cref="SetBoardInputEnabled"/> has already been told — and
        /// <see cref="InitializeGame"/> builds a fresh board for the next game anyway, so wiping it
        /// here would only be work that shows up as a flicker if a menu ever animates over it.
        /// </summary>
        public void AbandonGame()
        {
            Log("AbandonGame()");

            // Every worker first, so nothing can post a result into the teardown behind us.
            CancelAiThinking();
            CancelHintSearch();
            CancelHotSeatTurn();

            // The coroutine driving the current move, and the animation it was driving. Both are
            // needed: stopping the coroutine leaves the animator mid-flight with its flags set, and
            // cancelling the animation alone leaves the coroutine free to start another one.
            if (activeMoveRoutine != null)
            {
                StopCoroutine(activeMoveRoutine);
                activeMoveRoutine = null;
            }

            if (uiManager != null)
            {
                uiManager.CancelMoveAnimation();
                uiManager.ClearHighlights();
            }

            // Authoritative results still queued for the screen. Without this they would be waiting
            // for the next online game and play into it, one match late.
            onlineResults.Clear();
            playingOnlineMove = false;
            pendingMove = null;
            ClearInterruption();

            isPaused = false;
            gameState = GameState.GameOver;
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
            gameState = GameState.GameOver;
            RefreshActionButton();

            // Score is just each player's live pit total — there's no separate captured pile.
            int p1Stones = gameBoard != null ? gameEngine.GetPlayerStones(gameBoard, 1) : 0;
            int p2Stones = gameBoard != null ? gameEngine.GetPlayerStones(gameBoard, 2) : 0;

            float elapsed = Time.time - gameStartTime;
            LastGameSeconds = elapsed;

            int seat = LocalSeat;
            LastGameCaptures = seat >= 1 && seat < capturesThisGame.Length ? capturesThisGame[seat] : 0;
            LastGameLongestRelay = seat >= 1 && seat < longestRelayThisGame.Length ? longestRelayThisGame[seat] : 0;
            bool humanWon = winner == humanPlayer;
            bool hotSeat = mode == GameMode.VersusHuman;
            bool online = mode == GameMode.Online;
            bool localWon = online ? winner == onlineLocalPlayer : humanWon;

            // Neither hot-seat nor online results are recorded, for the same reason: the whole stats
            // system is keyed by AI difficulty — win rate, per-difficulty records, the "loser starts
            // next" rule — and filing a game nobody played at a difficulty under one would make
            // those numbers mean nothing. Online needs its own counters and its own axis (an
            // opponent context rather than a difficulty), which is a save-data migration and belongs
            // with the profile rework, not here.
            if (!hotSeat && !online)
            {
                ProfileManager.Instance?.RecordGameResult((int)aiDifficulty, humanWon, elapsed);
            }

            // Filed for every mode, unlike the result above. The objection that keeps hot-seat and
            // online out of the win record is that those stats are keyed by AI difficulty and a
            // game played at no difficulty would make them meaningless. These three are not: time
            // at the board is time at the board, and a relay is a relay whoever was across from you.
            ProfileManager.Instance?.RecordSessionStats(
                elapsed, LastGameCaptures, LastGameLongestRelay, offline: !online);

            AudioManager.Silence();
            // Somebody in the room won a hot-seat game, so it always gets the victory sting.
            AudioManager.GameOver(hotSeat || localWon);
            Haptics.Heavy();

            if (!hotSeat && !online)
            {
                int loser = winner == humanPlayer ? aiPlayer : humanPlayer;
                PlayerPrefs.SetInt($"LastLoser_Diff{(int)aiDifficulty}", loser);
                PlayerPrefs.Save();
            }

            // A finished game is not one to offer a rejoin into. The record would expire on its own
            // a few minutes from now, but "a few minutes" is exactly the window in which somebody
            // closes the app after a win and opens it again — and being asked whether you want to
            // rejoin the game you just won reads as the app having lost track of it.
            if (online) Net.SavedMatch.Forget();

            // Started before the panel goes up so the turn is already under way behind it. Online is
            // left alone: the joiner's camera is parked on their own seat, which is where it should
            // stay — turning it back would show them the board upside down at the final whistle.
            if (hotSeat) StartCoroutine(ReturnBoardToDefaultView());

            if (online) uiManager?.ShowGameOverOnline(winner, onlineLocalPlayer);
            else uiManager?.ShowGameOver(winner, hotSeat);

            GameOver?.Invoke(winner, p1Stones, p2Stones);
        }

        /// <summary>
        /// Files one move's captures and relay length against the player who made it.
        ///
        /// Called from all three move paths — the human's, the computer's, and the shared one
        /// hot-seat and online both run through — and keyed on the result's own player rather than
        /// on whose turn the caller believes it is. That last part matters online, where the move
        /// being applied is one the host resolved and may belong to either seat.
        /// </summary>
        private void TrackMoveStats(MoveResult moveResult)
        {
            if (moveResult == null) return;

            int player = moveResult.Player;
            if (player < 1 || player >= capturesThisGame.Length) return;

            capturesThisGame[player] += moveResult.CapturedStones;

            int relay = moveResult.SowingSegments?.Count ?? 0;
            if (relay > longestRelayThisGame[player]) longestRelayThisGame[player] = relay;
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
