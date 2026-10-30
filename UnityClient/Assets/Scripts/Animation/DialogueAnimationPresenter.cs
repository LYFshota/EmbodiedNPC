using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Low-priority, arms-only speech accents with deliberate listening pauses.</summary>
    [DisallowMultipleComponent]
    public sealed class DialogueAnimationPresenter : MonoBehaviour
    {
        [SerializeField] private ExpressiveController expressive;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private Animator animator;
        [SerializeField, Min(.01f)] private float blendIn = .25f;
        [SerializeField, Min(.01f)] private float blendOut = .2f;
        [SerializeField, Range(0, .5f)] private float gestureStrength = .36f;
        [SerializeField, Min(.8f)] private float phraseSeconds = 1.8f;
        [SerializeField, Min(.6f)] private float restSeconds = 2.2f;
        [SerializeField, Range(0, 3)] private float headAccentDegrees = 1.6f;
        private int layer = -1;
        private long speechId;
        private float elapsed;
        public float CurrentWeight { get; private set; }
        // Eligibility envelope is separate from the intentionally intermittent visible motion.
        public float ActivationWeight { get; private set; }
        public float HeadPitchAccent { get; private set; }
        public float HeadTiltAccent { get; private set; }
        public bool IsReady => animator != null && animator.isActiveAndEnabled && animator.isInitialized &&
            animator.runtimeAnimatorController != null && layer >= 0 && layer < animator.layerCount;

        public void RebindAnimator(Animator character) => Configure(expressive, motionChannels, character);
        public void Configure(ExpressiveController expression, MotionChannelCoordinator channels, Animator character)
        {
            ResetPresentation(); expressive = expression; motionChannels = channels; animator = character;
            layer = animator != null && animator.runtimeAnimatorController != null ? animator.GetLayerIndex("Dialogue") : -1;
        }
        private void Awake() => Configure(expressive, motionChannels, animator);
        public void Tick(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime < 0) return;
            if (!isActiveAndEnabled || !IsReady) { ResetPresentation(); return; }
            bool speaking = expressive != null && expressive.isActiveAndEnabled &&
                expressive.State == DialoguePresentationState.Talking;
            long id = speaking && expressive.Speech != null ? expressive.Speech.Current.Id : 0;
            if (id != speechId) { speechId = id; elapsed = 0; }
            if (speaking) elapsed += deltaTime;
            bool allowed = speaking && motionChannels != null && motionChannels.isActiveAndEnabled &&
                !motionChannels.IsTaskMotionActive && !motionChannels.IsEmergencyMotionActive &&
                !motionChannels.IsCarryPresentationActive && motionChannels.ActiveGesture != "wave";
            ActivationWeight = Mathf.MoveTowards(ActivationWeight, allowed ? 1 : 0,
                deltaTime / Mathf.Max(.01f, allowed ? blendIn : blendOut));
            string tone = expressive == null ? "neutral" : expressive.ExpressionHint;
            float energy = tone == "happy" || tone == "surprised" ? 1.2f :
                tone == "thoughtful" || tone == "concerned" || tone == "fear" ? .55f : .8f;
            float active = Mathf.Max(.8f, phraseSeconds);
            float phase = elapsed % (active + Mathf.Max(.6f, restSeconds));
            // A full phrase has a soft entrance/exit, followed by a genuine rest. No random gesture spam.
            float beat = phase < active ? Mathf.Sin(Mathf.PI * phase / active) : 0;
            float target = allowed ? Mathf.Clamp(gestureStrength * energy * beat, 0, .5f) : 0;
            CurrentWeight = Mathf.MoveTowards(CurrentWeight, target, deltaTime / Mathf.Max(.01f, allowed ? blendIn : blendOut));
            animator.SetLayerWeight(layer, CurrentWeight);
            float accent = allowed ? headAccentDegrees * ActivationWeight : 0;
            float pitch = accent * (tone == "thoughtful" || tone == "concerned" ? -.6f : .35f) * beat;
            float tilt = accent * (tone == "curious" ? 1 : tone == "concerned" ? -.6f : .2f) * beat;
            HeadPitchAccent = Mathf.MoveTowards(HeadPitchAccent, pitch, deltaTime * 6);
            HeadTiltAccent = Mathf.MoveTowards(HeadTiltAccent, tilt, deltaTime * 6);
        }
        public void ResetPresentation()
        {
            if (animator != null && animator.isInitialized && animator.runtimeAnimatorController != null && layer >= 0 && layer < animator.layerCount)
                animator.SetLayerWeight(layer, 0);
            CurrentWeight = ActivationWeight = HeadPitchAccent = HeadTiltAccent = elapsed = 0;
            speechId = 0;
        }
        private void Update() => Tick(Time.deltaTime);
        private void OnDisable() => ResetPresentation();
    }
}
