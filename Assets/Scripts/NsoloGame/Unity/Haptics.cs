using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>How firm a pulse to ask for. Maps to a duration/amplitude pair below.</summary>
    public enum HapticStrength
    {
        Light,
        Medium,
        Heavy,
    }

    /// <summary>
    /// Short vibration pulses that honour the pause-menu vibration setting.
    ///
    /// Unity's own <see cref="Handheld.Vibrate"/> is the only built-in option and it fires a fixed
    /// ~500ms buzz, which is far too long for UI feedback, so the Android vibrator service is
    /// called directly with a real duration instead. Handheld.Vibrate is still referenced as a
    /// last-resort fallback — and that reference matters for a second reason: Unity's Android
    /// build only adds android.permission.VIBRATE to the manifest when it sees a call to it.
    /// Deleting the fallback would silently drop the permission and kill vibration entirely.
    /// </summary>
    public static class Haptics
    {
        /// <summary>PlayerPrefs key shared with MenuManager's vibration toggle.</summary>
        public const string PrefKey = "Vibration";

        // Tuned to read as UI ticks rather than alerts. Under ~10ms is imperceptible on most
        // phones; over ~60ms starts to feel like an error buzz.
        private const int LightMs = 12;
        private const int MediumMs = 25;
        private const int HeavyMs = 45;

        // Out of 255. Only used on Android 8+ (API 26), which is where amplitude control landed.
        private const int LightAmplitude = 70;
        private const int MediumAmplitude = 140;
        private const int HeavyAmplitude = 255;

        // Below this the fallback's half-second buzz is worse than no vibration at all, so short
        // pulses simply do nothing on devices where the native path is unavailable.
        private const int FallbackMinimumMs = 20;

        private static bool enabledCache = true;
        private static bool enabledLoaded;

        /// <summary>
        /// Whether vibration is switched on. Read from PlayerPrefs the first time it is needed and
        /// cached from then on, so the per-pulse cost is a bool read rather than a prefs lookup.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                if (!enabledLoaded)
                {
                    enabledCache = PlayerPrefs.GetInt(PrefKey, 1) == 1;
                    enabledLoaded = true;
                }
                return enabledCache;
            }
        }

        /// <summary>
        /// Updates the setting and persists it. Call this from the vibration toggle rather than
        /// writing the pref directly, so the cache above cannot go stale.
        /// </summary>
        public static void SetEnabled(bool value)
        {
            enabledCache = value;
            enabledLoaded = true;
            PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }

        public static void Light() => Pulse(HapticStrength.Light);
        public static void Medium() => Pulse(HapticStrength.Medium);
        public static void Heavy() => Pulse(HapticStrength.Heavy);

        public static void Pulse(HapticStrength strength)
        {
            if (!Enabled) return;

            switch (strength)
            {
                case HapticStrength.Light:
                    Vibrate(LightMs, LightAmplitude);
                    break;
                case HapticStrength.Medium:
                    Vibrate(MediumMs, MediumAmplitude);
                    break;
                default:
                    Vibrate(HeavyMs, HeavyAmplitude);
                    break;
            }
        }

        /// <summary>
        /// Fires a single pulse, bypassing the strength presets. Still respects <see cref="Enabled"/>.
        /// </summary>
        public static void Vibrate(int milliseconds, int amplitude = -1)
        {
            if (!Enabled || milliseconds <= 0) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (TryNativeVibrate(milliseconds, amplitude)) return;

            // Native path unavailable on this device — only fall back when the pulse is long
            // enough that a half-second buzz is not wildly out of proportion.
            if (milliseconds >= FallbackMinimumMs)
                Handheld.Vibrate();
#else
            // Editor and desktop have no vibration motor. Kept as a no-op rather than a log so
            // that tapping through menus in play mode does not flood the Console.
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject vibrator;
        private static AndroidJavaClass vibrationEffectClass;
        private static int sdkInt;
        private static bool nativeChecked;
        private static bool nativeAvailable;

        private static bool TryNativeVibrate(int milliseconds, int amplitude)
        {
            if (!EnsureNativeVibrator()) return false;

            try
            {
                if (sdkInt >= 26 && vibrationEffectClass != null)
                {
                    // createOneShot takes a long duration and an amplitude, where -1 means
                    // VibrationEffect.DEFAULT_AMPLITUDE.
                    int clamped = amplitude < 0 ? -1 : Mathf.Clamp(amplitude, 1, 255);
                    using (AndroidJavaObject effect = vibrationEffectClass.CallStatic<AndroidJavaObject>(
                               "createOneShot", (long)milliseconds, clamped))
                    {
                        vibrator.Call("vibrate", effect);
                    }
                }
                else
                {
                    vibrator.Call("vibrate", (long)milliseconds);
                }

                return true;
            }
            catch (System.Exception e)
            {
                // A device that throws once will throw every time; stop trying so a broken
                // vibrator service cannot cost an exception per tap.
                Debug.LogWarning($"Haptics: native vibrate failed, falling back. {e.Message}");
                nativeAvailable = false;
                return false;
            }
        }

        private static bool EnsureNativeVibrator()
        {
            if (nativeChecked) return nativeAvailable;
            nativeChecked = true;

            try
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    sdkInt = version.GetStatic<int>("SDK_INT");

                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }

                if (vibrator == null || !vibrator.Call<bool>("hasVibrator"))
                {
                    nativeAvailable = false;
                    return false;
                }

                if (sdkInt >= 26)
                    vibrationEffectClass = new AndroidJavaClass("android.os.VibrationEffect");

                nativeAvailable = true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Haptics: could not reach the Android vibrator service. {e.Message}");
                nativeAvailable = false;
            }

            return nativeAvailable;
        }
#endif
    }
}
