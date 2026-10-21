using System;
using System.Collections.Generic;

namespace EmbodiedNPC.Perception
{
    /// <summary>Small deterministic vocabulary bridge for request-to-observation relevance.</summary>
    internal static class SemanticRequestMatcher
    {
        private static readonly IReadOnlyDictionary<string, string[]> ConceptTerms =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["cup"] = new[] { "cup", "cups", "杯", "杯子" },
            };

        public static bool Matches(string utterance, string semanticTag)
        {
            if (string.IsNullOrWhiteSpace(utterance) || string.IsNullOrWhiteSpace(semanticTag)) return false;
            string tag = semanticTag.Trim();
            if (Contains(utterance, tag)) return true;
            if (!ConceptTerms.TryGetValue(tag, out string[] terms)) return false;
            foreach (string term in terms)
                if (Contains(utterance, term)) return true;
            return false;
        }

        private static bool Contains(string text, string fragment) =>
            text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
