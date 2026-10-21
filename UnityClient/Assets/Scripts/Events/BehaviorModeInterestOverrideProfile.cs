using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    [Serializable]
    public sealed class BehaviorModeInterestOverride
    {
        [SerializeField] private string modeId;
        [SerializeField] private EventInterestRule[] rules = Array.Empty<EventInterestRule>();

        public BehaviorModeInterestOverride(string configuredModeId, params EventInterestRule[] configuredRules)
        {
            modeId = configuredModeId;
            rules = configuredRules ?? Array.Empty<EventInterestRule>();
        }

        public string ModeId => modeId ?? string.Empty;
        public IReadOnlyList<EventInterestRule> Rules => rules ?? Array.Empty<EventInterestRule>();

        public bool TryEvaluate(AgentEvent agentEvent, string receiverEntityId,
            out EventInterestDecision decision)
        {
            foreach (EventInterestRule rule in Rules)
            {
                if (rule == null || !rule.Matches(agentEvent, receiverEntityId)) continue;
                decision = rule.Decision;
                return true;
            }

            decision = EventInterestDecision.Ignore;
            return false;
        }
    }

    /// <summary>Optional exact EventInterest replacements selected by the agent's current mode.</summary>
    [CreateAssetMenu(fileName = "BehaviorModeInterestOverrides",
        menuName = "EmbodiedNPC/Behavior Mode Interest Overrides")]
    public sealed class BehaviorModeInterestOverrideProfile : ScriptableObject
    {
        [SerializeField] private BehaviorModeInterestOverride[] overrides =
            Array.Empty<BehaviorModeInterestOverride>();

        public IReadOnlyList<BehaviorModeInterestOverride> Overrides =>
            overrides ?? Array.Empty<BehaviorModeInterestOverride>();

        public void Configure(params BehaviorModeInterestOverride[] configuredOverrides) =>
            overrides = configuredOverrides ?? Array.Empty<BehaviorModeInterestOverride>();

        public bool TryEvaluate(string modeId, AgentEvent agentEvent, string receiverEntityId,
            out EventInterestDecision decision)
        {
            foreach (BehaviorModeInterestOverride modeOverride in Overrides)
            {
                if (modeOverride == null ||
                    !string.Equals(modeOverride.ModeId, modeId, StringComparison.Ordinal)) continue;
                return modeOverride.TryEvaluate(agentEvent, receiverEntityId, out decision);
            }

            decision = EventInterestDecision.Ignore;
            return false;
        }

        public bool TryValidateConfiguration(out string error)
        {
            HashSet<string> modeIds = new(StringComparer.Ordinal);
            foreach (BehaviorModeInterestOverride modeOverride in Overrides)
            {
                if (modeOverride == null || !BehaviorModeIds.IsValid(modeOverride.ModeId))
                {
                    error = "BehaviorMode interest overrides require a valid ModeId.";
                    return false;
                }
                if (!modeIds.Add(modeOverride.ModeId))
                {
                    error = $"Duplicate BehaviorMode interest override for '{modeOverride.ModeId}'.";
                    return false;
                }

                HashSet<string> ruleKeys = new(StringComparer.Ordinal);
                foreach (EventInterestRule rule in modeOverride.Rules)
                {
                    if (rule == null || string.IsNullOrWhiteSpace(rule.EventType))
                    {
                        error = $"BehaviorMode '{modeOverride.ModeId}' requires an EventType for every rule.";
                        return false;
                    }
                    if (!ruleKeys.Add(rule.ValidationKey))
                    {
                        error = $"Duplicate EventInterest rule in BehaviorMode '{modeOverride.ModeId}'.";
                        return false;
                    }
                }
            }

            error = string.Empty;
            return true;
        }
    }
}
