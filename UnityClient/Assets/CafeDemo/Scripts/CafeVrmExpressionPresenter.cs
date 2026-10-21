using System;
using EmbodiedNPC.Animation;
using UniVRM10;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Bounded face projection through UniVRM's mixer. Never writes meshes, mouth or LookAt.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(200)]
    public sealed class CafeVrmExpressionPresenter : MonoBehaviour
    {
        [SerializeField] private ExpressiveController expressive;
        [SerializeField] private Animator animator;
        [SerializeField, Range(0, .3f)] private float smileWeight = .14f;
        [SerializeField, Range(0, .1f)] private float relaxedWeight = .035f;
        [SerializeField, Min(1f)] private float minimumBlinkInterval = 3f;
        [SerializeField, Min(1f)] private float maximumBlinkInterval = 6f;
        private Vrm10Instance model;
        private Vrm10RuntimeExpression mixer;
        private OwnedWeight blink, happy, relaxed;
        private System.Random random;
        private float blinkWait, blinkTime;
        private bool blinking;
        private bool pendingInitialization;
        public bool HasBlink => blink != null && blink.Available;
        public bool HasHappy => happy != null && happy.Available;
        public bool HasRelaxed => relaxed != null && relaxed.Available;
        public float BlinkWeight => blink == null ? 0 : blink.Value;
        public float HappyWeight => happy == null ? 0 : happy.Value;
        public float RelaxedWeight => relaxed == null ? 0 : relaxed.Value;

        public void Configure(ExpressiveController presentation, Animator character)
        {
            Release(); expressive = presentation; animator = character;
            if (isActiveAndEnabled) BeginInitialization();
        }
        private void OnEnable() => BeginInitialization();
        private void BeginInitialization()
        {
            model = animator == null ? null : animator.GetComponent<Vrm10Instance>();
            pendingInitialization = model != null;
            Initialize();
        }
        private void Initialize()
        {
            // Runtime initialization happens only in Play Mode, including tests; never on an imported prefab asset.
            if (!Application.isPlaying || !pendingInitialization || mixer != null || animator == null) return;
            if (model == null || !model.isActiveAndEnabled || model.Vrm == null) return;
            mixer = model.Runtime.Expression;
            pendingInitialization = false;
            blink = Own(ExpressionPreset.blink, model.Vrm.Expression.Blink, false);
            happy = Own(ExpressionPreset.happy, model.Vrm.Expression.Happy, true);
            relaxed = Own(ExpressionPreset.relaxed, model.Vrm.Expression.Relaxed, true);
            random = new System.Random(Guid.NewGuid().GetHashCode());
            blinkWait = NextInterval(); blinkTime = 0; blinking = false;
        }
        private OwnedWeight Own(ExpressionPreset preset, VRM10Expression clip, bool emotion)
        {
            // A named but empty preset is not a facial capability. Reject overrides that could mute lips or eyes.
            if (clip == null || !HasUsableBindings(clip) ||
                (emotion && (clip.IsBinary || (int)clip.OverrideBlink != 0 ||
                    (int)clip.OverrideLookAt != 0 || (int)clip.OverrideMouth != 0))) return null;
            var key = new ExpressionKey(preset);
            bool found = false;
            foreach (var available in mixer.ExpressionKeys) if (available.Equals(key)) { found = true; break; }
            return found ? new OwnedWeight(mixer, key) : null;
        }
        private bool HasUsableBindings(VRM10Expression clip)
        {
            if (clip.MorphTargetBindings == null || clip.MorphTargetBindings.Length == 0) return false;
            bool effective = false;
            foreach (var binding in clip.MorphTargetBindings)
            {
                var node = string.IsNullOrEmpty(binding.RelativePath) ? model.transform : model.transform.Find(binding.RelativePath);
                var renderer = node == null ? null : node.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null || renderer.sharedMesh == null || binding.Index < 0 ||
                    binding.Index >= renderer.sharedMesh.blendShapeCount || !float.IsFinite(binding.Weight)) return false;
                effective |= binding.Weight > 0;
            }
            return effective;
        }
        private float NextInterval() => Mathf.Max(1f, minimumBlinkInterval) +
            (float)random.NextDouble() * Mathf.Max(0, maximumBlinkInterval - Mathf.Max(1f, minimumBlinkInterval));

        public void Tick(float deltaTime)
        {
            if (!isActiveAndEnabled || !float.IsFinite(deltaTime) || deltaTime < 0 || mixer == null) return;
            if (model == null || !model.isActiveAndEnabled || animator == null || !animator.isActiveAndEnabled)
            { Release(); return; }
            float blinkTarget = 0;
            if (HasBlink)
            {
                if (!blinking)
                {
                    blinkWait -= deltaTime;
                    if (blinkWait <= 0) { blinking = true; blinkTime = 0; }
                }
                if (blinking)
                {
                    blinkTime += deltaTime;
                    // Asymmetric close/open with an explicit neutral end; a stalled frame cannot leave eyes closed.
                    blinkTarget = blinkTime < .065f ? Mathf.SmoothStep(0, 1, blinkTime / .065f) :
                        1 - Mathf.SmoothStep(0, 1, (blinkTime - .065f) / .13f);
                    if (blinkTime >= .195f) { blinkTarget = 0; blinking = false; blinkWait = NextInterval(); }
                }
            }
            blink?.Write(blinkTarget);
            var channels = expressive == null ? null : expressive.MotionChannels;
            bool allowed = expressive != null && expressive.isActiveAndEnabled && channels != null &&
                !channels.IsEmergencyMotionActive && !channels.IsTaskMotionActive;
            bool talking = allowed && expressive.State == DialoguePresentationState.Talking;
            float smile = talking && expressive.ExpressionHint == "happy" ? smileWeight : 0;
            happy?.Blend(smile, deltaTime, .45f);
            relaxed?.Blend(allowed && expressive.ExpressionHint == "neutral" && !talking ? relaxedWeight : 0,
                deltaTime, .65f);
        }
        public void ResetPresentation() => Release();
        private void Release()
        {
            blink?.Release(); happy?.Release(); relaxed?.Release();
            blink = happy = relaxed = null; mixer = null; model = null;
            pendingInitialization = false;
            blinking = false; blinkTime = 0;
        }
        private void Update()
        {
            // Parent OnEnable can precede the imported child model's activation; retry cached references only.
            if (pendingInitialization) Initialize();
            Tick(Time.deltaTime);
        }
        private void OnDisable() => Release();

        private sealed class OwnedWeight
        {
            private readonly Vrm10RuntimeExpression mixer;
            private readonly ExpressionKey key;
            private readonly float baseline;
            private float written;
            private float blendStart, blendTarget, blendElapsed;
            public bool Available { get; private set; }
            public float Value => Available ? written : 0;
            public OwnedWeight(Vrm10RuntimeExpression expression, ExpressionKey expressionKey)
            {
                mixer = expression; key = expressionKey; baseline = written = mixer.GetWeight(key);
                // Respect an existing owner. A later change on this key also permanently yields this enable interval.
                Available = Mathf.Abs(baseline) < .0001f;
            }
            public void Write(float value)
            {
                if (!Available) return;
                if (Mathf.Abs(mixer.GetWeight(key) - written) > .0001f) { Available = false; return; }
                written = Mathf.Clamp01(value); mixer.SetWeight(key, written);
            }
            public void Blend(float target, float deltaTime, float seconds)
            {
                if (!Available) return;
                if (!Mathf.Approximately(target, blendTarget))
                { blendStart = written; blendTarget = target; blendElapsed = 0; }
                blendElapsed = Mathf.Min(seconds, blendElapsed + deltaTime);
                Write(Mathf.Lerp(blendStart, blendTarget, Mathf.SmoothStep(0, 1, blendElapsed / seconds)));
            }
            public void Release()
            {
                if (Available && Mathf.Abs(mixer.GetWeight(key) - written) < .0001f) mixer.SetWeight(key, baseline);
                Available = false;
            }
        }
    }
}
