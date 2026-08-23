using System.Collections.Generic;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Inspector-driven coordinator for existing <see cref="UIPanelTransition"/> panels.
    /// Assign panel references in the Inspector, then call ShowPanel from buttons or game code.
    /// </summary>
    public sealed class UIScreenManager : MonoBehaviour
    {
        [SerializeField] private List<UIPanelTransition> panels = new List<UIPanelTransition>();
        [SerializeField] private UIPanelTransition initialPanel;

        public UIPanelTransition CurrentPanel { get; private set; }

        private void Start()
        {
            if (initialPanel != null) ShowPanel(initialPanel);
        }

        /// <summary>Switches to a panel already configured with UIPanelTransition.</summary>
        public void ShowPanel(UIPanelTransition panel)
        {
            if (panel == null)
            {
                Debug.LogWarning("UIScreenManager: Cannot show a null panel.", this);
                return;
            }

            if (!panels.Contains(panel)) panels.Add(panel);

            foreach (UIPanelTransition otherPanel in panels)
            {
                if (otherPanel != null && otherPanel != panel)
                    otherPanel.Close();
            }

            CurrentPanel = panel;
            panel.Open();
        }

        /// <summary>
        /// Convenience overload for existing GameObject panels. Add UIPanelTransition to the panel
        /// in the Inspector, then wire this directly from a Button event if desired.
        /// </summary>
        public void ShowPanel(GameObject panel)
        {
            if (panel == null)
            {
                Debug.LogWarning("UIScreenManager: Cannot show a null panel GameObject.", this);
                return;
            }

            UIPanelTransition transition = panel.GetComponent<UIPanelTransition>();
            if (transition == null)
            {
                Debug.LogWarning(
                    $"UIScreenManager: '{panel.name}' needs a UIPanelTransition component before it can be shown.",
                    panel);
                return;
            }

            ShowPanel(transition);
        }

        /// <summary>Closes every assigned panel without changing any existing Button events.</summary>
        public void HideAllPanels()
        {
            foreach (UIPanelTransition panel in panels)
            {
                if (panel != null) panel.Close();
            }

            CurrentPanel = null;
        }
    }
}
