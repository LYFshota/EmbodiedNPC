using EmbodiedNPC.Cognition;
using EmbodiedNPC.Memory;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Planning
{
    public sealed partial class GoalPlanRunner
    {
        // Retain only the Runner's latest accepted feedback and final outcome. No replay queue,
        // persisted activity flag, independent transition machine, or per-navigation-step history.
        private TaskFeedbackSnapshot executionFeedback;
        private GoalOutcome? executionOutcome;
        private WorldEntity executionActor, executionPlayer;
        private PersistentIdentityKey executionActorIdentity, executionPlayerIdentity;
        private string executionSession, executionScene;
        private UnityEngine.SceneManagement.Scene executionSceneOwner;

        internal static string SceneInstance(Component owner)
        {
            string token = null;
            foreach (var bridge in FindObjectsByType<TaskRuntimeBridge>(FindObjectsInactive.Include))
                if (bridge.gameObject.scene == owner.gameObject.scene)
                {
                    if (token != null) return null; // Ambiguous scope is not a trustworthy current snapshot.
                    token = bridge.DiagnosticSceneInstanceId;
                }
            return token;
        }

        private void BindExecutionContext()
        {
            executionFeedback = CurrentTaskFeedback;
            executionOutcome = null;
            executionActor = actor;
            executionPlayer = null;
            executionActorIdentity = executionPlayerIdentity = default;
            executionSession = ClientSessionIdentity.Current;
            executionScene = SceneInstance(this);
            executionSceneOwner = gameObject.scene;
            if (executionActor != null) executionActor.TryGetPersistentIdentityKey(out executionActorIdentity);
            if (entityRegistry != null && entityRegistry.TryGetEntity(executionFeedback.Player, out var player) &&
                player.IsAgent(AgentRole.Player))
            {
                executionPlayer = player;
                player.TryGetPersistentIdentityKey(out executionPlayerIdentity);
            }
        }

        // One main-thread transaction on the existing execution owner. No queue, resume state or replay.
        public string CancelOwnedGoal(CognitiveExecutionState expected)
        {
            var current = CaptureExecutionState(expected?.player_entity_id);
            if (expected == null || !expected.IsValid || expected.status != "IN_PROGRESS" ||
                expected.allowed_controls == null || System.Array.IndexOf(expected.allowed_controls, "cancel_active_goal") < 0 ||
                current.status == "UNKNOWN" || current.actor_entity_id != expected.actor_entity_id ||
                current.player_entity_id != expected.player_entity_id || current.source_session_id != expected.source_session_id ||
                current.scene_instance_id != expected.scene_instance_id || current.proposal_id != expected.proposal_id ||
                current.proposal_revision != expected.proposal_revision || current.trace_id != expected.trace_id ||
                current.world_revision < expected.world_revision) return "unconfirmed";
            if (current.status != "IN_PROGRESS")
                return current.status == "CANCELLED" ? "already_cancelled" : current.status == "SUCCEEDED" ? "already_completed" : "already_ended";
            if (current.allowed_controls == null || System.Array.IndexOf(current.allowed_controls, "cancel_active_goal") < 0)
                return "not_allowed";
            InterruptCurrentPlan("CANCELLED_BY_PLAYER_CONTROL");
            current = CaptureExecutionState(expected.player_entity_id);
            return current.status == "CANCELLED" && current.proposal_id == expected.proposal_id &&
                current.proposal_revision == expected.proposal_revision && current.trace_id == expected.trace_id
                ? "cancelled" : "unconfirmed";
        }

        public CognitiveExecutionState CaptureExecutionState(string requesterEntityId)
        {
            var state = CognitiveExecutionState.Unknown(actor?.EntityId ?? activeActorEntityId, requesterEntityId,
                SceneInstance(this), worldState == null ? 0 : worldState.WorldRevision);
            if (!isActiveAndEnabled || worldState == null || !worldState.isActiveAndEnabled ||
                !gameObject.scene.isLoaded || executionFeedback == null || executionActor == null || executionPlayer == null ||
                actor != executionActor || !executionActor.isActiveAndEnabled || !executionPlayer.isActiveAndEnabled ||
                !executionActor.IsAgent(AgentRole.Npc) || !executionPlayer.IsAgent(AgentRole.Player) ||
                !executionActorIdentity.IsValid || !executionPlayerIdentity.IsValid ||
                !executionActor.TryGetPersistentIdentityKey(out var actorIdentity) || !actorIdentity.Equals(executionActorIdentity) ||
                !executionPlayer.TryGetPersistentIdentityKey(out var playerIdentity) || !playerIdentity.Equals(executionPlayerIdentity) ||
                executionPlayer.EntityId != requesterEntityId || executionFeedback.Player != requesterEntityId ||
                executionFeedback.Key.Actor != actor.EntityId || executionSession != state.source_session_id ||
                executionScene == null || executionScene != state.scene_instance_id || executionSceneOwner != gameObject.scene ||
                entityRegistry == null || !entityRegistry.TryGetEntity(actor.EntityId, out var registeredActor) || registeredActor != actor ||
                !entityRegistry.TryGetEntity(requesterEntityId, out var registeredPlayer) || registeredPlayer != executionPlayer)
                return state;

            var key = executionFeedback.Key;
            if (executionOutcome.HasValue)
            {
                var outcome = executionOutcome.Value;
                // Only HandleCompleted writes this evidence. Late/repeated presentation publications
                // cannot replace it. Old terminal evidence belongs in historical Recent Action Results.
                if (!TaskFeedbackKey.From(outcome).Equals(key) || outcome.OccurredAt > state.captured_at ||
                    state.captured_at - outcome.OccurredAt > 900 || outcome.WorldRevision > state.world_revision) return state;
                state.status = outcome.Status switch
                {
                    PlanExecutionStatus.Succeeded => "SUCCEEDED",
                    PlanExecutionStatus.Cancelled => "CANCELLED",
                    PlanExecutionStatus.Failed or PlanExecutionStatus.TimedOut => "FAILED",
                    _ => "UNKNOWN",
                };
                if (state.status == "UNKNOWN") return state;
                if (state.status == "SUCCEEDED" && executionFeedback.ActionId == "prepare_drink")
                {
                    if (!worldState.TryGetDrinkReceipt(key.Actor, key.Proposal, key.Trace, out var receipt) ||
                        receipt.Player != requesterEntityId || receipt.DrinkType != executionFeedback.DrinkType || receipt.WorldRevision > outcome.WorldRevision ||
                        !receipt.ActorIdentity.Equals(actorIdentity) || !receipt.PlayerIdentity.Equals(playerIdentity))
                        return CognitiveExecutionState.Unknown(state.actor_entity_id, requesterEntityId, state.scene_instance_id, state.world_revision);
                    state.receipt_id = receipt.Id;
                }
                state.outcome_id = outcome.OutcomeId;
                state.outcome_world_revision = outcome.WorldRevision;
                state.failure_code = string.IsNullOrEmpty(outcome.FailureCode) ? null : outcome.FailureCode;
                state.stage = "terminal";
            }
            else
            {
                if (!IsExecuting || CurrentTaskFeedback == null || !CurrentTaskFeedback.Key.Equals(key) ||
                    ActiveProposalId != key.Proposal || ActiveProposalRevision != key.Revision || activeTraceId != key.Trace) return state;
                state.status = "IN_PROGRESS";
                // Immediate player-owned goals only. Saved tasks keep TaskExecutionBridge authority.
                if (GetComponent<TaskExecutionBridge>()?.Owns(key.Proposal, key.Trace) != true)
                    state.allowed_controls = new[] { "cancel_active_goal" };
                state.stage = executionFeedback.Progress.Kind.ToString().ToLowerInvariant();
                state.step = executionFeedback.Progress.Action?.ToString();
            }
            state.proposal_id = key.Proposal; state.proposal_revision = key.Revision; state.trace_id = key.Trace;
            state.action_id = executionFeedback.ActionId;
            state.drink_type = string.IsNullOrEmpty(executionFeedback.DrinkType) ? null : executionFeedback.DrinkType;
            return state;
        }
    }
}
