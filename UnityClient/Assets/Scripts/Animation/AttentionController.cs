using System;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Cognition;
using UnityEngine;

namespace EmbodiedNPC.Animation
{
    public enum AttentionSource { None, CasualPlayer, Dialogue, ExplicitLookAt, DailyRest, EnvironmentIdle }

    /// <summary>Selects observer-local presentation targets; never discovers through the Registry.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(110)]
    public sealed class AttentionController : MonoBehaviour
    {
        [SerializeField] private WorldEntity owner;
        [SerializeField] private PerceivedStateStore perceivedState;
        [SerializeField] private EntityRegistry registry;
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private ExpressiveController expressive;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private Transform visualBody;
        [SerializeField, Min(0)] private float acquireDistance = 4f;
        [SerializeField, Min(0)] private float maintainDistance = 5f;
        [SerializeField, Range(0, 90)] private float acquireYaw = 45f;
        [SerializeField, Range(0, 90)] private float releaseYaw = 55f;
        [SerializeField, Min(0)] private float acquireDelay = .3f;
        [SerializeField, Min(0)] private float releaseGrace = .3f;
        [SerializeField] private Transform[] idleLookPoints = Array.Empty<Transform>();
        [SerializeField] private Vector2 idleLookInterval = new Vector2(12, 26);
        private string pendingId;
        private float pendingTime, lostTime;
        private DailyBehaviorController dailyBehavior;
        private AgentOrchestrator orchestrator;
        private HeadGazeController headGaze;
        private Transform idlePoint;
        private int previousIdleIndex = -1;
        private float idleWait, idleHold;
        private bool idleEligible;
        private void Awake()
        {
            CacheIdleReferences();
            SuspendIdleLook();
        }
        private void CacheIdleReferences()
        {
            dailyBehavior = owner == null ? null : owner.GetComponent<DailyBehaviorController>();
            orchestrator = owner == null ? null : owner.GetComponent<AgentOrchestrator>();
            headGaze = GetComponent<HeadGazeController>();
        }
        public AttentionSource Source { get; private set; }
        public string CurrentTargetEntityId { get; private set; }
        public Vector3 CurrentGazePoint { get; private set; }
        public bool IsLiveTracking { get; private set; }
        public bool HasGazePoint => Source != AttentionSource.None;
        public WorldEntity Owner => owner;
        public bool IsSuppressed => motionChannels != null && (motionChannels.IsEmergencyMotionActive ||
            motionChannels.IsInteractionMotionActive || motionChannels.IsFormalTaskMotionActive);

        public void Configure(WorldEntity entity, PerceivedStateStore state, EntityRegistry entities,
            DialogueController conversation, ExpressiveController presentation, MotionChannelCoordinator channels,
            Transform body)
        {
            ResetAttention(); owner = entity; perceivedState = state; registry = entities;
            dialogue = conversation; expressive = presentation; motionChannels = channels; visualBody = body;
            CacheIdleReferences();
        }

        public void ConfigureIdleLook(Transform[] points, Vector2 interval)
        {
            idleLookPoints = points ?? Array.Empty<Transform>();
            idleLookInterval = new Vector2(Mathf.Max(.1f, interval.x), Mathf.Max(.1f, interval.x, interval.y));
            SuspendIdleLook();
        }

        public void Tick(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime < 0) return;
            if (!isActiveAndEnabled || owner == null || !owner.isActiveAndEnabled || IsSuppressed ||
                visualBody == null || registry == null) { ResetAttention(); return; }
            string speakerId = LegalDialogueSpeakerId();
            string hint = expressive != null && expressive.isActiveAndEnabled ? expressive.AttentionHint : null;
            // Check current evidence before resolving or reading the hinted entity's live position.
            if (hint != null && (hint == speakerId || HasCurrentEvidence(hint)) && TryResolve(hint, out WorldEntity target))
            { SuspendIdleLook(); Select(AttentionSource.ExplicitLookAt, target); return; }
            if (speakerId != null && TryResolve(speakerId, out target))
            { SuspendIdleLook(); Select(AttentionSource.Dialogue, target); return; }

            if (Source == AttentionSource.CasualPlayer && !TryResolve(CurrentTargetEntityId, out target))
                ClearSelection(); // Destruction/unregistration must not retain a stale identity during grace.
            if (Source == AttentionSource.CasualPlayer && HasCurrentEvidence(CurrentTargetEntityId) &&
                TryResolve(CurrentTargetEntityId, out target) && target.IsAgent(AgentRole.Player) &&
                Within(target, maintainDistance, releaseYaw))
            { SuspendIdleLook(); Select(AttentionSource.CasualPlayer, target); return; }

            WorldEntity candidate = null;
            float nearest = float.PositiveInfinity;
            if (perceivedState != null && perceivedState.isActiveAndEnabled && perceivedState.Aggregator != null)
                foreach (var pair in perceivedState.Aggregator.Entities)
                {
                    if (!HasCurrentEvidence(pair.Key) || !TryResolve(pair.Key, out target) ||
                        !target.IsAgent(AgentRole.Player) || !Within(target, acquireDistance, acquireYaw)) continue;
                    float distance = (target.transform.position - owner.transform.position).sqrMagnitude;
                    if (distance < nearest || (distance == nearest &&
                        string.CompareOrdinal(target.EntityId, candidate == null ? null : candidate.EntityId) < 0))
                    { candidate = target; nearest = distance; }
                }
            string candidateId = candidate == null ? null : candidate.EntityId;
            if (candidateId != pendingId) { pendingId = candidateId; pendingTime = 0; }
            if (candidate != null)
            {
                pendingTime += deltaTime;
                if (pendingTime >= acquireDelay) { SuspendIdleLook(); Select(AttentionSource.CasualPlayer, candidate); return; }
            }
            IsLiveTracking = false; // Freeze the last legal point; never follow a hidden Transform during grace.
            if (Source == AttentionSource.CasualPlayer)
            {
                lostTime += deltaTime;
                if (lostTime < releaseGrace) return;
            }
            ClearSelection();
            if (dailyBehavior != null && dailyBehavior.TryGetRestGazePoint(out var point))
            {
                SuspendIdleLook();
                Source = AttentionSource.DailyRest; CurrentGazePoint = point;
                CurrentTargetEntityId = null; IsLiveTracking = false;
                return;
            }
            TickIdleLook(deltaTime);
        }

        // Static authored environment points carry no entity identity and create no perception/cognition events.
        private void TickIdleLook(float deltaTime)
        {
            bool blocked = idleLookPoints == null || idleLookPoints.Length == 0 || motionChannels == null ||
                !motionChannels.isActiveAndEnabled || motionChannels.IsTaskMotionActive ||
                motionChannels.IsCarryPresentationActive || motionChannels.ActiveGestureId != 0 ||
                (expressive != null && expressive.State != DialoguePresentationState.Idle) ||
                (orchestrator != null && orchestrator.HasActiveRun) ||
                (dailyBehavior != null && !dailyBehavior.AllowsEnvironmentObservation);
            if (blocked) { SuspendIdleLook(); return; }
            if (!idleEligible)
            {
                idleEligible = true;
                idleWait = UnityEngine.Random.Range(idleLookInterval.x, idleLookInterval.y);
            }
            if (idleHold > 0)
            {
                idleHold -= deltaTime;
                if (idleHold > 0 && IsBoundedIdlePoint(idlePoint))
                { SelectIdlePoint(); return; }
                idlePoint = null;
                idleWait = UnityEngine.Random.Range(idleLookInterval.x, idleLookInterval.y);
            }
            idleWait -= deltaTime;
            if (idleWait > 0) return;
            idleWait = UnityEngine.Random.Range(idleLookInterval.x, idleLookInterval.y);
            int start = UnityEngine.Random.Range(0, idleLookPoints.Length);
            for (int offset = 0; offset < idleLookPoints.Length; offset++)
            {
                int index = (start + offset) % idleLookPoints.Length;
                if (idleLookPoints.Length > 1 && index == previousIdleIndex) continue;
                var candidate = idleLookPoints[index];
                if (!IsBoundedIdlePoint(candidate) || !IsUnobstructed(candidate.position)) continue;
                previousIdleIndex = index; idlePoint = candidate;
                idleHold = UnityEngine.Random.Range(2f, 4f);
                SelectIdlePoint(); return;
            }
        }
        private Vector3 IdleEyePosition => headGaze != null && headGaze.Head != null ?
            headGaze.Head.position : owner.transform.position + Vector3.up * 1.4f;
        private bool IsBoundedIdlePoint(Transform point)
        {
            if (point == null || !point.gameObject.activeInHierarchy) return false;
            Vector3 local = visualBody.InverseTransformDirection(point.position - IdleEyePosition);
            float distance = local.magnitude;
            return distance >= .3f && distance <= 4f && local.z > .1f &&
                Mathf.Abs(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg) <= 25f &&
                Mathf.Abs(Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg) <= 18f;
        }
        private bool IsUnobstructed(Vector3 point)
        {
            Vector3 eye = IdleEyePosition, direction = point - eye;
            foreach (var hit in Physics.RaycastAll(eye, direction.normalized, Mathf.Max(0, direction.magnitude - .15f),
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(owner.transform)) return false;
            return true;
        }
        private void SelectIdlePoint()
        {
            Source = AttentionSource.EnvironmentIdle; CurrentGazePoint = idlePoint.position;
            CurrentTargetEntityId = null; IsLiveTracking = false;
        }
        private void SuspendIdleLook()
        { idleEligible = false; idlePoint = null; idleHold = 0; }

        private string LegalDialogueSpeakerId()
        {
            if (dialogue == null || !dialogue.isActiveAndEnabled || !dialogue.HasActiveSession ||
                !dialogue.IsPresentationVisible || dialogue.SelectedTarget == null ||
                dialogue.SelectedTarget.GetComponent<WorldEntity>() != owner) return null;
            return dialogue.ActiveSpeakerEntityId;
        }
        private bool HasCurrentEvidence(string id)
        {
            if (id == null || perceivedState == null || !perceivedState.isActiveAndEnabled ||
                perceivedState.Aggregator == null || perceivedState.ObserverEntityId != owner.EntityId ||
                !perceivedState.Aggregator.Entities.TryGetValue(id, out PerceivedEntityRecord record)) return false;
            var fields = record.KnownFields;
            return (fields.TryGetValue("visibility_state", out string visible) && visible == "current_visible") ||
                (fields.TryGetValue("proximity_state", out string nearby) && nearby == "nearby");
        }
        private bool TryResolve(string id, out WorldEntity entity)
        {
            return registry.TryGetEntity(id, out entity) && entity != owner && entity.isActiveAndEnabled &&
                entity.gameObject.activeInHierarchy;
        }
        private bool Within(WorldEntity entity, float distance, float yaw)
        {
            Vector3 direction = entity.transform.position - owner.transform.position;
            Vector3 planar = Vector3.ProjectOnPlane(direction, visualBody.up);
            return direction.sqrMagnitude <= distance * distance &&
                (planar.sqrMagnitude < .0001f || Vector3.Angle(visualBody.forward, planar) <= yaw);
        }
        private void Select(AttentionSource source, WorldEntity target)
        {
            var perceivable = target.GetComponent<PerceivableEntity>();
            Transform anchor = perceivable == null ? target.transform : perceivable.VisualAnchor;
            CurrentGazePoint = anchor.position; CurrentTargetEntityId = target.EntityId;
            Source = source; IsLiveTracking = true; lostTime = 0; pendingId = null; pendingTime = 0;
        }
        private void ClearSelection()
        { Source = AttentionSource.None; CurrentTargetEntityId = null; IsLiveTracking = false; lostTime = 0; }
        public void ResetAttention()
        { ClearSelection(); CurrentGazePoint = default; pendingId = null; pendingTime = 0; SuspendIdleLook(); }
        private void LateUpdate() => Tick(Time.deltaTime);
        private void OnDisable() => ResetAttention();
    }
}
