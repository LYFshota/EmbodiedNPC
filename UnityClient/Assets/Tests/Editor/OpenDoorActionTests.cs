using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Tests
{
    public sealed class OpenDoorActionTests
    {
        private readonly List<GameObject> objects = new();
        private EntityRegistry registry;
        private WorldStateService worldState;
        private WorldEntity actor;
        private WorldEntity doorEntity;
        private DoorState door;

        [SetUp]
        public void SetUp()
        {
            actor = Entity("npc_01", EntityKind.Agent);
            doorEntity = CreateDoor("door_01", false);
            registry = CreateObject("Registry").AddComponent<EntityRegistry>();
            SetField(registry, "entities", new List<WorldEntity> { actor, doorEntity });
            SetField(registry, "autoDiscoverSceneEntities", false);
            registry.RebuildRegistry();
            worldState = CreateObject("WorldState").AddComponent<WorldStateService>();
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = objects.Count - 1; index >= 0; index--)
                if (objects[index] != null) Object.DestroyImmediate(objects[index]);
            objects.Clear();
        }

        [Test]
        public void Binder_UsesSubmittedStableTargetAndRejectsUndeclaredDestination()
        {
            ActionRegistration registration = Registration();
            ActionAdmissionContext context = new(registry, null, worldState, actor, null);

            ActionBindingResult bound = registration.Binder.Bind(new GoalProposal
            {
                type = "open_door", target_entity_id = "door_01",
            }, registration.Descriptor, context);
            ActionBindingResult rejected = registration.Binder.Bind(new GoalProposal
            {
                type = "open_door", target_entity_id = "door_01", destination_entity_id = "npc_01",
            }, registration.Descriptor, context);

            Assert.That(bound.IsBound, Is.True);
            Assert.That(bound.Target, Is.SameAs(doorEntity));
            Assert.That(rejected.FailureCode, Is.EqualTo(GoalValidationFailureCode.InvalidEntityId));
        }

        [Test]
        public void Admission_DistinguishesAlreadyOpenLockedAndInvalidDoor()
        {
            ActionRegistration registration = Registration();
            GoalProposal proposal = new() { type = "open_door", target_entity_id = "door_01" };
            ActionAdmissionContext context = new(registry, null, worldState, actor, null);

            SetField(door, "locked", true);
            GoalValidationResult locked = registration.AdmissionPolicy.Validate(
                proposal, registration.Descriptor, registration.Binder, context);
            SetField(door, "locked", false);
            Assert.That(worldState.TryOpen("open:door_01", "trace", actor, doorEntity, door).Status,
                Is.EqualTo(SkillStatus.Succeeded));
            GoalValidationResult opened = registration.AdmissionPolicy.Validate(
                proposal, registration.Descriptor, registration.Binder, context);
            GoalValidationResult invalid = registration.AdmissionPolicy.Validate(
                new GoalProposal { type = "open_door", target_entity_id = "npc_01" },
                registration.Descriptor, registration.Binder, context);

            Assert.That(locked.FailureCode, Is.EqualTo(GoalValidationFailureCode.TargetLocked));
            Assert.That(opened.FailureCode, Is.EqualTo(GoalValidationFailureCode.TargetAlreadySatisfied));
            Assert.That(invalid.FailureCode, Is.EqualTo(GoalValidationFailureCode.TargetNotDoor));
        }

        [Test]
        public void PlanFactory_CreatesDeterministicWalkThenOpenPlan()
        {
            const string goalId = "goal_open_door";
            PlannerSnapshot snapshot = new("snapshot_open_door", goalId, 3, 4,
                new PlanningState(Pairs(
                    (PlanningFact.Location(actor.EntityId), PlanningFact.Current),
                    (PlanningFact.Busy(actor.EntityId), PlanningFact.False),
                    (PlanningFact.Reachable(doorEntity.EntityId), PlanningFact.True),
                    (PlanningFact.Open(doorEntity.EntityId), PlanningFact.False),
                    (PlanningFact.Locked(doorEntity.EntityId), PlanningFact.False))));
            OpenDoorPlanningInput input = new(snapshot, actor.EntityId, doorEntity.EntityId);

            PlanResult first = Registration().PlanFactory.CreatePlan(new UniformCostPlanner(), goalId, input);
            PlanResult replay = Registration().PlanFactory.CreatePlan(new UniformCostPlanner(), goalId, input);

            Assert.That(first.Status, Is.EqualTo(PlanSearchStatus.Found));
            Assert.That(first.Actions.Select(action => action.Definition.ActionType),
                Is.EqualTo(new[] { PlannerActionType.WalkTo, PlannerActionType.Open }));
            Assert.That(first.Actions[0].Bindings["destination"], Is.EqualTo("door_01"));
            Assert.That(first.Actions[1].Bindings["door"], Is.EqualTo("door_01"));
            Assert.That(first.PlanHash, Is.EqualTo(replay.PlanHash));
        }

        [Test]
        public void CandidateAndRecovery_StayAuthorizedObserverLocalAndExact()
        {
            WorldEntity otherDoor = CreateDoor("door_02", false);
            SetField(registry, "entities", new List<WorldEntity> { actor, doorEntity, otherDoor });
            registry.RebuildRegistry();
            ActionRegistration registration = Registration();
            ExecutionAuthorization exact = new("open_door", ActionSelectionMode.Exact,
                new[] { "door_01" }, ActionConfirmationPolicy.OnRebind, 0, 0);
            ActionCandidateContext hidden = new(exact, new[] { "door_02" }, registry, actor, 11);

            IReadOnlyList<WorldEntity> candidates = registration.CandidateProvider.GetCandidates(
                registration.Descriptor, hidden);
            ActionRecoveryDecision decision = registration.RecoveryPolicy.Decide("door_01",
                registration.Descriptor, registration.CandidateProvider, hidden);

            Assert.That(candidates, Is.Empty);
            Assert.That(decision.Disposition, Is.EqualTo(ActionRecoveryDisposition.RequireConfirmation));
            Assert.That(decision.Candidates, Is.Empty);
        }

        [TestCase(ActionExecutionStatus.Accepted, "", ActionExecutionStatus.Accepted, "好的，我去开门。")]
        [TestCase(ActionExecutionStatus.Recovering, "", ActionExecutionStatus.Recovering, "我正在重新确认门的位置和状态。")]
        [TestCase(ActionExecutionStatus.Succeeded, "", ActionExecutionStatus.Succeeded, "门已经打开了。")]
        [TestCase(ActionExecutionStatus.Cancelled, "CANCELLED_BY_REQUEST", ActionExecutionStatus.Cancelled,
            "我已经停止开门。")]
        [TestCase(ActionExecutionStatus.TimedOut, "GOAL_DEADLINE_EXCEEDED", ActionExecutionStatus.TimedOut,
            "开门操作超时了。")]
        [TestCase(ActionExecutionStatus.Failed, "TARGET_UNREACHABLE", ActionExecutionStatus.Failed,
            "我无法走到门前。")]
        [TestCase(ActionExecutionStatus.Rejected, "TargetLocked", ActionExecutionStatus.Rejected,
            "门被锁住了，我不能打开它。")]
        [TestCase(ActionExecutionStatus.RequiresConfirmation, "ACTION_BINDING_STALE",
            ActionExecutionStatus.RequiresConfirmation, "这扇门已失效，需要你重新确认目标。")]
        [TestCase(ActionExecutionStatus.Clarification, "TARGET_AMBIGUOUS", ActionExecutionStatus.Clarification,
            "请明确要打开哪扇门。")]
        public void Presentation_MapsExistingLifecycleStatuses(ActionExecutionStatus inputStatus,
            string code, ActionExecutionStatus expectedStatus, string expectedMessage)
        {
            ActionExecutionResult result = Result(inputStatus, code);
            ActionPresentationInfo presentation = Registration().PresentationPolicy.Present(result);

            Assert.That(presentation.Status, Is.EqualTo(expectedStatus));
            Assert.That(presentation.Message, Is.EqualTo(expectedMessage));
        }

        [Test]
        public void Presentation_MapsAlreadySatisfiedAdmissionWithoutPretendingToExecute()
        {
            ActionPresentationInfo presentation = Registration().PresentationPolicy.Present(
                Result(ActionExecutionStatus.Rejected, "TargetAlreadySatisfied"));

            Assert.That(presentation.Status, Is.EqualTo(ActionExecutionStatus.Accepted));
            Assert.That(presentation.Code, Is.EqualTo("TargetAlreadySatisfied"));
            Assert.That(presentation.Message, Is.EqualTo("门已经打开了。"));
        }

        [Test]
        public void OpenRunner_UsesWorldStateTryOpenAsAuthority()
        {
            ActionDefinition definition = new("open_door.open", PlannerActionType.Open, 1, "Open");
            ActionInstance action = new(definition, Pairs(("actor", "npc_01"), ("door", "door_01")),
                System.Array.Empty<KeyValuePair<string, string>>(),
                System.Array.Empty<KeyValuePair<string, string>>());
            PlanActionExecutionContext context = new(action,
                new ValidatedActionBindings(actor, doorEntity), string.Empty, "trace-open", null,
                registry, worldState, null, null, null);

            PlanActionRunResult result = new OpenActionRunner().Start(context);

            Assert.That(result.Status, Is.EqualTo(PlanActionRunStatus.Succeeded));
            Assert.That(door.IsOpen, Is.True);
            Assert.That(worldState.WorldRevision, Is.EqualTo(1));
            Assert.That(worldState.EventHistory.Single().SkillType, Is.EqualTo("Open"));
        }
        private ActionRegistration Registration()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("open_door", out ActionRegistration registration),
                Is.True);
            return registration;
        }

        private ActionExecutionResult Result(ActionExecutionStatus status, string code) => new(
            "open_door", "proposal_01", "trace_01", status, code,
            new Dictionary<string, string> { ["target"] = "door_01" }, 2, 2);

        private WorldEntity CreateDoor(string id, bool locked)
        {
            GameObject value = CreateObject(id);
            value.SetActive(false);
            WorldEntity entity = value.AddComponent<WorldEntity>();
            entity.Configure(id, $"test.{id}", EntityKind.Environment);
            Transform panel = Child(value.transform, "Panel");
            NavMeshObstacle obstacle = panel.gameObject.AddComponent<NavMeshObstacle>();
            Transform approach = Child(value.transform, "Approach");
            DoorState state = value.AddComponent<DoorState>();
            SetField(state, "door", entity);
            SetField(state, "movingPanel", panel);
            SetField(state, "blockingObstacle", obstacle);
            SetField(state, "approachPoint", approach);
            SetField(state, "locked", locked);
            value.SetActive(true);
            if (id == "door_01") door = state;
            return entity;
        }

        private WorldEntity Entity(string id, EntityKind kind)
        {
            WorldEntity entity = CreateObject(id).AddComponent<WorldEntity>();
            entity.Configure(id, $"test.{id}", kind,
                kind == EntityKind.Agent ? AgentRole.Npc : AgentRole.None);
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

        private static IEnumerable<KeyValuePair<string, string>> Pairs(
            params (string key, string value)[] facts) => facts.Select(fact =>
            new KeyValuePair<string, string>(fact.key, fact.value));

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
    }
}
