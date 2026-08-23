using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Adds responsive press/release scale feedback to a standard Unity UI Button.
    /// The original RectTransform scale is preserved, including non-uniform scale.
    /// </summary>
    [RequireComponent(typeof(Button), typeof(RectTransform))]
    public sealed class AnimatedUIButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        [Header("Scale")]
        [SerializeField, Range(0.5f, 1f)] private float pressedScale = 0.94f;
        [SerializeField, Range(1f, 1.25f)] private float releaseOvershoot = 1.05f;
        [SerializeField, Min(0f)] private float pressDuration = 0.08f;
        [SerializeField, Min(0f)] private float releaseDuration = 0.08f;
        [SerializeField, Min(0f)] private float settleDuration = 0.1f;
        [Tooltip("Optional easing curve. Leave empty to use a smooth built-in ease.")]
        [SerializeField] private AnimationCurve animationCurve;

        [Header("Click Audio")]
        [SerializeField] private AudioClip clickClip;
        [SerializeField] private AudioSource clickAudioSource;

        private RectTransform rectTransform;
        private Button button;
        private Vector3 originalLocalScale;
        private Coroutine scaleRoutine;
        private bool isHeld;

        private bool CanAnimate => isActiveAndEnabled && button != null && button.interactable;

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            button = GetComponent<Button>();
            originalLocalScale = rectTransform.localScale;
        }

        private void OnEnable()
        {
            if (button == null) button = GetComponent<Button>();
            button.onClick.AddListener(PlayClickSound);
            RestoreOriginalScale();
        }

        private void OnDisable()
        {
            if (button != null) button.onClick.RemoveListener(PlayClickSound);
            StopScaleAnimation();
            isHeld = false;
            RestoreOriginalScale();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!CanAnimate) return;

            isHeld = true;
            StartScaleAnimation(ScaleTo(pressedScale, pressDuration));
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Release();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            // Dragging off a button cancels Unity's click, but should still restore its visual state.
            Release();
        }

        private void Release()
        {
            if (!isHeld) return;

            isHeld = false;
            if (!CanAnimate)
            {
                RestoreOriginalScale();
                return;
            }

            StartScaleAnimation(ReleaseAndSettle());
        }

        private void PlayClickSound()
        {
            // Button.onClick is invoked only for a completed, valid Button click.
            if (clickClip != null && clickAudioSource != null)
                clickAudioSource.PlayOneShot(clickClip);
        }

        private void StartScaleAnimation(IEnumerator routine)
        {
            StopScaleAnimation();
            scaleRoutine = StartCoroutine(routine);
        }

        private void StopScaleAnimation()
        {
            if (scaleRoutine == null) return;

            StopCoroutine(scaleRoutine);
            scaleRoutine = null;
        }

        private IEnumerator ReleaseAndSettle()
        {
            yield return ScaleTo(releaseOvershoot, releaseDuration);
            yield return ScaleTo(1f, settleDuration);
            scaleRoutine = null;
        }

        private IEnumerator ScaleTo(float targetMultiplier, float duration)
        {
            Vector3 from = rectTransform.localScale;
            Vector3 to = originalLocalScale * targetMultiplier;

            if (duration <= 0f)
            {
                rectTransform.localScale = to;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float normalizedTime = Mathf.Clamp01(elapsed / duration);
                rectTransform.localScale = Vector3.LerpUnclamped(from, to, Evaluate(normalizedTime));
                yield return null;
            }

            rectTransform.localScale = to;
        }

        private float Evaluate(float time)
        {
            return animationCurve != null && animationCurve.length > 0
                ? animationCurve.Evaluate(time)
                : Mathf.SmoothStep(0f, 1f, time);
        }

        private void RestoreOriginalScale()
        {
            if (rectTransform != null)
                rectTransform.localScale = originalLocalScale;
        }
    }
}
