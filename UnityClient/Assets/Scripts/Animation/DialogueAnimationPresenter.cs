using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Projects the existing speech state onto a masked visual layer.</summary>
    [DisallowMultipleComponent]
    public sealed class DialogueAnimationPresenter : MonoBehaviour
    {
        [SerializeField] private ExpressiveController expressive;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private Animator animator;
        [SerializeField, Min(.01f)] private float blendIn = .25f;
        [SerializeField, Min(.01f)] private float blendOut = .2f;
        private int layer = -1;
        public float CurrentWeight { get; private set; }
        public bool IsReady => animator != null && animator.isActiveAndEnabled && animator.isInitialized &&
            animator.runtimeAnimatorController != null && layer >= 0 && layer < animator.layerCount;

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
            bool talking = expressive != null && expressive.isActiveAndEnabled &&
                expressive.State == DialoguePresentationState.Talking && motionChannels != null &&
                motionChannels.isActiveAndEnabled && !motionChannels.IsTaskMotionActive && !motionChannels.IsEmergencyMotionActive &&
                !motionChannels.IsCarryPresentationActive && motionChannels.ActiveGesture != "wave";
            CurrentWeight = Mathf.MoveTowards(CurrentWeight, talking ? 1f : 0f,
                deltaTime / Mathf.Max(.01f, talking ? blendIn : blendOut));
            // The layer's looping state keeps its phase through suppression and speech replacement.
            animator.SetLayerWeight(layer, CurrentWeight);
        }
        public void ResetPresentation()
        {
            if (animator != null && animator.isInitialized && animator.runtimeAnimatorController != null && layer >= 0 && layer < animator.layerCount)
                animator.SetLayerWeight(layer, 0);
            CurrentWeight = 0;
        }
        private void Update() => Tick(Time.deltaTime);
        private void OnDisable() => ResetPresentation();
    }
}
