using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Actions
{
    [DisallowMultipleComponent]
    public sealed class CounterCleaningCapability : MonoBehaviour
    {
        [SerializeField] private CounterCleaningStation station;
        public CounterCleaningStation Station => station;
        public bool IsAuthorized => isActiveAndEnabled && station != null && station.IsAvailable &&
            TryGetComponent(out WorldEntity actor) && actor.EntityId == "npc_cafe_01" && actor.IsAgent(AgentRole.Npc) &&
            TryGetComponent(out AgentActionCapabilityProfile profile) && profile.isActiveAndEnabled &&
            profile.AllowedActionIds.Contains("clean_counter");
        public void Configure(CounterCleaningStation value) => station = value;
        // Configured work knowledge, explicitly separate from visual perception of movable objects.
        public CounterWorkDto Capture() => !IsAuthorized ? null : new CounterWorkDto
        {
            target_entity_id = station.Entity.EntityId,
            needs_cleaning = station.World.GetCounterState(station.Entity.EntityId) == CounterCleanliness.NeedsCleaning,
            condition_revision = station.World.GetCounterConditionRevision(station.Entity.EntityId),
        };
    }
}
