using System.Collections.Generic;
using EmbodiedNPC.Communication;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>One authored fixed seat, not a seat allocator. State is checked anew for every observation.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity), typeof(PerceivableEntity))]
    public sealed class FixedSeatBinding : MonoBehaviour
    {
        [SerializeField] private WorldEntity seat;
        [SerializeField] private WorldEntity assignedOccupant;
        [SerializeField] private Animator seatedAnimator;
        [SerializeField] private FixedSeatBinding oppositeSeat;
        [SerializeField] private string seatedStateName = "Base Layer.SeatedIdle";
        [SerializeField, Min(.01f)] private float maximumRootDistance = .3f;
        [SerializeField, Min(.01f)] private float maximumHipDistance = .35f;
        private readonly Collider[] nearbyColliders = new Collider[16];

        public WorldEntity Seat => seat != null ? seat : GetComponent<WorldEntity>();
        public WorldEntity AssignedOccupant => assignedOccupant;

        public void Configure(WorldEntity entity, WorldEntity occupant, Animator animator, FixedSeatBinding opposite)
        { seat = entity; assignedOccupant = occupant; seatedAnimator = animator; oppositeSeat = opposite; }

        public bool TryGetCurrentOccupant(out WorldEntity occupant)
        {
            occupant = null;
            if (!isActiveAndEnabled || assignedOccupant == null || !assignedOccupant.isActiveAndEnabled ||
                seatedAnimator == null || !seatedAnimator.isActiveAndEnabled || !seatedAnimator.isHuman ||
                !seatedAnimator.GetCurrentAnimatorStateInfo(0).IsName(seatedStateName)) return false;
            Vector3 rootOffset = assignedOccupant.transform.position - transform.position;
            if (Vector3.ProjectOnPlane(rootOffset, Vector3.up).magnitude > maximumRootDistance) return false;
            Transform hips = seatedAnimator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || Vector3.Distance(hips.position, transform.position + Vector3.up * .075f) > maximumHipDistance)
                return false;
            occupant = assignedOccupant;
            return true;
        }

        public string CurrentOccupancy()
        {
            if (!isActiveAndEnabled || Seat == null) return "unknown";
            if (TryGetCurrentOccupant(out _)) return "occupied";
            if (assignedOccupant != null && assignedOccupant.isActiveAndEnabled &&
                Vector3.ProjectOnPlane(assignedOccupant.transform.position - transform.position, Vector3.up).magnitude <= maximumRootDistance)
                return "unknown"; // Present body with an unverified pose is not an empty seat.
            // A disabled/moved assigned actor is not permanently seated. An unclassified body in
            // the seat space prevents an empty assertion; furniture colliders are not occupants.
            int count = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * .25f, .3f,
                nearbyColliders, ~0, QueryTriggerInteraction.Collide);
            if (count == nearbyColliders.Length) return "unknown";
            for (int i = 0; i < count; i++)
            {
                WorldEntity body = nearbyColliders[i].GetComponentInParent<WorldEntity>();
                if (body != null && body.isActiveAndEnabled && body.EntityKind == EntityKind.Agent) return "unknown";
            }
            return "empty";
        }

        public SelfSceneStateDto ReadSelf(WorldEntity observer) =>
            TryGetCurrentOccupant(out WorldEntity occupant) && occupant == observer
                ? new SelfSceneStateDto { posture = "seated", seat_entity_id = Seat.EntityId }
                : new SelfSceneStateDto();

        // Called only AFTER this seat passed the observer's physical visibility checks.
        internal void WriteObservedFields(IDictionary<string, string> fields, IReadOnlyCollection<string> visibleIds)
        {
            string occupancy = CurrentOccupancy();
            fields["seat_occupancy"] = occupancy;
            if (oppositeSeat != null && oppositeSeat.Seat != null)
                fields["seat_opposite_id"] = oppositeSeat.Seat.EntityId;
            if (occupancy == "occupied" && TryGetCurrentOccupant(out WorldEntity occupant))
                foreach (string id in visibleIds)
                    if (id == occupant.EntityId) { fields["seat_occupant_id"] = id; break; }
        }
    }
}
