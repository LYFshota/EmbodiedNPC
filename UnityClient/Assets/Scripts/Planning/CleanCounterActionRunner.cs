using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Planning
{
    public sealed class CleanCounterActionRunner : IPlanActionRunner
    {
        public const float CleaningSeconds = 2.5f;
        private PlanActionExecutionContext context;
        private MotionChannelCoordinator motion;
        private float elapsed;
        private bool originalUpdateRotation, ownsFacing;
        public PlannerActionType ActionType => PlannerActionType.CleanCounter;
        public bool IsRunning { get; private set; }
        public static bool Validate(ValidatedActionBindings bindings, EntityRegistry registry, ActionInstance action) =>
            bindings != null && action.Bindings.TryGetValue("condition", out var version) && int.TryParse(version, out var condition) &&
            CleanCounterRules.ValidateRuntime(registry, bindings.Actor, bindings.Target, condition) &&
            action.Bindings.TryGetValue("actor", out var actor) && actor == bindings.Actor.EntityId &&
            action.Bindings.TryGetValue("target", out var target) && target == bindings.Target.EntityId &&
            Vector3.Distance(bindings.Actor.transform.position, bindings.Target.GetComponent<CounterCleaningStation>().ApproachPoint.position) <= .5f;
        public PlanActionRunResult Start(PlanActionExecutionContext value)
        {
            context = value; elapsed = 0;
            if (!IsValid()) return Stop(SkillStatus.Failed, "COUNTER_UNAVAILABLE");
            motion = context.Actor.GetComponent<MotionChannelCoordinator>();
            if (motion == null || !motion.isActiveAndEnabled || motion.IsEmergencyMotionActive || motion.IsCarryPresentationActive)
                return Stop(SkillStatus.Failed, "INTERACTION_UNAVAILABLE");
            originalUpdateRotation = context.Agent.updateRotation; context.Agent.updateRotation = false; ownsFacing = true;
            var facing = context.Target.GetComponent<CounterCleaningStation>().OperationPoint.position - context.Actor.transform.position; facing.y = 0;
            if (facing.sqrMagnitude > .001f) context.Actor.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);
            motion.SetInteractionActive(true); IsRunning = true; return PlanActionRunResult.Running();
        }
        public PlanActionRunResult Tick(float deltaTime)
        {
            if (!IsRunning) return new PlanActionRunResult(PlanActionRunStatus.Idle, default);
            if (!IsValid() || motion == null || !motion.isActiveAndEnabled || !motion.IsInteractionMotionActive ||
                motion.IsEmergencyMotionActive || motion.IsCarryPresentationActive) return Stop(SkillStatus.Failed, "COUNTER_UNAVAILABLE");
            if (!float.IsFinite(deltaTime)) return Stop(SkillStatus.Failed, "INVALID_TICK");
            elapsed += Mathf.Max(0, deltaTime); if (elapsed < CleaningSeconds) return PlanActionRunResult.Running();
            int.TryParse(context.Action.Bindings["condition"], out var condition);
            var result = context.WorldState.TryCommitCounterCleaning(context.ProposalId, context.TraceId, context.EntityRegistry, context.Actor, context.Target, condition, context.Reservations, context.ReservationId);
            Release(); return new PlanActionRunResult(result.Status == SkillStatus.Succeeded ? PlanActionRunStatus.Succeeded : PlanActionRunStatus.Failed, result);
        }
        public PlanActionRunResult Cancel(string code) => IsRunning ? Stop(SkillStatus.Cancelled, code) : new PlanActionRunResult(PlanActionRunStatus.Idle, default);
        private bool IsValid() => context?.WorldState != null && context.Agent != null && context.Agent.isActiveAndEnabled && context.Agent.isOnNavMesh &&
            Validate(context.Bindings, context.EntityRegistry, context.Action) && context.Reservations != null && context.Reservations.isActiveAndEnabled &&
            context.Reservations.TryGetSnapshot(context.ReservationId, out var lease) && lease.OwnerEntityId == context.Actor.EntityId &&
            lease.TargetEntityId == context.Target.EntityId && lease.TraceId == context.TraceId && !lease.IsExpired;
        private PlanActionRunResult Stop(SkillStatus status, string code)
        {
            Release(); var result = context.WorldState.RecordAttempt(context.SkillId, context.TraceId, "CleanCounter", context.Actor, context.Target, null, status, code);
            return new PlanActionRunResult(status == SkillStatus.Cancelled ? PlanActionRunStatus.Cancelled : PlanActionRunStatus.Failed, result);
        }
        private void Release()
        {
            IsRunning = false; if (motion != null) motion.SetInteractionActive(false);
            if (ownsFacing && context?.Agent != null) context.Agent.updateRotation = originalUpdateRotation;
            ownsFacing = false;
        }
    }
}
