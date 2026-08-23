using System.Collections.Generic;
using System.Text;
using NsoloGame.Unity;
using UnityEditor;
using UnityEngine;

namespace NsoloGame.EditorTools
{
    /// <summary>
    /// Menu item: <b>Nsolo &gt; Check UI Wiring</b>. Reads the open scene and says what is tagged,
    /// what is missing, and what is tagged twice.
    ///
    /// This exists because the failure it looks for is silent. A button with nothing behind it
    /// looks exactly like a button with something behind it until somebody taps it, and finding
    /// that out by walking the whole game by hand is slow and easy to be unlucky at. Running this
    /// takes a second and needs no play mode.
    /// </summary>
    public static class NsoloWiringCheck
    {
        /// <summary>
        /// The elements a screen is expected to have once it is rebuilt. Missing ones are reported,
        /// not enforced — a screen part-way through a rebuild is a normal state to be in, and the
        /// report is there to say what is left rather than to complain.
        /// </summary>
        private static readonly Dictionary<PanelId, ElementId[]> Expected =
            new Dictionary<PanelId, ElementId[]>
        {
            { PanelId.MainMenu, new[]
                {
                    ElementId.MainMenuPlay, ElementId.MainMenuProfile,
                    ElementId.MainMenuHowToPlay, ElementId.MainMenuQuit,
                } },

            // Play online is a card here, not a main-menu button: beside PLAY it promised a game
            // without saying which kind, and this is the screen whose whole job is to say.
            { PanelId.Mode, new[]
                {
                    ElementId.ModeVsComputer, ElementId.ModeVsHuman, ElementId.ModeOnline,
                    ElementId.ModeContinue,
                } },
            { PanelId.Difficulty, new[]
                {
                    ElementId.DifficultyEasy, ElementId.DifficultyMedium, ElementId.DifficultyHard,
                    ElementId.DifficultyStart,
                } },
            { PanelId.Profile, new[]
                {
                    ElementId.ProfileUsernameLabel, ElementId.ProfileMatchesLabel,
                    ElementId.ProfileWinRateLabel, ElementId.ProfileBestStreakLabel,
                } },
            { PanelId.Online, new[] { ElementId.OnlineCreateRoom, ElementId.OnlineJoinRoom } },
            { PanelId.Lobby, new[]
                {
                    ElementId.LobbyRoomCodeLabel, ElementId.LobbyStart, ElementId.LobbyLeave,
                } },
            { PanelId.Pause, new[]
                {
                    ElementId.PauseResume, ElementId.PauseRestart, ElementId.PauseMainMenu,
                } },
            { PanelId.GameOver, new[]
                {
                    ElementId.GameOverTitleLabel, ElementId.GameOverPlayAgain, ElementId.GameOverMainMenu,
                    ElementId.GameOverCapturesLabel, ElementId.GameOverRelayLabel,
                } },
            { PanelId.Tutorial, new[] { ElementId.TutorialClose } },
        };

        /// <summary>Screens that need a BACK, because there is no other way off them.</summary>
        private static readonly PanelId[] NeedBack =
        {
            PanelId.Mode, PanelId.Difficulty, PanelId.Profile, PanelId.Tutorial,
        };

        [MenuItem("Nsolo/Check UI Wiring")]
        public static void Check()
        {
            var panels = new List<NsoloPanel>();
            foreach (NsoloPanel panel in Resources.FindObjectsOfTypeAll<NsoloPanel>())
                if (panel != null && panel.gameObject.scene.IsValid()) panels.Add(panel);

            var elements = new List<NsoloElement>();
            foreach (NsoloElement element in Resources.FindObjectsOfTypeAll<NsoloElement>())
                if (element != null && element.gameObject.scene.IsValid()) elements.Add(element);

            var report = new StringBuilder();
            int problems = 0;

            report.AppendLine("── Nsolo UI wiring ───────────────────────────────");
            report.AppendLine($"{panels.Count} tagged screen(s), {elements.Count} tagged element(s).");
            report.AppendLine();

            // ── Screens ──────────────────────────────────────────────────
            var byPanelId = new Dictionary<PanelId, NsoloPanel>();
            foreach (NsoloPanel panel in panels)
            {
                if (panel.Id == PanelId.None)
                {
                    report.AppendLine($"  PROBLEM  '{Path(panel.gameObject)}' has a Nsolo Panel but its Id is None. Pick which screen it is.");
                    problems++;
                    continue;
                }

                if (byPanelId.ContainsKey(panel.Id))
                {
                    report.AppendLine($"  PROBLEM  Two screens are both tagged {panel.Id}: '{Path(byPanelId[panel.Id].gameObject)}' and '{Path(panel.gameObject)}'. Delete one or set its Id to None.");
                    problems++;
                    continue;
                }

                byPanelId[panel.Id] = panel;
            }

            // ── Elements ─────────────────────────────────────────────────
            var byElementId = new Dictionary<ElementId, List<NsoloElement>>();
            foreach (NsoloElement element in elements)
            {
                if (element.Id == ElementId.None)
                {
                    report.AppendLine($"  PROBLEM  '{Path(element.gameObject)}' has a Nsolo Element but nothing picked. Pick what it is, or remove the component.");
                    problems++;
                    continue;
                }

                if (!byElementId.TryGetValue(element.Id, out List<NsoloElement> list))
                {
                    list = new List<NsoloElement>();
                    byElementId[element.Id] = list;
                }
                list.Add(element);
            }

            foreach (KeyValuePair<ElementId, List<NsoloElement>> pair in byElementId)
            {
                if (pair.Value.Count <= 1 || NsoloUI.IsAction(pair.Key)) continue;

                report.AppendLine($"  PROBLEM  {pair.Key} is on {pair.Value.Count} objects. Only labels' and ticks' first one is ever used:");
                foreach (NsoloElement element in pair.Value)
                    report.AppendLine($"             {Path(element.gameObject)}");
                problems++;
            }

            // ── What each rebuilt screen is still missing ────────────────
            report.AppendLine();
            foreach (KeyValuePair<PanelId, NsoloPanel> pair in byPanelId)
            {
                var missing = new List<string>();

                if (Expected.TryGetValue(pair.Key, out ElementId[] wanted))
                    foreach (ElementId id in wanted)
                        if (!byElementId.ContainsKey(id)) missing.Add(id.ToString());

                if (System.Array.IndexOf(NeedBack, pair.Key) >= 0 && !byElementId.ContainsKey(ElementId.Back))
                    missing.Add("Back");

                if (missing.Count == 0)
                {
                    report.AppendLine($"  OK       {pair.Key}  ('{pair.Value.name}')");
                    continue;
                }

                report.AppendLine($"  TO DO    {pair.Key}  ('{pair.Value.name}') is missing: {string.Join(", ", missing)}");
            }

            // ── Screens not rebuilt yet ──────────────────────────────────
            var notRebuilt = new List<string>();
            foreach (PanelId id in System.Enum.GetValues(typeof(PanelId)))
            {
                if (id == PanelId.None || id == PanelId.Welcome) continue;
                if (!byPanelId.ContainsKey(id)) notRebuilt.Add(id.ToString());
            }

            if (notRebuilt.Count > 0)
            {
                report.AppendLine();
                report.AppendLine($"  Not tagged yet (still using the old screens): {string.Join(", ", notRebuilt)}");
            }

            report.AppendLine();
            report.AppendLine(problems == 0
                ? "No problems found."
                : $"{problems} problem(s) above need fixing.");

            if (problems > 0) Debug.LogWarning(report.ToString());
            else Debug.Log(report.ToString());
        }

        /// <summary>Hierarchy path, so a name like 'Button' can actually be found.</summary>
        private static string Path(GameObject go)
        {
            string path = go.name;
            Transform cursor = go.transform.parent;

            while (cursor != null)
            {
                path = cursor.name + "/" + path;
                cursor = cursor.parent;
            }

            return path;
        }
    }
}
