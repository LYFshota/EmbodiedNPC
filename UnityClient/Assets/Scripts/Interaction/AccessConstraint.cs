using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Interaction
{
    /// <summary>Explicit authoring link from a target entity to the door that gates access to it.</summary>
    public sealed class AccessConstraint : MonoBehaviour
    {
        [SerializeField] private WorldEntity target;
        [SerializeField] private DoorState blockingDoor;

        public WorldEntity Target => target != null ? target : GetComponent<WorldEntity>();
        public DoorState BlockingDoor => blockingDoor;
        public bool IsConfigured => Target != null && Target.gameObject == gameObject &&
                                    blockingDoor != null && blockingDoor.IsConfigured;
        public bool IsBlocking => IsConfigured && !blockingDoor.IsOpen;

        private void Awake()
        {
            if (target == null) target = GetComponent<WorldEntity>();
        }

        private void OnValidate()
        {
            if (target == null) target = GetComponent<WorldEntity>();
        }
    }
}
