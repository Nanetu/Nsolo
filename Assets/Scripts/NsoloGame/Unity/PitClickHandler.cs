using UnityEngine;
using UnityEngine.EventSystems;

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
            if (uiManager == null) return;

            // OnMouseDown comes from the collider, not the EventSystem, so a full-screen panel over
            // the board does not stop it — a tap on a menu or a modal reaches the pit underneath as
            // well. It only ever looked harmless because the menu states reject the move further
            // down; during the online formation phase, where panels do sit over a live board, it
            // rearranges stones behind whatever the player thinks they are tapping.
            if (IsPointerOverUI()) return;

            uiManager.OnPitClicked(row, col);
        }

        private static bool IsPointerOverUI()
        {
            if (EventSystem.current == null) return false;

            return Input.touchCount > 0
                ? EventSystem.current.IsPointerOverGameObject(Input.GetTouch(0).fingerId)
                : EventSystem.current.IsPointerOverGameObject();
        }
    }
}
