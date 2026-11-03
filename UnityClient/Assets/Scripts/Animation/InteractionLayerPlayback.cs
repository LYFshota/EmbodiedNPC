using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Presentation-only masked layer/temporary prop lifetime shared by bounded work interactions.</summary>
    internal sealed class InteractionLayerPlayback
    {
        private readonly Animator animator;
        private readonly GameObject prop;
        private readonly ParticleSystem effect;
        private readonly string layerName;
        private readonly int primaryStateHash, alternateStateHash, emptyHash;
        private int stateHash;
        private RuntimeAnimatorController controller;
        private int layer = -1;
        private bool playing;
        public InteractionLayerPlayback(Animator animator, string layer, string state, GameObject prop, ParticleSystem effect = null, string alternateState = null)
        {
            this.animator = animator; layerName = layer; stateHash = primaryStateHash = Animator.StringToHash(layer + "." + state);
            alternateStateHash = alternateState == null ? 0 : Animator.StringToHash(layer + "." + alternateState);
            emptyHash = Animator.StringToHash(layer + ".Empty"); this.prop = prop; this.effect = effect; Inspect(); Reset();
        }
        private void Inspect() { controller = animator == null ? null : animator.runtimeAnimatorController; layer = controller == null ? -1 : animator.GetLayerIndex(layerName); }
        public float Weight { get; private set; }
        public bool IsPlaying => playing && Weight > 0;
        public float PlaybackSeconds
        {
            get
            {
                if (!playing || !IsReady) return 0;
                var state = animator.GetCurrentAnimatorStateInfo(layer);
                return state.fullPathHash == stateHash ? state.normalizedTime * state.length : 0;
            }
        }
        public bool IsReady => animator != null && animator.isActiveAndEnabled && animator.isInitialized && animator.isHuman &&
            animator.avatar != null && animator.avatar.isValid && layer >= 0 && layer < animator.layerCount &&
            animator.HasState(layer, stateHash) && animator.HasState(layer, emptyHash);
        public bool IsAlternateReady => IsReady && alternateStateHash != 0 && animator.HasState(layer, alternateStateHash);
        public void LoopAlternate(float start, float end)
        {
            if (!playing || !IsAlternateReady || stateHash != alternateStateHash || end <= start) return;
            var state = animator.GetCurrentAnimatorStateInfo(layer);
            if (!animator.IsInTransition(layer) && state.fullPathHash == stateHash && PlaybackSeconds >= end)
                animator.CrossFadeInFixedTime(stateHash, .14f, layer, start);
        }
        public void FinishAlternate(float at)
        {
            if (!playing || !IsAlternateReady || stateHash != alternateStateHash) return;
            var state = animator.GetCurrentAnimatorStateInfo(layer);
            if (state.fullPathHash == stateHash)
                animator.CrossFadeInFixedTime(stateHash, .12f, layer, Mathf.Max(0, at - .12f));
        }
        public bool IsTransitioning => animator != null && layer >= 0 && animator.IsInTransition(layer);
        public void Tick(bool active, float deltaTime, bool alternate = false, bool showProp = true)
        {
            int requested = alternate && alternateStateHash != 0 ? alternateStateHash : primaryStateHash;
            if (requested != stateHash) { Reset(); stateHash = requested; }
            if (animator != null && controller != animator.runtimeAnimatorController) { Reset(); Inspect(); }
            if (!active || !IsReady || !float.IsFinite(deltaTime) || deltaTime < 0) { Reset(); return; }
            if (!playing)
            { animator.Play(stateHash, layer, 0); playing = true; if (prop != null) prop.SetActive(showProp); if (effect != null && showProp) effect.Play(true); }
            Weight = Mathf.MoveTowards(Weight, 1, deltaTime / .12f); animator.SetLayerWeight(layer, Weight);
        }
        public void Reset()
        {
            if (animator != null && animator.isInitialized && animator.runtimeAnimatorController == controller && layer >= 0 && layer < animator.layerCount)
            { animator.SetLayerWeight(layer, 0); if (playing) animator.Play(emptyHash, layer, 0); }
            playing = false; Weight = 0;
            if (effect != null) effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (prop != null) prop.SetActive(false);
        }
    }
}
