using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Gives every button in the game a click, once, from one place.
    ///
    /// Until now the click was played by hand: fifteen <c>AudioManager.Click()</c> calls scattered
    /// through the four controllers, each inside one particular handler. A button made a sound only
    /// if the method its onClick happened to point at was one of those fifteen — so roughly half the
    /// UI was silent, with no pattern a player could perceive. Adding a panel meant remembering to
    /// sprinkle more calls, and forgetting was invisible until someone noticed the quiet.
    ///
    /// This walks the canvas at startup and subscribes to every <see cref="Button"/> it finds,
    /// including the ones on panels that start switched off — which is all of them, since the panels
    /// live in the scene and are merely inactive. New panels are picked up with no wiring at all,
    /// which is the point: the rule is "buttons click", not "these buttons click".
    ///
    /// The existing hand-placed calls are left alone. They fire in the same frame as this one, and
    /// <see cref="AudioManager.PlayUiClick"/> ignores a repeat within the same frame, so the two
    /// paths coexist without doubling up.
    /// </summary>
    public sealed class UIClickSound : MonoBehaviour
    {
        [Tooltip("Root to search. Left empty, the canvas on or above this object is used, and " +
                 "failing that every canvas in the scene.")]
        [SerializeField] private Canvas canvasRoot;

        [Tooltip("Buttons whose sound is handled elsewhere, by name. Rarely needed — a button that " +
                 "should be silent is usually a button that should not be a Button.")]
        [SerializeField] private string[] excludeByName;

        private void Start()
        {
            // Start rather than Awake: panels and their buttons are all present by now, and any
            // controller that rebuilds its own UI in Awake has finished doing so.
            int hooked = 0;

            foreach (Canvas canvas in Roots())
            {
                if (canvas == null) continue;

                foreach (Button button in canvas.GetComponentsInChildren<Button>(true))
                {
                    if (button == null || IsExcluded(button.name)) continue;

                    button.onClick.AddListener(AudioManager.Click);
                    hooked++;
                }
            }

            Debug.Log($"UIClickSound: {hooked} button(s) now play the UI click.");
        }

        private Canvas[] Roots()
        {
            if (canvasRoot != null) return new[] { canvasRoot };

            Canvas own = GetComponentInParent<Canvas>();
            if (own != null) return new[] { own.rootCanvas };

            return FindObjectsOfType<Canvas>(true);
        }

        private bool IsExcluded(string name)
        {
            if (excludeByName == null) return false;

            foreach (string excluded in excludeByName)
                if (!string.IsNullOrEmpty(excluded) && excluded == name) return true;

            return false;
        }

        /// <summary>
        /// Hooks a button created after startup. Nothing needs this yet — every panel is authored in
        /// the scene — but UI built at runtime would otherwise be the one silent corner.
        /// </summary>
        public static void Register(GameObject uiObject)
        {
            if (uiObject == null) return;

            foreach (Button button in uiObject.GetComponentsInChildren<Button>(true))
                if (button != null) button.onClick.AddListener(AudioManager.Click);
        }
    }
}
