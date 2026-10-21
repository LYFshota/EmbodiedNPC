using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>
    /// Minimal per-Agent allowlist projected from the existing Unity ActionCatalog.
    /// It exposes semantic descriptors only, never plan, navigation or mutation details.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AgentActionCapabilityProfile : MonoBehaviour
    {
        [SerializeField] private string[] allowedActionIds = Array.Empty<string>();

        public IReadOnlyList<string> AllowedActionIds => allowedActionIds;

        public void Configure(params string[] actionIds) =>
            allowedActionIds = actionIds == null ? Array.Empty<string>() : actionIds.ToArray();

        public bool TryBuildManifest(out CognitiveCapabilityManifestDto manifest, ActionCatalog catalog = null)
        {
            catalog ??= BuiltInActionCatalog.Instance;
            manifest = null;
            if (allowedActionIds == null || allowedActionIds.Length > 16 ||
                allowedActionIds.Any(string.IsNullOrWhiteSpace) ||
                allowedActionIds.Distinct(StringComparer.Ordinal).Count() != allowedActionIds.Length) return false;

            var actions = new List<CognitiveActionCapabilityDto>(allowedActionIds.Length);
            foreach (string actionId in allowedActionIds)
            {
                if (actionId == "clean_counter" && (!TryGetComponent(out CounterCleaningCapability cleaning) || !cleaning.IsAuthorized)) continue;
                if (actionId == "prepare_drink" && (!TryGetComponent(out EmbodiedNPC.World.WorldEntity actor) ||
                    actor.EntityId != "npc_cafe_01" || !TryGetComponent(out EmbodiedNPC.Actions.DrinkPreparationCapability drink) ||
                    !drink.IsAuthorized)) continue;
                if (!catalog.TryGet(actionId, out ActionRegistration registration) ||
                    !registration.Descriptor.Available) return false;
                ActionDescriptor descriptor = registration.Descriptor;
                var bindings = new List<CognitiveActionBindingCapabilityDto>(descriptor.BindingSchema.Count);
                foreach (ActionBindingDescriptor binding in descriptor.BindingSchema)
                {
                    if (binding.Kind != ActionBindingKind.EntityRef && binding.Kind != ActionBindingKind.Enum) return false;
                    bindings.Add(new CognitiveActionBindingCapabilityDto
                    {
                        name = binding.Name,
                        kind = binding.Kind == ActionBindingKind.Enum ? "enum" : "entity_ref",
                        required = binding.Required,
                        enum_values = binding.EnumValues.ToArray(),
                    });
                }
                actions.Add(new CognitiveActionCapabilityDto
                {
                    action_id = descriptor.ActionId,
                    description = descriptor.Description,
                    bindings = bindings.ToArray(),
                });
            }

            manifest = new CognitiveCapabilityManifestDto { available_actions = actions.ToArray() };
            return manifest.IsValid;
        }
    }
}
