using EmbodiedNPC.Cognition;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class AgentFeatureProfileTests
    {
        [Test]
        public void Profile_ProjectsOnlyCognitionModuleDeclaration()
        {
            AgentFeatureProfile profile = ScriptableObject.CreateInstance<AgentFeatureProfile>();
            try
            {
                profile.Configure(true);

                AgentFeatureConfiguration configuration = profile.ToConfiguration();

                Assert.That(configuration.CognitionEnabled, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Binding_ValidatesDeclaredCompositionWithoutBuildingIt()
        {
            var root = new GameObject("Agent");
            AgentFeatureProfile disabled = ScriptableObject.CreateInstance<AgentFeatureProfile>();
            AgentFeatureProfile enabled = ScriptableObject.CreateInstance<AgentFeatureProfile>();
            try
            {
                disabled.Configure(false);
                enabled.Configure(true);
                AgentFeatureProfileBinding binding = root.AddComponent<AgentFeatureProfileBinding>();

                binding.Configure(disabled);
                Assert.That(binding.TryValidateConfiguration(out string disabledError), Is.True, disabledError);

                binding.Configure(enabled);
                Assert.That(binding.TryValidateConfiguration(out string enabledError), Is.False);
                Assert.That(enabledError, Does.Contain(nameof(AgentOrchestrator)));
                Assert.That(root.GetComponent<AgentOrchestrator>(), Is.Null,
                    "Validation must not dynamically build the cognition chain.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(disabled);
                Object.DestroyImmediate(enabled);
            }
        }
    }
}
