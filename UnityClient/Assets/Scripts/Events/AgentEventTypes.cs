namespace EmbodiedNPC.Events
{
    public static class AgentEventTypes
    {
        public const string PlayerEnteredArea = "player.entered_area";
        public const string PlayerLeftArea = "player.left_area";
        public const string PlayerUtterance = "player.utterance";
        public const string CounterNeedsCleaning = "counter.needs_cleaning";
        public const string PlayerDwelledInServiceZone = "player.dwelled_in_service_zone";
        public const string DialogueContinue = "npc.dialogue_continue";
        public const string TaskConsideration = "npc.task_consideration";
        public const string PlayerEmote = "player.emote";
        public const string SpeechPresented = "speech.presented";
        public const string HeardSpeech = "heard_speech";
        public const string EntityBecameVisible = "entity.became_visible";
        public const string ActionCompleted = "action.completed";
        public const string ActionFailed = "action.failed";
        public const string WorkBreakStarted = "work.break_started";
        public const string WorkBreakEnded = "work.break_ended";
    }

    public static class AgentEventPayloadKeys
    {
        public const string Text = "text";
        public const string EmoteId = "emote_id";
        public const string SocialObserver = "social_observer_entity_id";
        public const string SocialResponseEligible = "social_response_eligible";
        public const string ConversationId = "conversation_id";
        public const string ActionId = "action_id";
        public const string ActionTargetEntityId = "action_target_entity_id";
        public const string DestinationEntityId = "destination_entity_id";
        public const string FailureCode = "failure_code";
        public const string OutcomeStatus = "outcome_status";
        public const string ProposalId = "proposal_id";
        public const string ProposalRevision = "proposal_revision";
        public const string TraceId = "trace_id";
    }
}
