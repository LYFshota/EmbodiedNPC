using System;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Planning
{
    public enum RuntimeActionDecision { Ready, NeedsReplan, Abort }

    public readonly struct RuntimeActionValidationResult
    {
        private RuntimeActionValidationResult(RuntimeActionDecision decision, string failureCode, string detail)
        {
            Decision = decision;
            FailureCode = failureCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public RuntimeActionDecision Decision { get; }
        public string FailureCode { get; }
        public string Detail { get; }
        public bool IsReady => Decision == RuntimeActionDecision.Ready;

        public static RuntimeActionValidationResult Ready() =>
            new(RuntimeActionDecision.Ready, string.Empty, string.Empty);
        public static RuntimeActionValidationResult NeedsReplan(string code, string detail) =>
            new(RuntimeActionDecision.NeedsReplan, code, detail);
        public static RuntimeActionValidationResult Abort(string code, string detail) =>
            new(RuntimeActionDecision.Abort, code, detail);
    }

    public interface IPlannerPerceptionProbe
    {
        bool IsReady { get; }
        int PerceptionRevision { get; }
        bool IsKnownAndNotForgotten(string entityId);
    }

    public sealed class PerceivedStatePlannerProbe : IPlannerPerceptionProbe
    {
        private readonly PerceivedStateStore store;

        public PerceivedStatePlannerProbe(PerceivedStateStore store) => this.store = store;
        public bool IsReady => store != null && store.Aggregator != null;
        public int PerceptionRevision => IsReady ? store.Aggregator.PerceptionRevision : 0;

        public bool IsKnownAndNotForgotten(string entityId)
        {
            if (!IsReady || !store.Aggregator.Entities.TryGetValue(entityId, out PerceivedEntityRecord record))
                return false;
            bool currentlyVisible = record.KnownFields.TryGetValue("visibility_state", out string visibility) &&
                                    visibility == "current_visible";
            bool currentlyNearby = record.KnownFields.TryGetValue("proximity_state", out string proximity) &&
                                   proximity == "nearby";
            return currentlyVisible || currentlyNearby;
        }
    }

    public sealed class RuntimeActionValidationContext
    {
        public RuntimeActionValidationContext(PlanResult plan, ActionInstance action,
            ActionExecutionPreparation preparation,
            string reservationId, SafeDropZone dropZone = null)
        {
            Plan = plan;
            Action = action;
            Preparation = preparation;
            ReservationId = reservationId ?? string.Empty;
            DropZone = dropZone;
        }

        public PlanResult Plan { get; }
        public ActionInstance Action { get; }
        public ActionExecutionPreparation Preparation { get; }
        public IActionPlanningInput CurrentInput => Preparation?.PlanningInput;
        public ValidatedActionBindings Bindings => Preparation?.Bindings;
        public string ReservationId { get; }
        public SafeDropZone DropZone { get; }
    }

    /// <summary>
    /// Side-effect-free admission check performed immediately before starting one planned action.
    /// A revision change alone is informational; only action-relevant facts can reject admission.
    /// </summary>
    public sealed class RuntimeActionValidator
    {
        private readonly EntityRegistry entityRegistry;
        private readonly ResourceReservationService reservations;
        private readonly IPlannerNavigationProbe navigationProbe;
        private readonly IPlannerPerceptionProbe perceptionProbe;
        private readonly float interactionRange;
        private readonly float giveInteractionRange;

        public RuntimeActionValidator(EntityRegistry entityRegistry, ResourceReservationService reservations,
            IPlannerNavigationProbe navigationProbe, IPlannerPerceptionProbe perceptionProbe,
            float interactionRange = 0.85f,
            float giveInteractionRange = NavigationApproachResolver.DeliveryInteractionRange)
        {
            this.entityRegistry = entityRegistry;
            this.reservations = reservations;
            this.navigationProbe = navigationProbe;
            this.perceptionProbe = perceptionProbe;
            this.interactionRange = Mathf.Max(0.01f, interactionRange);
            this.giveInteractionRange = Mathf.Max(this.interactionRange, giveInteractionRange);
        }

        public RuntimeActionValidationResult Validate(RuntimeActionValidationContext context)
        {
            RuntimeActionValidationResult common = ValidateCommon(context);
            if (!common.IsReady) return common;

            return context.Action.Definition.ActionType switch
            {
                PlannerActionType.WalkTo => ValidateWalkTo(context),
                PlannerActionType.PickUp => ValidatePickUp(context),
                PlannerActionType.PutDown => ValidatePutDown(context),
                PlannerActionType.Give => ValidateGive(context),
                PlannerActionType.Open => ValidateOpen(context),
                PlannerActionType.CleanCounter => CleanCounterActionRunner.Validate(context.Bindings, entityRegistry, context.Action)
                    ? RuntimeActionValidationResult.Ready() : Stale("清洁条件失效。"),
                PlannerActionType.PrepareDrink => PrepareDrinkActionRunner.Validate(context.Bindings, entityRegistry,
                    context.Action) ? RuntimeActionValidationResult.Ready() : Stale("制作绑定、能力或交互距离无效。"),
                _ => RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "当前阶段没有该 Action 的运行时契约。"),
            };
        }

        private RuntimeActionValidationResult ValidateCommon(RuntimeActionValidationContext context)
        {
            if (context == null || context.Plan == null || context.Action == null ||
                context.CurrentInput == null || context.Bindings == null)
                return RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "Action 运行时上下文不完整。");
            if (!context.Plan.IsSuccess ||
                !string.Equals(context.Plan.PlanHash, PlanHasher.Compute(context.Plan.Goal, context.Plan.Actions), StringComparison.Ordinal) ||
                !string.Equals(context.Plan.PlanId, $"plan_{context.Plan.PlanHash[..12]}", StringComparison.Ordinal) ||
                !context.Plan.Actions.Any(candidate =>
                    string.Equals(candidate.CanonicalSignature, context.Action.CanonicalSignature, StringComparison.Ordinal)))
                return RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "Plan ID/Hash 或 Action 归属已失效。");
            if (!string.Equals(context.Plan.Goal.GoalId, context.CurrentInput.Snapshot.GoalId, StringComparison.Ordinal) ||
                !string.Equals(context.Plan.Goal.GoalType, context.CurrentInput.ActionId, StringComparison.Ordinal))
                return RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "当前 Snapshot 不属于该 Plan/Goal 绑定。");
            if (entityRegistry == null || !entityRegistry.IsReady ||
                !IsCurrentEntity(context.Bindings.Actor) || !IsCurrentEntity(context.Bindings.Target) ||
                context.Bindings.Destination != null && !IsCurrentEntity(context.Bindings.Destination))
                return RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "Goal 实体引用已失效。");
            return RuntimeActionValidationResult.Ready();
        }

        private RuntimeActionValidationResult ValidateWalkTo(RuntimeActionValidationContext context)
        {
            if (TryBinding(context.Action, "destination", out string targetCandidate) &&
                targetCandidate == context.Bindings.Target.EntityId &&
                context.Bindings.RequiresTargetPerception && !TargetAlreadyHeld(context))
            {
                RuntimeActionValidationResult sight = ValidateTargetPerception(context);
                if (!sight.IsReady) return sight;
            }
            if (!TryBinding(context.Action, "destination", out string destinationId) ||
                !TryResolveDestination(context, destinationId, out Vector3 destination))
                return RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "WalkTo 的目标或 ApproachPoint 已失效。");
            if (navigationProbe == null || !navigationProbe.IsReady)
                return RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "NavMesh 查询上下文不可用。");
            if (navigationProbe.IsBusy || !HasFact(context,
                    PlanningFact.Busy(context.Bindings.Actor.EntityId), PlanningFact.False))
                return Changed("Actor 已在执行导航。");
            if (!HasFact(context, PlanningFact.Reachable(destinationId), PlanningFact.True) ||
                !navigationProbe.HasCompletePath(context.Bindings.Actor.transform.position, destination))
                return RuntimeActionValidationResult.NeedsReplan(
                    destinationId == context.Bindings.DropZoneId ? "DROP_ZONE_UNREACHABLE" : "ACTION_PRECONDITION_CHANGED",
                    "绑定目标当前不可达。");
            if (destinationId == context.Bindings.Target.EntityId &&
                context.Bindings.RequiresTargetPerception && !TargetAlreadyHeld(context))
            {
                RuntimeActionValidationResult perception = ValidateTargetPerception(context);
                if (!perception.IsReady) return perception;
            }
            return RuntimeActionValidationResult.Ready();
        }

        private RuntimeActionValidationResult ValidatePickUp(RuntimeActionValidationContext context)
        {
            if (!TryBinding(context.Action, "target", out string targetId) ||
                targetId != context.Bindings.Target.EntityId)
                return Stale("PickUp 目标绑定已失效。");
            if (context.Bindings.TargetAnchors?.PickupApproachPoint == null ||
                !context.Bindings.Target.TryGetComponent(out InteractableEntity interactable) || !interactable.SupportsPickUp)
                return Stale("PickUp 能力或锚点缺失。");
            RuntimeActionValidationResult perception = ValidateTargetPerception(context);
            if (!perception.IsReady) return perception;
            if (!InRange(context.Bindings.Actor.transform, context.Bindings.TargetAnchors.PickupApproachPoint))
                return RuntimeActionValidationResult.NeedsReplan("ACTION_OUT_OF_RANGE", "Actor 尚未到达 Pickup 交互距离。");
            if (!HasFact(context, PlanningFact.Hand(context.Bindings.Actor.EntityId), PlanningFact.Free) ||
                !HasFact(context, PlanningFact.Holder(targetId), PlanningFact.None) ||
                context.Bindings.TargetHeldState == null || context.Bindings.TargetHeldState.IsHeld)
                return Changed("手部或目标持有关系已变化。");
            return ValidateLease(context);
        }

        private RuntimeActionValidationResult ValidatePutDown(RuntimeActionValidationContext context)
        {
            if (!TryBinding(context.Action, "item", out string itemId) ||
                !TryBinding(context.Action, "destination", out string zoneId) ||
                itemId != context.Bindings.HeldItemId || zoneId != context.Bindings.DropZoneId)
                return Stale("PutDown 物品或放置区绑定已失效。");
            if (context.DropZone == null || !context.DropZone.IsConfigured || context.DropZone.Zone == null ||
                context.DropZone.Zone.EntityId != zoneId || !IsCurrentEntity(context.DropZone.Zone))
                return RuntimeActionValidationResult.Abort("DROP_ZONE_MISSING", "安全放置区配置或实体引用缺失。");
            if (!InRange(context.Bindings.Actor.transform, context.DropZone.ApproachPoint))
                return RuntimeActionValidationResult.NeedsReplan("ACTION_OUT_OF_RANGE", "Actor 尚未到达 DropZone 交互距离。");
            if (!entityRegistry.TryGetEntity(itemId, out WorldEntity item) ||
                !item.TryGetComponent(out HeldItemState held) || !held.IsHeld ||
                held.HolderEntityId != context.Bindings.Actor.EntityId)
                return RuntimeActionValidationResult.NeedsReplan("ITEM_NOT_HELD_BY_ACTOR", "待放物品不再由 Actor 持有。");
            if (!context.DropZone.CanPlace ||
                !HasFact(context, PlanningFact.Available(zoneId), PlanningFact.True))
                return RuntimeActionValidationResult.NeedsReplan("DROP_ZONE_OCCUPIED", "安全放置区已被占用。");
            if (!HasFact(context, PlanningFact.Hand(context.Bindings.Actor.EntityId), itemId) ||
                !HasFact(context, PlanningFact.Holder(itemId), context.Bindings.Actor.EntityId))
                return Changed("待放物品的规划持有关系已变化。");
            return RuntimeActionValidationResult.Ready();
        }

        private RuntimeActionValidationResult ValidateGive(RuntimeActionValidationContext context)
        {
            if (!TryBinding(context.Action, "target", out string targetId) ||
                targetId != context.Bindings.Target.EntityId ||
                !TryBinding(context.Action, "destination", out string destinationId) ||
                context.Bindings.Destination == null || destinationId != context.Bindings.Destination.EntityId)
                return Stale("Give 目标绑定已失效。");
            if (context.Bindings.DestinationAnchors?.ReceivePoint == null ||
                !context.Bindings.Destination.TryGetComponent(out InteractableEntity interactable) || !interactable.SupportsGive)
                return Stale("Give 能力或锚点缺失。");
            if (PlanarDistance(context.Bindings.Actor.transform.position,
                    context.Bindings.Destination.transform.position) > giveInteractionRange)
                return RuntimeActionValidationResult.NeedsReplan("ACTION_OUT_OF_RANGE", "Actor 尚未到达 Receive 交互距离。");
            if (context.Bindings.TargetHeldState == null || !context.Bindings.TargetHeldState.IsHeld ||
                context.Bindings.TargetHeldState.HolderEntityId != context.Bindings.Actor.EntityId ||
                !HasFact(context, PlanningFact.Hand(context.Bindings.Actor.EntityId), targetId) ||
                !HasFact(context, PlanningFact.Holder(targetId), context.Bindings.Actor.EntityId))
                return RuntimeActionValidationResult.NeedsReplan("ITEM_NOT_HELD_BY_ACTOR", "目标物不再由 Actor 持有。");
            return ValidateLease(context);
        }

        private RuntimeActionValidationResult ValidateOpen(RuntimeActionValidationContext context)
        {
            if (!TryBinding(context.Action, "door", out string doorId) ||
                (!string.IsNullOrEmpty(context.Bindings.BlockingDoorId) &&
                 doorId != context.Bindings.BlockingDoorId) ||
                !entityRegistry.TryGetEntity(doorId, out WorldEntity doorEntity) ||
                !doorEntity.TryGetComponent(out DoorState door) || !door.IsConfigured)
                return Stale("Open 的门绑定或 DoorState 已失效。");
            if (TryBinding(context.Action, "unblocks", out string targetId) &&
                (targetId != context.Bindings.Target.EntityId ||
                 !context.Bindings.Target.TryGetComponent(out AccessConstraint accessConstraint) ||
                 !accessConstraint.IsConfigured || accessConstraint.Target != context.Bindings.Target ||
                 accessConstraint.BlockingDoor != door))
                return Stale("Open 的 AccessConstraint 已失效。");

            if (!InRange(context.Bindings.Actor.transform, door.ApproachPoint))
                return RuntimeActionValidationResult.NeedsReplan("ACTION_OUT_OF_RANGE", "Actor 尚未到达门的交互距离。");
            if (door.IsOpen || !HasFact(context, PlanningFact.Open(doorId), PlanningFact.False))
                return Changed("门的开闭状态已变化。");
            if (door.IsLocked || !HasFact(context, PlanningFact.Locked(doorId), PlanningFact.False))
                return RuntimeActionValidationResult.NeedsReplan("DOOR_LOCKED", "门当前处于锁定状态。");
            return RuntimeActionValidationResult.Ready();
        }

        private RuntimeActionValidationResult ValidateLease(RuntimeActionValidationContext context)
        {
            if (reservations == null ||
                !reservations.IsLeaseValid(context.ReservationId, context.Bindings.Actor.EntityId) ||
                !reservations.TryGetSnapshot(context.ReservationId, out ReservationLeaseSnapshot lease) ||
                lease.TargetEntityId != context.Bindings.Target.EntityId)
                return RuntimeActionValidationResult.NeedsReplan("TARGET_LEASE_INVALID", "当前 Plan 不再持有目标租约。");
            return RuntimeActionValidationResult.Ready();
        }

        private RuntimeActionValidationResult ValidateTargetPerception(RuntimeActionValidationContext context)
        {
            if (perceptionProbe == null || !perceptionProbe.IsReady)
                return RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", "Perceived State 查询上下文不可用。");
            return perceptionProbe.IsKnownAndNotForgotten(context.Bindings.Target.EntityId)
                ? RuntimeActionValidationResult.Ready()
                : Changed("目标物已不在 NPC 的有效 Perceived State 中。");
        }

        private bool TargetAlreadyHeld(RuntimeActionValidationContext context) =>
            context.Bindings.TargetHeldState != null && context.Bindings.TargetHeldState.IsHeld &&
            context.Bindings.TargetHeldState.HolderEntityId == context.Bindings.Actor.EntityId;

        private bool TryResolveDestination(RuntimeActionValidationContext context, string entityId, out Vector3 point)
        {
            point = default;
            bool found = false;
            if (entityId == context.Bindings.Target.EntityId && context.Bindings.TargetApproachPosition.HasValue)
            {
                point = context.Bindings.TargetApproachPosition.Value;
                found = true;
            }
            else if (entityId == context.Bindings.Target.EntityId &&
                     context.Bindings.TargetAnchors?.PickupApproachPoint != null)
            {
                point = context.Bindings.TargetAnchors.PickupApproachPoint.position;
                found = true;
            }
            else if (context.Bindings.Destination != null && entityId == context.Bindings.Destination.EntityId &&
                     context.Bindings.DestinationApproachPosition.HasValue)
            {
                point = context.Bindings.DestinationApproachPosition.Value;
                found = true;
            }
            else if (context.Bindings.Destination != null && entityId == context.Bindings.Destination.EntityId &&
                     context.Bindings.DestinationAnchors?.ReceivePoint != null)
            {
                point = context.Bindings.DestinationAnchors.ReceivePoint.position;
                found = true;
            }
            else if (context.DropZone != null && context.DropZone.Zone != null &&
                     entityId == context.DropZone.Zone.EntityId && context.DropZone.ApproachPoint != null)
            {
                point = context.DropZone.ApproachPoint.position;
                found = true;
            }
            else if (entityRegistry.TryGetEntity(entityId, out WorldEntity entity) &&
                     entity.TryGetComponent(out DoorState door) && door.IsConfigured)
            {
                point = door.ApproachPoint.position;
                found = true;
            }
            return found;
        }

        private bool IsCurrentEntity(WorldEntity entity) => entity != null &&
            entityRegistry.TryGetEntity(entity.EntityId, out WorldEntity registered) && registered == entity;
        private bool InRange(Transform actor, Transform point) => actor != null && point != null &&
            Vector3.Distance(actor.position, point.position) <= interactionRange;
        private static float PlanarDistance(Vector3 left, Vector3 right)
        {
            left.y = 0f;
            right.y = 0f;
            return Vector3.Distance(left, right);
        }
        private static bool HasFact(RuntimeActionValidationContext context, string key, string expected) =>
            context.CurrentInput.Snapshot.State.TryGetValue(key, out string actual) && actual == expected;
        private static bool TryBinding(ActionInstance action, string key, out string value) =>
            action.Bindings.TryGetValue(key, out value) && !string.IsNullOrEmpty(value);
        private static RuntimeActionValidationResult Changed(string detail) =>
            RuntimeActionValidationResult.NeedsReplan("ACTION_PRECONDITION_CHANGED", detail);
        private static RuntimeActionValidationResult Stale(string detail) =>
            RuntimeActionValidationResult.Abort("ACTION_BINDING_STALE", detail);
    }
}
