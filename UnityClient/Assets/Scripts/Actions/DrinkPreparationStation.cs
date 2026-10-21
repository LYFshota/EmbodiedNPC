using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Actions
{
    /// <summary>Authored station capability. An arbitrary Transform is not a drink station.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(WorldEntity))]
    public sealed class DrinkPreparationStation : MonoBehaviour
    {
        [SerializeField] private Transform approachPoint;
        [SerializeField] private Transform operationPoint;
        public WorldEntity Entity => GetComponent<WorldEntity>();
        public Transform ApproachPoint => approachPoint;
        public Transform OperationPoint => operationPoint;
        public bool IsAvailable => isActiveAndEnabled && gameObject.activeInHierarchy && Entity != null &&
            Entity.isActiveAndEnabled && Entity.EntityKind == EntityKind.Environment && approachPoint != null &&
            operationPoint != null && approachPoint.gameObject.activeInHierarchy && operationPoint.gameObject.activeInHierarchy;
        public void Configure(Transform approach, Transform operation)
        { approachPoint = approach; operationPoint = operation; }
    }
}
