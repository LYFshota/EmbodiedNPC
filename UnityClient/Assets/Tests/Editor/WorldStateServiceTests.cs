using System.Reflection;
using System.Text.RegularExpressions;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.AI;

namespace EmbodiedNPC.Tests
{
    public sealed class WorldStateServiceTests
    {
        private WorldStateService service;
        private WorldEntity actor;
        private WorldEntity book;
        private WorldEntity player;
        private HeldItemState heldItem;
        private Transform hand;
        private Transform grabPoint;
        private Transform destinationPlacement;
        private Transform destinationParent;
        private SafeDropZone dropZone;

        [SetUp]
        public void SetUp()
        {
            service = new GameObject("WorldStateService_Test").AddComponent<WorldStateService>();
            actor = CreateEntity("npc_ybot_01", EntityKind.Agent);
            book = CreateEntity("book_01", EntityKind.Object);
            player = CreateEntity("player_01", EntityKind.Agent);
            player.gameObject.AddComponent<MeshRenderer>();

            book.gameObject.AddComponent<BoxCollider>();
            book.gameObject.AddComponent<Rigidbody>();
            book.gameObject.AddComponent<MeshRenderer>();
            book.gameObject.AddComponent<PerceivableEntity>();
            heldItem = book.gameObject.AddComponent<HeldItemState>();

            hand = new GameObject("Hand").transform;
            hand.SetPositionAndRotation(new Vector3(3f, 2f, -1f), Quaternion.Euler(15f, 70f, 5f));
            grabPoint = new GameObject("BookGrabPoint").transform;
            grabPoint.SetParent(book.transform, false);
            grabPoint.SetLocalPositionAndRotation(new Vector3(0.25f, 0f, 0f), Quaternion.Euler(0f, 30f, 0f));
            destinationPlacement = new GameObject("DestinationPlacement").transform;
            destinationParent = new GameObject("PlayerHoldPoint").transform;
            destinationParent.SetParent(player.transform, false);
            destinationPlacement.SetParent(destinationParent, false);
            destinationPlacement.SetPositionAndRotation(new Vector3(-2f, 1f, 4f), Quaternion.Euler(0f, 120f, 0f));

            WorldEntity zoneEntity = CreateEntity("safe_drop_zone_01", EntityKind.Environment);
            dropZone = zoneEntity.gameObject.AddComponent<SafeDropZone>();
            SetPrivateField(dropZone, "zone", zoneEntity);
            Transform approach = new GameObject("ApproachPoint").transform;
            approach.SetParent(zoneEntity.transform, false);
            Transform placement = new GameObject("PlacementPoint").transform;
            placement.SetParent(zoneEntity.transform, false);
            placement.SetPositionAndRotation(new Vector3(1f, 0.5f, 2f), Quaternion.Euler(0f, 40f, 0f));
            SetPrivateField(dropZone, "approachPoint", approach);
            SetPrivateField(dropZone, "placementPoint", placement);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(service.gameObject);
            Object.DestroyImmediate(actor.gameObject);
            Object.DestroyImmediate(book.gameObject);
            Object.DestroyImmediate(player.gameObject);
            Object.DestroyImmediate(hand.gameObject);
            Object.DestroyImmediate(dropZone.gameObject);
        }

        [Test]
        public void PickupTransaction_CommitsOwnershipVisualsAndRevision()
        {
            SkillResult result = service.TryPickUp("pickup_book", "trace-pickup", actor, book, heldItem, hand, grabPoint);

            Assert.That(result.Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(result.WorldRevisionBefore, Is.EqualTo(0));
            Assert.That(result.WorldRevisionAfter, Is.EqualTo(1));
            Assert.That(service.WorldRevision, Is.EqualTo(1));
            Assert.That(heldItem.HolderEntityId, Is.EqualTo(actor.EntityId));
            Assert.That(heldItem.transform.parent, Is.EqualTo(hand));
            Assert.That(Vector3.Distance(grabPoint.position, hand.position), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(grabPoint.rotation, hand.rotation), Is.LessThan(0.01f));
            Assert.That(heldItem.GetComponent<Collider>().enabled, Is.False);
            Assert.That(service.EventHistory.Count, Is.EqualTo(1));
            Assert.That(service.EventHistory[0].EventSequence, Is.EqualTo(1));
        }

        [Test]
        public void FailedTransaction_RecordsAttemptWithoutAdvancingRevision()
        {
            SkillResult result = service.TryPickUp("pickup_book", "trace-failed", actor, book, null, hand, grabPoint);

            Assert.That(result.Status, Is.EqualTo(SkillStatus.Failed));
            Assert.That(service.WorldRevision, Is.EqualTo(0));
            Assert.That(service.EventHistory.Count, Is.EqualTo(1));
            Assert.That(service.EventHistory[0].FailureCode, Is.EqualTo("REFERENCE_MISSING"));
        }

        [Test]
        public void TransferTransaction_CommitsDestinationOwnershipAndRevision()
        {
            service.TryPickUp("pickup_book", "trace-pickup", actor, book, heldItem, hand, grabPoint);
            string identity = book.EntityId;
            SkillResult result = service.TryTransferOwnership("give_book", "trace-give", actor, book, player, heldItem, destinationParent, destinationPlacement);

            Assert.That(result.Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(service.WorldRevision, Is.EqualTo(2));
            Assert.That(result.WorldRevisionBefore, Is.EqualTo(1));
            Assert.That(result.WorldRevisionAfter, Is.EqualTo(2));
            Assert.That(heldItem.HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(heldItem.transform.parent, Is.EqualTo(destinationParent));
            Assert.That(book.transform.position, Is.EqualTo(destinationPlacement.position));
            Assert.That(book.EntityId, Is.EqualTo(identity));
            Assert.That(book.gameObject.activeInHierarchy && book.isActiveAndEnabled && heldItem.isActiveAndEnabled, Is.True);
            Assert.That(book.GetComponent<Renderer>().enabled, Is.False);
            Assert.That(book.GetComponent<PerceivableEntity>().enabled, Is.False);
            Assert.That(heldItem.GetComponent<Collider>().enabled, Is.False);
            Assert.That(book.GetComponent<Rigidbody>().isKinematic, Is.True);
            Assert.That(player.GetComponent<Renderer>().enabled, Is.True, "Only the delivered item's hierarchy is hidden.");
            Assert.That(service.EventHistory.Count, Is.EqualTo(2));
        }

        [TestCase(EntityKind.Agent, AgentRole.Player)]
        [TestCase(EntityKind.Agent, AgentRole.Npc)]
        [TestCase(EntityKind.Agent, AgentRole.Companion)]
        [TestCase(EntityKind.Environment, AgentRole.None)]
        public void TransferTransaction_ReceiverKindControlsOnlyPresentation(EntityKind receiverKind, AgentRole receiverRole)
        {
            player.Configure(player.EntityId, player.DefinitionId, EntityKind.Agent, receiverRole);
            WorldEntity receiver = receiverKind == EntityKind.Agent ? player : dropZone.Zone;
            var child = new GameObject("ChildVisual");
            child.transform.SetParent(book.transform, false);
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.enabled = false;
            service.TryPickUp("pickup", "trace", actor, book, heldItem, hand, grabPoint);
            var result = service.TryTransferOwnership("give", "trace", actor, book, receiver, heldItem,
                destinationParent, destinationPlacement);
            Assert.That(result.Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(heldItem.HolderEntityId, Is.EqualTo(receiver.EntityId));
            Assert.That(service.WorldRevision, Is.EqualTo(2));
            bool stored = receiverKind == EntityKind.Agent;
            Assert.That(book.GetComponent<Renderer>().enabled, Is.EqualTo(!stored));
            Assert.That(book.GetComponent<PerceivableEntity>().enabled, Is.EqualTo(!stored));
            Assert.That(book.GetComponent<Collider>().enabled, Is.EqualTo(!stored));
            Assert.That(renderer.enabled, Is.False, "Previously disabled children must not be enabled by transfer.");
        }

        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        public void ItemVisualSnapshot_RestoresExactMixedPresentationAndPhysics(bool perceivableEnabled,
            bool colliderEnabled, bool kinematic)
        {
            book.transform.SetParent(hand, false);
            book.transform.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(10, 20, 30));
            var child = new GameObject("DisabledChildVisual");
            child.transform.SetParent(book.transform, false);
            var disabledRenderer = child.AddComponent<MeshRenderer>(); disabledRenderer.enabled = false;
            child.SetActive(false); // The snapshot must include inactive descendants as well.
            var renderer = book.GetComponent<Renderer>();
            var perceivable = book.GetComponent<PerceivableEntity>(); perceivable.enabled = perceivableEnabled;
            var collider = book.GetComponent<Collider>(); collider.enabled = colliderEnabled;
            var body = book.GetComponent<Rigidbody>(); body.isKinematic = kinematic;
            Vector3 position = book.transform.position; Quaternion rotation = book.transform.rotation;
            var snapshotType = typeof(WorldStateService).GetNestedType("ItemVisualState", BindingFlags.NonPublic);
            object snapshot = snapshotType.GetMethod("Capture").Invoke(null, new object[] { book });
            book.transform.SetParent(destinationParent, false);
            book.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            renderer.enabled = false; disabledRenderer.enabled = true;
            perceivable.enabled = !perceivableEnabled; collider.enabled = !colliderEnabled; body.isKinematic = !kinematic;
            snapshotType.GetMethod("Restore").Invoke(snapshot, new object[] { book });
            Assert.That(book.transform.parent, Is.SameAs(hand));
            Assert.That(Vector3.Distance(book.transform.position, position), Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(book.transform.rotation, rotation), Is.LessThan(.01f));
            Assert.That(renderer.enabled, Is.True); Assert.That(disabledRenderer.enabled, Is.False);
            Assert.That(perceivable.enabled, Is.EqualTo(perceivableEnabled));
            Assert.That(collider.enabled, Is.EqualTo(colliderEnabled)); Assert.That(body.isKinematic, Is.EqualTo(kinematic));
            Assert.That(service.WorldRevision, Is.Zero);
        }

        [Test]
        public void CommittedSkillResult_IsPublishedAfterEnteringAuthoritativeHistory()
        {
            SkillResult published = default;
            int publishCount = 0;
            service.SkillResultPublished += result => { published = result; publishCount++; };

            SkillResult committed = service.TryPickUp("pickup_book", "trace-publish", actor, book, heldItem, hand, grabPoint);

            Assert.That(publishCount, Is.EqualTo(1));
            Assert.That(published.EventSequence, Is.EqualTo(committed.EventSequence));
            Assert.That(service.EventHistory[0].EventSequence, Is.EqualTo(published.EventSequence));
            Assert.That(published.WorldRevisionAfter, Is.EqualTo(service.WorldRevision));
        }

        [Test]
        public void FaultySkillResultSubscriber_CannotBlockOtherSubscribersOrCommittedWorldTransaction()
        {
            bool healthySubscriberReceivedResult = false;
            service.SkillResultPublished += _ => throw new System.InvalidOperationException("test listener failure");
            service.SkillResultPublished += _ => healthySubscriberReceivedResult = true;
            LogAssert.Expect(LogType.Error, new Regex("SkillResult subscriber failed: InvalidOperationException"));

            SkillResult result = service.TryPickUp("pickup_book", "trace-listener-failure", actor, book, heldItem, hand, grabPoint);

            Assert.That(result.Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(service.WorldRevision, Is.EqualTo(1));
            Assert.That(service.EventHistory.Count, Is.EqualTo(1));
            Assert.That(healthySubscriberReceivedResult, Is.True);
        }

        [Test]
        public void PickupTransaction_RejectsGrabPointOutsideTargetHierarchy()
        {
            Transform invalidGrabPoint = new GameObject("InvalidExternalGrabPoint").transform;
            try
            {
                SkillResult result = service.TryPickUp("pickup_book", "trace-invalid-anchor", actor, book,
                    heldItem, hand, invalidGrabPoint);

                Assert.That(result.Status, Is.EqualTo(SkillStatus.Failed));
                Assert.That(result.FailureCode, Is.EqualTo("GRAB_POINT_NOT_ON_TARGET"));
                Assert.That(service.WorldRevision, Is.EqualTo(0));
                Assert.That(heldItem.IsHeld, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(invalidGrabPoint.gameObject);
            }
        }

        [Test]
        public void PutDownTransaction_CommitsEmptyHolderZoneOccupancyVisualsAndOneRevision()
        {
            service.TryPickUp("pickup_book", "trace-pickup", actor, book, heldItem, hand, grabPoint);

            SkillResult result = service.TryPutDown("putdown_book", "trace-putdown", actor, book, dropZone, heldItem);

            Assert.That(result.Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(result.SkillType, Is.EqualTo("PutDown"));
            Assert.That(result.DestinationEntityId, Is.EqualTo("safe_drop_zone_01"));
            Assert.That(result.WorldRevisionBefore, Is.EqualTo(1));
            Assert.That(result.WorldRevisionAfter, Is.EqualTo(2));
            Assert.That(service.WorldRevision, Is.EqualTo(2));
            Assert.That(heldItem.IsHeld, Is.False);
            Assert.That(dropZone.OccupyingItemId, Is.EqualTo("book_01"));
            Assert.That(book.transform.parent, Is.EqualTo(dropZone.transform));
            Assert.That(book.transform.position, Is.EqualTo(dropZone.PlacementPoint.position));
            Assert.That(Quaternion.Angle(book.transform.rotation, dropZone.PlacementPoint.rotation), Is.LessThan(0.01f));
            Assert.That(book.GetComponent<Collider>().enabled, Is.True);
            Assert.That(book.GetComponent<Rigidbody>().isKinematic, Is.True);
            Assert.That(service.EventHistory.Count, Is.EqualTo(2));
        }

        [Test]
        public void PickupFromZone_ReleasesOccupancyBeforePublishing_AndZoneCanBeReused()
        {
            service.TryPickUp("pickup", "trace", actor, book, heldItem, hand, grabPoint);
            service.TryPutDown("putdown", "trace", actor, book, dropZone, heldItem);
            bool sawReleasedZone = false;
            service.SkillResultPublished += result =>
            {
                if (result.SkillId == "pickup_again") sawReleasedZone = dropZone.CanPlace;
            };
            SkillResult picked = service.TryPickUp("pickup_again", "trace", actor, book, heldItem, hand, grabPoint);
            Assert.That(picked.Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(sawReleasedZone, Is.True);
            Assert.That(dropZone.OccupyingItemId, Is.Empty);
            Assert.That(service.WorldRevision, Is.EqualTo(3));
            Assert.That(service.TryPutDown("putdown_again", "trace", actor, book, dropZone, heldItem).Status,
                Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(dropZone.OccupyingItemId, Is.EqualTo(book.EntityId));
            Assert.That(service.WorldRevision, Is.EqualTo(4));
        }

        [Test]
        public void RejectedPickupFromZone_PreservesOccupancyAndParent()
        {
            service.TryPickUp("pickup", "trace", actor, book, heldItem, hand, grabPoint);
            service.TryPutDown("putdown", "trace", actor, book, dropZone, heldItem);
            SkillResult result = service.TryPickUp("invalid", "trace", actor, book, heldItem, hand, hand);
            Assert.That(result.Status, Is.EqualTo(SkillStatus.Failed));
            Assert.That(dropZone.OccupyingItemId, Is.EqualTo(book.EntityId));
            Assert.That(book.transform.parent, Is.EqualTo(dropZone.transform));
            Assert.That(heldItem.IsHeld, Is.False);
            Assert.That(service.WorldRevision, Is.EqualTo(2));
        }

        [Test]
        public void PutDownTransaction_HolderMismatchFailsWithoutChangingWorld()
        {
            Transform originalParent = book.transform.parent;
            Vector3 originalPosition = book.transform.position;

            SkillResult result = service.TryPutDown("putdown_book", "trace-mismatch", actor, book, dropZone, heldItem);

            Assert.That(result.Status, Is.EqualTo(SkillStatus.Failed));
            Assert.That(result.FailureCode, Is.EqualTo("ITEM_NOT_HELD_BY_ACTOR"));
            Assert.That(service.WorldRevision, Is.Zero);
            Assert.That(dropZone.OccupyingItemId, Is.Empty);
            Assert.That(book.transform.parent, Is.EqualTo(originalParent));
            Assert.That(book.transform.position, Is.EqualTo(originalPosition));
            Assert.That(service.EventHistory.Count, Is.EqualTo(1));
        }

        [Test]
        public void PutDownTransaction_MissingZoneFailsWithoutReleasingHeldItem()
        {
            service.TryPickUp("pickup_book", "trace-pickup", actor, book, heldItem, hand, grabPoint);
            int revisionBefore = service.WorldRevision;

            SkillResult result = service.TryPutDown("putdown_book", "trace-missing-zone", actor, book, null, heldItem);

            Assert.That(result.Status, Is.EqualTo(SkillStatus.Failed));
            Assert.That(result.FailureCode, Is.EqualTo("DROP_ZONE_MISSING"));
            Assert.That(service.WorldRevision, Is.EqualTo(revisionBefore));
            Assert.That(heldItem.HolderEntityId, Is.EqualTo(actor.EntityId));
            Assert.That(book.transform.parent, Is.EqualTo(hand));
            Assert.That(service.EventHistory.Count, Is.EqualTo(2));
        }

        [Test]
        public void PutDownTransaction_OccupiedZoneFailsWithoutReleasingSecondItem()
        {
            service.TryPickUp("pickup_book", "trace-book", actor, book, heldItem, hand, grabPoint);
            service.TryPutDown("putdown_book", "trace-book", actor, book, dropZone, heldItem);

            WorldEntity cup = CreateEntity("cup_test", EntityKind.Object);
            cup.gameObject.AddComponent<BoxCollider>();
            cup.gameObject.AddComponent<Rigidbody>();
            HeldItemState cupState = cup.gameObject.AddComponent<HeldItemState>();
            Transform cupGrab = new GameObject("CupGrabPoint").transform;
            cupGrab.SetParent(cup.transform, false);
            try
            {
                service.TryPickUp("pickup_cup", "trace-cup", actor, cup, cupState, hand, cupGrab);
                int revisionBefore = service.WorldRevision;

                SkillResult result = service.TryPutDown("putdown_cup", "trace-cup", actor, cup, dropZone, cupState);

                Assert.That(result.Status, Is.EqualTo(SkillStatus.Failed));
                Assert.That(result.FailureCode, Is.EqualTo("DROP_ZONE_OCCUPIED"));
                Assert.That(service.WorldRevision, Is.EqualTo(revisionBefore));
                Assert.That(cupState.HolderEntityId, Is.EqualTo(actor.EntityId));
                Assert.That(cup.transform.parent, Is.EqualTo(hand));
                Assert.That(dropZone.OccupyingItemId, Is.EqualTo(book.EntityId));
            }
            finally
            {
                Object.DestroyImmediate(cup.gameObject);
            }
        }

        [Test]
        public void OpenTransaction_CommitsDoorObstacleAndExactlyOneRevision()
        {
            GameObject doorObject = new GameObject("Door_Test");
            doorObject.SetActive(false);
            WorldEntity doorEntity = doorObject.AddComponent<WorldEntity>();
            doorEntity.Configure("door_01", "test.door_01", EntityKind.Environment);
            Transform panel = new GameObject("Panel").transform;
            panel.SetParent(doorObject.transform, false);
            NavMeshObstacle obstacle = panel.gameObject.AddComponent<NavMeshObstacle>();
            Transform approach = new GameObject("Approach").transform;
            approach.SetParent(doorObject.transform, false);
            DoorState door = doorObject.AddComponent<DoorState>();
            SetPrivateField(door, "door", doorEntity);
            SetPrivateField(door, "movingPanel", panel);
            SetPrivateField(door, "blockingObstacle", obstacle);
            SetPrivateField(door, "approachPoint", approach);
            doorObject.SetActive(true);

            try
            {
                SkillResult opened = service.TryOpen("open_door", "trace-open", actor, doorEntity, door);
                SkillResult duplicate = service.TryOpen("open_door_again", "trace-open", actor, doorEntity, door);

                Assert.That(opened.Status, Is.EqualTo(SkillStatus.Succeeded));
                Assert.That(opened.SkillType, Is.EqualTo("Open"));
                Assert.That(opened.WorldRevisionBefore, Is.Zero);
                Assert.That(opened.WorldRevisionAfter, Is.EqualTo(1));
                Assert.That(door.IsOpen, Is.True);
                Assert.That(obstacle.enabled, Is.False);
                Assert.That(Quaternion.Angle(panel.localRotation, Quaternion.Euler(0f, 90f, 0f)),
                    Is.LessThan(0.01f));
                Assert.That(duplicate.Status, Is.EqualTo(SkillStatus.Failed));
                Assert.That(duplicate.FailureCode, Is.EqualTo("DOOR_ALREADY_OPEN"));
                Assert.That(service.WorldRevision, Is.EqualTo(1));
                Assert.That(service.EventHistory.Count, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(doorObject);
            }
        }

        private static WorldEntity CreateEntity(string id, EntityKind kind)
        {
            GameObject entityObject = new GameObject(id);
            WorldEntity entity = entityObject.AddComponent<WorldEntity>();
            AgentRole role = kind == EntityKind.Agent
                ? id.Contains("player") ? AgentRole.Player : AgentRole.Npc
                : AgentRole.None;
            entity.Configure(id, $"test.{id}", kind, role);
            return entity;
        }

        private static void SetPrivateField<T>(SafeDropZone zone, string fieldName, T value)
        {
            typeof(SafeDropZone).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(zone, value);
        }

        private static void SetPrivateField<T>(DoorState door, string fieldName, T value)
        {
            typeof(DoorState).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(door, value);
        }
    }
}
