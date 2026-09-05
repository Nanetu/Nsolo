using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Put this on one thing inside a screen — a button, a label, a tick — pick what it is from the
    /// dropdown, and it wires itself.
    ///
    /// A button tagged this way needs nothing else: no Button component, no onClick entry, no
    /// reference dragged into a controller. If the object is only a picture, a Button is added to
    /// it; if it has no press feedback, that is added too. The point is that rebuilding a screen
    /// should not mean rebuilding its wiring, and that a mistake should be a warning in the console
    /// rather than a button that quietly does nothing.
    ///
    /// See <see cref="ElementId"/> for what each entry means, and <see cref="NsoloPanel"/> for the
    /// screen this sits inside.
    /// </summary>
    [DisallowMultipleComponent]
    public class NsoloElement : MonoBehaviour
    {
        /// <summary>How the press glow is shaped. Auto reads it off the button's proportions.</summary>
        public enum Shape
        {
            [InspectorName("Auto (work it out from the size)")] Auto = 0,
            [InspectorName("Pill (long and thin)")] Pill = 1,
            [InspectorName("Card (square-ish)")] Card = 2,
            [InspectorName("None (no glow, just the squash)")] None = 3,
        }

        [Tooltip("What this object is. Everything else on this component is optional.")]
        [SerializeField] private ElementId id = ElementId.None;

        [Header("Optional")]
        [Tooltip("On: any On Click () entries set here in the Inspector are dropped, and this " +
                 "component's action is the only one. That is what you want when retrofitting an " +
                 "old button. Turn it off to keep the Inspector wiring and add to it.")]
        [SerializeField] private bool replaceExistingClicks = true;

        [Tooltip("On: the button squashes and glows under a finger. Turn off only for something " +
                 "that should not react, like a label you made tappable by mistake.")]
        [SerializeField] private bool addPressFeedback = true;

        [Tooltip("The shape of the glow. Leave on Auto.")]
        [SerializeField] private Shape pressShape = Shape.Auto;

        /// <summary>What this object is. Read by <see cref="NsoloUI"/> when it builds the register.</summary>
        public ElementId Id => id;

        /// <summary>
        /// The heading a label was authored with, captured before anything writes a value into it.
        ///
        /// The profile's stat cards are one TMP object each, carrying the heading — "MATCHES",
        /// "BEST STREAK" — with the number to be added underneath. Writing the number alone would
        /// wipe the heading and leave six cards showing bare figures; hard-coding the headings in
        /// C# would mean the design lived in two places and drifted the first time one was renamed
        /// in the Inspector. So the label keeps its own heading and the code only supplies the part
        /// it knows. See <see cref="NsoloUI.SetValue"/>.
        /// </summary>
        public string Caption { get; private set; }

        /// <summary>
        /// Set once the action is attached, so a second scan does not attach it twice. Deliberately
        /// not serialized — it describes this run, not the saved object.
        /// </summary>
        private bool bound;

        /// <summary>
        /// Attaches the behaviour this element's id names. Called by <see cref="NsoloUI"/> during
        /// its scan rather than from Awake, because menu panels sit in the scene switched off and a
        /// disabled object never runs Awake.
        /// </summary>
        internal void Bind()
        {
            if (bound || id == ElementId.None) return;

            bool isAction = NsoloUI.IsAction(id);

            // Captured before the first value is written, and only once — a second scan must not
            // read back a heading that already has last game's number stuck to the end of it.
            if (!isAction && Caption == null)
            {
                var label = GetComponent<TMPro.TMP_Text>();
                Caption = label != null ? label.text : string.Empty;
            }

            // Order matters: the press feedback reads the RectTransform to choose its shape, and
            // wants the Button in place first so it can grey itself out with it.
            if (isAction) EnsureClickable();
            if (addPressFeedback && GetComponent<Button>() != null) AttachPressFeedback();

            if (!isAction)
            {
                // A label or a tick has nothing to attach. Being in the register is the whole job.
                bound = true;
                return;
            }

            var button = GetComponent<Button>();
            if (button == null)
            {
                Debug.LogWarning(
                    $"NsoloElement on '{name}': '{id}' is a button action, but this object could " +
                    "not be made clickable. It needs to be under a Canvas.", this);
                return;
            }

            System.Action action = NsoloUI.ActionFor(id);
            if (action == null)
            {
                Debug.LogWarning(
                    $"NsoloElement on '{name}': nothing to bind '{id}' to — there is no " +
                    $"{NsoloUI.OwnerOf(id)} in the scene. The button will do nothing.", this);
                return;
            }

            // Dropping the Inspector's own entries is the point of the default: a rebuilt button
            // that still carries an old On Click () would fire both, and two navigations from one
            // tap is a bug that only shows up on the screen you land on. UIClickSound adds the
            // click sound in Start, which is after this, so clearing here cannot silence it.
            if (replaceExistingClicks) button.onClick = new Button.ButtonClickedEvent();

            button.onClick.AddListener(() => action());
            bound = true;
        }

        /// <summary>
        /// Makes sure a tap on this object registers: a Button to receive it, and a graphic for the
        /// raycast to hit. A picture exported from Figma is an Image and nothing else, so both are
        /// usually missing.
        /// </summary>
        private void EnsureClickable()
        {
            var graphic = GetComponent<Graphic>();

            if (graphic == null)
            {
                // Nothing to hit. An invisible Image over the object's own rect gives the raycast
                // something to land on without changing what is drawn.
                var filler = gameObject.AddComponent<Image>();
                filler.color = new Color(1f, 1f, 1f, 0f);
                graphic = filler;
            }

            // A graphic that is not a raycast target is invisible to touch however opaque it looks.
            graphic.raycastTarget = true;

            var button = GetComponent<Button>();
            if (button != null) return;

            button = gameObject.AddComponent<Button>();
            button.targetGraphic = graphic;

            // Unity's own feedback is a colour tint on that graphic. On real artwork it reads as
            // the picture changing colour, which is not what a press should look like — and this
            // game's press feedback is a separate layer over the top. One or the other, not both.
            // Only done for a Button we added: one that was already here may have been set up
            // deliberately, and it is not this component's place to overrule that.
            button.transition = Selectable.Transition.None;
        }

        private void AttachPressFeedback()
        {
            if (pressShape == Shape.None)
            {
                UIPressFeedback.Attach(gameObject, null);
                return;
            }

            Sprite sprite;
            switch (pressShape)
            {
                case Shape.Pill: sprite = UIShapes.Pill; break;
                case Shape.Card: sprite = UIShapes.Card; break;
                default: sprite = UIShapes.For(transform as RectTransform); break;
            }

            UIPressFeedback.Attach(gameObject, sprite);
        }
    }
}
