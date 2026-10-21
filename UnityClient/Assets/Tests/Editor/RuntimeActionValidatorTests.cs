using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Tests
{
    public sealed class RuntimeActionValidatorTests
    {
        private readonly List<GameObject> objects = new();
        private WorldEntity actor;
        private WorldEntity target;
        private WorldEntity destination;
        private WorldEntity heldItem;
        private HeldItemState targetState;
        private HeldItemState otherState;
        private InteractionAnchorProvider targetAnchors;
        private InteractionAnchorProvider destinationAnchors;
        private SafeDropZone dropZone;
        private EntityRegistry registry;
        private ResourceReservationService reservations;
        private WorldStateService worldState;
        private FakeNavigationProbe navigation;
        private FakePerceptionProbe perception;
        private ResolvedBringObjectGoal goal;
        private Transform hand;

        [SetUp]
        public void SetUp()
        {
            actor = Entity("npc_01", EntityKind.Agent);
            target = Entity("cup_01", EntityKind.Object);
            destination = Entity("player_01", EntityKind.Agent);
            heldItem = Entity("book_01", EntityKind.Object);
            targetState = target.gameObject.AddComponent<HeldItemState>();
            otherState = heldItem.gameObject.AddComponent<HeldItemState>();
            target.gameObject.AddComponent<BoxCollider>();
            target.gameObject.AddComponent<Rigidbody>();
            heldItem.gameObject.AddComponent<BoxCollider>();
            heldItem.gameObject.AddComponent<Rigidbody>();
            target.gameObject.AddComponent<InteractableEntity>();
            InteractableEntity receiver = destination.gameObject.AddComponent<InteractableEntity>();
            SetField(receiver, "supportsPickUp", false);
            SetField(receiver, "supportsGive", true);

            targetAnchors = target.gameObject.AddComponent<InteractionAnchorProvider>();
            Transform pickup = Child(target.transform, "Pickup", Vector3.zero);
            Transform grab = Child(target.transform, "Grab", Vector3.zero);
            SetField(targetAnchors, "pickupApproachPoint", pickup);
            SetField(targetAnchors, "grabPoint", grab);
            destinationAnchors = destination.gameObject.AddComponent<InteractionAnchorProvider>();
            Transform receive = Child(destination.transform, "Receive", Vector3.zero);
            Transform playerHold = Child(destination.transform, "PlayerHold", Vector3.zero);
            SetField(destinationAnchors, "receivePoint", receive);
            SetField(destinationAnchors, "holdPoint", playerHold);

            WorldEntity zone = Entity("safe_drop_zone_01", EntityKind.Environment);
            dropZone = zone.gameObject.AddComponent<SafeDropZone>();
            SetField(dropZone, "zone", zone);
            SetField(dropZone, "approachPoint", Child(zone.transform, "Approach", Vector3.zero));
            SetField(dropZone, "placementPoint", Child(zone.transform, "Placement", Vector3.up * 0.1f));

            reservations = CreateObject("Reservations").AddComponent<ResourceReservationService>();
            worldState = CreateObject("WorldState").AddComponent<WorldStateService>();
            registry = CreateObject("Registry").AddComponent<EntityRegistry>();
            SetField(registry, "autoDiscoverSceneEntities", false);
            SetField(registry, "entities", new List<WorldEntity>
                { actor, target, destination, heldItem, zone });
            registry.RebuildRegistry();
            navigation = new FakeNavigationProbe();
            perception = new FakePerceptionProbe();
            goal = new ResolvedBringObjectGoal(actor, target, destination, targetState,
                targetAnchors, destinationAnchors, worldState.WorldRevision);
            hand = Child(actor.transform, "Hand", Vector3.up);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            objects.Clear();
        }

        [Test]
        public void WalkTo_UnrelatedRevisionChangeStaysReady_BusyFactNeedsReplan()
        {
            BringObjectBindings bindings = new(actor.EntityId, target.EntityId, destination.EntityId);
            RuntimeActionValidationContext ready = Context(PlannerActionType.WalkTo, bindings, 99, Facts(
                (PlanningFact.Busy(actor.EntityId), PlanningFact.False),
                (PlanningFact.Reachable(target.EntityId), PlanningFact.True)));

            RuntimeActionValidationResult readyResult = Validator().Validate(ready);

            Assert.That(readyResult.Decision, Is.EqualTo(RuntimeActionDecision.Ready));
            navigation.IsBusy = true;
            RuntimeActionValidationResult changed = Validator().Validate(ready);
            Assert.That(changed.Decision, Is.EqualTo(RuntimeActionDecision.NeedsReplan));
            Assert.That(changed.FailureCode, Is.EqualTo("ACTION_PRECONDITION_CHANGED"));
        }

        [Test]
        public void PickUp_ValidLeaseIsReady_WrongLeaseNeedsReplan()
        {
            BringObjectBindings bindings = new(actor.EntityId, target.EntityId, destination.EntityId);
            Assert.That(reservations.TryAcquire(target.EntityId, actor.EntityId, "trace", out ReservationLeaseSnapshot lease), Is.True);
            RuntimeActionValidationContext context = Context(PlannerActionType.PickUp, bindings, 0, Facts(
                (PlanningFact.Hand(actor.EntityId), PlanningFact.Free),
                (PlanningFact.Holder(target.EntityId), PlanningFact.None)), lease.ReservationId);

            Assert.That(Validator().Validate(context).Decision, Is.EqualTo(RuntimeActionDecision.Ready));
            RuntimeActionValidationContext wrongLease = Context(PlannerActionType.PickUp, bindings, 0,
                context.CurrentInput.Snapshot.State.Values.Select(pair => (pair.Key, pair.Value)), "lease-wrong");
            RuntimeActionValidationResult result = Validator().Validate(wrongLease);
            Assert.That(result.Decision, Is.EqualTo(RuntimeActionDecision.NeedsReplan));
            Assert.That(result.FailureCode, Is.EqualTo("TARGET_LEASE_INVALID"));
        }

        [Test]
        public void PutDown_ValidBindingIsReady_OccupiedZoneNeedsReplan()
        {
            Pickup(heldItem, otherState);
            BringObjectBindings bindings = new(actor.EntityId, target.EntityId, destination.EntityId,
                heldItem.EntityId, dropZoneId: dropZone.Zone.EntityId);
            RuntimeActionValidationContext context = Context(PlannerActionType.PutDown, bindings, 0, Facts(
                (PlanningFact.Hand(actor.EntityId), heldItem.EntityId),
                (PlanningFact.Holder(heldItem.EntityId), actor.EntityId),
                (PlanningFact.Available(dropZone.Zone.EntityId), PlanningFact.True),
                (PlanningFact.Reachable(dropZone.Zone.EntityId), PlanningFact.True)), dropZone: dropZone);

            Assert.That(Validator().Validate(context).Decision, Is.EqualTo(RuntimeActionDecision.Ready));
            SetField(dropZone, "occupyingItemId", "other_item");
            RuntimeActionValidationResult result = Validator().Validate(context);
            Assert.That(result.Decision, Is.EqualTo(RuntimeActionDecision.NeedsReplan));
            Assert.That(result.FailureCode, Is.EqualTo("DROP_ZONE_OCCUPIED"));
        }

        [Test]
        public void Give_HeldTargetAndLeaseIsReady_OutOfRangeNeedsReplan()
        {
            Pickup(target, targetState);
            actor.transform.position = destinationAnchors.ReceivePoint.position;
            BringObjectBindings bindings = new(actor.EntityId, target.EntityId, destination.EntityId, target.EntityId);
            Assert.That(reservations.TryAcquire(target.EntityId, actor.EntityId, "trace", out ReservationLeaseSnapshot lease), Is.True);
            RuntimeActionValidationContext context = Context(PlannerActionType.Give, bindings, 0, Facts(
                (PlanningFact.Hand(actor.EntityId), target.EntityId),
                (PlanningFact.Holder(target.EntityId), actor.EntityId)), lease.ReservationId);

            Assert.That(Validator().Validate(context).Decision, Is.EqualTo(RuntimeActionDecision.Ready));
            actor.transform.position = Vector3.one * 50f;
            RuntimeActionValidationResult result = Validator().Validate(context);
            Assert.That(result.Decision, Is.EqualTo(RuntimeActionDecision.NeedsReplan));
            Assert.That(result.FailureCode, Is.EqualTo("ACTION_OUT_OF_RANGE"));
        }

        [Test]
        public void StaleActionBindingAborts()
        {
            BringObjectBindings bindings = new(actor.EntityId, target.EntityId, destination.EntityId);
            RuntimeActionValidationContext original = Context(PlannerActionType.PickUp, bindings, 0, Facts(
                (PlanningFact.Hand(actor.EntityId), PlanningFact.Free),
                (PlanningFact.Holder(target.EntityId), PlanningFact.None)));
            ActionInstance stale = new(new ActionDefinition("pick_stale", PlannerActionType.PickUp, 1, "PickUp"),
                Pairs(("target", "missing_item")), System.Array.Empty<KeyValuePair<string, string>>(),
                System.Array.Empty<KeyValuePair<string, string>>());
            PlanResult stalePlan = new(PlanSearchStatus.Found, original.Plan.Snapshot, original.Plan.Goal,
                new[] { stale }, 1, 1);

            RuntimeActionValidationResult result = Validator().Validate(new RuntimeActionValidationContext(
                stalePlan, stale, original.Preparation, string.Empty));

            Assert.That(result.Decision, Is.EqualTo(RuntimeActionDecision.Abort));
            Assert.That(result.FailureCode, Is.EqualTo("ACTION_BINDING_STALE"));
        }

        [Test]
        public void Open_ValidClosedDoorIsReady_LockedDoorNeedsReplan()
        {
            GameObject doorObject = CreateObject("Door");
            doorObject.SetActive(false);
            WorldEntity doorEntity = doorObject.AddComponent<WorldEntity>();
            doorEntity.Configure("door_01", "test.door_01", EntityKind.Environment);
            Transform panel = Child(doorObject.transform, "Panel", Vector3.zero);
            NavMeshObstacle obstacle = panel.gameObject.AddComponent<NavMeshObstacle>();
            Transform approach = Child(doorObject.transform, "Approach", Vector3.zero);
            DoorState door = doorObject.AddComponent<DoorState>();
            SetField(door, "door", doorEntity);
            SetField(door, "movingPanel", panel);
            SetField(door, "blockingObstacle", obstacle);
            SetField(door, "approachPoint", approach);
            doorObject.SetActive(true);

            AccessConstraint constraint = target.gameObject.AddComponent<AccessConstraint>();
            SetField(constraint, "target", target);
            SetField(constraint, "blockingDoor", door);
            SetField(registry, "entities", new List<WorldEntity>
                { actor, target, destination, heldItem, dropZone.Zone, doorEntity });
            registry.RebuildRegistry();
            actor.transform.position = approach.position;

            BringObjectBindings bindings = new(actor.EntityId, target.EntityId, destination.EntityId,
                blockingDoorId: doorEntity.EntityId);
            RuntimeActionValidationContext context = Context(PlannerActionType.Open, bindings, 0, Facts(
                (PlanningFact.Open(doorEntity.EntityId), PlanningFact.False),
                (PlanningFact.Locked(doorEntity.EntityId), PlanningFact.False)));

            Assert.That(Validator().Validate(context).Decision, Is.EqualTo(RuntimeActionDecision.Ready));

            SetField(door, "locked", true);
            RuntimeActionValidationResult locked = Validator().Validate(context);
            Assert.That(locked.Decision, Is.EqualTo(RuntimeActionDecision.NeedsReplan));
            Assert.That(locked.FailureCode, Is.EqualTo("DOOR_LOCKED"));
        }

        private RuntimeActionValidator Validator() =>
            new(registry, reservations, navigation, perception, 0.85f);

        private RuntimeActionValidationContext Context(PlannerActionType type, BringObjectBindings bindings,
            int worldRevision, IEnumerable<(string key, string value)> facts, string lease = "", SafeDropZone dropZone = null)
        {
            PlanningGoal planningGoal = BringObjectActionCatalog.CreateGoal("goal_test", bindings);
            PlannerSnapshot planned = new("planned", planningGoal.GoalId, 0, 0, new PlanningState(Pairs(facts.ToArray())));
            List<ActionInstance> actions = BringObjectActionCatalog.CreateActions(bindings).ToList();
            ActionInstance action = actions.First(candidate => candidate.Definition.ActionType == type);
            PlanResult plan = new(PlanSearchStatus.Found, planned, planningGoal, actions, actions.Sum(x => x.ComputedCost), 1);
            PlannerSnapshot current = new("current", planningGoal.GoalId, worldRevision, perception.PerceptionRevision,
                new PlanningState(Pairs(facts.ToArray())));
            ValidatedActionBindings validatedBindings = new(goal.Actor, goal.Target, goal.Destination,
                goal.TargetHeldState, goal.TargetAnchors, goal.DestinationAnchors,
                heldItemId: bindings.HeldItemId,
                blockingDoorId: bindings.BlockingDoorId,
                dropZoneId: bindings.DropZoneId,
                requiresTargetPerception: true);
            ActionExecutionPreparation preparation = new(new BringObjectPlanningInput(current, bindings),
                validatedBindings, ActionLeaseRequirement.Exclusive(goal.Target),
                refreshBeforeActionValidation: true);
            return new RuntimeActionValidationContext(plan, action,
                preparation, lease, dropZone);
        }

        private void Pickup(WorldEntity item, HeldItemState state)
        {
            InteractionAnchorProvider anchors = item == target ? targetAnchors : item.gameObject.AddComponent<InteractionAnchorProvider>();
            Transform grab = item == target ? targetAnchors.GrabPoint : Child(item.transform, "Grab", Vector3.zero);
            if (item != target) SetField(anchors, "grabPoint", grab);
            Assert.That(worldState.TryPickUp("pickup", "trace", actor, item, state, hand, grab).Status,
                Is.EqualTo(SkillStatus.Succeeded));
        }

        private WorldEntity Entity(string id, EntityKind kind)
        {
            WorldEntity entity = CreateObject(id).AddComponent<WorldEntity>();
            AgentRole role = kind == EntityKind.Agent
                ? id.Contains("player") ? AgentRole.Player : AgentRole.Npc
                : AgentRole.None;
            entity.Configure(id, $"test.{id}", kind, role);
            return entity;
        }

        private GameObject CreateObject(string name)
        {
            GameObject value = new(name);
            objects.Add(value);
            return value;
        }

        private Transform Child(Transform parent, string name, Vector3 localPosition)
        {
            Transform child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = localPosition;
            return child;
        }

        private static void SetField<T>(object targetObject, string fieldName, T value) =>
            targetObject.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(targetObject, value);
        private static IEnumerable<(string key, string value)> Facts(params (string key, string value)[] facts) => facts;
        private static IEnumerable<KeyValuePair<string, string>> Pairs(params (string key, string value)[] facts) =>
            facts.Select(fact => new KeyValuePair<string, string>(fact.key, fact.value));

        private sealed class FakeNavigationProbe : IPlannerNavigationProbe
        {
            public bool IsReady { get; set; } = true;
            public bool IsBusy { get; set; }
            public bool HasCompletePath(Vector3 origin, Vector3 destination) => true;
        }

        private sealed class FakePerceptionProbe : IPlannerPerceptionProbe
        {
            public bool IsReady => true;
            public int PerceptionRevision => 5;
            public bool IsKnownAndNotForgotten(string entityId) => entityId == "cup_01";
        }
    }
}
