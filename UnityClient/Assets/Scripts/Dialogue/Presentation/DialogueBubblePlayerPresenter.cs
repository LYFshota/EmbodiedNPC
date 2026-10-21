using UnityEngine;

namespace EmbodiedNPC.Dialogue.Presentation
{
    /// <summary>Projects only accepted player submissions into the presentation history.</summary>
    [DisallowMultipleComponent]
    public sealed class DialogueBubblePlayerPresenter : MonoBehaviour
    {
        [SerializeField] private DialogueController dialogueController;
        [SerializeField] private DialogueBubbleManager manager;

        private void OnEnable()
        {
            if (dialogueController == null) dialogueController = FindAnyObjectByType<DialogueController>();
            if (manager == null) manager = DialogueBubbleManager.Active ?? FindAnyObjectByType<DialogueBubbleManager>();
            if (dialogueController != null) dialogueController.DialogueSubmissionAccepted += HandleAccepted;
        }

        private void OnDisable()
        {
            if (dialogueController != null) dialogueController.DialogueSubmissionAccepted -= HandleAccepted;
        }

        public void Configure(DialogueController controller, DialogueBubbleManager bubbleManager)
        {
            if (isActiveAndEnabled && dialogueController != null)
                dialogueController.DialogueSubmissionAccepted -= HandleAccepted;
            dialogueController = controller;
            manager = bubbleManager;
            if (isActiveAndEnabled && dialogueController != null)
                dialogueController.DialogueSubmissionAccepted += HandleAccepted;
        }

        private void HandleAccepted(UserUtterance utterance)
        {
            if (utterance != null)
                manager?.PushMessage(utterance.speakerEntityId, utterance.text, DialogueSpeakerKind.Player);
        }
    }
}
