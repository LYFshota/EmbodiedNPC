using System;

namespace EmbodiedNPC.Events
{
    /// <summary>Deterministic interest decision: static profile, optional mode override, mandatory floor.</summary>
    public static class EventInterestEvaluator
    {
        public static EventInterestDecision Evaluate(AgentEvent agentEvent, string receiverEntityId,
            EventInterestProfile profile, string currentBehaviorModeId = "",
            BehaviorModeInterestOverrideProfile modeOverrides = null, string currentDailyMode = "")
        {
            if (agentEvent == null || string.IsNullOrWhiteSpace(receiverEntityId) || profile == null ||
                !profile.TryValidateConfiguration(out _))
                return EventInterestDecision.Ignore;

            EventInterestDecision contextualDecision = profile.FallbackDecision;
            if (profile.TryEvaluateStatic(agentEvent, receiverEntityId,
                    out EventInterestDecision matchedDecision, currentDailyMode))
                contextualDecision = matchedDecision;

            if (BehaviorModeIds.IsValid(currentBehaviorModeId) && modeOverrides != null &&
                modeOverrides.TryValidateConfiguration(out _) &&
                modeOverrides.TryEvaluate(currentBehaviorModeId, agentEvent, receiverEntityId,
                    out EventInterestDecision modeDecision))
                contextualDecision = modeDecision;

            EventInterestDecision mandatoryFloor = GetMandatoryFloor(agentEvent, receiverEntityId);
            // Authored overrides may suppress social attention, but cannot promote a bystander to responder.
            if (agentEvent.EventType == AgentEventTypes.PlayerEmote &&
                !EventInterestRule.IsSocialResponseCandidate(agentEvent, receiverEntityId))
                return contextualDecision == EventInterestDecision.Ignore ? EventInterestDecision.Ignore : EventInterestDecision.Record;
            return Stronger(contextualDecision, mandatoryFloor);
        }

        private static EventInterestDecision GetMandatoryFloor(AgentEvent agentEvent, string receiverEntityId)
        {
            if (IsDirectUtteranceToSelf(agentEvent, receiverEntityId) ||
                IsSelfAction(agentEvent, receiverEntityId, AgentEventTypes.ActionFailed))
                return EventInterestDecision.Wake;
            return IsSelfAction(agentEvent, receiverEntityId, AgentEventTypes.ActionCompleted)
                ? EventInterestDecision.Record
                : EventInterestDecision.Ignore;
        }

        private static EventInterestDecision Stronger(EventInterestDecision contextualDecision,
            EventInterestDecision mandatoryFloor)
        {
            return Strength(contextualDecision) > Strength(mandatoryFloor)
                ? contextualDecision
                : mandatoryFloor;
        }

        private static int Strength(EventInterestDecision decision) => decision switch
        {
            EventInterestDecision.Ignore => 0,
            EventInterestDecision.Record => 1,
            EventInterestDecision.Wake => 2,
            EventInterestDecision.FastPath => 3,
            _ => 0,
        };

        private static bool IsDirectUtteranceToSelf(AgentEvent agentEvent, string receiverEntityId) =>
            string.Equals(agentEvent.EventType, AgentEventTypes.PlayerUtterance, StringComparison.Ordinal) &&
            string.Equals(agentEvent.TargetEntityId, receiverEntityId, StringComparison.Ordinal);

        private static bool IsSelfAction(AgentEvent agentEvent, string receiverEntityId, string eventType) =>
            string.Equals(agentEvent.EventType, eventType, StringComparison.Ordinal) &&
            string.Equals(agentEvent.SourceEntityId, receiverEntityId, StringComparison.Ordinal);
    }
}
