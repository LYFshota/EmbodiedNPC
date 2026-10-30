using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Planning
{
    /// <summary>One atomic bounded interaction in PlanExecutor. Never writes navigation destinations or Daily mode.</summary>
    public sealed class PrepareDrinkActionRunner : IPlanActionRunner
    {
        // CafePrepareDrink is a non-looping 2.5-second clip, played at .83x.
        // Commit only after its complete presentation, keeping the same transaction boundary.
        public const float PreparationSeconds = 2.5f / .83f;
        private readonly float preparationSeconds;
        private PlanActionExecutionContext context;
        private MotionChannelCoordinator motion;
        private float elapsed;
        public PlannerActionType ActionType => PlannerActionType.PrepareDrink;
        public bool IsRunning { get; private set; }
        // Instance-only timing seam for isolated acceptance; production callers retain the clip duration.
        public PrepareDrinkActionRunner(float preparationSeconds = PreparationSeconds)
        {
            if (float.IsNaN(preparationSeconds) || float.IsInfinity(preparationSeconds) || preparationSeconds <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(preparationSeconds));
            this.preparationSeconds = preparationSeconds;
        }
        public static bool Validate(ValidatedActionBindings bindings, EntityRegistry registry, ActionInstance action) =>
            bindings != null && DrinkTypes.ValidateRuntime(registry, bindings.Actor, bindings.Target, bindings.Destination) &&
            action.Bindings.TryGetValue("drink_type", out var drink) && DrinkTypes.IsValid(drink) &&
            action.Bindings.TryGetValue("station", out var station) && station == bindings.Target.EntityId &&
            action.Bindings.TryGetValue("actor", out var actor) && actor == bindings.Actor.EntityId &&
            action.Bindings.TryGetValue("player", out var player) && player == bindings.Destination.EntityId &&
            Vector3.Distance(bindings.Actor.transform.position,
                bindings.Target.GetComponent<DrinkPreparationStation>().ApproachPoint.position) <= .85f;

        public PlanActionRunResult Start(PlanActionExecutionContext value)
        {
            if (IsRunning) return PlanActionRunResult.Running();
            context = value; elapsed = 0;
            if (!IsValid()) return Stop(SkillStatus.Failed, "STATION_UNAVAILABLE");
            motion = context.Actor.GetComponent<MotionChannelCoordinator>();
            if (motion == null || !motion.isActiveAndEnabled || motion.IsEmergencyMotionActive)
                return Stop(SkillStatus.Failed, "INTERACTION_UNAVAILABLE");
            var station = context.Target.GetComponent<DrinkPreparationStation>();
            Vector3 facing = station.OperationPoint.position - context.Actor.transform.position; facing.y = 0;
            if (facing.sqrMagnitude > .001f) context.Actor.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);
            motion.SetInteractionActive(true); IsRunning = true;
            return PlanActionRunResult.Running();
        }
        public PlanActionRunResult Tick(float deltaTime)
        {
            if (!IsRunning) return new PlanActionRunResult(PlanActionRunStatus.Idle, default);
            if (!IsValid() || motion == null || !motion.isActiveAndEnabled || !motion.IsInteractionMotionActive || motion.IsEmergencyMotionActive)
                return Stop(SkillStatus.Failed, "STATION_UNAVAILABLE");
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return Stop(SkillStatus.Failed, "INVALID_TICK");
            elapsed += Mathf.Max(0, deltaTime);
            if (elapsed < preparationSeconds) return PlanActionRunResult.Running();
            context.Action.Bindings.TryGetValue("drink_type", out var drink);
            var result = context.WorldState.TryCommitDrink(context.ProposalId, context.TraceId, context.EntityRegistry,
                context.Actor, context.Target, context.Bindings.Destination, drink);
            Release();
            return new PlanActionRunResult(result.Status == SkillStatus.Succeeded ? PlanActionRunStatus.Succeeded : PlanActionRunStatus.Failed, result);
        }
        public PlanActionRunResult Cancel(string failureCode) => IsRunning ? Stop(SkillStatus.Cancelled, failureCode) :
            new PlanActionRunResult(PlanActionRunStatus.Idle, default);
        private bool IsValid() => context?.WorldState != null && context.Agent != null &&
            context.Agent.isActiveAndEnabled && context.Agent.isOnNavMesh &&
            Validate(context.Bindings, context.EntityRegistry, context.Action);
        private PlanActionRunResult Stop(SkillStatus status, string code)
        {
            Release();
            var result = context.WorldState.RecordAttempt(context.SkillId, context.TraceId, "PrepareDrink",
                context.Actor, context.Target, context.Bindings.Destination, status, code);
            return new PlanActionRunResult(status == SkillStatus.Cancelled ? PlanActionRunStatus.Cancelled : PlanActionRunStatus.Failed, result);
        }
        private void Release() { IsRunning = false; if (motion != null) motion.SetInteractionActive(false); }
    }
}
