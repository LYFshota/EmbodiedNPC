using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Interaction
{
    /// <summary>Small adapter that exposes WorldEntity capabilities to GoalValidator.</summary>
    [RequireComponent(typeof(WorldEntity))]
    public sealed class InteractableEntity : MonoBehaviour, IInteractable
    {
        [SerializeField] private bool supportsPickUp = true;
        [SerializeField] private bool supportsGive;
        private WorldEntity worldEntity;

        public string EntityId => worldEntity != null ? worldEntity.EntityId : string.Empty;
        public bool SupportsPickUp => supportsPickUp;
        public bool SupportsGive => supportsGive;

        private void Awake() { worldEntity = GetComponent<WorldEntity>(); }
    }
}
