using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;

namespace EmbodiedNPC.Cognition
{
    [Serializable]
    public sealed class CognitiveCharacterDto
    {
        public string character_id;
        public string display_name;
        public string role_summary;
        public string backstory;
        public string personality;
        public string speaking_style;
        public string boundaries;

        public bool IsValid => CharacterDefinition.ValidText(character_id, 120) &&
            CharacterDefinition.ValidText(display_name, CharacterDefinition.DisplayNameLimit) &&
            CharacterDefinition.ValidText(role_summary, CharacterDefinition.RoleSummaryLimit) &&
            CharacterDefinition.ValidText(backstory, CharacterDefinition.BackstoryLimit) &&
            CharacterDefinition.ValidText(personality, CharacterDefinition.PersonalityLimit) &&
            CharacterDefinition.ValidText(speaking_style, CharacterDefinition.SpeakingStyleLimit) &&
            CharacterDefinition.ValidText(boundaries, CharacterDefinition.BoundariesLimit);

        public static CognitiveCharacterDto From(CharacterDefinition definition) => definition == null ? null : new()
        {
            character_id = definition.CharacterId,
            display_name = definition.DisplayName,
            role_summary = definition.RoleSummary,
            backstory = definition.Backstory,
            personality = definition.Personality,
            speaking_style = definition.SpeakingStyle,
            boundaries = definition.Boundaries,
        };
    }

    [Serializable]
    public sealed class CognitiveTriggerDto
    {
        public string trigger_id;
        public string trigger_type;
        public string agent_entity_id;
        public string source_event_id;
        public double created_at;
        public string reason;

        public bool IsValid => !string.IsNullOrWhiteSpace(trigger_id) &&
                               !string.IsNullOrWhiteSpace(trigger_type) &&
                               !string.IsNullOrWhiteSpace(agent_entity_id);

        public static CognitiveTriggerDto From(CognitiveTrigger trigger) => trigger == null ? null : new()
        {
            trigger_id = trigger.TriggerId,
            trigger_type = trigger.TriggerType,
            agent_entity_id = trigger.AgentEntityId,
            source_event_id = trigger.SourceEventId,
            created_at = trigger.CreatedAt,
            reason = trigger.Reason,
        };
    }

    [Serializable]
    public sealed class CognitiveKeyValueDto
    {
        public string key;
        public string value;
    }

    [Serializable]
    public sealed class CognitiveEventDto
    {
        public string event_id;
        public string trace_id;
        public string source_observation_id;
        public string event_type;
        public string source_entity_id;
        public string target_entity_id;
        public string area_entity_id;
        public CognitiveKeyValueDto[] payload = Array.Empty<CognitiveKeyValueDto>();
        public double occurred_at;
        public int world_revision;
        public bool has_world_revision;

        public bool IsValid => !string.IsNullOrWhiteSpace(event_id) &&
                               !string.IsNullOrWhiteSpace(event_type) && payload != null &&
                               payload.All(item => item != null && !string.IsNullOrWhiteSpace(item.key));

        public static CognitiveEventDto From(AgentEvent value) => value == null ? null : new()
        {
            event_id = value.EventId,
            trace_id = value.TraceId,
            source_observation_id = value.SourceObservationId,
            event_type = value.EventType,
            source_entity_id = value.SourceEntityId,
            target_entity_id = value.TargetEntityId,
            area_entity_id = value.AreaEntityId,
            payload = value.Payload.Select(pair => new CognitiveKeyValueDto { key = pair.Key, value = pair.Value })
                .OrderBy(pair => pair.key, StringComparer.Ordinal).ToArray(),
            occurred_at = value.OccurredAt,
            world_revision = value.WorldRevision ?? 0,
            has_world_revision = value.WorldRevision.HasValue,
        };
    }

    [Serializable]
    public sealed class CognitiveBehaviorModeDto
    {
        public string mode_id;
        public bool IsValid => !string.IsNullOrWhiteSpace(mode_id);
    }

    [Serializable]
    public sealed class CognitivePlayerDto
    {
        public string player_entity_id;
        public string display_name;
        public bool IsValid => !string.IsNullOrWhiteSpace(player_entity_id) &&
                               !string.IsNullOrWhiteSpace(display_name);
    }

    /// <summary>One bounded, engine-neutral input snapshot for a CognitiveRun.</summary>
    [Serializable]
    public sealed class CognitiveDailyModeCandidateDto
    {
        public string target_mode;
        public string condition;
        public bool IsValid => CognitiveDailyBehaviorDto.IsMode(target_mode) &&
            !string.IsNullOrWhiteSpace(condition) && condition.Length <= 160;
    }
    [Serializable]
    public sealed class CognitiveDailyBehaviorDto
    {
        public string context_token;
        public string current_mode;
        public int mode_revision, profile_revision, condition_revision;
        public string objective, tendency;
        public string[] considered_activities = Array.Empty<string>();
        public CognitiveDailyModeCandidateDto[] candidates = Array.Empty<CognitiveDailyModeCandidateDto>();
        public string[] constraints = Array.Empty<string>();
        public static bool IsMode(string mode) => mode == "work" || mode == "rest" || mode == "leisure";
        public bool IsValid => !string.IsNullOrWhiteSpace(context_token) && context_token.Length <= 64 &&
            IsMode(current_mode) && mode_revision >= 0 && profile_revision > 0 && condition_revision >= 0 &&
            !string.IsNullOrWhiteSpace(objective) && objective.Length <= 160 &&
            !string.IsNullOrWhiteSpace(tendency) && tendency.Length <= 160 &&
            considered_activities != null && considered_activities.Length <= 6 &&
            considered_activities.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 48) &&
            candidates != null && candidates.Length <= 2 && candidates.All(x => x != null && x.IsValid && x.target_mode != current_mode) &&
            constraints != null && constraints.Length <= 4 && constraints.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 160);
    }

    [Serializable]
    public sealed class CounterWorkDto
    {
        public string target_entity_id;
        public string condition_token, event_id, trace_id;
        public bool needs_cleaning;
        public int condition_revision;
        public bool IsValid => CognitiveActionProposal.IsEntityId(target_entity_id) && condition_revision >= 0;
    }
    [Serializable]
    public sealed class CognitiveContextSnapshot
    {
        public string run_id;
        public string agent_entity_id;
        public CognitiveTriggerDto trigger;
        public CognitiveEventDto current_event;
        public CognitiveCharacterDto character;
        public CognitiveBehaviorModeDto behavior_mode;
        public CognitiveDailyBehaviorDto daily_behavior;
        public CognitivePlayerDto relevant_player;
        public PerceptionSnapshotDto perception;
        public CounterWorkDto counter_work;
        public SceneFactsDto scene_facts;
        public CognitiveCapabilityManifestDto capabilities;
        public CognitiveExecutionState runtime_state;

        public bool IsValid => !string.IsNullOrWhiteSpace(run_id) &&
                               !string.IsNullOrWhiteSpace(agent_entity_id) && trigger != null && trigger.IsValid &&
                               string.Equals(trigger.agent_entity_id, agent_entity_id, StringComparison.Ordinal) &&
                               (current_event == null || current_event.IsValid) && character != null && character.IsValid &&
                               (behavior_mode == null || behavior_mode.IsValid) &&
                               (daily_behavior == null || daily_behavior.IsValid) &&
                               (relevant_player == null || relevant_player.IsValid) && perception != null &&
                               perception.IsValid && string.Equals(perception.observer_entity_id, agent_entity_id,
                                   StringComparison.Ordinal) && (capabilities == null || capabilities.IsValid) &&
                               (scene_facts == null || scene_facts.IsValid) && (counter_work == null || counter_work.IsValid) &&
                               (runtime_state == null || runtime_state.IsValid && runtime_state.actor_entity_id == agent_entity_id &&
                                runtime_state.player_entity_id == relevant_player?.player_entity_id);
    }
}
