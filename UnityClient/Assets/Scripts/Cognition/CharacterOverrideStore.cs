using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using EmbodiedNPC.Settings;

namespace EmbodiedNPC.Cognition
{
    [Serializable]
    public sealed class CharacterOverride
    {
        public string character_id;
        public string display_name;
        public string role_summary;
        public string backstory;
        public string personality;
        public string speaking_style;
        public string boundaries;
    }

    [Serializable]
    internal sealed class CharacterOverrideDocument
    {
        public int schema_version;
        public List<CharacterOverride> characters = new();
    }

    /// <summary>Small, Unity-local cache of user persona edits. CharacterDefinition remains the authoring default.</summary>
    public sealed class CharacterOverrideStore
    {
        public const string FileName = "npc_character_overrides.json";
        private static CharacterOverrideStore shared;
        private readonly string filePath;
        private readonly Dictionary<string, CharacterOverride> characters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CharacterDefinition> defaults = new(StringComparer.Ordinal);
        private bool loadRejected;

        internal void RegisterDefault(CharacterDefinition definition) => defaults[definition.CharacterId] = definition;

        public static CharacterOverrideStore Shared => shared ??= CreateShared();
        private static CharacterOverrideStore CreateShared()
        {
            if (!PortableDataPaths.IsPortable) return new CharacterOverrideStore(Path.Combine(Application.persistentDataPath, FileName));
            if (PortableDataPaths.StartupError != null || !PortableDataPaths.Current.SafeFile(PortableDataPaths.Current.CharacterFile))
                return new CharacterOverrideStore(); // Built-in defaults remain readable; all writes stay rejected.
            return new CharacterOverrideStore(PortableDataPaths.Current.CharacterFile);
        }
        private CharacterOverrideStore() { loadRejected = true; }
        public string FilePath => filePath;
        public bool LoadRejected => loadRejected;

        public CharacterOverrideStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A local file path is required.", nameof(path));
            filePath = path;
            Load();
        }

        public bool TryGet(string characterId, out CharacterOverride value)
        {
            if (characterId != null && characters.TryGetValue(characterId, out CharacterOverride stored))
            {
                value = Copy(stored);
                return true;
            }
            value = null;
            return false;
        }

        public void Save(string characterId, string displayName, string roleSummary, string backstory,
            string personality, string speakingStyle, string boundaries)
        {
            var profile = new CognitiveCharacterDto { character_id = characterId, display_name = displayName,
                role_summary = roleSummary, backstory = backstory, personality = personality,
                speaking_style = speakingStyle, boundaries = boundaries };
            if (!profile.IsValid) throw new ArgumentException("All character fields are required and must fit their character limits.");

            var next = characters.ToDictionary(x => x.Key, x => Copy(x.Value), StringComparer.Ordinal);
            next[characterId] = new CharacterOverride
            {
                    character_id = characterId,
                    display_name = displayName.Trim(),
                    role_summary = roleSummary.Trim(),
                    backstory = backstory.Trim(),
                    personality = personality.Trim(),
                    speaking_style = speakingStyle.Trim(),
                    boundaries = boundaries.Trim(),
            };
            Write(next);
            ReplaceCache(next);
        }

        public void Remove(string characterId)
        {
            if (loadRejected) throw new IOException("The rejected overrides file must be preserved.");
            if (string.IsNullOrWhiteSpace(characterId) || !characters.ContainsKey(characterId)) return;
            var next = characters.ToDictionary(x => x.Key, x => Copy(x.Value), StringComparer.Ordinal);
            next.Remove(characterId);
            Write(next);
            ReplaceCache(next);
        }

        private void Load()
        {
            if (!File.Exists(filePath)) return;
            try
            {
                var document = JsonUtility.FromJson<CharacterOverrideDocument>(File.ReadAllText(filePath));
                if (document == null || (document.schema_version != 1 && document.schema_version != 2) || document.characters == null)
                    throw new FormatException("Unsupported NPC character overrides document.");
                var loaded = new Dictionary<string, CharacterOverride>(StringComparer.Ordinal);
                foreach (CharacterOverride entry in document.characters)
                {
                    if (entry == null || !CharacterDefinition.ValidText(entry.character_id, 120) ||
                        !CharacterDefinition.ValidText(entry.display_name, CharacterDefinition.DisplayNameLimit) ||
                        !CharacterDefinition.ValidText(entry.role_summary, CharacterDefinition.RoleSummaryLimit) ||
                        !CharacterDefinition.ValidText(entry.speaking_style, CharacterDefinition.SpeakingStyleLimit))
                        throw new FormatException("Invalid NPC character override entry.");
                    if (document.schema_version == 1)
                        entry.backstory = entry.personality = entry.boundaries = null;
                    else if (!AsProfile(entry).IsValid)
                        throw new FormatException("Invalid v2 NPC character override entry.");
                    if (!loaded.TryAdd(entry.character_id, Copy(entry)))
                        throw new FormatException("Duplicate NPC character override entry.");
                }
                ReplaceCache(loaded);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                                          error is ArgumentException || error is FormatException)
            {
                characters.Clear();
                loadRejected = true;
                Debug.LogWarning("NPC character overrides could not be loaded; using built-in defaults. " +
                                 error.GetType().Name);
            }
        }

        private void Write(Dictionary<string, CharacterOverride> next)
        {
            if (loadRejected) throw new IOException("The rejected overrides file must be preserved.");
            // Upgrade every remaining v1 entry, not just the NPC currently being edited.
            // The resolver registers real defaults; never invent or discard an unknown NPC's persona.
            foreach (CharacterOverride entry in next.Values)
            {
                if (entry.backstory != null) continue;
                if (!defaults.TryGetValue(entry.character_id, out CharacterDefinition definition))
                    throw new ArgumentException("Built-in defaults are required to upgrade this NPC: " + entry.character_id);
                CognitiveCharacterDto effective = CharacterProfileResolver.Resolve(definition, this);
                entry.backstory = effective.backstory;
                entry.personality = effective.personality;
                entry.boundaries = effective.boundaries;
                if (!AsProfile(entry).IsValid) throw new ArgumentException("Invalid built-in defaults for NPC upgrade.");
            }
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var document = new CharacterOverrideDocument
            {
                schema_version = 2,
                characters = next.Values.OrderBy(x => x.character_id, StringComparer.Ordinal).Select(Copy).ToList(),
            };
            string temporary = filePath + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(document, true), new UTF8Encoding(false));
                if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
                else File.Move(temporary, filePath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private void ReplaceCache(Dictionary<string, CharacterOverride> next)
        {
            characters.Clear();
            foreach (var pair in next) characters.Add(pair.Key, pair.Value);
        }

        private static CharacterOverride Copy(CharacterOverride source) => new()
        {
            character_id = source.character_id,
            display_name = source.display_name,
            role_summary = source.role_summary,
            backstory = source.backstory,
            personality = source.personality,
            speaking_style = source.speaking_style,
            boundaries = source.boundaries,
        };

        private static CognitiveCharacterDto AsProfile(CharacterOverride entry) => new()
        {
            character_id = entry.character_id, display_name = entry.display_name, role_summary = entry.role_summary,
            backstory = entry.backstory, personality = entry.personality, speaking_style = entry.speaking_style,
            boundaries = entry.boundaries,
        };
    }

    public static class CharacterProfileResolver
    {
        public static CognitiveCharacterDto Resolve(CharacterDefinition definition,
            CharacterOverrideStore store = null)
        {
            if (definition == null) return null;
            store ??= CharacterOverrideStore.Shared;
            store.RegisterDefault(definition);
            if (store.TryGet(definition.CharacterId, out CharacterOverride value))
                return new CognitiveCharacterDto
                {
                    character_id = definition.CharacterId,
                    display_name = value.display_name,
                    role_summary = value.role_summary,
                    backstory = value.backstory ?? definition.Backstory,
                    personality = value.personality ?? definition.Personality,
                    speaking_style = value.speaking_style,
                    boundaries = value.boundaries ?? definition.Boundaries,
                };
            return CognitiveCharacterDto.From(definition);
        }
    }
}
