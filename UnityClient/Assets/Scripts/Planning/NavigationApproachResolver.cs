using System.Collections.Generic;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Planning
{
    /// <summary>Resolves model-free, NavMesh-valid approach positions around authoritative entities.</summary>
    public sealed class NavigationApproachResolver
    {
        private const float SampleRadius = 1.5f;
        public const float DeliveryInteractionRange = 1.4f;
        private const float DeliveryClearance = 0.15f;
        private const float DeliverySampleRadius = 0.45f;
        private readonly Collider[] clearanceHits = new Collider[24];

        public bool TryResolve(NavMeshAgent agent, WorldEntity target, out Vector3 position)
        {
            position = default;
            if (agent == null || target == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return false;

            if (target.TryGetComponent(out EmbodiedNPC.Actions.DrinkPreparationStation station))
            {
                if (!station.IsAvailable || !NavMesh.SamplePosition(station.ApproachPoint.position,
                        out NavMeshHit hit, .3f, agent.areaMask)) return false;
                NavMeshPath path = new();
                if (!NavMesh.CalculatePath(agent.transform.position, hit.position, agent.areaMask, path) ||
                    path.status != NavMeshPathStatus.PathComplete) return false;
                position = hit.position;
                return true;
            }

            if (target.TryGetComponent(out EmbodiedNPC.Actions.CounterCleaningStation counter))
            {
                if (!counter.IsAvailable || !NavMesh.SamplePosition(counter.ApproachPoint.position, out NavMeshHit counterHit, .15f, agent.areaMask)) return false;
                NavMeshPath counterPath = new();
                if (!NavMesh.CalculatePath(agent.transform.position, counterHit.position, agent.areaMask, counterPath) ||
                    counterPath.status != NavMeshPathStatus.PathComplete) return false;
                position = counterHit.position; return true;
            }
            if (target.TryGetComponent(out InteractionAnchorProvider anchors) &&
                anchors.PickupApproachPoint != null && TryUse(agent, anchors.PickupApproachPoint.position, out position))
                return true;
            // Authored door interaction points are authoritative. Falling back to the panel/collider would
            // incorrectly classify a door as reachable from a side where Open cannot be performed.
            if (target.TryGetComponent(out DoorState door) && door.IsConfigured)
                return TryUse(agent, door.ApproachPoint.position, out position);

            Collider collider = target.GetComponentInChildren<Collider>();
            Vector3 direction = agent.transform.position - target.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) direction = -target.transform.forward;
            direction.Normalize();
            Vector3 boundary = collider == null
                ? target.transform.position + direction * (agent.radius + agent.stoppingDistance)
                : collider.ClosestPoint(target.transform.position + direction * 100f) +
                  direction * (agent.radius + agent.stoppingDistance);
            if (TryUse(agent, boundary, out position)) return true;
            return TryUse(agent, target.transform.position, out position);
        }

        /// <summary>
        /// Finds a deterministic, complete-path delivery position around a receiver. The receiver-facing
        /// authored ReceivePoint remains the hand-off anchor, but is not used as a navigation destination.
        /// </summary>
        public bool TryResolveDeliveryApproach(NavMeshAgent agent, WorldEntity receiver, out Vector3 position)
        {
            position = default;
            if (agent == null || receiver == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return false;

            Collider receiverCollider = receiver.GetComponentInChildren<Collider>();
            float receiverRadius = receiverCollider == null
                ? 0.35f
                : Mathf.Max(receiverCollider.bounds.extents.x, receiverCollider.bounds.extents.z);
            float radius = receiverRadius + agent.radius + DeliveryClearance;
            Vector3 towardAgent = agent.transform.position - receiver.transform.position;
            towardAgent.y = 0f;
            if (towardAgent.sqrMagnitude < 0.001f) towardAgent = -receiver.transform.forward;
            towardAgent.Normalize();

            Vector3 forward = receiver.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = new(forward.z, 0f, -forward.x);
            Vector3[] directions =
            {
                towardAgent, forward, -forward, right, -right,
                (forward + right).normalized, (forward - right).normalized,
                (-forward + right).normalized, (-forward - right).normalized,
            };

            float bestLength = float.PositiveInfinity;
            bool found = false;
            var seen = new HashSet<Vector2>();
            foreach (Vector3 direction in directions)
            {
                Vector3 raw = receiver.transform.position + direction * radius;
                raw.y = agent.transform.position.y;
                if (!NavMesh.SamplePosition(raw, out NavMeshHit hit, DeliverySampleRadius, agent.areaMask)) continue;
                Vector2 key = new(Mathf.Round(hit.position.x * 100f), Mathf.Round(hit.position.z * 100f));
                if (!seen.Add(key) || !HasDeliveryClearance(agent, receiver, receiverCollider, hit.position)) continue;

                NavMeshPath path = new();
                if (!NavMesh.CalculatePath(agent.transform.position, hit.position, agent.areaMask, path) ||
                    path.status != NavMeshPathStatus.PathComplete) continue;
                float length = PathLength(path);
                if (length >= bestLength) continue;
                bestLength = length;
                position = hit.position;
                found = true;
            }
            return found;
        }

        private bool HasDeliveryClearance(NavMeshAgent agent, WorldEntity receiver,
            Collider receiverCollider, Vector3 candidate)
        {
            if (receiverCollider != null)
            {
                Vector3 nearest = receiverCollider.ClosestPoint(candidate);
                nearest.y = candidate.y;
                Vector3 planar = nearest - candidate;
                planar.y = 0f;
                if (planar.magnitude < agent.radius + DeliveryClearance - 0.02f) return false;
            }

            float radius = agent.radius + DeliveryClearance;
            const float groundContactTolerance = 0.05f;
            float lowerY = candidate.y + radius + groundContactTolerance;
            float upperY = candidate.y + Mathf.Max(radius, agent.height - radius) + groundContactTolerance;
            int count = Physics.OverlapCapsuleNonAlloc(
                new Vector3(candidate.x, lowerY, candidate.z),
                new Vector3(candidate.x, upperY, candidate.z), radius,
                clearanceHits, ~0, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < count; index++)
            {
                Collider hit = clearanceHits[index];
                clearanceHits[index] = null;
                if (hit == null || hit.transform.IsChildOf(agent.transform) ||
                    hit.transform.IsChildOf(receiver.transform)) continue;
                return false;
            }
            return true;
        }

        private static float PathLength(NavMeshPath path)
        {
            float length = 0f;
            for (int index = 1; index < path.corners.Length; index++)
                length += Vector3.Distance(path.corners[index - 1], path.corners[index]);
            return length;
        }

        private static bool TryUse(NavMeshAgent agent, Vector3 candidate, out Vector3 position)
        {
            position = default;
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, SampleRadius, agent.areaMask)) return false;
            NavMeshPath path = new();
            if (!NavMesh.CalculatePath(agent.transform.position, hit.position, agent.areaMask, path) ||
                path.status != NavMeshPathStatus.PathComplete) return false;
            position = hit.position;
            return true;
        }
    }
}
