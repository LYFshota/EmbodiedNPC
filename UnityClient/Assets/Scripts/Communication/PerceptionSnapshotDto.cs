using System;
using System.Linq;

namespace EmbodiedNPC.Communication
{
    [Serializable]
    public sealed class PerceptionSnapshotDto
    {
        public string observer_entity_id;
        public int perception_revision;
        public PerceivedEntityDto[] entities = Array.Empty<PerceivedEntityDto>();

        public bool IsValid => !string.IsNullOrWhiteSpace(observer_entity_id) && perception_revision >= 0 &&
                               entities != null && entities.All(entity => entity != null && entity.IsValid);

        public static PerceptionSnapshotDto Empty(string observerEntityId) => new()
        {
            observer_entity_id = observerEntityId,
            perception_revision = 0,
            entities = Array.Empty<PerceivedEntityDto>(),
        };
    }

    [Serializable]
    public sealed class PerceivedEntityDto
    {
        public string entity_id;
        public string kind;
        public string display_name;
        public string appearance_description;
        public string[] aliases = Array.Empty<string>();
        public string[] semantic_tags = Array.Empty<string>();
        public string visibility;
        public string[] capabilities = Array.Empty<string>();

        public bool IsValid => !string.IsNullOrWhiteSpace(entity_id) && !string.IsNullOrWhiteSpace(kind) &&
                               aliases != null && semantic_tags != null && capabilities != null &&
                               (appearance_description == null || (kind == "player" && visibility == "current_visible" &&
                                EmbodiedNPC.Settings.PortablePreferenceFile.TryValidateAppearanceDescription(appearance_description, out var text) && text != null));
    }
}
