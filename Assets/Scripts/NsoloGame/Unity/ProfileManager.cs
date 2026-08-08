using System;
using System.Collections.Generic;
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

        // Index into ProfileManager's avatarSprites, or MonogramAvatarId for the username-initial
        // fallback. Profiles saved before avatars existed have no such key, so they load with the
        // initializer's value and fall back to the monogram.
        public int avatarId = ProfileManager.MonogramAvatarId;

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

        [Header("Username Editing")]
        [Tooltip("Optional. If assigned, the Edit button focuses this field and submitting it saves " +
                 "the name. If left empty, the device's on-screen keyboard is opened directly.")]
        [SerializeField] private TMP_InputField usernameInput;
        [SerializeField] private int maxUsernameLength = 16;

        [Header("Achievements")]
        [Tooltip("The three achievement icons shown below the edit button. Drag each icon group here and pick which achievement it represents.")]
        [SerializeField] private AchievementSlot[] achievementSlots;
        [SerializeField, Range(0f, 1f)] private float lockedAlpha = 0.35f;
        [SerializeField] private Color unlockedIconColor = new Color(1f, 0.82f, 0.35f);
        [SerializeField] private Color lockedIconColor = new Color(0.45f, 0.45f, 0.45f);

        [Header("Avatar")]
        [Tooltip("Optional: the chosen avatar's icon inside the profile circle. Hidden while the monogram is showing.")]
        [SerializeField] private Image avatarIcon;
        [Tooltip("Optional: the username's first letter, shown when no avatar has been picked.")]
        [SerializeField] private TMP_Text avatarMonogram;
        [Tooltip("Optional: the profile circle itself. Made non-interactable while there are no avatars to choose from.")]
        [SerializeField] private Button avatarButton;
        [SerializeField] private GameObject avatarPickerPanel;
        [Tooltip("Parent the picker options are instantiated under — normally a GridLayoutGroup.")]
        [SerializeField] private Transform avatarPickerGrid;
        [Tooltip("Inactive option cloned once per choice. Its target graphic is the background; any other Image on it is the icon.")]
        [SerializeField] private Button avatarOptionTemplate;
        [Tooltip("Drop avatar sprites here. Leave empty to ship monogram-only — the picker stays shut until this has entries.")]
        [SerializeField] private Sprite[] avatarSprites;
        [SerializeField, Range(0f, 1f)] private float unselectedOptionAlpha = 0.3f;

        /// <summary>avatarId value meaning "no avatar picked — draw the username's initial instead".</summary>
        public const int MonogramAvatarId = -1;

        // Unlock thresholds for the streak/hard-mode achievements.
        private const int TenStreakThreshold = 10;
        private const int HardMasterThreshold = 10;

        private PlayerProfile profile;

        // Picker options, built once on first open. Index order is fixed by OptionIdAt.
        private readonly List<Button> avatarOptionButtons = new List<Button>();

        private static string SavePath =>
            Path.Combine(Application.persistentDataPath, "profile.json");

        private static ProfileManager _instance;
        public static ProfileManager Instance => _instance;

        /// <summary>Lifetime wins, for the game-over panel's victory counter.</summary>
        public int GamesWon => profile?.gamesWon ?? 0;

        /// <summary>The saved display name, for any screen that wants to greet the player.</summary>
        public string Username => profile?.username;

        // Only used on the fallback path, when no TMP_InputField is wired up.
        private TouchScreenKeyboard nameKeyboard;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                // Same trap as AudioManager: a duplicate on the same object must only remove the
                // spare component, or the whole GameObject and its siblings go with it.
                if (_instance.gameObject == gameObject) Destroy(this);
                else Destroy(gameObject);
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

            // Reopening the page always shows the name, even if a previous edit was abandoned
            // by navigating away rather than confirming.
            if (usernameInput != null) usernameInput.gameObject.SetActive(false);
            SetUsernameLabelVisible(true);
            CloseAvatarPicker();

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
                    $" Easy: {profile.easyWins}W/{profile.easyLosses}L"+
                    $"          Medium: {profile.mediumWins}W/{profile.mediumLosses}L"+
                    $"       Hard: {profile.hardWins}W/{profile.hardLosses}L";

            RefreshAchievements();
            ApplyAvatar();
        }

        // ── Avatar ────────────────────────────────────────────────────────

        /// <summary>
        /// Maps a picker slot to the avatarId it selects. Slot 0 is always the monogram, so the
        /// sprite indices follow it. Both the build and refresh passes go through here so the
        /// ordering is defined in exactly one place.
        /// </summary>
        private static int OptionIdAt(int index) => index - 1;

        private int AvatarCount => avatarSprites != null ? avatarSprites.Length : 0;

        /// <summary>The username's first character, uppercased — the no-avatar fallback.</summary>
        private string Monogram()
        {
            string name = profile?.username;
            if (string.IsNullOrWhiteSpace(name)) return "?";
            return char.ToUpperInvariant(name.Trim()[0]).ToString();
        }

        /// <summary>Draws whichever of the icon/monogram the current avatarId calls for.</summary>
        private void ApplyAvatar()
        {
            if (profile == null) return;

            Sprite chosen = null;
            if (profile.avatarId >= 0 && profile.avatarId < AvatarCount)
                chosen = avatarSprites[profile.avatarId];

            // Only the sprite and visibility are code's business — the gold tint is authored on
            // the Image in the scene, so it stays editable without touching this file.
            if (avatarIcon != null)
            {
                avatarIcon.enabled = chosen != null;
                if (chosen != null) avatarIcon.sprite = chosen;
            }

            if (avatarMonogram != null)
            {
                avatarMonogram.enabled = chosen == null;
                avatarMonogram.text = Monogram();
            }

            // With no sprites wired up there is nothing to pick, so the circle stays inert rather
            // than opening an empty picker.
            if (avatarButton != null)
                avatarButton.interactable = AvatarCount > 0;
        }

        /// <summary>Hook this to the profile circle's button.</summary>
        public void OpenAvatarPicker()
        {
            if (AvatarCount == 0) return;

            BuildAvatarPicker();
            RefreshAvatarOptions();
            if (avatarPickerPanel != null) avatarPickerPanel.SetActive(true);
        }

        public void CloseAvatarPicker()
        {
            if (avatarPickerPanel != null) avatarPickerPanel.SetActive(false);
        }

        public void SetAvatar(int id)
        {
            if (profile == null) return;

            profile.avatarId = (id >= 0 && id < AvatarCount) ? id : MonogramAvatarId;
            Save();
            ApplyAvatar();
            RefreshAvatarOptions();
            CloseAvatarPicker();
        }

        private void BuildAvatarPicker()
        {
            if (avatarOptionButtons.Count > 0) return;
            if (avatarOptionTemplate == null || avatarPickerGrid == null) return;

            // The template is a scene object rather than a prefab, so it has to be kept hidden
            // itself — only its clones are ever shown.
            avatarOptionTemplate.gameObject.SetActive(false);

            int slots = AvatarCount + 1;   // +1 for the monogram
            for (int i = 0; i < slots; i++)
                CreateAvatarOption(OptionIdAt(i));
        }

        private void CreateAvatarOption(int id)
        {
            Button option = Instantiate(avatarOptionTemplate, avatarPickerGrid);
            option.gameObject.SetActive(true);
            option.name = id == MonogramAvatarId ? "Option_Monogram" : $"Option_{id}";

            FindOptionParts(option, out Image icon, out TMP_Text letter);

            bool isMonogram = id == MonogramAvatarId;
            if (icon != null)
            {
                icon.enabled = !isMonogram;
                if (!isMonogram) icon.sprite = avatarSprites[id];
            }
            if (letter != null)
                letter.enabled = isMonogram;

            int selected = id;   // captured per-iteration, so every option keeps its own id
            option.onClick.AddListener(() => SetAvatar(selected));
            avatarOptionButtons.Add(option);
        }

        /// <summary>
        /// Picks the icon and letter out of an option clone. The background is whatever the button
        /// already targets, so the icon is identified as the other Image rather than by name.
        /// </summary>
        private static void FindOptionParts(Button option, out Image icon, out TMP_Text letter)
        {
            icon = null;
            foreach (Image image in option.GetComponentsInChildren<Image>(true))
            {
                if (image != option.targetGraphic)
                {
                    icon = image;
                    break;
                }
            }
            letter = option.GetComponentInChildren<TMP_Text>(true);
        }

        private void RefreshAvatarOptions()
        {
            if (profile == null) return;

            for (int i = 0; i < avatarOptionButtons.Count; i++)
            {
                Button option = avatarOptionButtons[i];
                if (option == null) continue;

                int id = OptionIdAt(i);

                if (option.targetGraphic is Image background)
                {
                    // Alpha only, so the template's authored gold carries through and the
                    // unselected options just recede.
                    Color tint = background.color;
                    tint.a = id == profile.avatarId ? 1f : unselectedOptionAlpha;
                    background.color = tint;
                }

                // The monogram option follows the username, which can change between opens.
                if (id == MonogramAvatarId)
                {
                    FindOptionParts(option, out _, out TMP_Text letter);
                    if (letter != null) letter.text = Monogram();
                }
            }
        }

        // ── Username editing ──────────────────────────────────────────────

        /// <summary>
        /// Hook this to the profile page's Edit (pen) button. Focuses the username field and raises
        /// the on-screen keyboard; the name is saved when the player confirms with enter/go.
        /// </summary>
        public void BeginEditUsername()
        {
            if (profile == null) return;

            // The saved name sits behind/near the input field, so leaving it up while the player
            // types shows two competing names. Hide it and restore it once the edit resolves.
            SetUsernameLabelVisible(false);

            if (usernameInput != null)
            {
                usernameInput.gameObject.SetActive(true);
                usernameInput.characterLimit = maxUsernameLength;
                usernameInput.text = profile.username;

                // Re-registering every time would stack duplicate listeners across edits.
                usernameInput.onEndEdit.RemoveListener(CommitUsernameEdit);
                usernameInput.onEndEdit.AddListener(CommitUsernameEdit);

                usernameInput.Select();
                usernameInput.ActivateInputField();
                return;
            }

            if (TouchScreenKeyboard.isSupported)
            {
                nameKeyboard = TouchScreenKeyboard.Open(
                    profile.username, TouchScreenKeyboardType.Default,
                    autocorrection: false, multiline: false, secure: false,
                    alert: false, textPlaceholder: "Username", characterLimit: maxUsernameLength);
            }
            else
            {
                Debug.LogWarning("ProfileManager: no usernameInput assigned and no on-screen " +
                                 "keyboard available, so the name cannot be edited here.");
                SetUsernameLabelVisible(true);
            }
        }

        private void Update()
        {
            if (nameKeyboard == null) return;

            if (nameKeyboard.status == TouchScreenKeyboard.Status.Done)
                SetUsername(nameKeyboard.text);

            // Done, cancelled or lost — either way we're finished with this keyboard, and the
            // label has to come back even when the player cancelled rather than confirmed.
            if (nameKeyboard.status != TouchScreenKeyboard.Status.Visible)
            {
                nameKeyboard = null;
                SetUsernameLabelVisible(true);
            }
        }

        // onEndEdit also fires when the field merely loses focus, so this covers cancelling as
        // well as confirming — the label is restored either way.
        private void CommitUsernameEdit(string value)
        {
            SetUsername(value);
            if (usernameInput != null)
            {
                usernameInput.DeactivateInputField();
                usernameInput.gameObject.SetActive(false);
            }
            SetUsernameLabelVisible(true);
        }

        private void SetUsernameLabelVisible(bool visible)
        {
            if (usernameText != null) usernameText.enabled = visible;
        }

        public void SetUsername(string name)
        {
            string trimmed = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
            if (trimmed.Length > maxUsernameLength)
                trimmed = trimmed.Substring(0, maxUsernameLength);

            profile.username = trimmed;
            Save();
            if (usernameText != null) usernameText.text = profile.username;

            // The monogram is derived from the name, so it has to follow a rename.
            ApplyAvatar();
        }
    }
}
