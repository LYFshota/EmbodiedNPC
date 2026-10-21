using System;
using System.Linq;

namespace EmbodiedNPC.Cognition
{
    [Serializable]
    public sealed class CognitiveActionBindingCapabilityDto
    {
        public string name;
        public string kind;
        public bool required;
        public string[] enum_values = Array.Empty<string>();

        public bool IsValid => !string.IsNullOrWhiteSpace(name) && enum_values != null &&
            (kind == "entity_ref" && enum_values.Length == 0 || kind == "enum" &&
             enum_values.Length > 0 && enum_values.Length <= 32 && enum_values.All(v => !string.IsNullOrWhiteSpace(v)) &&
             enum_values.Distinct(StringComparer.Ordinal).Count() == enum_values.Length);
    }

    [Serializable]
    public sealed class CognitiveActionCapabilityDto
    {
        public string action_id;
        public string description;
        public CognitiveActionBindingCapabilityDto[] bindings = Array.Empty<CognitiveActionBindingCapabilityDto>();

        public bool IsValid => !string.IsNullOrWhiteSpace(action_id) &&
                               !string.IsNullOrWhiteSpace(description) && bindings != null &&
                               bindings.Length <= 16 && bindings.All(binding => binding != null && binding.IsValid) &&
                               bindings.Select(binding => binding.name).Distinct(StringComparer.Ordinal).Count() ==
                               bindings.Length;
    }

    [Serializable]
    public sealed class CognitiveCapabilityManifestDto
    {
        public CognitiveActionCapabilityDto[] available_actions = Array.Empty<CognitiveActionCapabilityDto>();

        public bool IsValid => available_actions != null && available_actions.Length <= 16 &&
                               available_actions.All(action => action != null && action.IsValid) &&
                               available_actions.Select(action => action.action_id)
                                   .Distinct(StringComparer.Ordinal).Count() == available_actions.Length;
    }

}
