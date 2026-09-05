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

        /// <summary>
        /// What the in-game HUD is expected to answer to. Reported as a note rather than a problem:
        /// each of these still falls back to a UIManager slot when nothing is tagged, so an
        /// untagged HUD is a working HUD — it is simply one this check cannot vouch for.
        /// </summary>
        private static readonly ElementId[] HudExpected =
        {
            ElementId.HudPause, ElementId.HudAction,
            ElementId.HudStatusLabel, ElementId.HudTimerLabel,
            ElementId.HudPlayerScoreLabel, ElementId.HudOpponentScoreLabel,
            ElementId.HudLastMoveLabel,
            ElementId.HudOpponentNameLabel, ElementId.HudPlayerNameLabel,
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

            // Screens and elements that have deliberately given up their tag. Listed together at
            // the end so they can be found, and counted as neither working nor broken.
            var released = new List<string>();

            // ── Screens ──────────────────────────────────────────────────
            var byPanelId = new Dictionary<PanelId, NsoloPanel>();
            foreach (NsoloPanel panel in panels)
            {
                if (panel.Id == PanelId.None)
                {
                    // Retired, not broken. A screen that has been replaced keeps its component and
                    // gives up its id, which is what stops two panels claiming one screen while the
                    // old one is still in the hierarchy to be looked at.
                    released.Add(Path(panel.gameObject) + "  (whole screen)");
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
                    // Not a fault. Releasing a tag to None is how a screen is taken out of service
                    // without deleting it — the retired panels are full of these — so it is listed
                    // to be findable and left alone. An element that is *meant* to do something and
                    // has nothing picked shows up the same way, which is the point of listing it.
                    released.Add(Path(element.gameObject));
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

            // ── The board, which is not a screen ─────────────────────────
            // The HUD has no PanelId — it is not a panel, it lives over the board — so it would
            // otherwise be the one part of the game this check has nothing to say about. It is
            // also the part that was wired entirely by hand for longest, and so the part where an
            // empty slot went unnoticed.
            var hudMissing = new List<string>();
            foreach (ElementId id in HudExpected)
                if (!byElementId.ContainsKey(id)) hudMissing.Add(id.ToString());

            report.AppendLine();
            report.AppendLine(hudMissing.Count == 0
                ? "  OK       In-game HUD"
                : $"  NOTE     In-game HUD is untagged for: {string.Join(", ", hudMissing)} " +
                  "(these fall back to the UIManager slots, so check those are filled)");

            if (released.Count > 0)
            {
                report.AppendLine();
                report.AppendLine($"  {released.Count} tagged None — inert, and normally a retired screen:");
                foreach (string path in released) report.AppendLine($"             {path}");
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
