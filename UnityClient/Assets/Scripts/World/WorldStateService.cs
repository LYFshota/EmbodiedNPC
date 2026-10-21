using System;
using System.Collections.Generic;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using UnityEngine;

namespace EmbodiedNPC.World
{
    public enum SkillStatus { Succeeded, Failed, Cancelled, TimedOut }

    public readonly struct SkillResult
    {
        public SkillResult(string skillId, string traceId, string skillType, string actorEntityId,
            string targetEntityId, string destinationEntityId, SkillStatus status, string failureCode,
            long eventSequence, int worldRevisionBefore, int worldRevisionAfter)
        {
            SkillId = skillId; TraceId = traceId; SkillType = skillType; ActorEntityId = actorEntityId;
            TargetEntityId = targetEntityId; DestinationEntityId = destinationEntityId; Status = status;
            FailureCode = failureCode; EventSequence = eventSequence; WorldRevisionBefore = worldRevisionBefore;
            WorldRevisionAfter = worldRevisionAfter;
        }
        public string SkillId { get; }
        public string TraceId { get; }
        public string SkillType { get; }
        public string ActorEntityId { get; }
        public string TargetEntityId { get; }
        public string DestinationEntityId { get; }
        public SkillStatus Status { get; }
        public string FailureCode { get; }
        public long EventSequence { get; }
        public int WorldRevisionBefore { get; }
        public int WorldRevisionAfter { get; }
    }

    /// <summary>
    /// 权威 World State 事务入口：业务脚本不得直接提交所有权或表现状态。
    /// </summary>
    public sealed partial class WorldStateService : MonoBehaviour
    {
        [SerializeField, Min(0)] private int worldRevision;
        private readonly List<SkillResult> eventHistory = new();
        private long nextEventSequence;
        private readonly Dictionary<(string actor,string proposal,string trace),Func<string,string,string,string,int,bool>> taskGuards = new();
        // 任务回执来自通过即时校验的世界提交；注册 guard 只收紧该 attempt 的
        // 副作用权限，不替代各 TryCommit/技能入口原有的能力和状态校验。
        internal void RegisterTaskGuard(string actor,string proposal,string trace,Func<string,string,string,string,int,bool> guard)
        { taskGuards.Add((actor,proposal,trace),guard); }
        private bool TaskEffectAllowed(string actor,string proposal,string trace,string action,string target,string player,string drink,int condition) =>
            !taskGuards.TryGetValue((actor,proposal,trace),out var guard) || guard(action,target,player,drink,condition);

        public int WorldRevision => worldRevision;
        public SkillResult LastResult { get; private set; }
        public IReadOnlyList<SkillResult> EventHistory => eventHistory;
        /// <summary>Published after the immutable SkillResult enters the authoritative event history.</summary>
        public event Action<SkillResult> SkillResultPublished;

        public SkillResult TryPickUp(string skillId, string traceId, WorldEntity actor, WorldEntity target,
            HeldItemState itemState, Transform holdParent, Transform grabPoint)
        {
            if (actor == null || target == null || itemState == null || holdParent == null || grabPoint == null)
                return RecordAttempt(skillId, traceId, "PickUp", actor, target, null, SkillStatus.Failed, "REFERENCE_MISSING");
            if (grabPoint != target.transform && !grabPoint.IsChildOf(target.transform))
                return RecordAttempt(skillId, traceId, "PickUp", actor, target, null, SkillStatus.Failed, "GRAB_POINT_NOT_ON_TARGET");
            if (itemState.IsHeld)
                return RecordAttempt(skillId, traceId, "PickUp", actor, target, null, SkillStatus.Failed, "ITEM_ALREADY_HELD");

            ItemVisualState before = ItemVisualState.Capture(target);
            // PutDown parents the item to its authoritative zone. Capture it before reparenting.
            SafeDropZone originalZone = target.transform.parent == null
                ? null : target.transform.parent.GetComponent<SafeDropZone>();
            bool releasedZone = false;
            if (!itemState.TrySetHolder(actor.EntityId))
                return RecordAttempt(skillId, traceId, "PickUp", actor, target, null, SkillStatus.Failed, "OWNERSHIP_CONFLICT");
            try
            {
                ApplyPickupVisualState(target, holdParent, grabPoint);
                releasedZone = originalZone != null && originalZone.TryReleaseOccupancy(target.EntityId);
                return Commit(skillId, traceId, "PickUp", actor, target, null, SkillStatus.Succeeded, string.Empty);
            }
            catch (Exception exception)
            {
                itemState.RestoreHolder(string.Empty);
                if (releasedZone) originalZone.RestoreOccupancy(target.EntityId);
                before.Restore(target);
                Debug.LogError($"[WorldState] Pickup rollback: {exception.GetType().Name}", this);
                return RecordAttempt(skillId, traceId, "PickUp", actor, target, null, SkillStatus.Failed, "PICKUP_TRANSACTION_ROLLED_BACK");
            }
        }

        public SkillResult TryTransferOwnership(string skillId, string traceId, WorldEntity actor,
            WorldEntity target, WorldEntity destination, HeldItemState itemState, Transform holdParent,
            Transform placement)
        {
            if (actor == null || target == null || destination == null || itemState == null || holdParent == null || placement == null)
                return RecordAttempt(skillId, traceId, "Give", actor, target, destination, SkillStatus.Failed, "REFERENCE_MISSING");
            if (!itemState.IsHeld || itemState.HolderEntityId != actor.EntityId)
                return RecordAttempt(skillId, traceId, "Give", actor, target, destination, SkillStatus.Failed, "ITEM_NOT_HELD_BY_ACTOR");

            ItemVisualState before = ItemVisualState.Capture(target);
            if (!itemState.TryTransferHolder(actor.EntityId, destination.EntityId))
                return RecordAttempt(skillId, traceId, "Give", actor, target, destination, SkillStatus.Failed, "TRANSFER_CONFLICT");
            try
            {
                if (destination.EntityKind == EntityKind.Agent)
                    ApplyStoredActorVisualState(target, holdParent, placement);
                else
                    ApplyTransferredVisualState(target, holdParent, placement);
                return Commit(skillId, traceId, "Give", actor, target, destination, SkillStatus.Succeeded, string.Empty);
            }
            catch (Exception exception)
            {
                itemState.RestoreHolder(actor.EntityId);
                before.Restore(target);
                Debug.LogError($"[WorldState] Give rollback: {exception.GetType().Name}", this);
                return RecordAttempt(skillId, traceId, "Give", actor, target, destination, SkillStatus.Failed, "GIVE_TRANSACTION_ROLLED_BACK");
            }
        }

        public SkillResult TryPutDown(string skillId, string traceId, WorldEntity actor,
            WorldEntity target, SafeDropZone dropZone, HeldItemState itemState)
        {
            WorldEntity zoneEntity = dropZone != null ? dropZone.Zone : null;
            if (actor == null || target == null || dropZone == null || itemState == null ||
                itemState.gameObject != target.gameObject)
                return RecordAttempt(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Failed, "DROP_ZONE_MISSING");
            if (!dropZone.IsConfigured || zoneEntity == null || dropZone.PlacementPoint == null)
                return RecordAttempt(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Failed, "DROP_ZONE_MISSING");
            if (!itemState.IsHeld || itemState.HolderEntityId != actor.EntityId)
                return RecordAttempt(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Failed, "ITEM_NOT_HELD_BY_ACTOR");
            if (!dropZone.CanPlace)
                return RecordAttempt(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Failed, "DROP_ZONE_OCCUPIED");

            ItemVisualState before = ItemVisualState.Capture(target);
            string previousOccupant = dropZone.OccupyingItemId;
            if (!dropZone.TryOccupy(target.EntityId))
                return RecordAttempt(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Failed, "DROP_ZONE_OCCUPIED");
            if (!itemState.TryReleaseHolder(actor.EntityId))
            {
                dropZone.RestoreOccupancy(previousOccupant);
                return RecordAttempt(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Failed, "ITEM_NOT_HELD_BY_ACTOR");
            }

            try
            {
                ApplyPutDownVisualState(target, dropZone.transform, dropZone.PlacementPoint);
                return Commit(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Succeeded, string.Empty);
            }
            catch (Exception exception)
            {
                itemState.RestoreHolder(actor.EntityId);
                dropZone.RestoreOccupancy(previousOccupant);
                before.Restore(target);
                Debug.LogError($"[WorldState] PutDown rollback: {exception.GetType().Name}", this);
                return RecordAttempt(skillId, traceId, "PutDown", actor, target, zoneEntity,
                    SkillStatus.Failed, "PUTDOWN_TRANSACTION_ROLLED_BACK");
            }
        }

        public SkillResult TryOpen(string skillId, string traceId, WorldEntity actor,
            WorldEntity target, DoorState doorState)
        {
            if (actor == null || target == null || doorState == null || doorState.gameObject != target.gameObject ||
                !doorState.IsConfigured || doorState.Door != target)
                return RecordAttempt(skillId, traceId, "Open", actor, target, null,
                    SkillStatus.Failed, "REFERENCE_MISSING");
            if (doorState.IsLocked)
                return RecordAttempt(skillId, traceId, "Open", actor, target, null,
                    SkillStatus.Failed, "DOOR_LOCKED");
            if (doorState.IsOpen)
                return RecordAttempt(skillId, traceId, "Open", actor, target, null,
                    SkillStatus.Failed, "DOOR_ALREADY_OPEN");

            DoorRuntimeSnapshot before = doorState.CaptureRuntimeState();
            try
            {
                if (!doorState.TryOpenFromAuthority())
                    return RecordAttempt(skillId, traceId, "Open", actor, target, null,
                        SkillStatus.Failed, "DOOR_STATE_CONFLICT");
                return Commit(skillId, traceId, "Open", actor, target, null,
                    SkillStatus.Succeeded, string.Empty);
            }
            catch (Exception exception)
            {
                doorState.RestoreRuntimeState(before);
                Debug.LogError($"[WorldState] Open rollback: {exception.GetType().Name}", this);
                return RecordAttempt(skillId, traceId, "Open", actor, target, null,
                    SkillStatus.Failed, "OPEN_TRANSACTION_ROLLED_BACK");
            }
        }

        public SkillResult RecordAttempt(string skillId, string traceId, string skillType, WorldEntity actor,
            WorldEntity target, WorldEntity destination, SkillStatus status, string failureCode)
        {
            return Publish(skillId, traceId, skillType, actor, target, destination, status, failureCode, false);
        }

        private SkillResult Commit(string skillId, string traceId, string skillType, WorldEntity actor,
            WorldEntity target, WorldEntity destination, SkillStatus status, string failureCode)
        { return Publish(skillId, traceId, skillType, actor, target, destination, status, failureCode, true); }

        private SkillResult Publish(string skillId, string traceId, string skillType, WorldEntity actor,
            WorldEntity target, WorldEntity destination, SkillStatus status, string failureCode, bool changesWorld)
        {
            int before = worldRevision;
            if (changesWorld) worldRevision++;
            SkillResult result = new(skillId, traceId, skillType, actor?.EntityId, target?.EntityId,
                destination?.EntityId, status, failureCode ?? string.Empty, ++nextEventSequence, before, worldRevision);
            LastResult = result;
            eventHistory.Add(result);
            if (SkillResultPublished != null)
            {
                foreach (Delegate subscriber in SkillResultPublished.GetInvocationList())
                {
                    try
                    {
                        ((Action<SkillResult>)subscriber).Invoke(result);
                    }
                    catch (Exception exception)
                    {
                        // Perception and debug subscribers observe authority; they must never invalidate a committed transaction.
                        Debug.LogError($"[WorldState] SkillResult subscriber failed: {exception.GetType().Name}", this);
                    }
                }
            }
            Debug.Log($"[SkillResult] event_sequence={result.EventSequence}; skill_id={result.SkillId}; " +
                      $"trace_id={result.TraceId}; status={result.Status}; failure_code={result.FailureCode}; " +
                      $"world_revision_before={result.WorldRevisionBefore}; world_revision_after={result.WorldRevisionAfter}", this);
            return result;
        }

        private static void ApplyPickupVisualState(WorldEntity target, Transform holdPoint, Transform grabPoint)
        {
            Rigidbody body = target.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            Collider collider = target.GetComponent<Collider>();
            if (collider != null) collider.enabled = false;

            // Rotate and translate the item root so the authored item-side anchor
            // exactly matches the character's hand-side hold point.
            Quaternion rotationDelta = holdPoint.rotation * Quaternion.Inverse(grabPoint.rotation);
            target.transform.rotation = rotationDelta * target.transform.rotation;
            target.transform.position += holdPoint.position - grabPoint.position;
            target.transform.SetParent(holdPoint, true);
        }

        private static void ApplyTransferredVisualState(WorldEntity target, Transform parent, Transform placement)
        {
            Rigidbody body = target.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            Collider collider = target.GetComponent<Collider>();
            if (collider != null) collider.enabled = true;
            target.transform.SetParent(parent, false);
            target.transform.position = placement.position;
            target.transform.rotation = placement.rotation;
        }

        private static void ApplyStoredActorVisualState(WorldEntity target, Transform parent, Transform placement)
        {
            // The existing destination transform is containment; HeldItemState remains ownership authority.
            ApplyTransferredVisualState(target, parent, placement);
            Collider collider = target.GetComponent<Collider>();
            if (collider != null) collider.enabled = false;
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
            PerceivableEntity perceivable = target.GetComponent<PerceivableEntity>();
            if (perceivable != null) perceivable.enabled = false;
        }

        private static void ApplyPutDownVisualState(WorldEntity target, Transform parent, Transform placement)
        {
            Rigidbody body = target.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            Collider collider = target.GetComponent<Collider>();
            if (collider != null) collider.enabled = true;
            target.transform.SetParent(parent, true);
            target.transform.SetPositionAndRotation(placement.position, placement.rotation);
        }

        private readonly struct ItemVisualState
        {
            private readonly Transform parent;
            private readonly Vector3 position;
            private readonly Quaternion rotation;
            private readonly bool colliderEnabled;
            private readonly bool bodyKinematic;
            private readonly Rigidbody body;
            private readonly Collider collider;
            private readonly Renderer[] renderers;
            private readonly bool[] rendererEnabled;
            private readonly PerceivableEntity perceivable;
            private readonly bool perceivableEnabled;

            private ItemVisualState(WorldEntity target)
            {
                parent = target.transform.parent; position = target.transform.position; rotation = target.transform.rotation;
                collider = target.GetComponent<Collider>(); body = target.GetComponent<Rigidbody>();
                colliderEnabled = collider == null || collider.enabled; bodyKinematic = body != null && body.isKinematic;
                renderers = target.GetComponentsInChildren<Renderer>(true);
                rendererEnabled = new bool[renderers.Length];
                for (int i = 0; i < renderers.Length; i++) rendererEnabled[i] = renderers[i].enabled;
                perceivable = target.GetComponent<PerceivableEntity>();
                perceivableEnabled = perceivable != null && perceivable.enabled;
            }
            public static ItemVisualState Capture(WorldEntity target) => new(target);
            public void Restore(WorldEntity target)
            {
                target.transform.SetParent(parent, true); target.transform.position = position; target.transform.rotation = rotation;
                if (collider != null) collider.enabled = colliderEnabled;
                if (body != null) body.isKinematic = bodyKinematic;
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].enabled = rendererEnabled[i];
                if (perceivable != null) perceivable.enabled = perceivableEnabled;
            }
        }
    }
}
