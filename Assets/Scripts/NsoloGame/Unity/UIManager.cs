using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using NsoloGame.Core;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Bridges the 3D board scene to the game controller.
    /// Handles pit input, pit highlighting, status text, and stone visual refreshes.
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        [Header("Board")]
        [SerializeField] private GameObject[] holes = new GameObject[48];
        [SerializeField] private PitStoneVisualizer stoneVisualizer;
        [SerializeField] private GameController gameController;
        [SerializeField] private bool showStartingBoardIfNoControllerRefresh = true;

        [Header("HUD")]
        [SerializeField] private Canvas hudCanvas;
        [SerializeField] private TMP_Text capturedP1Text;
        [SerializeField] private TMP_Text capturedP2Text;
        [SerializeField] private TMP_Text gameStatusText;
        [SerializeField] private TMP_Text messageText;
        [SerializeField] private Button restartButton;
        [SerializeField] private float transientMessageSeconds = 2.5f;

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
        private Coroutine messageCoroutine;

        private void Awake()
        {
            Log("Awake()");

            if (stoneVisualizer == null)
            {
                stoneVisualizer = GetComponent<PitStoneVisualizer>();
                Log(stoneVisualizer == null
                    ? "No PitStoneVisualizer found on the UIManager GameObject."
                    : "Auto-found PitStoneVisualizer on the UIManager GameObject.");
            }

            if (gameController == null)
            {
                gameController = FindObjectOfType<GameController>();
                Log(gameController == null
                    ? "No GameController found in scene during Awake()."
                    : $"Auto-found GameController: {gameController.name}.");
            }

            InitializeBoardUI();
            EnsureHud();
            EnsureEventSystem();
        }

        private void Start()
        {
            Log("Start()");

            if (restartButton != null && gameController != null)
            {
                restartButton.onClick.AddListener(gameController.RestartGame);
                Log("Restart button wired.");
            }

            if (showStartingBoardIfNoControllerRefresh)
            {
                StartCoroutine(SpawnStartingBoardFallbackNextFrame());
            }
        }

        private void InitializeBoardUI()
        {
            Log("InitializeBoardUI()");

            inputCamera = Camera.main;
            holeRenderers = new Renderer[4, 12];
            propertyBlock = new MaterialPropertyBlock();
            int assignedHoles = 0;

            for (int i = 0; i < holes.Length && i < 48; i++)
            {
                GameObject hole = holes[i];
                int row = i / 12;
                int col = i % 12;

                if (hole == null)
                {
                    hole = GameObject.Find($"Hole_{row}_{col}");
                    holes[i] = hole;
                }

                if (hole == null)
                {
                    Debug.LogWarning($"UIManager: Missing pit object Hole_{row}_{col}.");
                    continue;
                }

                assignedHoles++;

                Collider pitCollider = hole.GetComponent<Collider>();
                if (pitCollider == null)
                {
                    Debug.LogWarning($"UIManager: {hole.name} needs a Collider for click input.");
                }

                PitClickHandler clickHandler = hole.GetComponent<PitClickHandler>();
                if (clickHandler == null)
                {
                    clickHandler = hole.AddComponent<PitClickHandler>();
                }

                clickHandler.Initialize(this, row, col);
                holeRenderers[row, col] = hole.GetComponentInChildren<Renderer>();
                ClearHoleColor(row, col);
            }

            if (stoneVisualizer != null)
            {
                stoneVisualizer.SetHoles(holes);
            }
            else
            {
                Debug.LogWarning("UIManager: Stone Visualizer is not assigned, so stone spawning cannot run.");
            }

            Log($"InitializeBoardUI() complete. Assigned holes: {assignedHoles}/48.");
        }

        private void Update()
        {
            HandlePointerInput();
        }

        public void OnPitClicked(int row, int col)
        {
            Log($"OnPitClicked(row={row}, col={col})");
            if (lastHandledInputFrame == Time.frameCount)
                return;

            lastHandledInputFrame = Time.frameCount;

            if (gameController != null)
            {
                gameController.OnHoleTouched(row, col);
            }
            else
            {
                Debug.LogWarning("UIManager: Pit click ignored because GameController is not assigned.");
            }
        }

        public void UpdateDisplay(GameBoard board, int capturedP1, int capturedP2)
        {
            Log("UpdateDisplay() called.");
            hasReceivedBoardState = board != null;

            if (stoneVisualizer != null)
            {
                stoneVisualizer.Refresh(board, capturedP1, capturedP2);
            }
            else
            {
                Debug.LogWarning("UIManager: UpdateDisplay cannot refresh stones because Stone Visualizer is not assigned.");
            }

            UpdateHudOnly(board, capturedP1, capturedP2);
        }

        public void HighlightLegalMoves(List<Move> moves)
        {
            Log($"HighlightLegalMoves() count={(moves == null ? 0 : moves.Count)}");
            ClearHighlights();

            if (moves == null)
                return;

            foreach (Move move in moves)
            {
                SetHoleColor(move.Row, move.Col, legalMoveColor);
            }
        }

        public void ClearHighlights()
        {
            Log("ClearHighlights()");

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    ClearHoleColor(r, c);
                }
            }
        }

        public void FlashIllegalMove(int row, int col)
        {
            Log($"FlashIllegalMove(row={row}, col={col})");
            ShowMessage("Invalid move", transientMessageSeconds);
            StartCoroutine(FlashCoroutine(row, col, illegalMoveColor, 0.35f));
        }

        public void ShowStatus(string message)
        {
            if (gameStatusText != null)
            {
                gameStatusText.text = message;
            }
        }

        public void ShowMessage(string message, float seconds = 2.5f)
        {
            if (messageText == null)
                return;

            if (messageCoroutine != null)
            {
                StopCoroutine(messageCoroutine);
            }

            messageText.text = message;
            messageText.enabled = true;
            messageCoroutine = StartCoroutine(ClearMessageAfterDelay(seconds));
        }

        private IEnumerator FlashCoroutine(int row, int col, Color flashColor, float duration)
        {
            SetHoleColor(row, col, flashColor);
            yield return new WaitForSeconds(duration);
            ClearHoleColor(row, col);
        }

        public void ShowGameOver(int winner)
        {
            Log($"ShowGameOver(winner={winner})");
            ClearHighlights();

            if (gameStatusText != null)
            {
                gameStatusText.text = winner == 1 ? "Player 1 Wins!" : "Player 2 (AI) Wins!";
            }

            ShowMessage(winner == 1 ? "You win!" : "AI wins!", 6f);
        }

        public void AnimateSowing(List<(int r, int c)> sequence)
        {
            Log($"AnimateSowing() sequence count={(sequence == null ? 0 : sequence.Count)}");
            // The visual board is rebuilt from authoritative GameBoard state.
            // This method remains as a hook for future per-stone movement animation.
        }

        public IEnumerator PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)
        {
            if (stoneVisualizer != null)
            {
                yield return stoneVisualizer.PlayMoveAnimation(startingBoard, moveResult);
            }

            if (moveResult != null)
            {
                UpdateHudOnly(moveResult.Board, moveResult.Board.CapturedP1, moveResult.Board.CapturedP2);
            }
        }

        private IEnumerator SpawnStartingBoardFallbackNextFrame()
        {
            yield return null;

            if (hasReceivedBoardState)
            {
                Log("Fallback skipped because GameController already supplied a GameBoard.");
                yield break;
            }

            if (stoneVisualizer == null)
            {
                Debug.LogWarning("UIManager: Starting-board fallback skipped because Stone Visualizer is not assigned.");
                yield break;
            }

            Debug.LogWarning("UIManager: No GameBoard was received from GameController by the next frame. Spawning a default starting board for visual debugging.");
            UpdateDisplay(new GameBoard(), 0, 0);
        }

        private void HandlePointerInput()
        {
            if (IsPointerOverUi())
                return;

            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began)
                {
                    TrySelectPitAtScreenPosition(touch.position);
                }

                return;
            }

            if (Input.GetMouseButtonDown(0))
            {
                TrySelectPitAtScreenPosition(Input.mousePosition);
            }
        }

        private void TrySelectPitAtScreenPosition(Vector2 screenPosition)
        {
            if (lastHandledInputFrame == Time.frameCount)
                return;

            if (inputCamera == null)
            {
                inputCamera = Camera.main;
            }

            if (inputCamera == null)
            {
                Debug.LogWarning("UIManager: No main camera found for pit raycast input.");
                return;
            }

            Ray ray = inputCamera.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 100f))
                return;

            PitClickHandler pit = hit.collider.GetComponent<PitClickHandler>();
            if (pit == null)
            {
                pit = hit.collider.GetComponentInParent<PitClickHandler>();
            }

            if (pit == null)
                return;

            OnPitClicked(pit.Row, pit.Col);
        }

        private bool IsPointerOverUi()
        {
            if (EventSystem.current == null)
                return false;

            if (Input.touchCount > 0)
            {
                return EventSystem.current.IsPointerOverGameObject(Input.GetTouch(0).fingerId);
            }

            return EventSystem.current.IsPointerOverGameObject();
        }

        private IEnumerator ClearMessageAfterDelay(float seconds)
        {
            yield return new WaitForSeconds(seconds);

            if (messageText != null)
            {
                messageText.text = string.Empty;
                messageText.enabled = false;
            }

            messageCoroutine = null;
        }

        private void EnsureHud()
        {
            if (hudCanvas == null)
            {
                hudCanvas = GetComponentInChildren<Canvas>();
            }

            if (hudCanvas == null)
            {
                GameObject canvasObject = new GameObject("HUD Canvas");
                canvasObject.transform.SetParent(transform, false);
                hudCanvas = canvasObject.AddComponent<Canvas>();
                hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvasObject.AddComponent<CanvasScaler>();
                canvasObject.AddComponent<GraphicRaycaster>();
            }

            CanvasScaler scaler = hudCanvas.GetComponent<CanvasScaler>();
            if (scaler != null)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (gameStatusText == null)
            {
                gameStatusText = CreateHudText("Status Text", hudCanvas.transform, "Your turn", 40, TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -45f), new Vector2(760f, 70f));
            }

            if (capturedP1Text == null)
            {
                capturedP1Text = CreateHudText("Player Score Text", hudCanvas.transform, "You captured: 0", 30, TextAlignmentOptions.Left, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(220f, 46f), new Vector2(400f, 56f));
            }

            if (capturedP2Text == null)
            {
                capturedP2Text = CreateHudText("AI Score Text", hudCanvas.transform, "AI captured: 0", 30, TextAlignmentOptions.Right, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-230f, -46f), new Vector2(400f, 56f));
            }

            if (messageText == null)
            {
                messageText = CreateHudText("Message Text", hudCanvas.transform, string.Empty, 34, TextAlignmentOptions.Center, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 110f), new Vector2(900f, 64f));
                messageText.enabled = false;
            }
        }

        private TMP_Text CreateHudText(string objectName, Transform parent, string text, int fontSize, TextAlignmentOptions alignment, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 size)
        {
            GameObject textObject = new GameObject(objectName);
            textObject.transform.SetParent(parent, false);

            TextMeshProUGUI label = textObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = Color.white;
            label.enableWordWrapping = false;
            label.raycastTarget = false;
            label.outlineWidth = 0.18f;
            label.outlineColor = new Color(0f, 0f, 0f, 0.75f);

            RectTransform rect = label.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = anchorMin;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            return label;
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            GameObject eventSystemObject = new GameObject("EventSystem");
            eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<StandaloneInputModule>();
        }

        private void SetHoleColor(int row, int col, Color color)
        {
            Renderer targetRenderer = holeRenderers == null ? null : holeRenderers[row, col];
            if (targetRenderer == null)
                return;

            targetRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor("_Color", color);
            propertyBlock.SetColor("_BaseColor", color);
            targetRenderer.SetPropertyBlock(propertyBlock);
        }

        private void ClearHoleColor(int row, int col)
        {
            Renderer targetRenderer = holeRenderers == null ? null : holeRenderers[row, col];
            if (targetRenderer == null)
                return;

            targetRenderer.SetPropertyBlock(null);
        }

        private void UpdateHudOnly(GameBoard board, int capturedP1, int capturedP2)
        {
            if (capturedP1Text != null)
            {
                capturedP1Text.text = "You captured: " + capturedP1;
            }

            if (capturedP2Text != null)
            {
                capturedP2Text.text = "AI captured: " + capturedP2;
            }

            if (gameStatusText != null && board != null)
            {
                gameStatusText.text = board.CurrentPlayer == 1 ? "Your turn" : "AI turn";
            }
        }

        private void Log(string message)
        {
            if (enableBreadcrumbLogs)
            {
                Debug.Log($"[UIManager] {message}", this);
            }
        }
    }
}
