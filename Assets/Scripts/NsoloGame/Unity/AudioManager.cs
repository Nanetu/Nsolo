using System.Collections;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Owns every sound in the game: one looping music source and one source for fire-and-forget
    /// effects, each on its own volume.
    ///
    /// This replaces the old approach of driving <c>AudioListener.volume</c> from the music slider,
    /// which was really a master volume — it dragged the stone-hit effects down with the music, and
    /// left the SFX slider controlling nothing at all. AudioListener.volume is now left at 1 and the
    /// two sliders drive their own sources.
    ///
    /// Every clip reference is optional. With no clips assigned the game plays exactly as it does
    /// today, silently, so this can be dropped in before any audio files exist.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        [Header("Sources — leave empty to have them created automatically")]
        [SerializeField] private AudioSource musicSource;
        [SerializeField] private AudioSource sfxSource;

        [Header("Music")]
        [Tooltip("Loops on the welcome/menu screens.")]
        [SerializeField] private AudioClip menuMusic;
        [Tooltip("Loops during play. Starts when the player presses START to commit their formation.")]
        [SerializeField] private AudioClip gameMusic;
        [SerializeField] private float musicCrossfadeSeconds = 0.6f;

        [Header("Effects")]
        [SerializeField] private AudioClip uiClick;
        [SerializeField] private AudioClip pitPickup;
        [SerializeField] private AudioClip stoneDrop;
        [SerializeField] private AudioClip capture;
        [SerializeField] private AudioClip illegalMove;
        [SerializeField] private AudioClip popup;
        [SerializeField] private AudioClip victory;
        [SerializeField] private AudioClip defeat;

        public const string MusicVolumeKey = "MusicVolume";
        public const string SfxVolumeKey = "SFXVolume";

        private static AudioManager instance;
        public static AudioManager Instance => instance;

        private AudioClip currentMusic;
        private Coroutine fade;
        private float musicVolume = 0.7f;
        private float sfxVolume = 0.85f;

        public float SfxVolume => sfxVolume;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                // A duplicate sitting on the *same* object must only take out the spare component.
                // Destroying the GameObject here removes GameSystems entirely — and TutorialCoach
                // along with it — which silently disables tips and audio at once.
                if (instance.gameObject == gameObject) Destroy(this);
                else Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            EnsureSources();

            musicVolume = PlayerPrefs.GetFloat(MusicVolumeKey, 0.7f);
            sfxVolume = PlayerPrefs.GetFloat(SfxVolumeKey, 0.85f);

            // The old build left this wherever the music slider had last put it, which quietly
            // capped every other sound in the game.
            AudioListener.volume = 1f;

            ApplyVolumes();
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void EnsureSources()
        {
            if (musicSource == null)
            {
                musicSource = gameObject.AddComponent<AudioSource>();
                musicSource.playOnAwake = false;
                musicSource.loop = true;
            }

            if (sfxSource == null)
            {
                sfxSource = gameObject.AddComponent<AudioSource>();
                sfxSource.playOnAwake = false;
                sfxSource.loop = false;
            }
        }

        // ── Volume ────────────────────────────────────────────────────────

        public void SetMusicVolume(float value)
        {
            musicVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(MusicVolumeKey, musicVolume);
            PlayerPrefs.Save();

            // Skipped mid-crossfade so the fade coroutine stays in charge of the music source.
            if (fade == null && musicSource != null) musicSource.volume = musicVolume;
        }

        public void SetSfxVolume(float value)
        {
            sfxVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(SfxVolumeKey, sfxVolume);
            PlayerPrefs.Save();
            if (sfxSource != null) sfxSource.volume = sfxVolume;
        }

        private void ApplyVolumes()
        {
            if (musicSource != null) musicSource.volume = musicVolume;
            if (sfxSource != null) sfxSource.volume = sfxVolume;
        }

        // ── Music ─────────────────────────────────────────────────────────

        /// <summary>Menu/welcome loop. Safe to call repeatedly — re-requesting the running track is a no-op.</summary>
        public void PlayMenuMusic() => PlayMusic(menuMusic);

        /// <summary>
        /// The in-game loop. Called from GameController the moment the player commits their opening
        /// formation, which is deliberately later than difficulty selection — arranging stones
        /// happens in silence.
        /// </summary>
        public void PlayGameMusic()
        {
            WarnIfInaudible(gameMusic, "Game Music");
            PlayMusic(gameMusic);
        }

        /// <summary>
        /// Music has two ways of failing that look identical from the player's seat — no clip in
        /// the slot, or a saved volume of zero — and neither produces any error on its own. Both
        /// become a Console line here rather than a silent afternoon.
        /// </summary>
        private void WarnIfInaudible(AudioClip clip, string slotName)
        {
            if (clip == null)
            {
                Debug.LogWarning($"AudioManager: the {slotName} slot on GameSystems is empty, so " +
                                 "nothing will play. Drag an AudioClip into it.");
            }
            else if (musicVolume <= 0.001f)
            {
                Debug.LogWarning($"AudioManager: '{clip.name}' is assigned but the saved music " +
                                 $"volume is {musicVolume}, so it will play silently. Raise the " +
                                 "music slider in the pause menu.");
            }
        }

        public void StopMusic() => PlayMusic(null);

        private void PlayMusic(AudioClip clip)
        {
            if (musicSource == null) return;
            if (clip == currentMusic && (clip == null || musicSource.isPlaying)) return;

            currentMusic = clip;

            if (fade != null) StopCoroutine(fade);
            fade = StartCoroutine(CrossfadeTo(clip));
        }

        private IEnumerator CrossfadeTo(AudioClip clip)
        {
            float half = Mathf.Max(0.01f, musicCrossfadeSeconds * 0.5f);

            if (musicSource.isPlaying)
            {
                float start = musicSource.volume;
                for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / half)
                {
                    musicSource.volume = Mathf.Lerp(start, 0f, t);
                    yield return null;
                }
                musicSource.Stop();
            }

            if (clip == null)
            {
                musicSource.clip = null;
                musicSource.volume = musicVolume;
                fade = null;
                yield break;
            }

            musicSource.clip = clip;
            musicSource.loop = true;
            musicSource.volume = 0f;
            musicSource.Play();

            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / half)
            {
                musicSource.volume = Mathf.Lerp(0f, musicVolume, t);
                yield return null;
            }

            musicSource.volume = musicVolume;
            fade = null;
        }

        // ── Effects ───────────────────────────────────────────────────────

        /// <summary>Plays a one-shot at the current SFX volume. Null clips are ignored.</summary>
        public void PlaySfx(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null || sfxSource == null) return;
            sfxSource.PlayOneShot(clip, Mathf.Clamp01(sfxVolume * volumeScale));
        }

        public void PlayUiClick() => PlaySfx(uiClick);
        public void PlayPitPickup() => PlaySfx(pitPickup);
        public void PlayStoneDrop() => PlaySfx(stoneDrop);
        public void PlayCapture() => PlaySfx(capture);
        public void PlayIllegalMove() => PlaySfx(illegalMove);
        public void PlayPopup() => PlaySfx(popup);

        public void PlayGameOver(bool playerWon) => PlaySfx(playerWon ? victory : defeat);

        // ── Static conveniences ───────────────────────────────────────────
        // Every trigger site is "play this if there is an AudioManager and a clip for it", so the
        // null-checking lives here rather than being repeated at each call.

        private static bool warnedNoInstance;

        /// <summary>
        /// All the static helpers resolve through here so that a missing manager announces itself
        /// once instead of quietly swallowing every call. Without this, an AudioManager that never
        /// registered is indistinguishable from an unassigned clip or a muted slider — which is
        /// precisely how a duplicate component deleting GameSystems passed for "audio is broken".
        /// </summary>
        private static AudioManager Active()
        {
            if (instance == null && !warnedNoInstance)
            {
                warnedNoInstance = true;
                Debug.LogWarning("AudioManager: no active instance, so every audio call is being " +
                                 "ignored. Check that GameSystems is in the scene and carries " +
                                 "exactly one AudioManager component.");
            }
            return instance;
        }

        public static void Click() => Active()?.PlayUiClick();
        public static void Popup() => Active()?.PlayPopup();
        public static void Capture() => Active()?.PlayCapture();
        public static void Illegal() => Active()?.PlayIllegalMove();
        public static void GameOver(bool playerWon) => Active()?.PlayGameOver(playerWon);
        public static void StartGameMusic() => Active()?.PlayGameMusic();
        public static void StartMenuMusic() => Active()?.PlayMenuMusic();
        public static void Silence() => Active()?.StopMusic();
    }
}
