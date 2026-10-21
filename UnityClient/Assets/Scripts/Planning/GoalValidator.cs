using System;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Planning
{
    /// <summary>
    /// M4.1 的 Unity 权威边界：验证模型提出的 Goal，但不执行、预留或修改世界。
    /// 完整的感知过滤与资源预留将在后续阶段接入此边界。
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent), typeof(WorldEntity))]
    public sealed class GoalValidator : MonoBehaviour
    {
        [Header("Authoritative world references")]
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private ResourceReservationService reservations;
        [SerializeField] private WorldStateService worldState;
        [SerializeField] private WorldEntity actor;
        [SerializeField] private NavMeshAgent agent;

        private void Awake()
        {
            if (actor == null)
            {
                actor = GetComponent<WorldEntity>();
            }

            if (agent == null)
            {
                agent = GetComponent<NavMeshAgent>();
            }

            if (worldState == null)
            {
                worldState = GetComponent<WorldStateService>();
            }
        }

        /// <summary>
        /// M4 固定执行链的兼容入口：准入通过后，继续要求目标现在就可拾取、手为空且路径可达。
        /// M7.2-D 起 GoalPlanRunner 调用 ValidateAdmission；此方法只保留给旧测试和诊断工具。
        /// </summary>
        public GoalValidationResult Validate(GoalProposal proposal)
        {
            GoalValidationResult admission = ValidateAdmission(proposal);
            if (!admission.IsApproved)
            {
                return admission;
            }
            if (admission.ResolvedGoal is ResolvedPrepareDrinkGoal || admission.ResolvedGoal is ResolvedCleanCounterGoal) return admission;

            if (admission.ResolvedMoveGoal != null)
            {
                if (agent.hasPath || agent.pathPending)
                    return Fail(GoalValidationFailureCode.ActorBusy, "NPC 已有正在执行的导航任务。");
                if (!new NavigationApproachResolver().TryResolve(agent, admission.ResolvedMoveGoal.Target,
                        out Vector3 approachPosition))
                    return Fail(GoalValidationFailureCode.TargetUnreachable, "无法解析目标附近的可达位置。");
                return GoalValidationResult.Approved(new ResolvedMoveToEntityGoal(actor,
                    admission.ResolvedMoveGoal.Target, approachPosition, worldState.WorldRevision));
            }

            ResolvedBringObjectGoal resolved = admission.ResolvedBringGoal;
            if (resolved.TargetHeldState.IsHeld)
            {
                return Fail(GoalValidationFailureCode.TargetAlreadyHeld, "目标已被持有。");
            }

            if (reservations.IsReservedByOther(resolved.Target.EntityId, resolved.Actor.EntityId))
            {
                return Fail(GoalValidationFailureCode.TargetReserved, "目标已被其他 NPC 预留。");
            }

            if (TryGetActorHeldEntity(out string heldEntityId))
            {
                return Fail(GoalValidationFailureCode.HandOccupied, $"NPC 正持有 {heldEntityId}。");
            }

            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh)
            {
                return Fail(GoalValidationFailureCode.ActorNotOnNavMesh, "NPC 当前不在有效 NavMesh 上。");
            }

            if (agent.hasPath || agent.pathPending)
            {
                return Fail(GoalValidationFailureCode.ActorBusy, "NPC 已有正在执行的导航任务。");
            }

            if (!HasCompletePath(resolved.TargetAnchors.PickupApproachPoint.position))
            {
                return Fail(GoalValidationFailureCode.TargetUnreachable, "无法到达物品交互点。");
            }

            if (!HasCompletePath(resolved.DestinationAnchors.ReceivePoint.position))
            {
                return Fail(GoalValidationFailureCode.DestinationUnreachable, "无法到达玩家交付点。");
            }

            return admission;
        }

        /// <summary>
        /// M7.2 的安全准入边界：只验证 Goal 契约、权威实体、能力和必需交互锚点。
        /// 持有关系、租约、忙碌状态和可达性是可变化的规划 Facts，由 PlannerSnapshotBuilder 读取。
        /// </summary>
        public GoalValidationResult ValidateAdmission(GoalProposal proposal)
        {
            if (proposal == null)
            {
                return Fail(GoalValidationFailureCode.GoalMissing, "Goal 为 null，保持纯对话。");
            }

            if (!BuiltInActionCatalog.Instance.TryGet(proposal.type, out ActionRegistration registration) ||
                !registration.Descriptor.Available)
            {
                return Fail(GoalValidationFailureCode.UnsupportedGoalType, "Goal type 未注册或当前不可用。");
            }

            if (registration.Binder == null || registration.AdmissionPolicy == null)
            {
                return Fail(GoalValidationFailureCode.UnsupportedGoalType, "动作注册缺少 Binder 或 AdmissionPolicy。");
            }

            ActionAdmissionContext context = new(entityRegistry, reservations, worldState, actor, agent);
            return registration.AdmissionPolicy.Validate(
                proposal, registration.Descriptor, registration.Binder, context);
        }

        private bool TryGetActorHeldEntity(out string heldEntityId)
        {
            heldEntityId = string.Empty;
            foreach (WorldEntity entity in entityRegistry.RegisteredEntities)
            {
                if (entity != null && entity.TryGetComponent(out HeldItemState state) &&
                    state.IsHeld && state.HolderEntityId == actor.EntityId)
                {
                    heldEntityId = entity.EntityId;
                    return true;
                }
            }

            return false;
        }

        private bool HasCompletePath(Vector3 destination)
        {
            NavMeshPath path = new NavMeshPath();
            return NavMesh.CalculatePath(transform.position, destination, agent.areaMask, path) &&
                   path.status == NavMeshPathStatus.PathComplete;
        }

        private static GoalValidationResult Fail(GoalValidationFailureCode code, string detail)
        {
            return GoalValidationResult.Rejected(code, detail);
        }
    }

    public enum GoalValidationFailureCode
    {
        None,
        GoalMissing,
        UnsupportedGoalType,
        EntityRegistryNotReady,
        ReservationServiceNotReady,
        WorldStateServiceNotReady,
        ActorNotRegistered,
        TargetNotFound,
        DestinationNotFound,
        TargetNotItem,
        DestinationNotPlayer,
        TargetNotPickupable,
        TargetActionUnavailable,
        DestinationActionUnavailable,
        TargetInteractionPointMissing,
        DestinationInteractionPointMissing,
        TargetAlreadyHeld,
        TargetReserved,
        HandOccupied,
        ActorNotOnNavMesh,
        ActorBusy,
        TargetUnreachable,
        DestinationUnreachable,
        InvalidEntityId,
        TargetNotDoor,
        TargetAlreadySatisfied,
        TargetLocked,
    }

    public readonly struct GoalValidationResult
    {
        private GoalValidationResult(bool isApproved, IResolvedActionGoal resolvedGoal,
            GoalValidationFailureCode failureCode, string detail)
        {
            IsApproved = isApproved;
            ResolvedGoal = resolvedGoal;
            FailureCode = failureCode;
            Detail = detail;
        }

        public bool IsApproved { get; }
        public IResolvedActionGoal ResolvedGoal { get; }
        // Compatibility accessors are for legacy tests/diagnostics only; the formal runtime uses ResolvedGoal.
        public ResolvedBringObjectGoal ResolvedBringGoal => ResolvedGoal as ResolvedBringObjectGoal;
        public ResolvedMoveToEntityGoal ResolvedMoveGoal => ResolvedGoal as ResolvedMoveToEntityGoal;
        public GoalValidationFailureCode FailureCode { get; }
        public string Detail { get; }

        public static GoalValidationResult Approved(IResolvedActionGoal resolvedGoal)
        {
            return new GoalValidationResult(true,
                resolvedGoal ?? throw new ArgumentNullException(nameof(resolvedGoal)),
                GoalValidationFailureCode.None, string.Empty);
        }

        public static GoalValidationResult Rejected(GoalValidationFailureCode failureCode, string detail)
        {
            return new GoalValidationResult(false, null, failureCode, detail ?? string.Empty);
        }

        public override string ToString()
        {
            return IsApproved
                ? $"APPROVED type={ResolvedGoal.ActionId}; target={ResolvedGoal.Target?.EntityId}"
                : $"REJECTED code={FailureCode}; detail={Detail}";
        }
    }
}
