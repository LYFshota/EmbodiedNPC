using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Planning
{
    /// <summary>Atomic logical service; no entities are picked up or held. PlanExecutor owns cancellation.</summary>
    public sealed class ServeSnackActionRunner : IPlanActionRunner
    {
        // Reach, pause and retract, with no prop ownership. Commit only after the full presentation.
        public const float ServiceSeconds = 2.4f;
        private readonly float serviceSeconds;
        private PlanActionExecutionContext context;
        private MotionChannelCoordinator motion;
        private float elapsed;
        public PlannerActionType ActionType => PlannerActionType.ServeSnack;
        public bool IsRunning { get; private set; }
        // Instance-only timing seam for isolated acceptance; production callers retain the clip duration.
        public ServeSnackActionRunner(float serviceSeconds = ServiceSeconds)
        {
            if (float.IsNaN(serviceSeconds) || float.IsInfinity(serviceSeconds) || serviceSeconds <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(serviceSeconds));
            this.serviceSeconds = serviceSeconds;
        }
        public static bool Validate(ValidatedActionBindings bindings, EntityRegistry registry, ActionInstance action) =>
            bindings != null && action.Bindings.TryGetValue("snack_type", out var snack) &&
            SnackServiceCapability.ValidateRuntime(registry, bindings.Actor, bindings.Target, bindings.Destination, snack) &&
            action.Bindings.TryGetValue("quantity", out var quantity) && SnackCatalog.IsQuantity(quantity) &&
            action.Bindings.TryGetValue("station", out var station) && station == bindings.Target.EntityId &&
            action.Bindings.TryGetValue("actor", out var actor) && actor == bindings.Actor.EntityId &&
            action.Bindings.TryGetValue("player", out var player) && player == bindings.Destination.EntityId &&
            Vector3.Distance(bindings.Actor.transform.position,
                bindings.Actor.GetComponent<SnackServiceCapability>().Approach(snack).position) <= .3f;

        public PlanActionRunResult Start(PlanActionExecutionContext value)
        {
            if (IsRunning) return PlanActionRunResult.Running();
            context = value; elapsed = 0;
            if (!IsValid()) return Stop(SkillStatus.Failed, "STATION_UNAVAILABLE");
            motion = context.Actor.GetComponent<MotionChannelCoordinator>();
            if (motion == null || !motion.isActiveAndEnabled || motion.IsEmergencyMotionActive || motion.IsCarryPresentationActive)
                return Stop(SkillStatus.Failed, "INTERACTION_UNAVAILABLE");
            var station = context.Actor.GetComponent<SnackServiceCapability>();
            context.Action.Bindings.TryGetValue("snack_type", out var snack);
            Vector3 facing = station.Operation(snack).position - context.Actor.transform.position; facing.y = 0;
            if (facing.sqrMagnitude > .001f) context.Actor.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);
            motion.SetInteractionActive(true); IsRunning = true;
            return PlanActionRunResult.Running();
        }
        public PlanActionRunResult Tick(float deltaTime)
        {
            if (!IsRunning) return new PlanActionRunResult(PlanActionRunStatus.Idle, default);
            if (!IsValid() || motion == null || !motion.isActiveAndEnabled || !motion.IsInteractionMotionActive || motion.IsEmergencyMotionActive || motion.IsCarryPresentationActive)
                return Stop(SkillStatus.Failed, "STATION_UNAVAILABLE");
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return Stop(SkillStatus.Failed, "INVALID_TICK");
            elapsed += Mathf.Max(0, deltaTime);
            if (elapsed < serviceSeconds) return PlanActionRunResult.Running();
            context.Action.Bindings.TryGetValue("snack_type", out var snack);
            context.Action.Bindings.TryGetValue("quantity", out var quantity);
            var result = context.WorldState.TryCommitSnack(context.ProposalId, context.TraceId, context.EntityRegistry,
                context.Actor, context.Target, context.Bindings.Destination, snack, quantity);
            Release();
            return new PlanActionRunResult(result.Status == SkillStatus.Succeeded ? PlanActionRunStatus.Succeeded : PlanActionRunStatus.Failed, result);
        }
        public PlanActionRunResult Cancel(string failureCode) => IsRunning ? Stop(SkillStatus.Cancelled, failureCode) :
            new PlanActionRunResult(PlanActionRunStatus.Idle, default);
        private bool IsValid() => context?.WorldState != null && context.Agent != null &&
            context.Agent.isActiveAndEnabled && context.Agent.isOnNavMesh &&
            Validate(context.Bindings, context.EntityRegistry, context.Action) &&
            context.Actor.GetComponent<DrinkPreparationPresenter>()?.IsSnackReady == true;
        private PlanActionRunResult Stop(SkillStatus status, string code)
        {
            Release();
            var result = context.WorldState.RecordAttempt(context.SkillId, context.TraceId, "ServeSnack",
                context.Actor, context.Target, context.Bindings.Destination, status, code);
            return new PlanActionRunResult(status == SkillStatus.Cancelled ? PlanActionRunStatus.Cancelled : PlanActionRunStatus.Failed, result);
        }
        private void Release() { IsRunning = false; if (motion != null) motion.SetInteractionActive(false); }
    }
}
