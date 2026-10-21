using System;
using System.Collections.Generic;
using System.Globalization;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    /// <summary>One configured spatial fact/lease. Never commands a mode, dialogue or world action.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(DailyBehaviorController))]
    public sealed class ServiceZoneDwellCognitionSource : MonoBehaviour
    {
        [SerializeField] private BoxCollider serviceZone;
        [SerializeField] private WorldEntity area, player;
        [SerializeField] private EntityRegistry registry;
        [SerializeField] private AgentEventRouter router;
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private bool cognitionEnabled;
        [SerializeField, Min(.1f)] private float dwellSeconds = 4.5f;
        [SerializeField, Min(.02f)] private float checkInterval = .2f;
        [SerializeField, Min(0)] private float cooldownSeconds = 90;
        private WorldEntity owner;
        private CharacterController playerBody;
        private DailyBehaviorController daily;
        private NpcDefaultRoutineController admission;
        private ReturnToHomeRoutine routine;
        private CognitiveRunDriver driver;
        private AgentOrchestrator orchestrator;
        private AgentEvent currentEvent;
        private bool inside, consumed, claimed;
        private double enteredAt, nextCheck, cooldownUntil;
        private string capturedRun, capturedEventJson;
        private BoxCollider capturedZone;
        public int ConditionRevision { get; private set; }
        public int PublishedCount { get; private set; }
        public string LastStatus { get; private set; } = "initializing";
        public AgentEvent CurrentEvent => currentEvent;
        public AgentEvent LastPublishedEvent { get; private set; }
        public BoxCollider ServiceZone => serviceZone;
        public bool CognitionEnabled => cognitionEnabled;
        public double? DwellStartedAt => inside ? enteredAt : null;
        public event Action<string> StateChanged;

        private void Awake()
        {
            owner = GetComponent<WorldEntity>(); daily = GetComponent<DailyBehaviorController>();
            admission = GetComponent<NpcDefaultRoutineController>(); routine = GetComponent<ReturnToHomeRoutine>();
            driver = GetComponent<CognitiveRunDriver>(); orchestrator = GetComponent<AgentOrchestrator>();
            playerBody = player == null ? null : player.GetComponent<CharacterController>();
        }
        private void OnEnable() { if (dialogue != null) dialogue.ActiveSessionChanged += OnSession; }
        private void OnDisable()
        {
            if (dialogue != null) dialogue.ActiveSessionChanged -= OnSession;
            Invalidate("source_disabled");
            if (inside) consumed = true; // Re-enabling inside cannot manufacture a new physical entry.
            nextCheck = 0;
        }
        private void OnSession(DialogueParticipant target)
        { if (target != null) Invalidate("player_session"); }
        private void Update() => Tick(Time.unscaledTimeAsDouble);
        public void Tick(double now)
        {
            if (!isActiveAndEnabled || !double.IsFinite(now) || now < nextCheck) return;
            nextCheck = now + Mathf.Max(.02f, checkInterval);
            bool present = ContainsPlayer();
            if (!present)
            {
                if (inside || currentEvent != null) Invalidate("condition_left_or_unavailable");
                inside = false; return;
            }
            if (!inside)
            { inside = true; consumed = false; enteredAt = now; ConditionRevision++; SetStatus("dwelling"); }
            if (currentEvent != null && (daily == null || daily.CurrentMode != DailyBehaviorMode.Rest))
                Invalidate("mode_changed");
            if (consumed || now - enteredAt < dwellSeconds) return;
            consumed = true; // Every outcome, including skip/NO_ACTION/cancellation, spends this stay.
            if (!cognitionEnabled || !float.IsFinite(dwellSeconds) || dwellSeconds < .1f ||
                !float.IsFinite(cooldownSeconds) || cooldownSeconds < 0 || now < cooldownUntil)
            { SetStatus("disabled_or_cooldown"); return; }
            cooldownUntil = now + cooldownSeconds;
            if (!CanAdmit()) { SetStatus("busy_or_mode_not_eligible"); return; }
            claimed = false; capturedRun = capturedEventJson = null;
            currentEvent = new AgentEvent("agent_event_" + Guid.NewGuid().ToString("N"),
                AgentEventTypes.PlayerDwelledInServiceZone, player.EntityId, owner.EntityId, area.EntityId,
                new Dictionary<string, string> {
                    ["zone_id"] = "counter_service_zone", ["condition_token"] = Guid.NewGuid().ToString("N"),
                    ["condition_revision"] = ConditionRevision.ToString(CultureInfo.InvariantCulture),
                    ["dwell_seconds"] = (now - enteredAt).ToString("F3", CultureInfo.InvariantCulture) },
                now, traceId: "cognitive-trace-" + Guid.NewGuid().ToString("N"));
            LastPublishedEvent = currentEvent;
            SetStatus("candidate_published");
            if (router != null && router.isActiveAndEnabled && router.Route(currentEvent) == 1) PublishedCount++;
            else { Invalidate("route_failed"); }
        }
        public bool ContainsPlayer()
        {
            if (!isActiveAndEnabled || owner == null || !owner.isActiveAndEnabled || serviceZone == null ||
                !serviceZone.enabled || !serviceZone.gameObject.activeInHierarchy || !serviceZone.isTrigger || player == null || !player.isActiveAndEnabled ||
                !player.IsAgent(AgentRole.Player) || area == null || !area.isActiveAndEnabled || area.EntityKind != EntityKind.Area ||
                registry == null || !registry.IsRegistered(player) || !registry.IsRegistered(owner) || !registry.IsRegistered(area)) return false;
            if (playerBody == null) playerBody = player.GetComponent<CharacterController>();
            if (playerBody == null || !playerBody.enabled || !playerBody.gameObject.activeInHierarchy) return false;
            var local = serviceZone.transform.InverseTransformPoint(playerBody.bounds.center) - serviceZone.center;
            var half = serviceZone.size * .5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }
        private bool HasConversation => dialogue != null && dialogue.HasActiveSession;
        private bool CanAdmit() => daily != null && daily.isActiveAndEnabled && daily.CurrentMode == DailyBehaviorMode.Rest &&
            daily.Profile != null && daily.Profile.IsValid && daily.Profile.AllowsCognitiveTransition(DailyBehaviorMode.Rest, DailyBehaviorMode.Work) &&
            !HasConversation && admission != null && !admission.IsDailyActivityBlocked &&
            (routine == null || (!routine.IsRunning && !routine.OwnsPath));
        public bool CanWake(AgentEvent value) => cognitionEnabled && !claimed && ReferenceEquals(value, currentEvent) &&
            currentEvent != null && ContainsPlayer() && CanAdmit();
        public bool TryClaim(AgentEvent value, CognitiveRun run)
        {
            // CanAdmit excludes our newly created Run; the pre-Run checks already ran in the bridge/orchestrator.
            if (claimed || !ReferenceEquals(value, currentEvent) || value == null || !ContainsPlayer() || HasConversation ||
                run == null || !run.IsServiceZoneConsideration || run.AgentEntityId != owner.EntityId) return false;
            claimed = true; capturedRun = run.RunId; capturedEventJson = JsonUtility.ToJson(CognitiveEventDto.From(value));
            capturedZone = serviceZone;
            SetStatus("cognition_started"); return true;
        }
        public bool Matches(CognitiveRequest request, CognitiveRun run) => isActiveAndEnabled && claimed && currentEvent != null &&
            run != null && run.IsServiceZoneConsideration && run.State == CognitiveRunState.Running &&
            request?.run != null && capturedRun == run.RunId && request.run.run_id == run.RunId &&
            request.run.agent_entity_id == owner.EntityId && request.trace_id == currentEvent.TraceId &&
            capturedZone == serviceZone && request.context.current_event.source_entity_id == player?.EntityId &&
            request.context.current_event.area_entity_id == area?.EntityId &&
            ReferenceEquals(orchestrator.CurrentRun, run) && JsonUtility.ToJson(request.context.current_event) == capturedEventJson &&
            ContainsPlayer() && !HasConversation;
        public bool IsConfiguredPlayer(string id) => player != null && player.isActiveAndEnabled &&
            player.IsAgent(AgentRole.Player) && registry != null && registry.IsRegistered(player) && player.EntityId == id;
        public void Invalidate(string reason)
        {
            currentEvent = null; capturedRun = capturedEventJson = null; claimed = false; ConditionRevision++;
            driver?.CancelServiceZoneConsideration(reason);
            SetStatus(reason);
        }
        public void RecordResult(string status) => SetStatus(status);
        private void SetStatus(string status) { LastStatus = status; StateChanged?.Invoke(status); }
    }
}
