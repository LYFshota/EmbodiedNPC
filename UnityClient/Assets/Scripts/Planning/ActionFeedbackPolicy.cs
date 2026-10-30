using System;
using System.Collections.Generic;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Cognition;

namespace EmbodiedNPC.Planning
{
    /// <summary>Deterministic boundary deciding which exhausted failures require semantic help.</summary>
    public static class ActionFeedbackPolicy
    {
        private static readonly HashSet<string> LocalOnlyCodes = new(StringComparer.Ordinal)
        {
            "PLAN_ALREADY_EXECUTING", "THREAT_RESPONSE_ACTIVE", "STALE_WORLD_REVISION",
            "REFERENCE_MISSING", "PLAN_EXECUTOR_UNAVAILABLE", "RUNNER_DISABLED",
            "CANCELLED_BY_REQUEST", "INTERRUPTED_BY_THREAT", "BACKEND_UNAVAILABLE",
            "WORLD_STATE_UNAVAILABLE", "CONNECTION_LOST", "PROVIDER_TIMEOUT",
        };

        public static bool ShouldRequestCognition(string failureCode, PlanExecutionStatus status)
        {
            if (string.IsNullOrWhiteSpace(failureCode) || LocalOnlyCodes.Contains(failureCode)) return false;
            return status is PlanExecutionStatus.Failed or PlanExecutionStatus.TimedOut;
        }

        /// <summary>Build exact alternative Goals from currently registered, pickup-capable Unity entities.</summary>
        public static bool IsSpatialEvidenceFailure(string code) => code == "TARGET_NOT_REDISCOVERED" ||
            code == "REDISCOVERY_PATH_UNAVAILABLE" || code == "TARGET_LOCATION_UNKNOWN" ||
            code == "TARGET_NOT_CURRENTLY_VISIBLE" || code == "REDISCOVERY_TIMEOUT";

        /// <summary>Builds only alternatives permitted by the original execution authorization.</summary>
        public static CognitiveActionAlternative[] BuildAlternatives(ActionRegistration registration,
            ActionCandidateContext context, GoalProposal failedGoal, int maxAlternatives = 4)
        {
            if (registration?.CandidateProvider == null || registration.RecoveryPolicy == null ||
                context == null || failedGoal == null || maxAlternatives <= 0 ||
                !string.Equals(registration.Descriptor.ActionId, failedGoal.type, StringComparison.Ordinal))
                return Array.Empty<CognitiveActionAlternative>();

            ActionRecoveryDecision decision = registration.RecoveryPolicy.Decide(
                failedGoal.target_entity_id, registration.Descriptor, registration.CandidateProvider, context);
            if (decision.Disposition != ActionRecoveryDisposition.OfferAlternatives)
                return Array.Empty<CognitiveActionAlternative>();

            List<CognitiveActionAlternative> alternatives = new();
            foreach (var entity in decision.Candidates)
            {
                alternatives.Add(new CognitiveActionAlternative
                {
                    alternative_id = $"bring_{entity.EntityId}",
                    description = $"改为交付 {entity.name}",
                    goal = new GoalProposal
                    {
                        type = registration.Descriptor.ActionId,
                        target_entity_id = entity.EntityId,
                        destination_entity_id = failedGoal.destination_entity_id,
                    },
                });
                if (alternatives.Count >= maxAlternatives) break;
            }
            return alternatives.ToArray();
        }
    }
}
