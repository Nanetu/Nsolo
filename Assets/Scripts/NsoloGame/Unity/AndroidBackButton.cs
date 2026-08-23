using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Gives the Android hardware back button a defined meaning, screen by screen.
    ///
    /// Nothing in the project handled it before, so it fell through to Unity's default — which on
    /// Android quits the app. That is a poor thing to have one reflexive press away at any moment,
    /// and a disastrous one during a match.
    ///
    /// The rule that shapes the rest: <b>back never destroys a room.</b> In the lobby it sends the
    /// app to the background instead, so the player can go to WhatsApp, paste the code and come
    /// back to the room still open (see <c>BackgroundKeepAliveSeconds</c> in
    /// <see cref="Net.PhotonMatchTransport"/>). Leaving a room is a deliberate act and stays with
    /// the on-screen BACK, which asks first.
    /// </summary>
    public sealed class AndroidBackButton : MonoBehaviour
    {
        [Tooltip("Auto-found if left empty.")]
        [SerializeField] private MenuManager menuManager;

        [Tooltip("Ignores repeat presses closer together than this, in seconds. Hardware back " +
                 "repeats easily and the actions behind it are not all reversible.")]
        [SerializeField] private float repeatGuardSeconds = 0.4f;

        private float lastPress;

        private void Awake()
        {
            if (menuManager == null) menuManager = FindObjectOfType<MenuManager>();
        }

        private void Update()
        {
            // Android maps the hardware back button to Escape, which also makes this testable in
            // the editor without a device.
            if (!Input.GetKeyDown(KeyCode.Escape)) return;

            if (Time.unscaledTime - lastPress < repeatGuardSeconds) return;
            lastPress = Time.unscaledTime;

            if (menuManager != null) menuManager.HandleBackPressed();
            else SendAppToBackground();
        }

        /// <summary>
        /// Minimises the app without ending it — the Android "temporarily leave" that Home performs,
        /// reached from the back button.
        ///
        /// Deliberately not <c>Application.Quit</c>: quitting drops the Photon connection, and the
        /// whole point of back in the lobby is that the room outlives the trip to another app. In
        /// the editor there is no task to move, so it logs instead of pretending.
        /// </summary>
        public static void SendAppToBackground()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    // true: keep the task in the back stack rather than finishing it, so returning
                    // resumes this activity instead of starting a new one.
                    activity.Call<bool>("moveTaskToBack", true);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"AndroidBackButton: could not background the app — {e.Message}");
            }
#else
            Debug.Log("AndroidBackButton: would send the app to the background on a device.");
#endif
        }
    }
}
