using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Stable authoring data describing who an NPC is; runtime state belongs elsewhere.</summary>
    [CreateAssetMenu(fileName = "CharacterDefinition", menuName = "EmbodiedNPC/Cognition/Character Definition")]
    public sealed class CharacterDefinition : ScriptableObject
    {
        public const int DisplayNameLimit = 80, RoleSummaryLimit = 240, BackstoryLimit = 1200,
            PersonalityLimit = 600, SpeakingStyleLimit = 480, BoundariesLimit = 600;
        [SerializeField] private string characterId;
        [SerializeField] private string displayName;
        [SerializeField, TextArea] private string roleSummary;
        [SerializeField, TextArea] private string backstory;
        [SerializeField, TextArea] private string personality;
        [SerializeField, TextArea] private string speakingStyle;
        [SerializeField, TextArea] private string boundaries;

        public string CharacterId => characterId ?? string.Empty;
        public string DisplayName => displayName ?? string.Empty;
        public string RoleSummary => roleSummary ?? string.Empty;
        public string Backstory => backstory ?? string.Empty;
        public string Personality => personality ?? string.Empty;
        public string SpeakingStyle => speakingStyle ?? string.Empty;
        public string Boundaries => boundaries ?? string.Empty;

        public void Configure(string id, string name, string role, string history, string traits, string style, string limits)
        {
            characterId = id;
            displayName = name;
            roleSummary = role;
            backstory = history;
            personality = traits;
            speakingStyle = style;
            boundaries = limits;
        }

        public bool TryValidateConfiguration(out string error)
        {
            if (!CognitiveCharacterDto.From(this).IsValid)
            {
                error = "CharacterDefinition requires seven nonblank fields within their character limits.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        // Same Unicode scalar counting as the Backend, rather than UTF-16 string.Length.
        public static int CountCharacters(string value)
        {
            int count = 0;
            for (int i = 0; i < (value?.Length ?? 0); i++, count++)
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            return count;
        }

        internal static bool ValidText(string value, int limit)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            // Count Unicode characters like Python/Pydantic, including supplementary characters.
            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(value[i])) return false;
                if (++count > limit) return false;
            }
            return true;
        }
    }
}
