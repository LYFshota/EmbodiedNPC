using System;
using System.Collections.Generic;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    /// <summary>One opportunity per authoritative transition. Publishes facts; never submits a goal or chooses an action.</summary>
    [DisallowMultipleComponent]
    public sealed class CounterCleaningCognitionSource : MonoBehaviour
    {
        [SerializeField] private CounterCleaningCapability capability;
        [SerializeField] private EntityRegistry registry;
        [SerializeField] private AgentEventRouter router;
        [SerializeField] private DialogueController dialogue;
        private WorldEntity owner;
        private CounterConditionChange change;
        private AgentEvent currentEvent;
        private string eventJson, runId, profileJson, manifestJson;
        private int modeRevision, seenVersion;
        private bool eligible, claimed;
        private bool pendingFact;
        private double expiresAt;
        private WorldStateService subscribedWorld;
        public int PublishedCount { get; private set; }
        public string LastStatus { get; private set; } = "waiting_for_change";
        public AgentEvent CurrentEvent => currentEvent;
        private DailyBehaviorController Daily => GetComponent<DailyBehaviorController>();
        private AgentOrchestrator Orchestrator => GetComponent<AgentOrchestrator>();
        public void Configure(CounterCleaningCapability cap, EntityRegistry entities, AgentEventRouter events, DialogueController sessions)
        { if(capability==cap && registry==entities && router==events && dialogue==sessions && owner!=null) return;
            Unsubscribe(); Invalidate("reconfigured"); capability = cap; registry = entities; router = events; dialogue = sessions; owner = GetComponent<WorldEntity>(); if (isActiveAndEnabled) Subscribe(); }
        private void Awake() { owner = GetComponent<WorldEntity>(); }
        private void OnEnable() => Subscribe();
        private void Subscribe()
        {
            subscribedWorld = capability?.Station?.World;
            if (subscribedWorld != null) subscribedWorld.CounterNeedsCleaning += OnChange;
            if (dialogue != null) dialogue.ActiveSessionChanged += OnSession;
        }
        private void Unsubscribe()
        { if (subscribedWorld != null) subscribedWorld.CounterNeedsCleaning -= OnChange; if (dialogue != null) dialogue.ActiveSessionChanged -= OnSession; subscribedWorld = null; }
        private void OnDisable() { Unsubscribe(); Invalidate("source_disabled"); }
        private void OnSession(DialogueParticipant target) { if (target != null) Invalidate("player_session"); }
        private bool Conditions(CognitiveRun ownRun = null)
        {
            var daily = Daily; var admission = GetComponent<NpcDefaultRoutineController>(); var routine = GetComponent<ReturnToHomeRoutine>();
            return isActiveAndEnabled && owner != null && DrinkTypes.IsCurrent(registry, owner) && capability != null && capability.IsAuthorized &&
                DrinkTypes.IsCurrent(registry, capability.Station.Entity) && subscribedWorld == capability.Station.World &&
                subscribedWorld.IsCurrentCounterChange(change) && change.Station == capability.Station &&
                (GetComponent<GoalPlanRunner>()?.HasActiveTask!=true) && (ownRun!=null || Orchestrator?.HasActiveRun!=true) &&
                daily != null && daily.isActiveAndEnabled && daily.CurrentMode == DailyBehaviorMode.Work && daily.Profile != null && daily.Profile.IsValid &&
                (dialogue == null || !dialogue.IsPresentationVisible) && admission != null && !admission.IsEngagementHeld &&
                (ownRun == null ? !admission.IsDailyActivityBlocked : !admission.IsCognitiveModeTransitionBlocked(ownRun)) &&
                (routine == null || (!routine.IsRunning && !routine.OwnsPath));
        }
        private void OnChange(CounterConditionChange value)
        {
            if (value == null || value.Station != capability?.Station || value.Owner != subscribedWorld ||
                !subscribedWorld.IsCurrentCounterChange(value) || value.Version <= seenVersion) return;
            Invalidate("new_condition"); seenVersion = value.Version; change = value;
            PublishFact();
        }
        private void PublishFact()
        {
            var value=change;
            claimed = false; eligible = Conditions(); pendingFact=!eligible; expiresAt = Time.unscaledTimeAsDouble + 30;
            var tasks=FindAnyObjectByType<TaskRuntimeBridge>();
            if(tasks?.HasConditionTask(owner.EntityId,tasks.CurrentCondition(owner))==true)
            { eligible=false;pendingFact=false;LastStatus="managed_by_task";return; }
            modeRevision = Daily == null ? -1 : Daily.ModeRevision; profileJson = Daily?.Profile == null ? null : JsonUtility.ToJson(Daily.Profile);
            currentEvent = new AgentEvent("counter-event-" + Guid.NewGuid().ToString("N"), AgentEventTypes.CounterNeedsCleaning,
                value.Station.Entity.EntityId, owner.EntityId, "area_cafe_counter", new Dictionary<string,string> {
                    ["target_entity_id"] = value.Station.Entity.EntityId, ["condition_token"] = value.Token,
                    ["condition_revision"] = value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                Time.realtimeSinceStartupAsDouble, value.WorldRevision, traceId: "counter-trace-" + Guid.NewGuid().ToString("N"));
            eventJson = JsonUtility.ToJson(CognitiveEventDto.From(currentEvent));
            LastStatus = eligible ? "fact_published" : "fact_published_no_opportunity";
            if (router != null && router.isActiveAndEnabled && router.Route(currentEvent) == 1) PublishedCount++;
            else { eligible = false; LastStatus = "route_failed"; }
        }
        public bool CanWake(AgentEvent value) => eligible && !claimed && ReferenceEquals(value, currentEvent) &&
            Time.unscaledTimeAsDouble < expiresAt && Conditions();
        public bool TryClaim(AgentEvent value, CognitiveRun run)
        {
            if (!eligible || claimed || !ReferenceEquals(value, currentEvent) || run == null || !run.IsCounterCleaningConsideration ||
                !ReferenceEquals(Orchestrator.CurrentRun, run) || !Conditions(run)) return false;
            if (!GetComponent<AgentActionCapabilityProfile>().TryBuildManifest(out var manifest)) return false;
            claimed = true; runId = run.RunId; manifestJson = JsonUtility.ToJson(manifest); LastStatus = "considering"; return true;
        }
        public CounterWorkDto Capture(CognitiveRun run)
        {
            var dto = capability?.Capture(); if (dto == null || run == null || !claimed || run.RunId != runId) return null;
            dto.condition_token = change.Token; dto.event_id = currentEvent.EventId; dto.trace_id = currentEvent.TraceId; return dto;
        }
        public bool Matches(CognitiveRequest request, CognitiveRun run)
        {
            if (!eligible || !claimed || currentEvent == null || change == null || run == null || !run.IsCounterCleaningConsideration ||
                run.State != CognitiveRunState.Running || request?.run == null || request.run.run_id != runId || run.RunId != runId ||
                !ReferenceEquals(Orchestrator.CurrentRun, run) || request.trace_id != currentEvent.TraceId ||
                request.trigger.source_event_id != currentEvent.EventId || request.run.agent_entity_id != owner.EntityId ||
                request.context.agent_entity_id != owner.EntityId || request.context.relevant_player != null ||
                JsonUtility.ToJson(request.context.current_event) != eventJson || Time.unscaledTimeAsDouble >= expiresAt || !Conditions(run) ||
                Daily.ModeRevision != modeRevision || JsonUtility.ToJson(Daily.Profile) != profileJson ||
                !GetComponent<AgentActionCapabilityProfile>().TryBuildManifest(out var manifest) || JsonUtility.ToJson(manifest) != manifestJson ||
                JsonUtility.ToJson(request.context.counter_work) != JsonUtility.ToJson(Capture(run))) return false;
            var snapshot = request.context.daily_behavior;
            return Daily.HasCurrentModeProfile(snapshot) && snapshot.current_mode == "work" && snapshot.mode_revision == modeRevision &&
                new NavigationApproachResolver().TryResolve(GetComponent<UnityEngine.AI.NavMeshAgent>(), capability.Station.Entity, out _);
        }
        public bool IsConfiguredPlayer(string id) => registry != null && registry.TryGetEntity(id, out var player) &&
            DrinkTypes.IsCurrent(registry, player) && player.IsAgent(AgentRole.Player);
        public void Invalidate(string reason)
        {
            eligible = false; currentEvent = null; runId = eventJson = null; claimed = false;
            GetComponent<CognitiveRunDriver>()?.CancelLowPrioritySystemConsideration(reason); LastStatus = reason;
        }
        public void RecordResult(string reason) { eligible = false; pendingFact=false; LastStatus = reason; }
        private void Update()
        {
            if(pendingFact && Conditions()) { pendingFact=false; PublishFact(); }
            var run = Orchestrator?.CurrentRun;
            if (claimed && run?.State == CognitiveRunState.Running && run.RunId == runId &&
                (Time.unscaledTimeAsDouble >= expiresAt || !Conditions(run))) Invalidate("condition_expired");
        }
    }
}
