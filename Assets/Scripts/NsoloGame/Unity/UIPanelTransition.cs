using System.Collections;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Reusable native UI transition for a panel. It preserves the authored RectTransform scale
    /// and uses a CanvasGroup to control visibility and input while the panel moves between states.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class UIPanelTransition : MonoBehaviour
    {
        [Header("Opening")]
        [SerializeField, Range(0.5f, 1f)] private float openStartScale = 0.96f;
        [SerializeField, Min(0f)] private float openDuration = 0.22f;
        [SerializeField] private AnimationCurve openCurve;

        [Header("Closing")]
        [SerializeField, Range(0.5f, 1f)] private float closeEndScale = 0.97f;
        [SerializeField, Min(0f)] private float closeDuration = 0.18f;
        [SerializeField] private AnimationCurve closeCurve;
        [Tooltip("Turns the panel GameObject off after its closing animation completes.")]
        [SerializeField] private bool deactivateWhenClosed = true;

        private RectTransform rectTransform;
        private CanvasGroup canvasGroup;
        private Vector3 originalLocalScale;
        private bool hasOriginalLocalScale;
        private Coroutine transitionRoutine;

        public bool IsOpen { get; private set; }
        public CanvasGroup CanvasGroup => canvasGroup;

        private void Awake()
        {
            CacheComponents();
            IsOpen = gameObject.activeInHierarchy && canvasGroup.alpha > 0f;
        }

        private void OnDisable()
        {
            StopTransition();
            // Scale is visual feedback only; never leave an authored panel permanently resized.
            if (rectTransform != null) rectTransform.localScale = originalLocalScale;
        }

        /// <summary>Shows this panel with fade-and-scale feedback.</summary>
        public void Open()
        {
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            CacheComponents();
            StopTransition();

            IsOpen = true;
            SetInputEnabled(false);
            canvasGroup.alpha = 0f;
            rectTransform.localScale = originalLocalScale * openStartScale;
            transitionRoutine = StartCoroutine(OpenRoutine());
        }

        /// <summary>Hides this panel with fade-and-scale feedback.</summary>
        public void Close()
        {
            CacheComponents();
            if (!gameObject.activeSelf)
            {
                SetClosedState();
                return;
            }

            StopTransition();
            IsOpen = false;
            SetInputEnabled(false);
            transitionRoutine = StartCoroutine(CloseRoutine());
        }

        /// <summary>Shows the panel immediately, useful for deterministic initial state setup.</summary>
        public void OpenImmediate()
        {
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            CacheComponents();
            StopTransition();
            IsOpen = true;
            canvasGroup.alpha = 1f;
            rectTransform.localScale = originalLocalScale;
            SetInputEnabled(true);
        }

        /// <summary>Hides the panel immediately without changing its authored layout values.</summary>
        public void CloseImmediate()
        {
            CacheComponents();
            StopTransition();
            IsOpen = false;
            SetClosedState();
            if (deactivateWhenClosed && gameObject.activeSelf) gameObject.SetActive(false);
        }

        private IEnumerator OpenRoutine()
        {
            yield return Animate(0f, 1f, openStartScale, 1f, openDuration, openCurve);
            canvasGroup.alpha = 1f;
            rectTransform.localScale = originalLocalScale;
            SetInputEnabled(true);
            transitionRoutine = null;
        }

        private IEnumerator CloseRoutine()
        {
            float currentScale = ScaleMultiplier(rectTransform.localScale);
            yield return Animate(canvasGroup.alpha, 0f, currentScale, closeEndScale, closeDuration, closeCurve);
            SetClosedState();
            transitionRoutine = null;

            if (deactivateWhenClosed) gameObject.SetActive(false);
        }

        private IEnumerator Animate(
            float fromAlpha,
            float toAlpha,
            float fromScale,
            float toScale,
            float duration,
            AnimationCurve curve)
        {
            if (duration <= 0f)
            {
                canvasGroup.alpha = toAlpha;
                rectTransform.localScale = originalLocalScale * toScale;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = Evaluate(curve, t);
                canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, eased);
                rectTransform.localScale = originalLocalScale * Mathf.Lerp(fromScale, toScale, eased);
                yield return null;
            }

            canvasGroup.alpha = toAlpha;
            rectTransform.localScale = originalLocalScale * toScale;
        }

        private void CacheComponents()
        {
            if (rectTransform == null) rectTransform = GetComponent<RectTransform>();
            if (!hasOriginalLocalScale)
            {
                originalLocalScale = rectTransform.localScale;
                hasOriginalLocalScale = true;
            }

            if (canvasGroup == null)
            {
                canvasGroup = GetComponent<CanvasGroup>();
                if (canvasGroup == null) canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
        }

        private void StopTransition()
        {
            if (transitionRoutine == null) return;

            StopCoroutine(transitionRoutine);
            transitionRoutine = null;
        }

        private void SetClosedState()
        {
            canvasGroup.alpha = 0f;
            rectTransform.localScale = originalLocalScale;
            SetInputEnabled(false);
        }

        private void SetInputEnabled(bool enabled)
        {
            canvasGroup.interactable = enabled;
            canvasGroup.blocksRaycasts = enabled;
        }

        private float ScaleMultiplier(Vector3 scale)
        {
            // The authored scale can be non-uniform. Use the X ratio when available, preserving
            // the original per-axis proportions when applying animated scale multipliers.
            return !Mathf.Approximately(originalLocalScale.x, 0f)
                ? scale.x / originalLocalScale.x
                : 1f;
        }

        private static float Evaluate(AnimationCurve curve, float time)
        {
            return curve != null && curve.length > 0
                ? curve.Evaluate(time)
                : Mathf.SmoothStep(0f, 1f, time);
        }
    }
}
