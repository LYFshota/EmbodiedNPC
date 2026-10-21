using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    [Serializable]
    public sealed class EventInterestRule
    {
        [SerializeField] private string eventType;
        [SerializeField] private string areaEntityId;
        [SerializeField] private EventInterestRelevance relevance;
        [SerializeField] private EventInterestDecision decision;
        [SerializeField] private string dailyModeId = "";

        public EventInterestRule(string configuredEventType, EventInterestDecision configuredDecision,
            string configuredAreaEntityId = "",
            EventInterestRelevance configuredRelevance = EventInterestRelevance.Any, string configuredDailyModeId = "")
        {
            eventType = configuredEventType;
            areaEntityId = configuredAreaEntityId;
            relevance = configuredRelevance;
            decision = configuredDecision;
            dailyModeId = configuredDailyModeId;
        }

        public string EventType => eventType ?? string.Empty;
        public string AreaEntityId => areaEntityId ?? string.Empty;
        public EventInterestRelevance Relevance => relevance;
        public EventInterestDecision Decision => decision;

        public bool Matches(AgentEvent agentEvent, string receiverEntityId, string currentDailyMode = "")
        {
            if (agentEvent == null || string.IsNullOrWhiteSpace(receiverEntityId) ||
                !string.Equals(EventType, agentEvent.EventType, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(dailyModeId) && dailyModeId != currentDailyMode) return false;
            if (!string.IsNullOrWhiteSpace(AreaEntityId) &&
                !string.Equals(AreaEntityId, agentEvent.AreaEntityId, StringComparison.Ordinal)) return false;

            return Relevance switch
            {
                EventInterestRelevance.Any => true,
                EventInterestRelevance.TargetSelf => string.Equals(
                    agentEvent.TargetEntityId, receiverEntityId, StringComparison.Ordinal),
                EventInterestRelevance.SourceSelf => string.Equals(
                    agentEvent.SourceEntityId, receiverEntityId, StringComparison.Ordinal),
                EventInterestRelevance.SocialResponseCandidate => IsSocialResponseCandidate(agentEvent, receiverEntityId),
                _ => false,
            };
        }

        internal static bool IsSocialResponseCandidate(AgentEvent agentEvent, string receiverEntityId) =>
            agentEvent.EventType == AgentEventTypes.PlayerEmote &&
            agentEvent.TryGetPayload(AgentEventPayloadKeys.SocialObserver, out string observerId) &&
            observerId == receiverEntityId &&
            agentEvent.TryGetPayload(AgentEventPayloadKeys.SocialResponseEligible, out string eligible) && eligible == "true";

        internal string ValidationKey => $"{EventType}\n{AreaEntityId}\n{(int)Relevance}\n{dailyModeId}";
    }

    /// <summary>Static authored rules only. Dynamic mode/state overrides are future insertion points.</summary>
    [CreateAssetMenu(fileName = "EventInterestProfile", menuName = "EmbodiedNPC/Event Interest Profile")]
    public sealed class EventInterestProfile : ScriptableObject
    {
        [SerializeField] private EventInterestDecision fallbackDecision = EventInterestDecision.Ignore;
        [SerializeField] private EventInterestRule[] rules = Array.Empty<EventInterestRule>();

        public EventInterestDecision FallbackDecision => fallbackDecision;
        public IReadOnlyList<EventInterestRule> Rules => rules ?? Array.Empty<EventInterestRule>();

        public void Configure(EventInterestDecision configuredFallback,
            params EventInterestRule[] configuredRules)
        {
            fallbackDecision = configuredFallback;
            rules = configuredRules ?? Array.Empty<EventInterestRule>();
        }

        public bool TryEvaluateStatic(AgentEvent agentEvent, string receiverEntityId,
            out EventInterestDecision decision, string currentDailyMode = "")
        {
            foreach (EventInterestRule rule in Rules)
            {
                if (rule == null || !rule.Matches(agentEvent, receiverEntityId, currentDailyMode)) continue;
                decision = rule.Decision;
                return true;
            }

            decision = fallbackDecision;
            return false;
        }

        public bool TryValidateConfiguration(out string error)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (EventInterestRule rule in Rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.EventType))
                {
                    error = "EventInterestProfile rules require an EventType.";
                    return false;
                }
                if (!keys.Add(rule.ValidationKey))
                {
                    error = $"Duplicate EventInterest rule for '{rule.EventType}'.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
    }
}
