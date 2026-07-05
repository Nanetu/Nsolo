using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace NsoloGame.Unity
{
    [Serializable]
    public class PlayerProfile
    {
        public string username = "Player";
        public int gamesPlayed;
        public int gamesWon;
        public int easyWins;
        public int easyLosses;
        public int mediumWins;
        public int mediumLosses;
        public int hardWins;
        public int hardLosses;
        public float shortestWinSeconds = float.MaxValue;

        // Win-streak tracking. currentWinStreak resets to 0 on any loss; longestWinStreak is
        // the best streak ever reached and is what the "10 Game Streak" achievement checks.
        public int currentWinStreak;
        public int longestWinStreak;

        // Achievements, once earned, stay earned even if the underlying stat later regresses
        // (e.g. a win streak dropping back below the threshold), so they're stored explicitly.
        public bool firstWinUnlocked;
        public bool tenStreakUnlocked;
        public bool hardMasterUnlocked;
    }

    /// <summary>Identifies each of the three profile-page achievements.</summary>
    public enum Achievement
    {
        FirstWin,
        TenGameStreak,
        HardModeMaster,
    }

    /// <summary>
    /// One achievement icon on the profile page. Every reference is optional — wire up only the
    /// pieces a given icon uses. RefreshProfileUI() dims/tints these based on unlock state.
    /// </summary>
    [Serializable]
    public class AchievementSlot
    {
        public Achievement achievement;

        [Tooltip("Optional: the whole icon group. Dimmed to Locked Alpha while the achievement is locked.")]
        public CanvasGroup group;

        [Tooltip("Optional: icon graphic tinted between the locked and unlocked colors.")]
        public Image icon;

        [Tooltip("Optional: caption auto-filled with the achievement's display name.")]
        public TMP_Text label;

        [Tooltip("Optional: object shown only while the achievement is still locked (e.g. a padlock badge).")]
        public GameObject lockedBadge;
    }

    public class ProfileManager : MonoBehaviour
    {
        [Header("Profile Panel UI")]
        [SerializeField] private TMP_Text usernameText;
        [SerializeField] private TMP_Text gamesPlayedText;
        [SerializeField] private TMP_Text gamesWonText;
        [SerializeField] private TMP_Text winRateText;
        [SerializeField] private TMP_Text shortestWinText;
        [SerializeField] private TMP_Text easyWinsText;
        [SerializeField] private TMP_Text hardWinsText;
        [SerializeField] private TMP_Text breakdownText;

        [Header("Achievements")]
        [Tooltip("The three achievement icons shown below the edit button. Drag each icon group here and pick which achievement it represents.")]
        [SerializeField] private AchievementSlot[] achievementSlots;
        [SerializeField, Range(0f, 1f)] private float lockedAlpha = 0.35f;
        [SerializeField] private Color unlockedIconColor = new Color(1f, 0.82f, 0.35f);
        [SerializeField] private Color lockedIconColor = new Color(0.45f, 0.45f, 0.45f);

        // Unlock thresholds for the streak/hard-mode achievements.
        private const int TenStreakThreshold = 10;
        private const int HardMasterThreshold = 10;

        private PlayerProfile profile;

        private static string SavePath =>
            Path.Combine(Application.persistentDataPath, "profile.json");

        private static ProfileManager _instance;
        public static ProfileManager Instance => _instance;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            Load();
        }

        // ── Persistence ───────────────────────────────────────────────────

        private void Load()
        {
            if (File.Exists(SavePath))
            {
                try { profile = JsonUtility.FromJson<PlayerProfile>(File.ReadAllText(SavePath)); }
                catch { profile = new PlayerProfile(); }
            }
            else
            {
                profile = new PlayerProfile();
            }

            // Backfill unlock flags from existing stats so profiles saved before achievements
            // existed still light up whatever they've already earned.
            EvaluateAchievements();
        }

        private void Save()
        {
            File.WriteAllText(SavePath, JsonUtility.ToJson(profile, true));
        }

        // ── Called by GameController on game end ──────────────────────────

        public void RecordGameResult(int difficulty, bool won, float elapsedSeconds)
        {
            profile.gamesPlayed++;
            if (won) profile.gamesWon++;

            switch (difficulty)
            {
                case 0: if (won) profile.easyWins++;   else profile.easyLosses++;   break;
                case 1: if (won) profile.mediumWins++;  else profile.mediumLosses++;  break;
                case 2: if (won) profile.hardWins++;   else profile.hardLosses++;   break;
            }

            if (won && elapsedSeconds < profile.shortestWinSeconds)
                profile.shortestWinSeconds = elapsedSeconds;

            if (won)
            {
                profile.currentWinStreak++;
                if (profile.currentWinStreak > profile.longestWinStreak)
                    profile.longestWinStreak = profile.currentWinStreak;
            }
            else
            {
                profile.currentWinStreak = 0;
            }

            EvaluateAchievements();
            Save();
        }

        // ── Achievements ──────────────────────────────────────────────────

        /// <summary>
        /// Sets any newly satisfied achievement flags. Only ever turns flags on, so an earned
        /// achievement is never revoked. Safe to call repeatedly.
        /// </summary>
        private void EvaluateAchievements()
        {
            if (profile == null) return;

            if (profile.gamesWon >= 1)
                profile.firstWinUnlocked = true;
            if (profile.longestWinStreak >= TenStreakThreshold)
                profile.tenStreakUnlocked = true;
            if (profile.hardWins >= HardMasterThreshold)
                profile.hardMasterUnlocked = true;
        }

        private bool IsUnlocked(Achievement achievement)
        {
            switch (achievement)
            {
                case Achievement.FirstWin:       return profile.firstWinUnlocked;
                case Achievement.TenGameStreak:  return profile.tenStreakUnlocked;
                case Achievement.HardModeMaster: return profile.hardMasterUnlocked;
                default:                         return false;
            }
        }

        private static string DisplayName(Achievement achievement)
        {
            switch (achievement)
            {
                case Achievement.FirstWin:       return "First Win";
                case Achievement.TenGameStreak:  return "10 Game Streak";
                case Achievement.HardModeMaster: return "Hard Mode Master";
                default:                         return achievement.ToString();
            }
        }

        private void RefreshAchievements()
        {
            if (achievementSlots == null) return;

            foreach (AchievementSlot slot in achievementSlots)
            {
                if (slot == null) continue;

                bool unlocked = IsUnlocked(slot.achievement);

                if (slot.group != null)
                    slot.group.alpha = unlocked ? 1f : lockedAlpha;
                if (slot.icon != null)
                    slot.icon.color = unlocked ? unlockedIconColor : lockedIconColor;
                if (slot.label != null)
                    slot.label.text = DisplayName(slot.achievement);
                if (slot.lockedBadge != null)
                    slot.lockedBadge.SetActive(!unlocked);
            }
        }

        // ── Called by MenuManager.ShowProfile() ───────────────────────────

        public void RefreshProfileUI()
        {
            if (profile == null) return;

            if (usernameText != null)
                usernameText.text = profile.username;

            if (gamesPlayedText != null)
                gamesPlayedText.text = $"[{profile.gamesPlayed}]";

            if (gamesWonText != null)
                gamesWonText.text = $"[{profile.gamesWon}]";

            if (winRateText != null)
            {
                float rate = profile.gamesPlayed > 0
                    ? (float)profile.gamesWon / profile.gamesPlayed * 100f
                    : 0f;
                winRateText.text = $"[{Mathf.RoundToInt(rate)}%]";
            }

            if (shortestWinText != null)
            {
                if (profile.shortestWinSeconds >= float.MaxValue)
                    shortestWinText.text = "[--:--]";
                else
                {
                    int m = (int)(profile.shortestWinSeconds / 60);
                    int s = (int)(profile.shortestWinSeconds % 60);
                    shortestWinText.text = $"[{m:00}:{s:00}]";
                }
            }

            if (easyWinsText != null)
                easyWinsText.text = $"[{profile.easyWins}]";

            if (hardWinsText != null)
                hardWinsText.text = $"[{profile.hardWins}]";

            if (breakdownText != null)
                breakdownText.text =
                    $"Easy: {profile.easyWins}W/{profile.easyLosses}L  " +
                    $"Medium: {profile.mediumWins}W/{profile.mediumLosses}L  " +
                    $"Hard: {profile.hardWins}W/{profile.hardLosses}L";

            RefreshAchievements();
        }

        // ── Called by an Edit Profile input field ─────────────────────────

        public void SetUsername(string name)
        {
            profile.username = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
            Save();
            if (usernameText != null) usernameText.text = profile.username;
        }
    }
}
