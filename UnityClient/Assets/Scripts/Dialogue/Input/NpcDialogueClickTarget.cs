using EmbodiedNPC.Dialogue;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EmbodiedNPC.Dialogue.Input
{
    /// <summary>
    /// Receives pointer clicks from the scene EventSystem. The camera's
    /// PhysicsRaycaster resolves the clicked collider before this is invoked.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(DialogueParticipant))]
    public sealed class NpcDialogueClickTarget : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private DialogueController dialogueController;
        [SerializeField] private DialogueParticipant dialogueParticipant;

        private void Awake()
        {
            if (dialogueParticipant == null)
            {
                dialogueParticipant = GetComponent<DialogueParticipant>();
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            dialogueController?.SelectTarget(dialogueParticipant);
        }
    }
}
