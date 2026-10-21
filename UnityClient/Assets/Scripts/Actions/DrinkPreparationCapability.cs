using System.Linq;
using EmbodiedNPC.Cognition;
using UnityEngine;

namespace EmbodiedNPC.Actions
{
    [DisallowMultipleComponent]
    public sealed class DrinkPreparationCapability : MonoBehaviour
    {
        [SerializeField] private DrinkPreparationStation station;
        public DrinkPreparationStation Station => station;
        public bool IsAuthorized => isActiveAndEnabled && station != null && station.IsAvailable &&
            TryGetComponent(out AgentActionCapabilityProfile profile) && profile.isActiveAndEnabled &&
            profile.AllowedActionIds.Contains("prepare_drink");
        public void Configure(DrinkPreparationStation value) => station = value;
    }
}
