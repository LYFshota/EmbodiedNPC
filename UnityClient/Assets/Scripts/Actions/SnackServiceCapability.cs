using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Actions
{
    /// <summary>Two visual-only pickup locations sharing the existing counter service reservation.</summary>
    [DisallowMultipleComponent]
    public sealed class SnackServiceCapability : MonoBehaviour
    {
        [SerializeField] private WorldEntity station;
        [SerializeField] private Transform showcaseApproach, showcaseOperation, rackApproach, rackOperation;
        public WorldEntity Station => station;
        public bool IsAuthorized => isActiveAndEnabled && TryGetComponent(out WorldEntity actor) && actor.EntityId == "npc_cafe_01" && actor.IsAgent(AgentRole.Npc) &&
            station != null && station.isActiveAndEnabled && station.gameObject.activeInHierarchy && station.EntityKind == EntityKind.Environment &&
            TryGetComponent(out AgentActionCapabilityProfile profile) && profile.isActiveAndEnabled && profile.AllowedActionIds.Contains("serve_snack") &&
            Active(showcaseApproach) && Active(showcaseOperation) && Active(rackApproach) && Active(rackOperation);
        private static bool Active(Transform t) => t != null && t.gameObject.activeInHierarchy;
        public Transform Approach(string snack) => SnackCatalog.Get(snack)?.location == "rack" ? rackApproach : showcaseApproach;
        public Transform Operation(string snack) => SnackCatalog.Get(snack)?.location == "rack" ? rackOperation : showcaseOperation;
        public void Configure(WorldEntity counter, Transform showcase, Transform showcaseHand, Transform rack, Transform rackHand)
        { station = counter; showcaseApproach = showcase; showcaseOperation = showcaseHand; rackApproach = rack; rackOperation = rackHand; }
        public bool TryResolve(NavMeshAgent agent, string snack, out Vector3 position)
        {
            position = default;
            if (!IsAuthorized || !SnackCatalog.IsValid(snack) || agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh ||
                !NavMesh.SamplePosition(Approach(snack).position, out var hit, .15f, agent.areaMask)) return false;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(agent.transform.position, hit.position, agent.areaMask, path) || path.status != NavMeshPathStatus.PathComplete) return false;
            position = hit.position; return true;
        }
        public static bool ValidateRuntime(EntityRegistry registry, WorldEntity actor, WorldEntity target, WorldEntity player, string snack) =>
            DrinkTypes.IsCurrent(registry, actor) && DrinkTypes.IsCurrent(registry, target) && DrinkTypes.IsCurrent(registry, player) &&
            player.IsAgent(AgentRole.Player) && SnackCatalog.IsValid(snack) && actor.TryGetComponent(out SnackServiceCapability cap) &&
            cap.IsAuthorized && cap.Station == target && !registry.RegisteredEntities.Any(item=>item!=null &&
                item.TryGetComponent(out HeldItemState held) && held.IsHeld && held.HolderEntityId==actor.EntityId);
    }
}
