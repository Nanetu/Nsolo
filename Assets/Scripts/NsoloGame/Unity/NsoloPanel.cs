using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Put this on the root object of a screen and say which screen it is. That is the entire
    /// wiring: <see cref="MenuManager"/> and <see cref="OnlineFlowController"/> look screens up by
    /// id, so a rebuilt panel takes over from the one it replaces the moment it is tagged, with
    /// nothing dragged into any Inspector slot.
    ///
    /// It also gives the screen its motion. A <see cref="PanelTransition"/> is added if there is
    /// none, so the panel slides and fades rather than cutting; and its contents come in one after
    /// another rather than all together, which is the difference between a screen that appears and
    /// a screen that arrives.
    /// </summary>
    [DisallowMultipleComponent]
    public class NsoloPanel : MonoBehaviour
    {
        [Tooltip("Which screen this is. Two panels must not claim the same one.")]
        [SerializeField] private PanelId id = PanelId.None;

        [Header("Motion")]
        [Tooltip("On: the panel slides and fades in and out. Leave on.")]
        [SerializeField] private bool addTransition = true;

        [Tooltip("On: the things inside arrive one shortly after another instead of all at once.")]
        [SerializeField] private bool staggerContents = true;

        [Tooltip("The gap between one element arriving and the next.")]
        [SerializeField, Range(0f, 0.12f)] private float staggerSeconds = 0.035f;

        [Tooltip("How long each element takes to fade and rise into place.")]
        [SerializeField, Range(0.05f, 0.5f)] private float elementSeconds = 0.18f;

        [Tooltip("How far each element rises, in canvas units.")]
        [SerializeField] private float elementLift = 18f;

        /// <summary>
        /// The longest the whole sequence may take. A screen with twenty elements at 35ms apart
        /// would otherwise spend most of a second assembling itself, and the player is waiting on
        /// it — so past this point the gap is squeezed rather than the screen being slow.
        /// </summary>
        private const float MaxSequenceSeconds = 0.34f;

        /// <summary>Which screen this is. Read by <see cref="NsoloUI"/> when it builds the register.</summary>
        public PanelId Id => id;

        private struct Element
        {
            public RectTransform rect;
            public CanvasGroup group;
            public Vector2 home;
            public float homeAlpha;
        }

        private List<Element> elements;
        private Coroutine motion;
        private bool prepared;

        /// <summary>
        /// Gives the panel its transition. Called by <see cref="NsoloUI"/> during its scan, which
        /// reaches panels that are switched off — where Awake would never run.
        /// </summary>
        internal void Prepare()
        {
            if (prepared) return;
            prepared = true;

            if (!addTransition) return;
            if (GetComponent<PanelTransition>() != null) return;

            // The older UIPanelTransition counts too. Two things animating one panel fight over its
            // alpha, and the result reads as a flicker rather than as either animation.
            if (GetComponent<UIPanelTransition>() != null) return;

            gameObject.AddComponent<PanelTransition>();
        }

        private void OnEnable()
        {
            if (!staggerContents) return;

            Capture();
            if (elements.Count == 0) return;

            motion = StartCoroutine(Arrive());
        }

        private void OnDisable()
        {
            // The coroutine dies with the object, so anything it had faded down or lifted out of
            // place would stay that way until something else moved it. Put the screen back as
            // authored, ready for the next time it opens.
            motion = null;
            Restore();
        }

        /// <summary>
        /// Records where the contents live and how visible they are, once. Everything the entrance
        /// touches is put back to exactly these values, so running it cannot slowly drift a screen
        /// away from how it was built.
        /// </summary>
        private void Capture()
        {
            if (elements != null) return;

            elements = new List<Element>();

            foreach (Transform child in transform)
            {
                var rect = child as RectTransform;
                if (rect == null) continue;

                var group = child.GetComponent<CanvasGroup>();
                if (group == null) group = child.gameObject.AddComponent<CanvasGroup>();

                elements.Add(new Element
                {
                    rect = rect,
                    group = group,
                    home = rect.anchoredPosition,

                    // The authored alpha, not 1. A child deliberately held faint stays faint.
                    homeAlpha = group.alpha,
                });
            }
        }

        private void Restore()
        {
            if (elements == null) return;

            foreach (Element element in elements)
            {
                if (element.rect == null || element.group == null) continue;

                element.rect.anchoredPosition = element.home;
                element.group.alpha = element.homeAlpha;
            }
        }

        private IEnumerator Arrive()
        {
            int count = elements.Count;

            float step = staggerSeconds;
            if (count > 1 && step * (count - 1) > MaxSequenceSeconds)
                step = MaxSequenceSeconds / (count - 1);

            // Hidden in one pass before any of it starts moving, or the first element would be
            // visible for a frame at the wrong place while the last was still being set up.
            for (int i = 0; i < count; i++)
            {
                Element element = elements[i];
                if (element.rect == null || element.group == null) continue;

                element.group.alpha = 0f;
                element.rect.anchoredPosition = element.home + new Vector2(0f, -elementLift);
            }

            float elapsed = 0f;
            bool moving = true;

            while (moving)
            {
                // Unscaled throughout: the pause screen opens at timeScale zero, and an entrance
                // that only plays while the game is running would never play there at all.
                elapsed += Time.unscaledDeltaTime;
                moving = false;

                for (int i = 0; i < count; i++)
                {
                    Element element = elements[i];
                    if (element.rect == null || element.group == null) continue;

                    float local = elapsed - i * step;

                    if (local <= 0f)
                    {
                        moving = true;
                        continue;
                    }

                    float t = elementSeconds > 0f ? Mathf.Clamp01(local / elementSeconds) : 1f;
                    if (t < 1f) moving = true;

                    // Ease out, matching PanelTransition: quick away, settling as it lands.
                    float e = 1f - (1f - t) * (1f - t);

                    element.group.alpha = element.homeAlpha * e;
                    element.rect.anchoredPosition =
                        element.home + new Vector2(0f, Mathf.Lerp(-elementLift, 0f, e));
                }

                yield return null;
            }

            Restore();
            motion = null;
        }
    }
}
