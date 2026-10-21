using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Planning
{
    /// <summary>Navigation query boundary used by the authoritative snapshot builder.</summary>
    public interface IPlannerNavigationProbe
    {
        bool IsReady { get; }
        bool IsBusy { get; }
        bool HasCompletePath(Vector3 origin, Vector3 destination);
    }

    /// <summary>Production path probe backed by the actor's current NavMesh settings.</summary>
    public sealed class NavMeshPlannerNavigationProbe : IPlannerNavigationProbe
    {
        private readonly NavMeshAgent agent;

        public NavMeshPlannerNavigationProbe(NavMeshAgent agent)
        {
            this.agent = agent;
        }

        public bool IsReady => agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;
        public bool IsBusy => IsReady && (agent.hasPath || agent.pathPending);

        public bool HasCompletePath(Vector3 origin, Vector3 destination)
        {
            if (!IsReady)
            {
                return false;
            }

            NavMeshPath path = new();
            return NavMesh.CalculatePath(origin, destination, agent.areaMask, path) &&
                   path.status == NavMeshPathStatus.PathComplete;
        }
    }

    public enum PlannerSnapshotBuildFailureCode
    {
        None,
        GoalMissing,
        SnapshotIdentityInvalid,
        GoalReferenceStale,
        EntityRegistryNotReady,
        WorldStateServiceNotReady,
        ReservationServiceNotReady,
        PathProbeNotReady,
        RequiredAnchorMissing,
        AccessConstraintInvalid,
        ActorHoldsMultipleItems,
    }

    /// <summary>Immutable planning input plus the concrete entity bindings used to create actions.</summary>
    public sealed class BringObjectPlanningInput : IActionPlanningInput
    {
        public BringObjectPlanningInput(PlannerSnapshot snapshot, BringObjectBindings bindings)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Bindings = bindings;
        }

        public PlannerSnapshot Snapshot { get; }
        public string ActionId => "bring_object";
        public BringObjectBindings Bindings { get; }
    }

    public readonly struct PlannerSnapshotBuildResult
    {
        private PlannerSnapshotBuildResult(BringObjectPlanningInput input,
            PlannerSnapshotBuildFailureCode failureCode, string detail)
        {
            Input = input;
            FailureCode = failureCode;
            Detail = detail ?? string.Empty;
        }

        public bool IsSuccess => Input != null && FailureCode == PlannerSnapshotBuildFailureCode.None;
        public BringObjectPlanningInput Input { get; }
        public PlannerSnapshotBuildFailureCode FailureCode { get; }
        public string Detail { get; }

        public static PlannerSnapshotBuildResult Succeeded(BringObjectPlanningInput input) =>
            new(input ?? throw new ArgumentNullException(nameof(input)), PlannerSnapshotBuildFailureCode.None, string.Empty);

        public static PlannerSnapshotBuildResult Failed(PlannerSnapshotBuildFailureCode code, string detail) =>
            new(null, code, detail);

        public override string ToString() => IsSuccess
            ? $"BUILT snapshot={Input.Snapshot.SnapshotId}; goal={Input.Snapshot.GoalId}; world_revision={Input.Snapshot.WorldRevision}"
            : $"FAILED code={FailureCode}; detail={Detail}";
    }

    /// <summary>
    /// Projects current Unity-authoritative state into immutable Mini-GOAP facts.
    /// It never changes ownership, reservations, navigation, or World State.
    /// </summary>
    public sealed class PlannerSnapshotBuilder
    {
        private readonly EntityRegistry entityRegistry;
        private readonly ResourceReservationService reservations;
        private readonly WorldStateService worldState;
        private readonly IPlannerNavigationProbe navigationProbe;
        private readonly SafeDropZone safeDropZone;

        public PlannerSnapshotBuilder(EntityRegistry entityRegistry, ResourceReservationService reservations,
            WorldStateService worldState, IPlannerNavigationProbe navigationProbe, SafeDropZone safeDropZone = null)
        {
            this.entityRegistry = entityRegistry;
            this.reservations = reservations;
            this.worldState = worldState;
            this.navigationProbe = navigationProbe;
            this.safeDropZone = safeDropZone;
        }

        public PlannerSnapshotBuildResult Build(string snapshotId, string goalId, int perceptionRevision,
            ResolvedBringObjectGoal goal, Vector3? confirmedTargetApproachPosition = null,
            Vector3? confirmedDestinationApproachPosition = null)
        {
            if (goal == null)
            {
                return Fail(PlannerSnapshotBuildFailureCode.GoalMissing, "Resolved Goal 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(goalId))
            {
                return Fail(PlannerSnapshotBuildFailureCode.SnapshotIdentityInvalid, "Goal ID 不能为空。");
            }

            if (entityRegistry == null || !entityRegistry.IsReady)
            {
                return Fail(PlannerSnapshotBuildFailureCode.EntityRegistryNotReady, "EntityRegistry 尚未准备完成。");
            }

            if (worldState == null)
            {
                return Fail(PlannerSnapshotBuildFailureCode.WorldStateServiceNotReady, "WorldStateService 尚未绑定。");
            }

            if (reservations == null)
            {
                return Fail(PlannerSnapshotBuildFailureCode.ReservationServiceNotReady, "ResourceReservationService 尚未绑定。");
            }

            if (goal.Actor == null || goal.Target == null || goal.Destination == null || goal.TargetHeldState == null ||
                !entityRegistry.TryGetEntity(goal.Actor.EntityId, out WorldEntity registeredActor) || registeredActor != goal.Actor ||
                !entityRegistry.TryGetEntity(goal.Target.EntityId, out WorldEntity registeredTarget) || registeredTarget != goal.Target ||
                !entityRegistry.TryGetEntity(goal.Destination.EntityId, out WorldEntity registeredDestination) ||
                registeredDestination != goal.Destination)
            {
                return Fail(PlannerSnapshotBuildFailureCode.GoalReferenceStale,
                    "Resolved Goal 的实体引用已缺失、被替换或不再属于当前注册表。");
            }

            if (navigationProbe == null || !navigationProbe.IsReady)
            {
                return Fail(PlannerSnapshotBuildFailureCode.PathProbeNotReady, "执行者当前没有可用的路径查询上下文。");
            }

            if (goal.TargetAnchors?.PickupApproachPoint == null || goal.DestinationAnchors?.ReceivePoint == null)
            {
                return Fail(PlannerSnapshotBuildFailureCode.RequiredAnchorMissing, "规划所需的 Pickup/Receive 锚点缺失。");
            }

            List<WorldEntity> heldByActor = entityRegistry.RegisteredEntities
                .Where(entity => entity != null && entity.TryGetComponent(out HeldItemState state) &&
                                 state.IsHeld && state.HolderEntityId == goal.Actor.EntityId)
                .OrderBy(entity => entity.EntityId, StringComparer.Ordinal)
                .ToList();
            if (heldByActor.Count > 1)
            {
                return Fail(PlannerSnapshotBuildFailureCode.ActorHoldsMultipleItems,
                    $"执行者同时持有多个物品：{string.Join(",", heldByActor.Select(entity => entity.EntityId))}。");
            }

            string heldItemId = heldByActor.Count == 1 ? heldByActor[0].EntityId : string.Empty;
            string targetHolder = goal.TargetHeldState.IsHeld
                ? goal.TargetHeldState.HolderEntityId
                : PlanningFact.None;
            Vector3 origin = goal.Actor.transform.position;
            bool targetReachable = navigationProbe.HasCompletePath(origin,
                confirmedTargetApproachPosition ?? goal.TargetAnchors.PickupApproachPoint.position);
            bool destinationReachable = navigationProbe.HasCompletePath(origin,
                confirmedDestinationApproachPosition ?? goal.DestinationAnchors.ReceivePoint.position);

            string blockingDoorId = string.Empty;
            DoorState blockingDoor = null;
            if (goal.Target.TryGetComponent(out AccessConstraint accessConstraint))
            {
                if (!accessConstraint.IsConfigured || accessConstraint.Target != goal.Target ||
                    accessConstraint.BlockingDoor.Door == null ||
                    !entityRegistry.TryGetEntity(accessConstraint.BlockingDoor.Door.EntityId, out WorldEntity registeredDoor) ||
                    registeredDoor != accessConstraint.BlockingDoor.Door)
                {
                    return Fail(PlannerSnapshotBuildFailureCode.AccessConstraintInvalid,
                        "目标的 AccessConstraint 或阻挡门引用无效。");
                }

                blockingDoor = accessConstraint.BlockingDoor;
                if (!blockingDoor.IsOpen)
                {
                    blockingDoorId = blockingDoor.Door.EntityId;
                    targetReachable = false;
                }
            }

            List<KeyValuePair<string, string>> facts = new()
            {
                Fact(PlanningFact.Location(goal.Actor.EntityId), PlanningFact.Current),
                Fact(PlanningFact.Busy(goal.Actor.EntityId), Bool(navigationProbe.IsBusy)),
                Fact(PlanningFact.Hand(goal.Actor.EntityId), string.IsNullOrEmpty(heldItemId) ? PlanningFact.Free : heldItemId),
                Fact(PlanningFact.Holder(goal.Target.EntityId), targetHolder),
                Fact(PlanningFact.Reachable(goal.Target.EntityId), Bool(targetReachable)),
                Fact(PlanningFact.Reachable(goal.Destination.EntityId), Bool(destinationReachable)),
                Fact(PlanningFact.Reserved(goal.Target.EntityId),
                    Bool(reservations.IsReservedByOther(goal.Target.EntityId, goal.Actor.EntityId))),
            };
            if (!string.IsNullOrEmpty(heldItemId) && heldItemId != goal.Target.EntityId)
            {
                facts.Add(Fact(PlanningFact.Holder(heldItemId), goal.Actor.EntityId));
            }

            if (!string.IsNullOrEmpty(blockingDoorId))
            {
                facts.Add(Fact(PlanningFact.Reachable(blockingDoorId),
                    Bool(navigationProbe.HasCompletePath(origin, blockingDoor.ApproachPoint.position))));
                facts.Add(Fact(PlanningFact.Open(blockingDoorId), Bool(blockingDoor.IsOpen)));
                facts.Add(Fact(PlanningFact.Locked(blockingDoorId), Bool(blockingDoor.IsLocked)));
            }

            string dropZoneId = string.Empty;
            if (!string.IsNullOrEmpty(heldItemId) && heldItemId != goal.Target.EntityId &&
                safeDropZone != null && safeDropZone.IsConfigured && safeDropZone.Zone != null &&
                entityRegistry.TryGetEntity(safeDropZone.Zone.EntityId, out WorldEntity registeredZone) &&
                registeredZone == safeDropZone.Zone)
            {
                dropZoneId = safeDropZone.Zone.EntityId;
                bool dropZoneReachable = navigationProbe.HasCompletePath(origin, safeDropZone.ApproachPoint.position);
                facts.Add(Fact(PlanningFact.Reachable(dropZoneId), Bool(dropZoneReachable)));
                facts.Add(Fact(PlanningFact.Available(dropZoneId), Bool(safeDropZone.CanPlace)));
                facts.Add(Fact(PlanningFact.Occupant(dropZoneId),
                    string.IsNullOrEmpty(safeDropZone.OccupyingItemId)
                        ? PlanningFact.None
                        : safeDropZone.OccupyingItemId));
            }

            PlannerSnapshot snapshot;
            try
            {
                snapshot = new PlannerSnapshot(snapshotId, goalId, worldState.WorldRevision,
                    perceptionRevision, new PlanningState(facts));
            }
            catch (ArgumentException exception)
            {
                return Fail(PlannerSnapshotBuildFailureCode.SnapshotIdentityInvalid, exception.Message);
            }

            BringObjectBindings bindings = new(goal.Actor.EntityId, goal.Target.EntityId,
                goal.Destination.EntityId, heldItemId, blockingDoorId, dropZoneId);
            return PlannerSnapshotBuildResult.Succeeded(new BringObjectPlanningInput(snapshot, bindings));
        }

        private static KeyValuePair<string, string> Fact(string key, string value) => new(key, value);
        private static string Bool(bool value) => value ? PlanningFact.True : PlanningFact.False;
        private static PlannerSnapshotBuildResult Fail(PlannerSnapshotBuildFailureCode code, string detail) =>
            PlannerSnapshotBuildResult.Failed(code, detail);
    }
}
