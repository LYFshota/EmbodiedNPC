using System;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Derives Cafe v1 new-world-action eligibility from the owned current Event Run.</summary>
    internal static class WorldActionEligibility
    {
        internal static bool MayProposeNewWorldAction(CognitiveRequest request, CognitiveRun run,
            WorldEntity owner, CognitiveActionProposal proposal = null)
        {
            if (request == null || !request.IsValid || run == null ||
                run.State != CognitiveRunState.Running || owner == null) return false;

            if (run.IsCounterCleaningConsideration)
                return proposal?.action_id == "clean_counter" && proposal.bindings?.Count == 1 &&
                    proposal.bindings.TryGetValue("target", out var target) && target == request.context.counter_work?.target_entity_id &&
                    (owner.GetComponent<EmbodiedNPC.Routines.CounterCleaningCognitionSource>()?.Matches(request, run) ?? false);
            if(run.IsTaskConsideration)
                return owner.GetComponent<TaskExecutionBridge>()?.HasPermission(request,run,proposal)??false;
            CognitiveRunDto runDto = request.run;
            CognitiveTriggerDto trigger = request.trigger;
            CognitiveContextSnapshot context = request.context;
            CognitiveEventDto currentEvent = context.current_event;
            CognitivePlayerDto player = context.relevant_player;
            return string.Equals(run.RunId, runDto.run_id, StringComparison.Ordinal) &&
                   string.Equals(run.AgentEntityId, owner.EntityId, StringComparison.Ordinal) &&
                   string.Equals(runDto.agent_entity_id, owner.EntityId, StringComparison.Ordinal) &&
                   string.Equals(run.TriggerId, trigger.trigger_id, StringComparison.Ordinal) &&
                   string.Equals(run.TriggerType, CognitiveTriggerTypes.Event, StringComparison.Ordinal) &&
                   string.Equals(runDto.trigger_type, run.TriggerType, StringComparison.Ordinal) &&
                   string.Equals(trigger.trigger_type, run.TriggerType, StringComparison.Ordinal) &&
                   currentEvent != null &&
                   !string.IsNullOrWhiteSpace(trigger.source_event_id) &&
                   string.Equals(trigger.source_event_id, currentEvent.event_id, StringComparison.Ordinal) &&
                   string.Equals(currentEvent.event_type, AgentEventTypes.PlayerUtterance,
                       StringComparison.Ordinal) &&
                   string.Equals(currentEvent.target_entity_id, owner.EntityId, StringComparison.Ordinal) &&
                   player != null && !string.IsNullOrWhiteSpace(currentEvent.source_entity_id) &&
                   string.Equals(currentEvent.source_entity_id, player.player_entity_id,
                       StringComparison.Ordinal) &&
                   (string.IsNullOrWhiteSpace(currentEvent.trace_id) ||
                    string.Equals(currentEvent.trace_id, request.trace_id, StringComparison.Ordinal));
        }
    }
}
