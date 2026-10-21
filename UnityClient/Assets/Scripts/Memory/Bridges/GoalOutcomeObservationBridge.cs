using System;
using System.Collections.Generic;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using UnityEngine;

namespace EmbodiedNPC.Memory
{
    /// <summary>Projects only this NPC's terminal Goal result into its own direct observations.</summary>
    [RequireComponent(typeof(DirectObservationReceiver), typeof(GoalPlanRunner))]
    public sealed class GoalOutcomeObservationBridge : MonoBehaviour
    {
        [SerializeField] private GoalPlanRunner goalPlanRunner;
        [SerializeField] private DirectObservationReceiver receiver;

        private void Awake()
        {
            if (goalPlanRunner == null) goalPlanRunner = GetComponent<GoalPlanRunner>();
            if (receiver == null) receiver = GetComponent<DirectObservationReceiver>();
        }

        private void OnEnable()
        {
            if (goalPlanRunner != null) goalPlanRunner.GoalOutcomePublished += HandleGoalOutcome;
        }

        private void OnDisable()
        {
            if (goalPlanRunner != null) goalPlanRunner.GoalOutcomePublished -= HandleGoalOutcome;
        }

        private void HandleGoalOutcome(GoalOutcome outcome) => Record(outcome);

        public ObservationApplyResult Record(GoalOutcome outcome)
        {
            if (receiver == null || outcome.ActorEntityId != receiver.ObserverEntityId)
                return new ObservationApplyResult(ObservationApplyCode.ObserverMismatch, null, 0);
            GameplayEventType eventType = ToEventType(outcome.Status);
            var gameplayEvent = new GameplayEvent(outcome.OutcomeId, eventType, outcome.ActorEntityId,
                outcome.TargetEntityId, outcome.TraceId, transform.position, outcome.WorldRevision, outcome.OccurredAt);
            var known = new Dictionary<string, string>
            {
                ["event_type"] = eventType.ToString(),
                ["goal_type"] = outcome.GoalType,
                ["status"] = outcome.Status.ToString(),
                ["actor_entity_id"] = outcome.ActorEntityId,
                ["target_entity_id"] = outcome.TargetEntityId,
                ["destination_entity_id"] = outcome.DestinationEntityId,
                ["failure_code"] = outcome.FailureCode,
                ["proposal_id"] = outcome.ProposalId,
                ["proposal_revision"] = outcome.ProposalRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            return receiver.Receive(gameplayEvent, outcome.TargetEntityId, known, new[] { "exact_position" });
        }

        private static GameplayEventType ToEventType(PlanExecutionStatus status) => status switch
        {
            PlanExecutionStatus.Succeeded => GameplayEventType.GoalSucceeded,
            PlanExecutionStatus.Failed => GameplayEventType.GoalFailed,
            PlanExecutionStatus.Cancelled => GameplayEventType.GoalCancelled,
            PlanExecutionStatus.TimedOut => GameplayEventType.GoalTimedOut,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }
}
