using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Moves only the existing hold point after the Humanoid pose has evaluated.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(150)]
    public sealed class CarryAnchorFollower : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private Transform holdPoint;
        [SerializeField] private Vector3 positionOffset;
        [SerializeField] private Vector3 rotationOffset;
        [SerializeField] private bool keepUpright;
        private Transform rightHand;
        public Transform RightHand => rightHand;
        public bool IsReady => animator != null && animator.isActiveAndEnabled && rightHand != null &&
            holdPoint != null && holdPoint != rightHand && !rightHand.IsChildOf(holdPoint);

        public void RebindAnimator(Animator character) => Configure(character, holdPoint, positionOffset, rotationOffset);

        public void Configure(Animator character, Transform anchor, Vector3 position, Vector3 rotation)
        {
            animator = character; holdPoint = anchor; positionOffset = position; rotationOffset = rotation;
            ResolveHand();
        }
        private void ResolveHand() => rightHand = animator != null && animator.avatar != null &&
            animator.avatar.isValid && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
        private void Awake() => ResolveHand();
        private void OnEnable() => ResolveHand();
        public void FollowPose()
        {
            if (!isActiveAndEnabled || !IsReady) return;
            Quaternion rotation = rightHand.rotation * Quaternion.Euler(rotationOffset);
            // A filled cup follows the hand's position and yaw, while gravity defines its up axis.
            // Opt-in per hold point; other items retain their authored wrist-relative rotation.
            if (keepUpright)
            {
                Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
                if (forward.sqrMagnitude > .0001f) rotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            holdPoint.SetPositionAndRotation(rightHand.TransformPoint(positionOffset), rotation);
        }
        private void LateUpdate() => FollowPose();
    }
}
