using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Presentation playback only; the coordinator owns admission and the safety deadline.</summary>
    public class GestureController : MonoBehaviour
    {
        public const string WaveLayerName = "GestureUpperBody";
        public const string WaveStateName = "GestureUpperBody.Wave";
        public const string EmptyStateName = "GestureUpperBody.Empty";
        public const float WaveStateSpeed = 1f;
        private static readonly int WaveHash = Animator.StringToHash(WaveStateName);
        private static readonly int EmptyHash = Animator.StringToHash(EmptyStateName);
        [SerializeField] private Animator animator;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private HeadGazeController headGaze;
        [SerializeField] private RuntimeAnimatorController waveController;
        [SerializeField] private AnimationClip waveClip;
        private long activeId;
        private float elapsed;
        private bool playingWave;
        private int waveLayer = -1;
        public const float NodDuration = .7f;
        public bool IsNodActive => HasMatchingLease && !playingWave;
        public bool IsWaveActive => HasMatchingLease && playingWave;
        private bool HasMatchingLease => activeId != 0 && motionChannels != null && motionChannels.ActiveGestureId == activeId;
        public bool WaveEntered { get; private set; }
        public float WaveWeight { get; private set; }
        public float NodElapsed => elapsed;
        public float NodPitch => IsNodActive && elapsed >= .15f ? 12f * Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Clamp01((elapsed - .15f) / .55f)), 2) : 0;
        public float PlaybackTimeout(string gesture) => gesture == "nod" ? NodDuration + .5f :
            gesture == "wave" && waveClip != null && animator != null && animator.speed > 0 ?
                waveClip.length / (WaveStateSpeed * animator.speed) + .75f : 0;
        public void Configure(Animator character, MotionChannelCoordinator channels, HeadGazeController gaze)
        {
            OnDisable(); animator = character; motionChannels = channels; headGaze = gaze;
            if (isActiveAndEnabled) OnEnable();
        }
        private void Awake()
        {
            if (animator == null) animator = GetComponent<Animator>();
            if (motionChannels == null) motionChannels = GetComponent<MotionChannelCoordinator>();
        }
        private void OnEnable()
        {
            if (motionChannels == null) return;
            motionChannels.GestureApproved += PlayGesture;
            motionChannels.GestureEnded += HandleEnded;
        }
        private void OnDisable()
        {
            if (motionChannels != null)
            {
                motionChannels.GestureApproved -= PlayGesture;
                motionChannels.GestureEnded -= HandleEnded;
                if (activeId != 0) motionChannels.CompleteGesture(activeId);
            }
            StopPlayback(true);
        }
        public bool CanPlay(string gesture)
        {
            if (!isActiveAndEnabled || animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) return false;
            if (gesture == "nod") return headGaze != null && headGaze.isActiveAndEnabled && headGaze.IsRigReady;
            if (gesture != "wave" || !animator.isInitialized || waveController == null ||
                animator.runtimeAnimatorController != waveController || waveClip == null || !waveClip.isHumanMotion ||
                waveClip.isLooping || waveClip.length <= 0 || animator.speed <= 0 ||
                animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman) return false;
            int layer = animator.GetLayerIndex(WaveLayerName);
            if (layer < 0 || !animator.HasState(layer, WaveHash) || !animator.HasState(layer, EmptyHash)) return false;
            foreach (var clip in waveController.animationClips) if (clip == waveClip) return true;
            return false;
        }
        public bool TryPlayApprovedGesture(string gesture, long id)
        {
            if (activeId != 0 || motionChannels == null || id == 0 || motionChannels.ActiveGestureId != id ||
                motionChannels.ActiveGesture != gesture || !CanPlay(gesture)) return false;
            ClearWaveVisual();
            activeId = id; elapsed = 0; playingWave = gesture == "wave"; WaveEntered = false;
            if (playingWave)
            {
                waveLayer = animator.GetLayerIndex(WaveLayerName);
                animator.Play(WaveHash, waveLayer, 0);
            }
            return true;
        }
        public void PlayGesture(string gesture)
        {
            if (motionChannels != null && !TryPlayApprovedGesture(gesture, motionChannels.ActiveGestureId))
                motionChannels.CancelGesture();
        }
        private bool CanWriteWaveLayer => animator != null && animator.isInitialized &&
            animator.runtimeAnimatorController == waveController && waveLayer >= 0 && waveLayer < animator.layerCount;
        private void ClearWaveVisual()
        {
            if (CanWriteWaveLayer)
            {
                animator.Play(EmptyHash, waveLayer, 0);
                animator.SetLayerWeight(waveLayer, 0);
            }
            WaveWeight = 0;
        }
        public void Tick(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime < 0) return;
            WaveWeight = Mathf.MoveTowards(WaveWeight, IsWaveActive ? 1f : 0f, deltaTime / (IsWaveActive ? .15f : .12f));
            if (CanWriteWaveLayer) animator.SetLayerWeight(waveLayer, WaveWeight);
            if (activeId == 0) return;
            if (!HasMatchingLease) { StopPlayback(); return; }
            if (!CanPlay(playingWave ? "wave" : "nod")) { motionChannels.CompleteGesture(activeId); return; }
            elapsed += deltaTime;
            if (playingWave)
            {
                var state = animator.GetCurrentAnimatorStateInfo(waveLayer);
                if (state.fullPathHash == WaveHash) WaveEntered = true;
                if (WaveEntered && state.fullPathHash == WaveHash && !animator.IsInTransition(waveLayer) && state.normalizedTime >= 1f)
                    motionChannels.CompleteGesture(activeId);
                else if ((!WaveEntered && elapsed >= .5f) || (WaveEntered && state.fullPathHash != WaveHash && !animator.IsInTransition(waveLayer)))
                    motionChannels.CompleteGesture(activeId); // Failed entry/interrupted playback, never a successful Wave.
            }
            else
            {
                elapsed = Mathf.Min(NodDuration, elapsed);
                if (elapsed >= NodDuration) motionChannels.CompleteGesture(activeId);
            }
        }
        private void Update() => Tick(Time.deltaTime);
        private void HandleEnded(long id) { if (id == activeId) StopPlayback(); }
        private void StopPlayback(bool immediate = false)
        {
            bool wasNod = activeId != 0 && !playingWave;
            if (playingWave && CanWriteWaveLayer) animator.CrossFadeInFixedTime(EmptyHash, .12f, waveLayer);
            activeId = 0; elapsed = 0; playingWave = false; WaveEntered = false;
            if (immediate) ClearWaveVisual();
            if (wasNod) headGaze?.ResetPresentation();
        }
    }
}