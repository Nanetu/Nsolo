using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Attach to each pit GameObject. Requires a Collider on the same GameObject.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PitClickHandler : MonoBehaviour
    {
        private UIManager uiManager;
        private int row;
        private int col;

        public int Row => row;
        public int Col => col;

        public void Initialize(UIManager manager, int pitRow, int pitCol)
        {
            uiManager = manager;
            row = pitRow;
            col = pitCol;
        }

        private void OnMouseDown()
        {
            if (uiManager != null)
            {
                uiManager.OnPitClicked(row, col);
            }
        }
    }
}
