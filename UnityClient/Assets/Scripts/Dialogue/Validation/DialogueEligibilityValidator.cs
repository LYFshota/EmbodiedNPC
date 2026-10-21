using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Dialogue.Validation
{
    public enum DialogueEligibilityFailureCode
    {
        None,
        TargetNotFound,
        TargetCannotCommunicate,
        TargetOutOfDialogueRange,
        DialogueNotAllowed,
    }

    public readonly struct DialogueEligibilityResult
    {
        public DialogueEligibilityResult(DialogueEligibilityFailureCode failureCode)
        {
            FailureCode = failureCode;
        }

        public DialogueEligibilityFailureCode FailureCode { get; }
        public bool IsEligible => FailureCode == DialogueEligibilityFailureCode.None;
    }

    /// <summary>Unity owns who may be addressed; models never infer the target.</summary>
    public sealed class DialogueEligibilityValidator : MonoBehaviour
    {
        [SerializeField] private WorldEntity playerEntity;
        [SerializeField, Min(0.1f)] private float dialogueDistance = 4f;
        [SerializeField] private bool dialogueAllowed = true;

        public float DialogueDistance => dialogueDistance;

        private void Awake()
        {
            if (playerEntity == null)
            {
                foreach (WorldEntity entity in FindObjectsByType<WorldEntity>())
                {
                    if (entity.IsAgent(AgentRole.Player))
                    {
                        playerEntity = entity;
                        break;
                    }
                }
            }
        }

        public DialogueEligibilityResult Validate(DialogueParticipant target)
        {
            if (!dialogueAllowed || playerEntity == null)
            {
                return new DialogueEligibilityResult(DialogueEligibilityFailureCode.DialogueNotAllowed);
            }

            if (target == null || target.Entity == null || !target.Entity.IsAgent(AgentRole.Npc))
            {
                return new DialogueEligibilityResult(DialogueEligibilityFailureCode.TargetNotFound);
            }

            if (!target.CanCommunicate)
            {
                return new DialogueEligibilityResult(DialogueEligibilityFailureCode.TargetCannotCommunicate);
            }

            if (Vector3.Distance(playerEntity.transform.position, target.transform.position) > dialogueDistance)
            {
                return new DialogueEligibilityResult(DialogueEligibilityFailureCode.TargetOutOfDialogueRange);
            }

            return new DialogueEligibilityResult(DialogueEligibilityFailureCode.None);
        }
    }
}
