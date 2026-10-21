using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class CognitiveActionProposalTests
    {
        private readonly List<Object> objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object value in objects)
                if (value != null) Object.DestroyImmediate(value);
            objects.Clear();
        }

        [Test]
        public void MerchantProfile_ProjectsOnlyAllowedBringObjectDescriptor()
        {
            GameObject merchant = new("Merchant");
            objects.Add(merchant);
            AgentActionCapabilityProfile profile = merchant.AddComponent<AgentActionCapabilityProfile>();
            profile.Configure("bring_object");

            Assert.That(profile.TryBuildManifest(out CognitiveCapabilityManifestDto manifest), Is.True);
            Assert.That(manifest.available_actions.Select(action => action.action_id),
                Is.EqualTo(new[] { "bring_object" }));
            Assert.That(manifest.available_actions[0].bindings.Select(binding => binding.name),
                Is.EqualTo(new[] { "target", "destination" }));
            Assert.That(manifest.available_actions[0].bindings.All(binding =>
                binding.kind == "entity_ref" && binding.required), Is.True);
        }

        [Test]
        public void ValidatedActionPayload_MapsToLegacyGoalProposalShape()
        {
            CognitiveActionProposal proposal = Proposal();

            Assert.That(CognitiveActionProposalAdapter.TryMap(proposal, out GoalProposal goal), Is.True);
            Assert.That(goal.type, Is.EqualTo("bring_object"));
            Assert.That(goal.target_entity_id, Is.EqualTo("cup_01"));
            Assert.That(goal.destination_entity_id, Is.EqualTo("player_01"));
        }

        [Test]
        public void ExistingGoalValidator_ConsumesMappedProposalWithoutExecuting()
        {
            GameObject actor = new("Merchant");
            objects.Add(actor);
            GoalValidator validator = actor.AddComponent<GoalValidator>();
            Assert.That(CognitiveActionProposalAdapter.TryMap(Proposal(), out GoalProposal goal), Is.True);

            GoalValidationResult result = validator.ValidateAdmission(goal);

            Assert.That(result.IsApproved, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(GoalValidationFailureCode.EntityRegistryNotReady));
        }

        private static CognitiveActionProposal Proposal() => new()
        {
            action_id = "bring_object",
            bindings = new Dictionary<string, string>
            {
                ["target"] = "cup_01",
                ["destination"] = "player_01",
            },
        };
    }
}
