using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Turns the board around between turns in hot-seat play, so the player picking the device up
    /// is looking at their own two rows from the near side.
    ///
    /// It is the <b>camera</b> that orbits, not the board. Stones are not children of the pits —
    /// PitStoneVisualizer instantiates them under its own root at absolute world positions — so
    /// rotating the board would swing the pits around and leave every stone behind. Orbiting the
    /// camera moves nothing in the scene, which also means the pit colliders never move: input is
    /// a physics raycast against those colliders, so taps keep resolving to the correct cell in
    /// both orientations without any coordinate remapping.
    ///
    /// Every pose is derived from the camera's original transform plus an angle, rather than by
    /// repeatedly rotating the live transform, so a hundred flips cannot accumulate drift.
    /// </summary>
    public class BoardFlipper : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("Camera to orbit. Falls back to Camera.main.")]
        [SerializeField] private Camera boardCamera;
        [Tooltip("Optional. Point to orbit around. Left empty, the centre of the 32 pits is used, " +
                 "which is what you want unless the board art moves independently of them.")]
        [SerializeField] private Transform pivotOverride;

        [Tooltip("Transforms that turn with the camera. Left empty, every Light in the scene is " +
                 "used — which is what keeps the two seats lit identically. Without this the key " +
                 "light stays put while the camera moves, so player 2 views the stones from their " +
                 "shadowed side and the board looks black.")]
        [SerializeField] private Transform[] orbitWithCamera;

        [Header("Motion")]
        [Tooltip("Seconds for a half turn. Long enough to read as a deliberate handover, short " +
                 "enough not to be a wait.")]
        [SerializeField, Range(0.3f, 1.5f)] private float flipSeconds = 0.7f;

        [Header("Debug")]
        [SerializeField] private bool enableBreadcrumbLogs = false;

        // The camera and everything that turns with it, plus each one's authored pose. Poses are
        // recorded once and every frame of the turn is computed from them, so repeated flips
        // cannot accumulate drift.
        private Transform[] rig;
        private Vector3[] rigBasePosition;
        private Quaternion[] rigBaseRotation;

        private Vector3 pivot;
        private bool poseCaptured;

        /// <summary>Which side the camera is currently parked on. False is player 1's view.</summary>
        public bool IsFlipped { get; private set; }

        /// <summary>
        /// True while a half turn is playing. GameController gates board taps on this so a stray
        /// tap mid-rotation cannot select a pit the player cannot properly see yet.
        /// </summary>
        public bool IsFlipping { get; private set; }

        private void Awake()
        {
            CapturePose();
        }

        /// <summary>
        /// Records the camera's authored transform and works out the board centre. Deferred rather
        /// than done in the field initialisers because Camera.main and the pits both need the scene
        /// to be loaded.
        /// </summary>
        private void CapturePose()
        {
            if (poseCaptured) return;

            if (boardCamera == null) boardCamera = Camera.main;
            if (boardCamera == null)
            {
                Debug.LogError("BoardFlipper: no camera assigned and no Camera.main in the scene.");
                return;
            }

            rig = BuildRig();
            rigBasePosition = new Vector3[rig.Length];
            rigBaseRotation = new Quaternion[rig.Length];
            for (int i = 0; i < rig.Length; i++)
            {
                rigBasePosition[i] = rig[i].position;
                rigBaseRotation[i] = rig[i].rotation;
            }

            pivot = ResolvePivot();
            poseCaptured = true;

            Log($"Captured pose for {rig.Length} rig transforms, pivot {pivot}.");
        }

        /// <summary>
        /// The camera first, then everything that turns with it.
        ///
        /// The lights belong in here. A directional light's direction is fixed in world space, so
        /// moving the camera to the far side of a stationary key light means the second player is
        /// looking at the shadowed side of every stone. Turning the lights by the same angle keeps
        /// each seat's view of the board identical — which is also the fair thing to do, since
        /// neither player should be reading a darker board than the other.
        /// </summary>
        private Transform[] BuildRig()
        {
            var transforms = new List<Transform> { boardCamera.transform };

            if (orbitWithCamera != null && orbitWithCamera.Length > 0)
            {
                foreach (Transform t in orbitWithCamera)
                    if (t != null && t != boardCamera.transform) transforms.Add(t);
            }
            else
            {
                foreach (Light light in FindObjectsOfType<Light>())
                    if (light.transform != boardCamera.transform) transforms.Add(light.transform);
            }

            return transforms.ToArray();
        }

        /// <summary>
        /// Centre of the 32 pits in world space. Averaging the actual pit transforms means the
        /// board can be moved or re-laid out in the scene without this needing to know.
        /// </summary>
        private Vector3 ResolvePivot()
        {
            if (pivotOverride != null) return pivotOverride.position;

            Vector3 sum = Vector3.zero;
            int found = 0;

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    GameObject hole = GameObject.Find($"Hole_{r}_{c}");
                    if (hole == null) continue;
                    sum += hole.transform.position;
                    found++;
                }
            }

            if (found == 0)
            {
                Debug.LogWarning("BoardFlipper: found no Hole_ objects, orbiting the world origin. " +
                                 "Assign Pivot Override if the board lives somewhere else.");
                return Vector3.zero;
            }

            return sum / found;
        }

        /// <summary>
        /// Plays a half turn. Yield on it from a coroutine to continue once the board has settled.
        /// Calling it while one is already running is ignored rather than queued — two overlapping
        /// half turns would land the camera somewhere between the two seats.
        /// </summary>
        public IEnumerator Flip()
        {
            CapturePose();
            if (!poseCaptured || IsFlipping) yield break;

            IsFlipping = true;

            float fromAngle = IsFlipped ? 180f : 0f;
            float toAngle = fromAngle + 180f;
            float t = 0f;

            while (t < 1f)
            {
                // Unscaled: the handover modal that precedes this freezes the game clock, and the
                // rotation should play at the same speed regardless of what timeScale is left at.
                t += flipSeconds > 0f ? Time.unscaledDeltaTime / flipSeconds : 1f;

                // SmoothStep rather than a straight lerp — the board eases away and settles rather
                // than starting and stopping dead, which is what makes it read as the board being
                // turned round instead of the picture being swapped.
                float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                ApplyAngle(Mathf.LerpUnclamped(fromAngle, toAngle, eased));

                yield return null;
            }

            IsFlipped = !IsFlipped;

            // Snap to the canonical pose for this seat. Landing on exactly 0 or 180 keeps the two
            // orientations pixel-identical every time rather than drifting by a fraction of a
            // degree per flip.
            ApplyAngle(IsFlipped ? 180f : 0f);

            IsFlipping = false;
            Log($"Flip complete. IsFlipped={IsFlipped}.");
        }

        /// <summary>
        /// Puts the camera back on player 1's side with no animation. Called when a game starts or
        /// restarts, so a new game never inherits the previous one's orientation.
        /// </summary>
        public void ResetToBase()
        {
            CapturePose();
            if (!poseCaptured) return;

            StopAllCoroutines();
            IsFlipping = false;
            IsFlipped = false;
            ApplyAngle(0f);
            Log("Reset to base orientation.");
        }

        private void ApplyAngle(float degrees)
        {
            Quaternion spin = Quaternion.AngleAxis(degrees, Vector3.up);

            for (int i = 0; i < rig.Length; i++)
            {
                if (rig[i] == null) continue;
                rig[i].SetPositionAndRotation(
                    pivot + spin * (rigBasePosition[i] - pivot),
                    spin * rigBaseRotation[i]);
            }
        }

        private void Log(string msg)
        {
            if (enableBreadcrumbLogs) Debug.Log($"[BoardFlipper] {msg}", this);
        }
    }
}
