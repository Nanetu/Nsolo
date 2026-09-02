using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// The rounded shapes the press feedback is drawn with, generated in code rather than imported.
    ///
    /// <see cref="UIPressFeedback"/> needs a 9-sliced rounded sprite to light a button up in the
    /// shape of the button. Two such PNGs exist in the project, but a sprite that has to be dragged
    /// into a slot is one more thing to forget on every new button — and forgetting it is silent,
    /// because the button still scales and simply never glows. A white rounded rectangle carries no
    /// design decisions worth keeping in a file, so it is made here instead and the slot disappears.
    ///
    /// This follows what the stone sound already does: <see cref="PitStoneAnimator"/> synthesises
    /// its click rather than sourcing one, for the same reason — the asset was never the point.
    /// </summary>
    public static class UIShapes
    {
        private static Sprite pill;
        private static Sprite card;

        /// <summary>Fully rounded ends, for the long pill buttons.</summary>
        public static Sprite Pill
        {
            get
            {
                if (pill == null) pill = Rounded(30, "NsoloPill");
                return pill;
            }
        }

        /// <summary>A gentler corner, for the square-ish cards on the mode and difficulty screens.</summary>
        public static Sprite Card
        {
            get
            {
                if (card == null) card = Rounded(16, "NsoloCard");
                return card;
            }
        }

        /// <summary>
        /// Picks a shape from the button's proportions. A wide, short rect is a pill; anything
        /// closer to square is a card. It only decides how the corners are drawn, so being wrong
        /// about a borderline case costs nothing.
        /// </summary>
        public static Sprite For(RectTransform rect)
        {
            if (rect == null) return Pill;

            // On-screen proportions, not the authored rect's. Elements exported from Figma arrive
            // at a uniform 160x30 and are sized by a non-uniform localScale instead, so judging by
            // rect.size alone reads every button in the game as the same 5.3:1 sliver and hands a
            // pill to the square cards on the mode and difficulty screens.
            Vector2 size = rect.rect.size;
            Vector3 scale = rect.localScale;
            size.x *= Mathf.Abs(scale.x);
            size.y *= Mathf.Abs(scale.y);

            if (size.y <= 0f) return Pill;

            return size.x / size.y >= 2.2f ? Pill : Card;
        }

        /// <summary>
        /// A white rounded square with a 9-slice border at the corner radius, so stretching it to
        /// any button size keeps the corners their authored size instead of smearing them.
        /// </summary>
        private static Sprite Rounded(int radius, string name)
        {
            // Two straight pixels in the middle of each edge. That strip is what the 9-slice
            // stretches; with none, the corners would meet and the sprite could not be widened.
            int size = radius * 2 + 2;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Pixel centres, so the coverage either side of the curve is symmetric.
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    // Distance out from whichever corner circle this pixel falls in. Pixels in the
                    // straight middle band are inside by definition and skip the test.
                    float cx = px < radius ? radius : (px > size - radius ? size - radius : px);
                    float cy = py < radius ? radius : (py > size - radius ? size - radius : py);

                    float dx = px - cx;
                    float dy = py - cy;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    // One pixel of coverage across the edge, which is all the antialiasing a shape
                    // that is about to be tinted to 14% alpha needs.
                    float alpha = Mathf.Clamp01(radius - distance + 0.5f);

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));

            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
