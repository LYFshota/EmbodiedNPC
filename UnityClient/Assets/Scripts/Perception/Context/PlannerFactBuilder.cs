using System;
using System.Collections.Generic;

namespace EmbodiedNPC.Perception
{
    /// <summary>Projects one observer's valid evidence into stable, machine-readable planner facts.</summary>
    public sealed class PlannerFactBuilder
    {
        public PlannerFacts Build(PerceptionAggregator state, double now)
        {
            PlannerFacts facts = new();
            if (state == null) return facts;

            foreach (KeyValuePair<string, PerceivedEntityRecord> pair in state.Entities)
            {
                PerceivedEntityRecord record = pair.Value;
                if (record.LifecycleState == PerceptionLifecycleState.Forgotten) continue;
                if (record.KnownFields.TryGetValue("visibility_state", out string visibility) && visibility == "current_visible")
                    facts.Set($"visible:{pair.Key}");
                if (record.KnownFields.TryGetValue("proximity_state", out string proximity) && proximity == "nearby")
                    facts.Set($"nearby:{pair.Key}");
            }

            foreach (Observation observation in state.Observations)
            {
                if (observation.ExpiresAt < now || observation.Channel != ObservationChannel.Audio) continue;
                if (!observation.KnownFields.TryGetValue("event_type", out string eventType)) continue;
                facts.Set($"heard:{eventType}");
                if (string.Equals(eventType, GameplayEventType.WeaponDrawn.ToString(), StringComparison.Ordinal))
                    facts.Set("danger_nearby");
            }
            return facts;
        }
    }
}
