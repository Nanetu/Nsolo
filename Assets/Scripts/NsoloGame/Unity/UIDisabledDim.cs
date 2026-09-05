using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Makes a button that is switched off look switched off.
    ///
    /// Three buttons in the game are held non-interactable until something has been chosen —
    /// CONTINUE on the mode screen, START GAME on the difficulty screen, START GAME in the lobby.
    /// With the old artwork that state was invisible either way, because the buttons were
    /// transparent rectangles over painted pills. On real artwork it stops being harmless: the
    /// button looks exactly as it does when it works, so a player taps it and concludes the game is
    /// broken rather than that they have missed a step.
    ///
    /// The fade is applied to the colours of the graphics rather than through a CanvasGroup, and
    /// that is not incidental: <see cref="NsoloPanel"/>'s entrance animates the CanvasGroup alpha of
    /// everything on a screen, so a dim living there would be overwritten every frame the screen
    /// was arriving — and would then be restored to the wrong value when it closed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIDisabledDim : MonoBehaviour
    {
        /// <summary>How far down a disabled button goes. Clearly dimmer, still clearly readable.</summary>
        public const float DisabledAlpha = 0.4f;

        private readonly List<Graphic> graphics = new List<Graphic>();
        private readonly List<float> authoredAlpha = new List<float>();
        private bool captured;
        private bool dimmed;

        /// <summary>Fades the button down, or puts it back exactly as it was authored.</summary>
        public void SetDimmed(bool value)
        {
            Capture();

            if (dimmed == value) return;
            dimmed = value;

            for (int i = 0; i < graphics.Count; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic == null) continue;

                Color colour = graphic.color;
                colour.a = value ? authoredAlpha[i] * DisabledAlpha : authoredAlpha[i];
                graphic.color = colour;
            }
        }

        /// <summary>
        /// Records the authored colours once, so putting the button back is exact rather than an
        /// assumed full alpha — some labels are deliberately drawn at less than that.
        /// </summary>
        private void Capture()
        {
            if (captured) return;
            captured = true;

            foreach (Graphic graphic in GetComponentsInChildren<Graphic>(true))
            {
                if (graphic == null) continue;

                // UIPressFeedback's own layer animates its alpha under a finger. Anything that
                // writes to it from outside would be fighting that, and it is invisible at rest
                // anyway, so there is nothing here to dim.
                if (graphic.gameObject.name == "PressLayer") continue;

                graphics.Add(graphic);
                authoredAlpha.Add(graphic.color.a);
            }
        }
    }
}
