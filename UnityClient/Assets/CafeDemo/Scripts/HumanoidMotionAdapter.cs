using System;
using System.Linq;
using EmbodiedNPC.Animation;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Opt-in composition boundary for the existing presentation components. No motion or world authority.</summary>
    [DisallowMultipleComponent]
    public sealed class HumanoidMotionAdapter : MonoBehaviour
    {
        [SerializeField] private Transform visualBody;
        [SerializeField] private Animator animator;
        private RuntimeAnimatorController boundController;
        public Animator Animator => animator;
        public string LastDiagnostic { get; private set; }

        // Authoring only: the stable wrapper and existing component configuration are retained across model changes.
        public void Configure(Transform wrapper, Animator initialCharacter = null)
        { visualBody = wrapper; animator = initialCharacter; }

        private void Start()
        {
            if (animator != null && !TryBind(animator, out var error))
                Debug.LogWarning("[MotionAdapter] " + error, this);
        }

        public static bool ValidateHumanoid(Animator candidate, out string error)
        {
            error = null;
            if (candidate == null || candidate.avatar == null || !candidate.avatar.isValid || !candidate.avatar.isHuman)
            { error = "A valid Humanoid Avatar is required."; return false; }
            var scale = candidate.transform.lossyScale;
            if (!float.IsFinite(scale.sqrMagnitude) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0 ||
                !Mathf.Approximately(scale.x, scale.y) || !Mathf.Approximately(scale.x, scale.z))
            { error = "Use a finite, positive uniform model scale; keep gameplay dimensions on ActorRoot."; return false; }
            foreach (var bone in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot })
                if (candidate.GetBoneTransform(bone) == null)
                { error = "Missing Humanoid bone: " + bone; return false; }
            return true;
        }

        // Call after the candidate has been activated/initialized, before removing the previous model.
        // A failed validation changes no binding, layer, gesture lease, transform or world state.
        public bool TryBind(Animator candidate, out string error)
        {
            string errorText = null;
            bool Reject(string reason) { LastDiagnostic = errorText = reason; return false; }
            bool result = Bind();
            error = errorText;
            return result;

            bool Bind()
            {
                if (boundController != null && animator != null && animator.runtimeAnimatorController != boundController)
                    return Reject("The bound controller changed externally; retain the old rig/controller until replacement completes.");
                if (!ValidateHumanoid(candidate, out var rigError)) return Reject(rigError);
                if (visualBody == null || visualBody.parent != transform || candidate.transform == visualBody ||
                    !candidate.transform.IsChildOf(visualBody))
                    return Reject("Keep the model below a stable Visual wrapper directly under this actor.");
                if (!candidate.isActiveAndEnabled || !candidate.isInitialized || candidate.runtimeAnimatorController == null)
                    return Reject("Activate and initialize the candidate with the existing motion controller before binding.");
                if (candidate.applyRootMotion) return Reject("Animator root motion must be disabled.");
                var motion = GetComponent<MotionChannelCoordinator>();
                if (motion != null && (motion.IsTaskMotionActive || motion.IsEmergencyMotionActive || motion.IsCarryPresentationActive))
                    return Reject("Wait until task, navigation, emergency and carry presentation are idle before binding.");

                // Resolve once at the explicit boundary, never each frame. Imported-model components cannot own actor bindings.
                var consumers = new Type[] { typeof(GestureController), typeof(HeadGazeController),
                    typeof(DialogueAnimationPresenter), typeof(LocomotionPresenter), typeof(CarryPresenter),
                    typeof(CarryAnchorFollower), typeof(DrinkPreparationPresenter), typeof(CounterCleaningPresenter),
                    typeof(CafeVrmExpressionPresenter), typeof(FacingController), typeof(PlayerLocomotionPresenter), typeof(PlayerEmoteController) };
                foreach (var type in consumers)
                {
                    var found = GetComponentsInChildren(type, true);
                    if (found.Length > 1 || found.Any(c => c.transform == candidate.transform || c.transform.IsChildOf(candidate.transform)))
                        return Reject("Keep one actor-owned " + type.Name + " outside the replaceable model.");
                }
                var facing = GetComponentInChildren<FacingController>(true);
                if (facing != null && facing.Visual != visualBody) return Reject("FacingController must retain the same Visual wrapper.");
                var gesture = GetComponentInChildren<GestureController>(true);
                var gaze = GetComponentInChildren<HeadGazeController>(true);
                var talk = GetComponentInChildren<DialogueAnimationPresenter>(true);
                var locomotion = GetComponentInChildren<LocomotionPresenter>(true);
                var carry = GetComponentInChildren<CarryPresenter>(true);
                var hand = GetComponentInChildren<CarryAnchorFollower>(true);
                var prepare = GetComponentInChildren<DrinkPreparationPresenter>(true);
                var clean = GetComponentInChildren<CounterCleaningPresenter>(true);
                var face = GetComponentInChildren<CafeVrmExpressionPresenter>(true);
                var playerWalk = GetComponentInChildren<PlayerLocomotionPresenter>(true);
                var playerEmote = GetComponentInChildren<PlayerEmoteController>(true);
                if (playerWalk != null && !candidate.parameters.Any(p => p.name == "Speed" && p.type == AnimatorControllerParameterType.Float))
                    return Reject("Missing player locomotion Speed parameter.");
                if (playerEmote != null && !playerEmote.CanBindAnimator(candidate))
                    return Reject("Wait for player emotes to finish; retain EmoteWaveArms.Wave/Empty states.");

                bool HasState(string layer, string state)
                {
                    int index = candidate.GetLayerIndex(layer);
                    return index >= 0 && candidate.HasState(index, UnityEngine.Animator.StringToHash(layer + "." + state));
                }
                if (locomotion != null && !candidate.parameters.Any(p => p.name == locomotion.SpeedParameter && p.type == AnimatorControllerParameterType.Float))
                    return Reject("The existing locomotion float parameter is missing.");
                if (talk != null && !HasState("Dialogue", "Talking")) return Reject("Missing Dialogue.Talking state.");
                if (gesture != null && (!HasState(GestureController.WaveLayerName, "Wave") ||
                    !HasState(GestureController.WaveLayerName, "Empty") || gesture.WaveClip == null || !gesture.WaveClip.isHumanMotion || gesture.WaveClip.isLooping || gesture.WaveClip.length <= 0 ||
                    !candidate.runtimeAnimatorController.animationClips.Contains(gesture.WaveClip)))
                    return Reject("Retain the configured Humanoid Wave clip and GestureUpperBody Wave/Empty states.");
                if (carry != null && !HasState(CarryPresenter.LayerName, "Carry")) return Reject("Missing CarryUpperBody.Carry state.");
                if (prepare != null && (!HasState(DrinkPreparationPresenter.LayerName, "PrepareDrink") ||
                    !HasState(DrinkPreparationPresenter.LayerName, "Empty"))) return Reject("Missing preparation states.");
                if (clean != null && (!HasState(CounterCleaningPresenter.LayerName, "CleanCounter") ||
                    !HasState(CounterCleaningPresenter.LayerName, "Empty"))) return Reject("Missing cleaning states.");

                // Existing Configure paths release old offsets/weights/leases and preserve nonvisual dependencies.
                gesture?.RebindAnimator(candidate);
                gaze?.RebindAnimator(candidate);
                talk?.RebindAnimator(candidate);
                locomotion?.RebindAnimator(candidate);
                carry?.RebindAnimator(candidate);
                hand?.RebindAnimator(candidate);
                prepare?.RebindAnimator(candidate);
                clean?.RebindAnimator(candidate);
                face?.RebindAnimator(candidate);
                playerWalk?.RebindAnimator(candidate);
                playerEmote?.RebindAnimator(candidate);
                GetComponent<EmbodiedNPC.Perception.PerceivableEntity>()?.RebindAnimator(candidate);
                animator = candidate; boundController = candidate.runtimeAnimatorController;
                LastDiagnostic = "Humanoid bound; neck=" + (candidate.GetBoneTransform(HumanBodyBones.Neck) != null) +
                    "; humanScale=" + candidate.humanScale.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    ". Facial presets degrade independently when bindings are absent.";
                return true;
            }
        }
    }
}
