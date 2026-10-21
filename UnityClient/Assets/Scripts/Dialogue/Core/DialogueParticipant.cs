using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Dialogue
{
    /// <summary>Marks a world NPC as a valid dialogue target.</summary>
    [RequireComponent(typeof(WorldEntity))]
    public sealed class DialogueParticipant : MonoBehaviour
    {
        [SerializeField] private bool canCommunicate = true;

        public bool CanCommunicate => canCommunicate;
        public WorldEntity Entity { get; private set; }

        private void Awake() => Entity = GetComponent<WorldEntity>();
    }
}
