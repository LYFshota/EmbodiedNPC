using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>
    /// Declares an Agent's authored feature composition and validates it against local components.
    /// It never adds, removes, enables or disables runtime modules.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AgentFeatureProfileBinding : MonoBehaviour
    {
        [SerializeField] private AgentFeatureProfile profile;

        public AgentFeatureProfile Profile => profile;
        public AgentFeatureConfiguration Configuration => profile == null ? null : profile.ToConfiguration();

        public void Configure(AgentFeatureProfile featureProfile) => profile = featureProfile;

        public bool TryValidateConfiguration(out string error)
        {
            if (profile == null)
            {
                error = "AgentFeatureProfileBinding requires an AgentFeatureProfile.";
                return false;
            }

            bool hasTriggerBridge = GetComponent<WakeCognitiveTriggerBridge>() != null;
            bool hasOrchestrator = GetComponent<AgentOrchestrator>() != null;
            bool hasContextComposer = GetComponent<CognitiveContextComposer>() != null;
            bool hasRunDriver = GetComponent<CognitiveRunDriver>() != null;

            if (!profile.CognitionEnabled)
            {
                if (hasTriggerBridge || hasOrchestrator || hasContextComposer || hasRunDriver)
                {
                    error = "Cognition is disabled, but the Agent root still contains cognition runtime components.";
                    return false;
                }

                error = string.Empty;
                return true;
            }

            var missing = new List<string>(4);
            if (!hasTriggerBridge) missing.Add(nameof(WakeCognitiveTriggerBridge));
            if (!hasOrchestrator) missing.Add(nameof(AgentOrchestrator));
            if (!hasContextComposer) missing.Add(nameof(CognitiveContextComposer));
            if (!hasRunDriver) missing.Add(nameof(CognitiveRunDriver));
            if (missing.Count > 0)
            {
                error = "Cognition is enabled, but the Agent root is missing: " + string.Join(", ", missing) + ".";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
