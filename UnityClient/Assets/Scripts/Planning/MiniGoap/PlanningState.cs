using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace EmbodiedNPC.Planning.MiniGoap
{
    /// <summary>
    /// Immutable symbolic state used only by the planner's simulation.
    /// It is deliberately separate from perceived PlannerFacts and authoritative Unity objects.
    /// </summary>
    public sealed class PlanningState : IEquatable<PlanningState>
    {
        private readonly Dictionary<string, string> values;
        private readonly IReadOnlyDictionary<string, string> readOnlyValues;
        private readonly string canonicalValue;
        private readonly int stableHashCode;

        public PlanningState(IEnumerable<KeyValuePair<string, string>> source)
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (source != null)
            {
                foreach (KeyValuePair<string, string> pair in source)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        throw new ArgumentException("Planning fact keys cannot be empty.", nameof(source));
                    }

                    if (!values.TryAdd(pair.Key, pair.Value ?? string.Empty))
                    {
                        throw new ArgumentException($"Duplicate planning fact key: {pair.Key}", nameof(source));
                    }
                }
            }

            readOnlyValues = new ReadOnlyDictionary<string, string>(values);
            canonicalValue = BuildCanonicalValue(values);
            stableHashCode = ComputeStableHashCode(canonicalValue);
        }

        public IReadOnlyDictionary<string, string> Values => readOnlyValues;
        public int Count => values.Count;
        public string CanonicalValue => canonicalValue;

        public bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value);

        public bool Matches(IReadOnlyDictionary<string, string> expected)
        {
            if (expected == null) return true;
            foreach (KeyValuePair<string, string> pair in expected)
            {
                if (!values.TryGetValue(pair.Key, out string actual) ||
                    !string.Equals(actual, pair.Value ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public PlanningState Apply(IReadOnlyDictionary<string, string> effects)
        {
            if (effects == null || effects.Count == 0) return this;
            Dictionary<string, string> next = new(values, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> effect in effects)
            {
                next[effect.Key] = effect.Value ?? string.Empty;
            }

            return new PlanningState(next);
        }

        public bool Equals(PlanningState other) =>
            other != null && string.Equals(canonicalValue, other.canonicalValue, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as PlanningState);
        public override int GetHashCode() => stableHashCode;
        public override string ToString() => canonicalValue;

        private static string BuildCanonicalValue(IReadOnlyDictionary<string, string> facts)
        {
            StringBuilder builder = new();
            foreach (KeyValuePair<string, string> pair in facts.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                AppendEscaped(builder, pair.Key);
                builder.Append('=');
                AppendEscaped(builder, pair.Value);
                builder.Append(';');
            }

            return builder.ToString();
        }

        private static void AppendEscaped(StringBuilder builder, string value)
        {
            foreach (char character in value ?? string.Empty)
            {
                if (character is '\\' or '=' or ';') builder.Append('\\');
                builder.Append(character);
            }
        }

        private static int ComputeStableHashCode(string value)
        {
            unchecked
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;
                uint hash = offsetBasis;
                foreach (char character in value)
                {
                    hash ^= character;
                    hash *= prime;
                }

                return (int)hash;
            }
        }
    }
}
