using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using NsoloGame.Core;

namespace NsoloGame.Unity
{
    public class UIManager : MonoBehaviour
    {
        [Header("Board")]
        [SerializeField] private GameObject[] holes = new GameObject[32];
        [SerializeField] private PitStoneVisualizer stoneVisualizer;
        [SerializeField] private GameController gameController;
        [Tooltip("Draws the stone count over each pit. Auto-found on this object if left empty.")]
        [SerializeField] private PitCountLabels pitCountLabels;
        [SerializeField] private bool showStartingBoardIfNoControllerRefresh = true;

        [Header("HUD — Status")]
        [SerializeField] private Canvas hudCanvas;
        [SerializeField] private TMP_Text turnStatusText;
        [SerializeField] private TMP_Text timerText;

        [Header("HUD — Scores")]
        [SerializeField] private TMP_Text playerScoreText;
        [SerializeField] private TMP_Text aiScoreText;
        [Tooltip("Caption above the left-hand score box, which is always the opponent's. Reads " +
                 "COMPUTER, PLAYER 1, or the opponent's name online.")]
        [SerializeField] private TMP_Text opponentNameText;
        [Tooltip("Caption above the right-hand score box, which is always this device's.")]
        [SerializeField] private TMP_Text playerNameText;

        [Header("HUD — Last Move")]
        [SerializeField] private TMP_Text lastMoveText;

        [Header("HUD — Buttons")]
        [SerializeField] private Button undoButton;
        [Tooltip("The bottom-centre pill. It doubles as START while the player is arranging their " +
                 "stones and as HINT once play begins, so both share one spot on the background art.")]
        [SerializeField] private Button actionButton;
        [SerializeField] private TMP_Text actionButtonLabel;

        [Header("Highlighting")]
        [SerializeField] private Color normalColor = Color.white;
        [SerializeField] private Color legalMoveColor = new Color(1f, 0.85f, 0.05f);
        [SerializeField] private Color illegalMoveColor = Color.red;

        [Header("Debug")]
        [SerializeField] private bool enableBreadcrumbLogs = false;

        private Renderer[,] holeRenderers;
        private MaterialPropertyBlock propertyBlock;
        private bool hasReceivedBoardState;
        private Camera inputCamera;
        private int lastHandledInputFrame = -1;

        // Double-tap-to-skip. The slop radius is generous because a deliberate double tap on a
        // phone rarely lands twice on the same pixel.
        private const float DoubleTapSeconds = 0.45f;
        private const float DoubleTapSlopPixels = 120f;
        private float lastTapTime = -1f;
        private Vector2 lastTapPosition;

        // Timer. This is a single game clock, not a per-turn one. StartTurnTimer is idempotent, so
        // the repeated calls as turns hand off leave it running; it only stops at game over. Pause
        // needs no special handling because Time.time is scaled and freezes with timeScale 0.
        private bool timerRunning;
        private float timerResumedAt;
        private float timerAccumulated;
        private string timerDisplayed;

        private void Awake()
        {
            Log("Awake()");

            if (stoneVisualizer == null)
                stoneVisualizer = GetComponent<PitStoneVisualizer>();

            if (gameController == null)
                gameController = FindObjectOfType<GameController>();

            InitializeBoardUI();
        }

        private void Start()
        {
            Log("Start()");

            if (showStartingBoardIfNoControllerRefresh)
                StartCoroutine(SpawnStartingBoardFallbackNextFrame());
        }

        private void InitializeBoardUI()
        {
            inputCamera = Camera.main;
            holeRenderers = new Renderer[4, 8];
            propertyBlock = new MaterialPropertyBlock();

            // Always rebuild by name from scratch rather than trusting whatever is serialized
            // in the Inspector array — that array was authored for the old 4x12 board and its
            // positions no longer line up with the current 4x8 layout, which silently wires
            // pit click handlers to the wrong row/col (or to nothing) for everything past row 0.
            holes = new GameObject[32];

            for (int i = 0; i < 32; i++)
            {
                int row = i / 8, col = i % 8;

                holes[i] = GameObject.Find($"Hole_{row}_{col}");

                if (holes[i] == null)
                {
                    Debug.LogWarning($"UIManager: Missing Hole_{row}_{col}.");
                    continue;
                }

                if (holes[i].GetComponent<Collider>() == null)
                    Debug.LogWarning($"UIManager: {holes[i].name} needs a Collider.");

                PitClickHandler handler = holes[i].GetComponent<PitClickHandler>()
                    ?? holes[i].AddComponent<PitClickHandler>();
                handler.Initialize(this, row, col);
                holeRenderers[row, col] = holes[i].GetComponentInChildren<Renderer>();
                ClearHoleColor(row, col);
            }

            if (stoneVisualizer != null)
                stoneVisualizer.SetHoles(holes);
            else
                Debug.LogWarning("UIManager: PitStoneVisualizer not assigned.");

            // After SetHoles, so the pit transforms the labels attach to actually resolve. Added
            // automatically when absent, so counts work without anything being wired up — drop a
            // PitCountLabels onto this object by hand if you want its sizing exposed in the
            // Inspector.
            if (pitCountLabels == null) pitCountLabels = GetComponent<PitCountLabels>();
            if (pitCountLabels == null) pitCountLabels = gameObject.AddComponent<PitCountLabels>();
            pitCountLabels.Initialize(stoneVisualizer, turnStatusText);
        }

        /// <summary>Hook for a "Show pit counts" setting.</summary>
        public void SetPitCountsVisible(bool value) => pitCountLabels?.SetVisible(value);

        public bool PitCountsVisible => pitCountLabels == null || pitCountLabels.Visible;

        private void Update()
        {
            HandlePointerInput();
            UpdateTimerDisplay();
        }

        // ── Public API called by GameController ───────────────────────────

        public void OnPitClicked(int row, int col)
        {
            Log($"OnPitClicked({row},{col})");
            if (lastHandledInputFrame == Time.frameCount) return;
            lastHandledInputFrame = Time.frameCount;
            gameController?.OnHoleTouched(row, col);
        }

        public void UpdateDisplay(GameBoard board)
        {
            hasReceivedBoardState = board != null;
            stoneVisualizer?.Refresh(board);
            UpdateScores(board);
            pitCountLabels?.Refresh(board);
        }

        /// <summary>
        /// Shows the caller's text verbatim (in caps). This used to guess the status by sniffing
        /// the message for substrings, which turned "Arrange your stones" into "YOUR TURN" — the
        /// caller already knows the state, so it just says what it means.
        /// </summary>
        public void ShowStatus(string message)
        {
            if (turnStatusText == null) return;
            turnStatusText.text = message.ToUpperInvariant();
        }

        /// <summary>
        /// Sets the last-move line. The text stays on screen until something replaces it, so the
        /// player can still read what happened well after the move that caused it.
        /// </summary>
        public void ShowLastMove(string message)
        {
            if (lastMoveText == null) return;
            lastMoveText.text = message;
            lastMoveText.enabled = true;
        }

        /// <summary>Blanks the last-move line, e.g. when a new game starts.</summary>
        public void ClearLastMove()
        {
            if (lastMoveText == null) return;
            lastMoveText.text = string.Empty;
        }

        // Keep ShowMessage as an alias so GameController's existing call compiles
        public void ShowMessage(string message, float seconds = 2.5f) => ShowLastMove(message);

        public void HighlightLegalMoves(List<Move> moves)
        {
            ClearHighlights();
            if (moves == null) return;
            foreach (var m in moves) SetHoleColor(m.Row, m.Col, legalMoveColor);
        }

        public void ClearHighlights()
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 8; c++)
                    ClearHoleColor(r, c);
        }

        public void FlashIllegalMove(int row, int col)
        {
            ShowLastMove("Invalid move");
            StartCoroutine(FlashCoroutine(row, col, illegalMoveColor, 0.35f));
        }

        /// <summary>
        /// Announces the result. In hot-seat both sides are people, so neither "you" nor "AI" is
        /// the right word for either of them — the winner is named by seat instead.
        /// </summary>
        public void ShowGameOver(int winner, bool hotSeat = false)
        {
            ClearHighlights();
            StopTurnTimer();

            if (hotSeat)
            {
                ShowStatus($"Player {winner} wins!");
                ShowLastMove($"Player {winner} wins!");
                return;
            }

            ShowStatus(winner == 1 ? "You win!" : "Computer wins!");
            ShowLastMove(winner == 1 ? "Victory!" : "Defeated!");
        }

        /// <summary>
        /// Announces an online result. Separate from <see cref="ShowGameOver"/> because "you" is not
        /// player 1 here — it is whichever seat this device is playing, which is player 2 for
        /// whoever joined the room.
        /// </summary>
        public void ShowGameOverOnline(int winner, int localPlayer)
        {
            ClearHighlights();
            StopTurnTimer();

            bool won = winner == localPlayer;
            ShowStatus(won ? "You win!" : "Opponent wins!");
            ShowLastMove(won ? "Victory!" : "Defeated!");
        }

        public void FlashHintPit(int row, int col)
        {
            if (stoneVisualizer != null)
                StartCoroutine(stoneVisualizer.FlashHintGlow(row, col));
        }

        public void SetUndoInteractable(bool value)
        {
            if (undoButton != null) undoButton.interactable = value;
        }

        /// <summary>
        /// Relabels the shared bottom pill. The button's own Image is transparent (the pill is
        /// painted into the background art), so the disabled tint never shows — the label is faded
        /// by hand instead to signal when the button is dead.
        /// </summary>
        public void SetActionButton(string label, bool interactable)
        {
            if (actionButton != null) actionButton.interactable = interactable;

            if (actionButtonLabel == null) return;
            actionButtonLabel.text = label;
            Color c = actionButtonLabel.color;
            actionButtonLabel.color = new Color(c.r, c.g, c.b, interactable ? 1f : 0.4f);
        }

        public IEnumerator PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)
        {
            ResetTapTracking();

            // Numbers would lag a frame behind stones in flight, and a wrong count is worse than
            // none — so they go away for the move and come back once the board has settled.
            pitCountLabels?.HideDuringAnimation();

            if (stoneVisualizer != null)
                yield return stoneVisualizer.PlayMoveAnimation(startingBoard, moveResult);

            if (moveResult != null)
            {
                UpdateScores(moveResult.Board);
                pitCountLabels?.Refresh(moveResult.Board);
            }
        }

        /// <summary>
        /// Subscribes GameController to each sowing segment as it begins, so relay and capture can
        /// be explained while they are happening instead of once the whole chain has finished.
        /// </summary>
        public void SetSowingSegmentListener(System.Action<SowingSegment, int> listener)
        {
            stoneVisualizer?.SetSegmentListener(listener);
        }

        /// <summary>Fast-forwards a move in progress. False when there is nothing to skip.</summary>
        public bool TrySkipMoveAnimation()
        {
            return stoneVisualizer != null && stoneVisualizer.TrySkipAnimation();
        }

        /// <summary>Abandons a move in progress, for when undo interrupts the AI.</summary>
        public void CancelMoveAnimation()
        {
            stoneVisualizer?.CancelAnimation();
        }

        // ── Timer ─────────────────────────────────────────────────────────

        /// <summary>Zeroes the game clock. Call once when a new game begins.</summary>
        public void ResetGameTimer()
        {
            timerRunning = false;
            timerAccumulated = 0f;
            RenderTimer(0f);
        }

        public void StartTurnTimer()
        {
            if (timerRunning) return;
            timerResumedAt = Time.time;
            timerRunning = true;
        }

        public void StopTurnTimer()
        {
            if (!timerRunning) return;
            timerAccumulated += Time.time - timerResumedAt;
            timerRunning = false;
            RenderTimer(timerAccumulated);
        }

        private float ElapsedSeconds =>
            timerAccumulated + (timerRunning ? Time.time - timerResumedAt : 0f);

        private void UpdateTimerDisplay()
        {
            if (timerRunning) RenderTimer(ElapsedSeconds);
        }

        private void RenderTimer(float seconds)
        {
            if (timerText == null) return;
            string formatted = $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";
            // Only touch the label when the visible value changes — assigning every frame
            // forces a TMP mesh rebuild, which is wasted work on a clock that ticks once a second.
            if (formatted == timerDisplayed) return;
            timerDisplayed = formatted;
            timerText.text = formatted;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        // Score is the total number of stones still sitting in each player's own pits
        // (not the captured/store count), so it starts at 32 and only drops when the
        // opponent captures from that player's side.
        // Which seat each score box belongs to. The two boxes are fixed on screen — aiScoreText is
        // the left-hand one and playerScoreText the right, whatever their names suggest — but the
        // three backgrounds label them differently:
        //
        //   vs Computer    COMPUTER SCORE  |  YOUR SCORE      left = the AI,      right = you
        //   local 2-player PLAYER 1 SCORE  |  PLAYER 2 SCORE  left = player 1,    right = player 2
        //   online         SCORE           |  YOUR SCORE      left = the opponent, right = you
        //
        // Hard-wiring left to player 2 was right only for the first of those. In hot-seat it put
        // player 2's total under a box captioned PLAYER 1, and online it would have done the same to
        // whoever joined, since the joiner is player 2 and "yours" is on the right for both of them.
        private int scoreLeftPlayer = 2;
        private int scoreRightPlayer = 1;

        /// <summary>
        /// Says which seat each score box is captioned for. Called when a game starts, once the mode
        /// — and online, the local seat — is known.
        /// </summary>
        public void SetScoreSides(int leftPlayer, int rightPlayer)
        {
            scoreLeftPlayer = leftPlayer;
            scoreRightPlayer = rightPlayer;
        }

        /// <summary>
        /// Captions the two score boxes for the mode being played. Left box is always the
        /// opponent's, right box always this device's.
        ///
        /// These used to be part of the background image — a different picture per mode, each with
        /// its captions already lettered on — so only the online one needed a label, and only
        /// because a name cannot be painted in advance. The rebuilt HUD draws the captions as text,
        /// so all three modes have to be spelled out here.
        ///
        /// <paramref name="opponentName"/> is used in <see cref="GameMode.Online"/> only, and falls
        /// back to OPPONENT while the nickname is still settling.
        /// </summary>
        public void SetSeatNames(GameMode mode, string opponentName = null)
        {
            TMP_Text opponent = Pick(NsoloUI.Label(ElementId.HudOpponentNameLabel), opponentNameText);
            TMP_Text player = Pick(NsoloUI.Label(ElementId.HudPlayerNameLabel), playerNameText);

            string opponentCaption;
            string playerCaption;

            switch (mode)
            {
                case GameMode.VersusHuman:
                    // Neither seat is "you" — both are people in the room, so they are named by
                    // seat. Player 1 is the left box because that is the seat the left box holds.
                    opponentCaption = "PLAYER 1";
                    playerCaption = "PLAYER 2";
                    break;

                case GameMode.Online:
                    opponentCaption = string.IsNullOrWhiteSpace(opponentName)
                        ? "OPPONENT"
                        : opponentName.ToUpperInvariant();
                    playerCaption = "YOU";
                    break;

                default:
                    opponentCaption = "COMPUTER";
                    playerCaption = "YOU";
                    break;
            }

            if (opponent != null)
            {
                opponent.gameObject.SetActive(true);
                opponent.text = opponentCaption;
            }

            if (player != null)
            {
                player.gameObject.SetActive(true);
                player.text = playerCaption;
            }
        }

        /// <summary>A tagged element wins over whatever was dragged into the slot, as elsewhere.</summary>
        private static T Pick<T>(T rebuilt, T current) where T : UnityEngine.Object
            => rebuilt != null ? rebuilt : current;

        private void UpdateScores(GameBoard board)
        {
            if (board == null) return;

            int p1Stones = 0;
            int p2Stones = 0;
            for (int c = 0; c < GameBoard.Cols; c++)
            {
                p1Stones += board.Get(0, c) + board.Get(1, c);
                p2Stones += board.Get(2, c) + board.Get(3, c);
            }

            int Stones(int player) => player == 1 ? p1Stones : p2Stones;

            if (aiScoreText != null) aiScoreText.text = Stones(scoreLeftPlayer).ToString("00");
            if (playerScoreText != null) playerScoreText.text = Stones(scoreRightPlayer).ToString("00");
        }

        private IEnumerator FlashCoroutine(int row, int col, Color color, float duration)
        {
            SetHoleColor(row, col, color);
            yield return new WaitForSeconds(duration);
            ClearHoleColor(row, col);
        }

        private IEnumerator SpawnStartingBoardFallbackNextFrame()
        {
            yield return null;
            if (hasReceivedBoardState || stoneVisualizer == null) yield break;
            Debug.LogWarning("UIManager: No board received — spawning default starting board.");
            UpdateDisplay(new GameBoard());
        }

        private void HandlePointerInput()
        {
            if (Input.touchCount > 0)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Began) HandleTapDown(t.position);
                return;
            }

            if (Input.GetMouseButtonDown(0))
                HandleTapDown(Input.mousePosition);
        }

        /// <summary>
        /// Routes a tap. Two taps in quick succession near the same spot skip a sowing animation in
        /// progress; anything else falls through to normal pit selection. Unscaled time, because
        /// the board is frozen while a tutorial tip is up.
        ///
        /// The UI check deliberately guards only pit selection, not the skip. The tip promises
        /// "double tap anywhere", and every TMP label in the HUD is a raycast target by default, so
        /// gating the whole method on it made the gesture die wherever the player happened to tap.
        /// </summary>
        private void HandleTapDown(Vector2 screenPos)
        {
            float now = Time.unscaledTime;
            bool isDoubleTap =
                lastTapTime > 0f &&
                now - lastTapTime <= DoubleTapSeconds &&
                (screenPos - lastTapPosition).sqrMagnitude <= DoubleTapSlopPixels * DoubleTapSlopPixels;

            lastTapPosition = screenPos;
            lastTapTime = now;

            if (isDoubleTap && gameController != null && gameController.TrySkipAnimation())
            {
                // Consumed. Cleared so a third tap cannot immediately chain into another double.
                lastTapTime = -1f;
                return;
            }

            // A failed skip attempt used to clear the timestamp, which ate the pairing and left the
            // player tapping four times to skip once. The tap now stays available to pair with.

            if (IsPointerOverUi()) return;
            TrySelectPitAtScreenPosition(screenPos);
        }

        /// <summary>
        /// Forgets the last tap. Called as a move begins so the tap that chose the pit cannot pair
        /// with the player's first skip tap and be swallowed as a premature double.
        /// </summary>
        private void ResetTapTracking()
        {
            lastTapTime = -1f;
        }

        private void TrySelectPitAtScreenPosition(Vector2 screenPos)
        {
            if (lastHandledInputFrame == Time.frameCount) return;
            inputCamera ??= Camera.main;
            if (inputCamera == null) return;

            Ray ray = inputCamera.ScreenPointToRay(screenPos);
            if (!Physics.Raycast(ray, out RaycastHit hit, 100f)) return;

            PitClickHandler pit = hit.collider.GetComponent<PitClickHandler>()
                ?? hit.collider.GetComponentInParent<PitClickHandler>();
            if (pit != null) OnPitClicked(pit.Row, pit.Col);
        }

        private bool IsPointerOverUi()
        {
            if (EventSystem.current == null) return false;
            return Input.touchCount > 0
                ? EventSystem.current.IsPointerOverGameObject(Input.GetTouch(0).fingerId)
                : EventSystem.current.IsPointerOverGameObject();
        }

        private void SetHoleColor(int row, int col, Color color)
        {
            Renderer r = holeRenderers?[row, col];
            if (r == null) return;
            r.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor("_Color", color);
            propertyBlock.SetColor("_BaseColor", color);
            r.SetPropertyBlock(propertyBlock);
        }

        private void ClearHoleColor(int row, int col)
        {
            holeRenderers?[row, col]?.SetPropertyBlock(null);
        }

        private void Log(string msg)
        {
            if (enableBreadcrumbLogs) Debug.Log($"[UIManager] {msg}", this);
        }

        // Kept for AnimateSowing hook
        public void AnimateSowing(List<(int r, int c)> sequence) { }
    }
}
