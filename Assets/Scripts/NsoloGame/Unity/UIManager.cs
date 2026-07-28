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
        [SerializeField] private bool showStartingBoardIfNoControllerRefresh = true;

        [Header("HUD — Status")]
        [SerializeField] private Canvas hudCanvas;
        [SerializeField] private TMP_Text turnStatusText;
        [SerializeField] private TMP_Text timerText;

        [Header("HUD — Scores")]
        [SerializeField] private TMP_Text playerScoreText;
        [SerializeField] private TMP_Text aiScoreText;

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
        }

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

        public void ShowGameOver(int winner)
        {
            ClearHighlights();
            StopTurnTimer();
            ShowStatus(winner == 1 ? "You win!" : "AI wins!");
            ShowLastMove(winner == 1 ? "Victory!" : "Defeated!");
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
            if (stoneVisualizer != null)
                yield return stoneVisualizer.PlayMoveAnimation(startingBoard, moveResult);

            if (moveResult != null)
                UpdateScores(moveResult.Board);
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

            if (playerScoreText != null) playerScoreText.text = p1Stones.ToString("00");
            if (aiScoreText != null) aiScoreText.text = p2Stones.ToString("00");
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
            if (IsPointerOverUi()) return;

            if (Input.touchCount > 0)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Began) TrySelectPitAtScreenPosition(t.position);
                return;
            }

            if (Input.GetMouseButtonDown(0))
                TrySelectPitAtScreenPosition(Input.mousePosition);
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
