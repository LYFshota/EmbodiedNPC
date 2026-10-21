using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EmbodiedNPC.Perception
{
    /// <summary>Small deterministic facts for planners. It intentionally contains no prose or hidden WorldState.</summary>
    public sealed class PlannerFacts
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Values => new ReadOnlyDictionary<string, string>(values);
        public bool Has(string key) => values.ContainsKey(key);
        public string Get(string key) => values.TryGetValue(key, out string value) ? value : string.Empty;
        internal void Set(string key, string value = "true") => values[key] = value;
    }
}
