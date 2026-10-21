using System;
using System.Linq;

namespace EmbodiedNPC.Communication
{
    [Serializable]
    public sealed class SceneFactsDto
    {
        public SelfSceneStateDto self;
        public ObservedSeatDto[] observed_seats = Array.Empty<ObservedSeatDto>();
        public bool IsValid => (self == null || self.IsValid) && observed_seats != null &&
            observed_seats.Length <= 4 && observed_seats.All(x => x != null && x.IsValid) &&
            observed_seats.Select(x => x.seat_entity_id).Distinct().Count() == observed_seats.Length;
    }

    [Serializable]
    public sealed class SelfSceneStateDto
    {
        public string posture = "unknown";
        public string seat_entity_id;
        public bool IsValid => (posture == "seated" && !string.IsNullOrWhiteSpace(seat_entity_id)) ||
            (posture == "unknown" && string.IsNullOrWhiteSpace(seat_entity_id));
    }

    [Serializable]
    public sealed class ObservedSeatDto
    {
        public string seat_entity_id;
        public string relation_to_self = "unknown";
        public string occupancy = "unknown";
        public string occupant_entity_id;
        public bool IsValid => !string.IsNullOrWhiteSpace(seat_entity_id) &&
            (relation_to_self == "self" || relation_to_self == "opposite" || relation_to_self == "unknown") &&
            (occupancy == "occupied" || occupancy == "empty" || occupancy == "unknown") &&
            (occupancy == "occupied" || string.IsNullOrWhiteSpace(occupant_entity_id));
    }
}
