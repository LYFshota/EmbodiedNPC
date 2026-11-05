using EmbodiedNPC.Animation;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Perception;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Projects the existing visible player conversation into the shared Humanoid gaze writer.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(110)]
    public sealed class PlayerDialogueGaze : MonoBehaviour
    {
        private DialogueController dialogue;
        private HeadGazeController gaze;
        private DialogueParticipant target;
        private PerceivableEntity face;
        private Transform head, neck, leftEye, rightEye;
        private bool subscribed;

        public void Configure(DialogueController conversation, Animator animator, Transform visual)
        {
            Unsubscribe();
            dialogue = conversation;
            gaze = GetComponent<HeadGazeController>() ?? gameObject.AddComponent<HeadGazeController>();
            gaze.Configure(null, animator, visual, transform);
            if (isActiveAndEnabled) Subscribe();
        }

        private void OnEnable() => Subscribe();
        private void Subscribe()
        {
            if (subscribed || dialogue == null) return;
            dialogue.ActiveSessionChanged += SelectTarget;
            dialogue.PresentationVisibilityChanged += OnVisibility;
            subscribed = true;
            SelectTarget(dialogue.HasActiveSession ? dialogue.SelectedTarget : null);
        }
        private void Unsubscribe()
        {
            if (subscribed && dialogue != null)
            {
                dialogue.ActiveSessionChanged -= SelectTarget;
                dialogue.PresentationVisibilityChanged -= OnVisibility;
            }
            subscribed = false;
        }
        private void SelectTarget(DialogueParticipant participant)
        {
            target = participant;
            face = participant == null ? null : participant.GetComponent<PerceivableEntity>();
            head = neck = leftEye = rightEye = null;
            if (participant == null) { gaze?.SetGazePoint(null); return; }
            var animator = participant.GetComponentInChildren<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.isHuman) return;
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
        }
        private void OnVisibility(bool visible)
        {
            // ESC/outside click hides input but intentionally retains the world conversation.
            // Release only the player's gaze; do not change that existing session lifetime.
            if (!visible) gaze?.SetGazePoint(null);
        }
        private void LateUpdate()
        {
            Vector3? point = null;
            if (dialogue != null && dialogue.isActiveAndEnabled && dialogue.HasActiveSession &&
                dialogue.IsPresentationVisible && target != null && target.isActiveAndEnabled &&
                dialogue.SelectedTarget == target)
            {
                // Use the existing live facial anchor when it is explicitly authored (and rebound on a rig swap).
                if (face != null && head != null && face.VisualAnchor == head) point = face.VisualTargetPosition;
                else if (leftEye != null && rightEye != null) point = (leftEye.position + rightEye.position) * .5f;
                else if (head != null) point = neck == null ? head.position : head.position + (head.position - neck.position) * 2f;
            }
            gaze?.SetGazePoint(point);
        }
        private void OnDisable()
        {
            Unsubscribe();
            target = null; face = null;
            head = neck = leftEye = rightEye = null;
            gaze?.SetGazePoint(null);
        }
    }
}
