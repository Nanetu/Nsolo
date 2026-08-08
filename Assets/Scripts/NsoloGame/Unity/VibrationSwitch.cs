using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Turns a stock UGUI <see cref="Toggle"/> into a sliding pill switch that matches the volume
    /// sliders next to it: a rounded track with a knob that travels between two positions.
    ///
    /// Despite the name it holds no opinion about vibration — it is a generic two-state switch and
    /// is reused for the tutorial-tips row, which is built by duplicating the vibration one. The
    /// name is kept because renaming the class would mean renaming the file and re-pointing the
    /// scene's script reference.
    ///
    /// The Toggle component is kept rather than replaced so MenuManager's existing
    /// <c>vibrationToggle</c> wiring and its onValueChanged listener keep working untouched. The
    /// Toggle's own transitions are switched off in the scene — this script owns every visual.
    ///
    /// All animation runs on unscaled time because the switch lives on the pause panel, and
    /// MenuManager sets Time.timeScale to 0 while that panel is up.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Toggle))]
    public class VibrationSwitch : MonoBehaviour
    {
        [Header("Parts")]
        [Tooltip("The sliding knob. Anchored to the track's left edge; this script sets its X.")]
        [SerializeField] private RectTransform knob;
        [Tooltip("The rounded background the knob slides along.")]
        [SerializeField] private Image track;
        [Tooltip("Optional ON/OFF caption. Sits opposite the knob and slides with it.")]
        [SerializeField] private RectTransform labelRect;
        [Tooltip("Legacy UI Text rather than TMP — this is the label the stock Toggle shipped with, " +
                 "reused in place so no new text component has to be authored.")]
        [SerializeField] private Text label;

        [Header("Layout")]
        [Tooltip("Gap in pixels between the knob and the end of the track at each extreme.")]
        [SerializeField] private float knobInset = 4f;

        [Header("Colours")]
        [SerializeField] private Color onTrackColor = new Color(0.8196079f, 0.6313726f, 0.34509805f);
        [SerializeField] private Color offTrackColor = new Color(0.32f, 0.28f, 0.24f);
        [SerializeField] private Color onKnobColor = new Color(0.99f, 0.94f, 0.83f);
        [SerializeField] private Color offKnobColor = new Color(0.72f, 0.68f, 0.63f);

        [Header("Motion")]
        [SerializeField] private float slideSeconds = 0.14f;

        [Header("Text")]
        [SerializeField] private string onLabel = "ON";
        [SerializeField] private string offLabel = "OFF";

        [Header("Feedback")]
        [Tooltip("Pulse when the switch is turned on, so the player feels what they just enabled.")]
        [SerializeField] private bool pulseOnEnable = true;

        private Toggle toggle;
        private Image knobImage;
        private Coroutine slide;

        private RectTransform Root => (RectTransform)transform;
        private float TrackWidth => Root.rect.width;
        private float KnobWidth => knob != null ? knob.rect.width : 0f;

        /// <summary>Knob centre when off — inset from the left edge.</summary>
        private float OffX => knobInset + KnobWidth * 0.5f;

        /// <summary>Knob centre when on — the mirror of <see cref="OffX"/> about the track centre.</summary>
        private float OnX => TrackWidth - OffX;

        private void Awake()
        {
            Resolve();
        }

        private void OnEnable()
        {
            Resolve();
            if (toggle == null) return;

            toggle.onValueChanged.RemoveListener(HandleValueChanged);
            toggle.onValueChanged.AddListener(HandleValueChanged);

            // The pause panel is shown and hidden repeatedly, so re-entering always snaps to the
            // saved state rather than animating from wherever the knob happened to be left.
            Apply(toggle.isOn, animated: false);
        }

        private void OnDisable()
        {
            if (toggle != null)
                toggle.onValueChanged.RemoveListener(HandleValueChanged);

            slide = null;
        }

        private void Resolve()
        {
            if (toggle == null) toggle = GetComponent<Toggle>();
            if (knobImage == null && knob != null) knobImage = knob.GetComponent<Image>();
        }

        private void HandleValueChanged(bool isOn)
        {
            Apply(isOn, animated: true);

            // Only pulse on the way on: buzzing to confirm that vibration was just switched off
            // is exactly the wrong feedback. Haptics.Enabled is updated by MenuManager before
            // this runs, so turning it off genuinely produces nothing.
            if (isOn && pulseOnEnable && Application.isPlaying)
                Haptics.Medium();
        }

        /// <summary>
        /// Drives every visual from the on/off state. Safe to call in edit mode, where it snaps
        /// rather than animating so the switch previews correctly in the Scene view.
        /// </summary>
        public void Apply(bool isOn, bool animated)
        {
            if (label != null) label.text = isOn ? onLabel : offLabel;

            float targetX = isOn ? OnX : OffX;
            Color targetTrack = isOn ? onTrackColor : offTrackColor;
            Color targetKnob = isOn ? onKnobColor : offKnobColor;

            if (!animated || !Application.isPlaying || !isActiveAndEnabled)
            {
                SetVisuals(targetX, targetTrack, targetKnob);
                return;
            }

            if (slide != null) StopCoroutine(slide);
            slide = StartCoroutine(SlideTo(targetX, targetTrack, targetKnob));
        }

        private IEnumerator SlideTo(float targetX, Color targetTrack, Color targetKnob)
        {
            float startX = knob != null ? knob.anchoredPosition.x : targetX;
            Color startTrack = track != null ? track.color : targetTrack;
            Color startKnob = knobImage != null ? knobImage.color : targetKnob;

            float t = 0f;
            while (t < 1f)
            {
                // Unscaled: the pause menu freezes Time.timeScale, and this switch lives on it.
                t += slideSeconds > 0f ? Time.unscaledDeltaTime / slideSeconds : 1f;
                float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

                SetVisuals(
                    Mathf.Lerp(startX, targetX, eased),
                    Color.Lerp(startTrack, targetTrack, eased),
                    Color.Lerp(startKnob, targetKnob, eased));

                yield return null;
            }

            SetVisuals(targetX, targetTrack, targetKnob);
            slide = null;
        }

        private void SetVisuals(float knobX, Color trackColor, Color knobColor)
        {
            if (knob != null)
            {
                Vector2 p = knob.anchoredPosition;
                knob.anchoredPosition = new Vector2(knobX, p.y);
            }

            // The caption sits at the knob's mirror position, so it always occupies the empty
            // half of the track instead of being slid over.
            if (labelRect != null)
            {
                Vector2 p = labelRect.anchoredPosition;
                labelRect.anchoredPosition = new Vector2(TrackWidth - knobX, p.y);
            }

            if (track != null) track.color = trackColor;
            if (knobImage != null) knobImage.color = knobColor;
        }

#if UNITY_EDITOR
        /// <summary>Keeps the Scene view honest while the colours and inset are being tweaked.</summary>
        private void OnValidate()
        {
            if (Application.isPlaying) return;
            Resolve();
            if (toggle != null) Apply(toggle.isOn, animated: false);
        }
#endif
    }
}
