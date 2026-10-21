using System;
using System.Collections.Generic;
using EmbodiedNPC.Planning;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    /// <summary>Projects only Unity authoritative terminal GoalOutcome values into AgentEvent.</summary>
    [DisallowMultipleComponent]
    public sealed class ActionOutcomeAgentEventBridge : MonoBehaviour
    {
        [SerializeField] private GoalPlanRunner goalPlanRunner;
        [SerializeField] private AgentEventRouter router;
        private bool subscribed;

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();

        public void Configure(GoalPlanRunner runner, AgentEventRouter eventRouter)
        {
            Unsubscribe();
            goalPlanRunner = runner;
            router = eventRouter;
            if (isActiveAndEnabled) Subscribe();
        }

        public int Publish(GoalOutcome outcome)
        {
            if (router == null || string.IsNullOrWhiteSpace(outcome.ActorEntityId)) return 0;
            string eventType = outcome.Status == PlanExecutionStatus.Succeeded
                ? AgentEventTypes.ActionCompleted
                : AgentEventTypes.ActionFailed;
            Dictionary<string, string> payload = new(StringComparer.Ordinal)
            {
                [AgentEventPayloadKeys.ActionId] = outcome.GoalType,
                [AgentEventPayloadKeys.ActionTargetEntityId] = outcome.TargetEntityId,
                [AgentEventPayloadKeys.DestinationEntityId] = outcome.DestinationEntityId,
                [AgentEventPayloadKeys.FailureCode] = outcome.FailureCode,
                [AgentEventPayloadKeys.OutcomeStatus] = outcome.Status.ToString(),
                [AgentEventPayloadKeys.ProposalId] = outcome.ProposalId,
                [AgentEventPayloadKeys.ProposalRevision] = outcome.ProposalRevision.ToString(),
                [AgentEventPayloadKeys.TraceId] = outcome.TraceId,
            };
            AgentEvent agentEvent = new($"agent_event_{Guid.NewGuid():N}", eventType,
                outcome.ActorEntityId, outcome.ActorEntityId, string.Empty, payload,
                outcome.OccurredAt, outcome.WorldRevision);
            return router.Route(agentEvent);
        }

        private void HandleGoalOutcome(GoalOutcome outcome) => Publish(outcome);

        private void Subscribe()
        {
            if (subscribed || goalPlanRunner == null) return;
            goalPlanRunner.GoalOutcomePublished += HandleGoalOutcome;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            if (goalPlanRunner != null) goalPlanRunner.GoalOutcomePublished -= HandleGoalOutcome;
            subscribed = false;
        }
    }
}
