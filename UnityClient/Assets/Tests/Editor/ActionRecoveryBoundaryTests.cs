using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class ActionRecoveryBoundaryTests
    {
        private readonly List<GameObject> objects = new();
        private EntityRegistry registry;
        private WorldEntity actor;
        private WorldEntity cupOne;
        private WorldEntity cupTwo;
        private WorldEntity hiddenCup;

        [SetUp]
        public void SetUp()
        {
            registry = CreateObject("Registry").AddComponent<EntityRegistry>();
            actor = Entity("npc_01", EntityKind.Agent, Vector3.zero);
            cupOne = Pickupable("cup_01", new Vector3(5f, 0f, 0f));
            cupTwo = Pickupable("cup_02", new Vector3(2f, 0f, 0f));
            hiddenCup = Pickupable("cup_hidden", new Vector3(1f, 0f, 0f));
            SetField(registry, "entities", new List<WorldEntity> { actor, cupOne, cupTwo, hiddenCup });
            SetField(registry, "autoDiscoverSceneEntities", false);
            registry.RebuildRegistry();
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = objects.Count - 1; index >= 0; index--)
                if (objects[index] != null) UnityEngine.Object.DestroyImmediate(objects[index]);
            objects.Clear();
        }

        [Test]
        public void BuiltInRegistrations_ComposeCandidateAndRecoveryPolicies()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration bring), Is.True);
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);

            Assert.That(bring.CandidateProvider, Is.TypeOf<BringObjectCandidateProvider>());
            Assert.That(move.CandidateProvider, Is.TypeOf<MoveToEntityCandidateProvider>());
            Assert.That(move.RecoveryPolicy, Is.SameAs(bring.RecoveryPolicy));
            Assert.That(bring.RecoveryPolicy, Is.TypeOf<AuthorizedReselectionRecoveryPolicy>());
        }

        [Test]
        public void CandidateProvider_IntersectsAuthorizationObserverLocalAndCurrentValidity()
        {
            hiddenCup.enabled = false;
            ExecutionAuthorization authorization = Authorization(ActionSelectionMode.Any,
                "cup_01", "cup_02", "cup_hidden");
            ActionCandidateContext context = Context(authorization,
                new[] { "cup_02", "cup_hidden", "not_authorized" });
            ActionRegistration registration = Registration("bring_object");

            IReadOnlyList<WorldEntity> candidates = registration.CandidateProvider.GetCandidates(
                registration.Descriptor, context);

            Assert.That(candidates.Select(candidate => candidate.EntityId), Is.EqualTo(new[] { "cup_02" }));
        }

        [Test]
        public void Exact_NeverOffersAnotherVisibleEntity()
        {
            ActionRegistration registration = Registration("bring_object");
            ExecutionAuthorization authorization = Authorization(ActionSelectionMode.Exact, "cup_01");
            ActionCandidateContext visibleContext = Context(authorization, new[] { "cup_01", "cup_02" });

            ActionRecoveryDecision retry = registration.RecoveryPolicy.Decide("cup_01",
                registration.Descriptor, registration.CandidateProvider, visibleContext);
            ActionRecoveryDecision confirmation = registration.RecoveryPolicy.Decide("cup_01",
                registration.Descriptor, registration.CandidateProvider,
                Context(authorization, new[] { "cup_02" }));

            Assert.That(retry.Disposition, Is.EqualTo(ActionRecoveryDisposition.RetryOriginal));
            Assert.That(retry.Candidates, Is.Empty);
            Assert.That(confirmation.Disposition, Is.EqualTo(ActionRecoveryDisposition.RequireConfirmation));
            Assert.That(confirmation.Candidates, Is.Empty);
        }

        [Test]
        public void Any_OffersOnlyAuthorizedObserverLocalAlternatives()
        {
            ActionRegistration registration = Registration("bring_object");
            ExecutionAuthorization authorization = Authorization(ActionSelectionMode.Any,
                "cup_01", "cup_02", "cup_hidden");

            ActionRecoveryDecision decision = registration.RecoveryPolicy.Decide("cup_01",
                registration.Descriptor, registration.CandidateProvider,
                Context(authorization, new[] { "cup_01", "cup_02" }));

            Assert.That(decision.Disposition, Is.EqualTo(ActionRecoveryDisposition.OfferAlternatives));
            Assert.That(decision.Candidates.Select(candidate => candidate.EntityId),
                Is.EqualTo(new[] { "cup_02" }));
        }

        [Test]
        public void Nearest_UsesAuthoritativeUnityPositionsAndStableIdTieBreak()
        {
            ActionRegistration registration = Registration("move_to_entity");
            ExecutionAuthorization authorization = Authorization(ActionSelectionMode.Nearest,
                "cup_01", "cup_02", "cup_hidden", actionId: "move_to_entity");
            cupTwo.transform.position = new Vector3(1f, 0f, 0f);
            hiddenCup.transform.position = new Vector3(-1f, 0f, 0f);

            ActionRecoveryDecision decision = registration.RecoveryPolicy.Decide("cup_01",
                registration.Descriptor, registration.CandidateProvider,
                Context(authorization, new[] { "cup_01", "cup_02", "cup_hidden" }));

            Assert.That(decision.Candidates, Has.Count.EqualTo(1));
            Assert.That(decision.Candidates[0].EntityId, Is.EqualTo("cup_02"));
        }

        [Test]
        public void Random_IsRepeatableForExplicitSeedAndStaysAuthorized()
        {
            ActionRegistration registration = Registration("move_to_entity");
            ExecutionAuthorization authorization = Authorization(ActionSelectionMode.Random,
                "cup_01", "cup_02", "cup_hidden", actionId: "move_to_entity");
            ActionCandidateContext context = Context(authorization,
                new[] { "cup_01", "cup_02", "cup_hidden" }, seed: 7123);

            ActionRecoveryDecision first = registration.RecoveryPolicy.Decide("cup_01",
                registration.Descriptor, registration.CandidateProvider, context);
            ActionRecoveryDecision replay = registration.RecoveryPolicy.Decide("cup_01",
                registration.Descriptor, registration.CandidateProvider, context);

            Assert.That(first.Candidates, Has.Count.EqualTo(1));
            Assert.That(replay.Candidates[0].EntityId, Is.EqualTo(first.Candidates[0].EntityId));
            Assert.That(authorization.Authorizes("move_to_entity", first.Candidates[0].EntityId), Is.True);
            Assert.That(first.Candidates[0].EntityId, Is.Not.EqualTo("cup_01"));
        }

        [Test]
        public void TaskFailureAlternatives_DoNotDiscoverGloballyRegisteredEntity()
        {
            ActionRegistration registration = Registration("bring_object");
            ExecutionAuthorization authorization = Authorization(ActionSelectionMode.Any, "cup_01", "cup_02");
            GoalProposal failedGoal = new()
            {
                type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01",
            };

            TaskAlternative[] alternatives = TaskFailurePolicy.BuildAlternatives(registration,
                Context(authorization, new[] { "cup_01", "cup_02", "cup_hidden" }), failedGoal);

            Assert.That(alternatives.Select(value => value.goal.target_entity_id), Is.EqualTo(new[] { "cup_02" }));
            Assert.That(alternatives.Select(value => value.goal.target_entity_id), Does.Not.Contain("cup_hidden"));
        }

        [Test]
        public void TaskFailureAlternatives_ExactAuthorizationNeverOffersReplacement()
        {
            ActionRegistration registration = Registration("bring_object");
            ExecutionAuthorization authorization = Authorization(ActionSelectionMode.Exact, "cup_01");
            GoalProposal failedGoal = new()
            {
                type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01",
            };

            TaskAlternative[] alternatives = TaskFailurePolicy.BuildAlternatives(registration,
                Context(authorization, new[] { "cup_01", "cup_02", "cup_hidden" }), failedGoal);

            Assert.That(alternatives, Is.Empty);
        }

        private ActionRegistration Registration(string actionId)
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet(actionId, out ActionRegistration registration), Is.True);
            return registration;
        }

        private ExecutionAuthorization Authorization(ActionSelectionMode mode, string first,
            string second = null, string third = null, string actionId = "bring_object") => new(
            actionId, mode, new[] { first, second, third }.Where(value => value != null),
            ActionConfirmationPolicy.OnRebind, 0, 0);

        private ActionCandidateContext Context(ExecutionAuthorization authorization,
            IEnumerable<string> observerLocalIds, int seed = 17) =>
            new(authorization, observerLocalIds, registry, actor, seed);

        private WorldEntity Pickupable(string id, Vector3 position)
        {
            WorldEntity entity = Entity(id, EntityKind.Object, position);
            entity.gameObject.AddComponent<HeldItemState>();
            entity.gameObject.AddComponent<InteractableEntity>();
            InteractionAnchorProvider anchors = entity.gameObject.AddComponent<InteractionAnchorProvider>();
            SetField(anchors, "pickupApproachPoint", Child(entity.transform, "Approach"));
            SetField(anchors, "grabPoint", Child(entity.transform, "Grab"));
            return entity;
        }

        private WorldEntity Entity(string id, EntityKind kind, Vector3 position)
        {
            WorldEntity entity = CreateObject(id).AddComponent<WorldEntity>();
            entity.Configure(id, $"test.{id}", kind,
                kind == EntityKind.Agent ? AgentRole.Npc : AgentRole.None);
            entity.transform.position = position;
            return entity;
        }

        private Transform Child(Transform parent, string name)
        {
            GameObject child = CreateObject(name);
            child.transform.SetParent(parent, false);
            return child.transform;
        }

        private GameObject CreateObject(string name)
        {
            GameObject value = new(name);
            objects.Add(value);
            return value;
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
    }
}
