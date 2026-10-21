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
        private readonly int stateHash, emptyHash;
        private RuntimeAnimatorController controller;
        private int layer = -1;
        private bool playing;
        public InteractionLayerPlayback(Animator animator, string layer, string state, GameObject prop, ParticleSystem effect = null)
        {
            this.animator = animator; layerName = layer; stateHash = Animator.StringToHash(layer + "." + state);
            emptyHash = Animator.StringToHash(layer + ".Empty"); this.prop = prop; this.effect = effect; Inspect(); Reset();
        }
        private void Inspect() { controller = animator == null ? null : animator.runtimeAnimatorController; layer = controller == null ? -1 : animator.GetLayerIndex(layerName); }
        public float Weight { get; private set; }
        public bool IsPlaying => playing && Weight > 0;
        public bool IsReady => animator != null && animator.isActiveAndEnabled && animator.isInitialized && animator.isHuman &&
            animator.avatar != null && animator.avatar.isValid && layer >= 0 && layer < animator.layerCount &&
            animator.HasState(layer, stateHash) && animator.HasState(layer, emptyHash);
        public void Tick(bool active, float deltaTime)
        {
            if (animator != null && controller != animator.runtimeAnimatorController) { Reset(); Inspect(); }
            if (!active || !IsReady || !float.IsFinite(deltaTime) || deltaTime < 0) { Reset(); return; }
            if (!playing)
            { animator.Play(stateHash, layer, 0); playing = true; if (prop != null) prop.SetActive(true); if (effect != null) effect.Play(true); }
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
