using UnityEngine;
namespace CafeDemo
{
    /// <summary>Fixed seated foot targets on the visual rig only. No seat ownership or world movement.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    public sealed class CafeSeatedFootPose : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private Vector3 leftFootLocal;
        [SerializeField] private Vector3 rightFootLocal;
        [SerializeField] private Vector3 leftKneeLocal;
        [SerializeField] private Vector3 rightKneeLocal;
        public void Configure(Animator character, Vector3 leftLocal, Vector3 rightLocal, Vector3 leftKnee, Vector3 rightKnee)
        { animator=character; leftFootLocal=leftLocal; rightFootLocal=rightLocal; leftKneeLocal=leftKnee; rightKneeLocal=rightKnee; }
        private void Awake(){if(animator==null)animator=GetComponent<Animator>();}
        private void OnAnimatorIK(int layerIndex)
        {
            if(layerIndex!=0||animator==null||!animator.isHuman)return;
            // The authored hip/body pose and actor transform remain authoritative for the seated placement.
            animator.SetIKHintPositionWeight(AvatarIKHint.LeftKnee,1);
            animator.SetIKHintPositionWeight(AvatarIKHint.RightKnee,1);
            animator.SetIKHintPosition(AvatarIKHint.LeftKnee,transform.TransformPoint(leftKneeLocal));
            animator.SetIKHintPosition(AvatarIKHint.RightKnee,transform.TransformPoint(rightKneeLocal));
            animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot,1);
            animator.SetIKPositionWeight(AvatarIKGoal.RightFoot,1);
            animator.SetIKPosition(AvatarIKGoal.LeftFoot,transform.TransformPoint(leftFootLocal));
            animator.SetIKPosition(AvatarIKGoal.RightFoot,transform.TransformPoint(rightFootLocal));
            animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot,1);
            animator.SetIKRotationWeight(AvatarIKGoal.RightFoot,1);
            animator.SetIKRotation(AvatarIKGoal.LeftFoot,Quaternion.LookRotation(transform.forward,Vector3.up));
            animator.SetIKRotation(AvatarIKGoal.RightFoot,Quaternion.LookRotation(transform.forward,Vector3.up));
        }
    }
}
