using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Engine-neutral projection of the runtime modules declared for one Agent.</summary>
    public sealed class AgentFeatureConfiguration
    {
        public AgentFeatureConfiguration(bool cognitionEnabled)
        {
            CognitionEnabled = cognitionEnabled;
        }

        public bool CognitionEnabled { get; }
    }

    /// <summary>
    /// Minimal authoring declaration for optional Agent runtime modules.
    /// Concrete action permissions, identity and runtime state remain in their existing owners.
    /// </summary>
    [CreateAssetMenu(fileName = "AgentFeatureProfile", menuName = "EmbodiedNPC/Cognition/Agent Feature Profile")]
    public sealed class AgentFeatureProfile : ScriptableObject
    {
        [SerializeField] private bool cognitionEnabled;

        public bool CognitionEnabled => cognitionEnabled;

        public void Configure(bool enableCognition) => cognitionEnabled = enableCognition;

        public AgentFeatureConfiguration ToConfiguration() =>
            new AgentFeatureConfiguration(cognitionEnabled);
    }
}
