using EmbodiedNPC.Dialogue;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EmbodiedNPC.Dialogue.Input
{
    /// <summary>Click-to-select adapter. It performs one raycast per click only.</summary>
    public sealed class NpcDialogueSelector : MonoBehaviour
    {
        [SerializeField] private Camera sceneCamera;
        [SerializeField] private DialogueController dialogueController;
        [SerializeField] private LayerMask selectionMask = ~0;
        [SerializeField, Min(0.1f)] private float maxSelectionDistance = 100f;

        private void Awake()
        {
            if (sceneCamera == null) sceneCamera = Camera.main;
        }

        private void Update()
        {
            if (!UnityEngine.Input.GetMouseButtonDown(0)) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            if (sceneCamera == null || dialogueController == null) return;

            Ray ray = sceneCamera.ScreenPointToRay(UnityEngine.Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, maxSelectionDistance, selectionMask,
                    QueryTriggerInteraction.Ignore)) return;
            dialogueController.SelectTarget(hit.collider.GetComponentInParent<DialogueParticipant>());
        }
    }
}
