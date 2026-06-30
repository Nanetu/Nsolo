using System;
using System.IO;
using UnityEngine;
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

            Save();
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
