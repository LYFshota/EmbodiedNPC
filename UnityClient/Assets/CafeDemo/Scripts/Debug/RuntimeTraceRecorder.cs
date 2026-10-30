using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CafeDemo
{
    /// <summary>Optional Cafe-only observer. Its file and handlers never participate in runtime decisions.</summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeTraceRecorder : MonoBehaviour
    {
        [SerializeField] private bool includeDialogueText = true;
        [SerializeField, HideInInspector] private string outputDirectoryOverride = string.Empty;

        private readonly List<Action> unsubscribe = new();
        private readonly Dictionary<AgentOrchestrator, CognitiveRun> trackedRuns = new();
        private readonly List<AgentOrchestrator> finishedRuns = new();
        private readonly Dictionary<string, PerceptionLifecycleState> visualStates = new(StringComparer.Ordinal);
        private RuntimeTraceJsonlWriter writer;

        public string CurrentTracePath { get; private set; } = string.Empty;
        public bool IsRecording => writer != null;
        public string UnavailableReason { get; private set; }
        public event Action<string> TraceLineRecorded;

        private void OnEnable()
        {
            Safe(StartRecording);
        }

        private void OnDisable()
        {
            UnsubscribeAll();
            StopRecording();
        }

        private void OnDestroy()
        {
            UnsubscribeAll();
            StopRecording();
        }

        private void Update()
        {
            if (writer == null) return;
            finishedRuns.Clear();
            foreach (KeyValuePair<AgentOrchestrator, CognitiveRun> pair in trackedRuns)
            {
                if (pair.Key == null || pair.Key.CurrentRun != null ||
                    !ReferenceEquals(pair.Key.LastRun, pair.Value)) continue;
                finishedRuns.Add(pair.Key);
            }
            foreach (AgentOrchestrator orchestrator in finishedRuns)
            {
                if (!trackedRuns.TryGetValue(orchestrator, out CognitiveRun run)) continue;
                trackedRuns.Remove(orchestrator);
                Safe(() => Record("cognition", "cognitive_run_terminal", run.AgentEntityId, null, null,
                    run.RunId, null, null, new { state = run.State.ToString(), trigger_id = run.TriggerId }));
            }
        }

        private void StartRecording()
        {
            if (writer != null) return;
            if (EmbodiedNPC.Settings.PortableDataPaths.StartupError != null) return;
            string directory = string.IsNullOrWhiteSpace(outputDirectoryOverride)
                ? (EmbodiedNPC.Settings.PortableDataPaths.IsPortable ? EmbodiedNPC.Settings.PortableDataPaths.Current.TraceDirectory :
                    System.IO.Path.Combine(Application.persistentDataPath, "EmbodiedNPC", "DebugTraces"))
                : outputDirectoryOverride;
            if (EmbodiedNPC.Settings.PortableDataPaths.IsPortable && !EmbodiedNPC.Settings.PortableDataPaths.Current.SafeFile(directory)) return;
            string sessionId = Guid.NewGuid().ToString("N");
            string fileName = $"cafe_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{sessionId[..8]}.jsonl";
            string scene = SceneManager.GetActiveScene().name;
            // Release traces reuse the existing diagnostic allowlist, never raw request/response data.
            // Do not delete older user logs; stop creating traces when the portable budget is full.
            bool restricted = !Application.isEditor;
            if (restricted && System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFiles(directory, "cafe_*.jsonl")
                .Sum(path => new System.IO.FileInfo(path).Length) > 7 * 1024 * 1024)
            { UnavailableReason = "TRACE_SIZE_LIMIT"; return; }
            writer = new RuntimeTraceJsonlWriter(System.IO.Path.Combine(directory, fileName), sessionId, scene,
                restricted, restricted ? 1024 * 1024 : long.MaxValue);
            CurrentTracePath = writer.Path;
            Debug.Log($"[RuntimeTrace] recording={CurrentTracePath}", this);
            Record("session", "session_started", null, null, null, null, null, null,
                new { unity_version = Application.unityVersion, application_version = Application.version,
                    output_path = CurrentTracePath });
            RefreshTraceSubscriptions();
        }

        private void StopRecording()
        {
            if (writer == null) return;
            RuntimeTraceJsonlWriter closing = writer;
            Record("session", "session_stopped", null, null, null, null, null, null, new { });
            closing.Dispose();
            writer = null;
        }

        [ContextMenu("Refresh Trace Subscriptions")]
        public void RefreshTraceSubscriptions()
        {
            UnsubscribeAll();
            if (writer == null || !isActiveAndEnabled) return;

            Action<Newtonsoft.Json.Linq.JObject> taskDiagnostic=value=>Safe(()=>Record("task","task_diagnostic",null,null,null,null,null,null,
                new Newtonsoft.Json.Linq.JObject { ["task_diagnostic"]=value }));
            EmbodiedNPC.Settings.DiagnosticsExport.TaskDiagnosticRecorded+=taskDiagnostic;
            unsubscribe.Add(()=>EmbodiedNPC.Settings.DiagnosticsExport.TaskDiagnosticRecorded-=taskDiagnostic);

            foreach (AgentEventReceiver receiver in FindObjectsByType<AgentEventReceiver>(FindObjectsSortMode.None))
            {
                Action<AgentEvent> received = value => Safe(() => RecordAgentEvent(value));
                Action<AgentEvent, EventInterestDecision> interested = (value, decision) =>
                    Safe(() => Record("agent_event", "interest_evaluated", receiver.EntityId,
                        value?.TraceId, null, null, value?.EventId, null,
                        new { event_type = value?.EventType, decision = decision.ToString() }));
                receiver.EventReceived += received;
                receiver.InterestEvaluated += interested;
                unsubscribe.Add(() => { if (receiver != null) { receiver.EventReceived -= received;
                    receiver.InterestEvaluated -= interested; } });
            }
            foreach (PerceivedStateStore store in FindObjectsByType<PerceivedStateStore>(FindObjectsSortMode.None))
            {
                Action<ObservationApplyResult> applied = value => Safe(() => RecordObservation(value));
                store.ObservationApplied += applied;
                unsubscribe.Add(() => { if (store != null) store.ObservationApplied -= applied; });
            }
            foreach (AgentOrchestrator orchestrator in FindObjectsByType<AgentOrchestrator>(FindObjectsSortMode.None))
            {
                Action<CognitiveRun> started = run => Safe(() =>
                {
                    trackedRuns[orchestrator] = run;
                    Record("cognition", "cognitive_run_started", run.AgentEntityId, null, null,
                        run.RunId, null, null, new { trigger_id = run.TriggerId, trigger_type = run.TriggerType });
                });
                orchestrator.RunStarted += started;
                unsubscribe.Add(() => { if (orchestrator != null) orchestrator.RunStarted -= started; });
            }
            foreach (CognitiveContextComposer composer in FindObjectsByType<CognitiveContextComposer>(FindObjectsSortMode.None))
            {
                Action<CognitiveRequest> created = request => Safe(() => RecordRequest(request));
                composer.CognitiveRequestCreated += created;
                unsubscribe.Add(() => { if (composer != null) composer.CognitiveRequestCreated -= created; });
            }
            foreach (CognitiveRunDriver driver in FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None))
            {
                Action<PresentedCognitiveUtterance> presented = value => Safe(() => RecordPresented(value));
                driver.UtterancePresented += presented;
                unsubscribe.Add(() => { if (driver != null) driver.UtterancePresented -= presented; });
            }
            foreach (var source in FindObjectsByType<EmbodiedNPC.Routines.ServiceZoneDwellCognitionSource>(FindObjectsSortMode.None))
            {
                Action<string> changed = state => Safe(() => Record("cognition", "service_zone_state",
                    source.GetComponent<WorldEntity>()?.EntityId, (source.CurrentEvent ?? source.LastPublishedEvent)?.TraceId, null,
                    (source.GetComponent<AgentOrchestrator>()?.CurrentRun ?? source.GetComponent<AgentOrchestrator>()?.LastRun)?.RunId,
                    (source.CurrentEvent ?? source.LastPublishedEvent)?.EventId, null,
                    new { condition_revision = source.ConditionRevision, state, published = source.PublishedCount,
                        provider_call_observed = (bool?)null }));
                source.StateChanged += changed;
                unsubscribe.Add(() => { if (source != null) source.StateChanged -= changed; });
            }
            foreach (GoalPlanRunner runner in FindObjectsByType<GoalPlanRunner>(FindObjectsSortMode.None))
            {
                Action<GoalPlanPresentation> presentation = value => Safe(() => Record("action",
                    "goal_plan_presentation", null, runner.ActiveTraceId, null, null, null,
                    runner.ActiveProposalId, new { presentation_kind = value.Kind.ToString(),
                        code = value.Code, message = Text(value.Message) }));
                Action<GoalOutcome> outcome = value => Safe(() => RecordOutcome(value));
                runner.PresentationReady += presentation;
                runner.GoalOutcomePublished += outcome;
                unsubscribe.Add(() => { if (runner != null) { runner.PresentationReady -= presentation;
                    runner.GoalOutcomePublished -= outcome; } });
            }
            BackendWebSocketClient backend = FindAnyObjectByType<BackendWebSocketClient>();
            if (backend != null) SubscribeBackend(backend);
            WorldStateService world = FindAnyObjectByType<WorldStateService>();
            if (world != null)
            {
                Action<SkillResult> skill = value => Safe(() => RecordSkill(value));
                world.SkillResultPublished += skill;
                unsubscribe.Add(() => { if (world != null) world.SkillResultPublished -= skill; });
            }
        }

        private void SubscribeBackend(BackendWebSocketClient backend)
        {
            Action<string> status = value => Safe(() => Record("transport", "connection_status", null,
                null, null, null, null, null, new { status = value }));
            Action<CognitiveTransportFailure> failure = value => Safe(() => Record("transport",
                "cognitive_transport_failure", value.AgentEntityId, null, value.RequestId, value.RunId,
                null, null, new { reason = value.Reason }));
            Action<CognitiveResponse> response = value => Safe(() => RecordResponse(value));
            Action<ValidatedExperienceAppendResult> experience = value => Safe(() => Record("experience",
                "experience_append_result", value.ObserverId, value.TraceId, value.RequestId, null,
                value.EventId, null, new { experience_seq = value.ExperienceSequence, status = value.Status,
                    reason = value.Reason, pending_count = backend.PendingExperienceCount,
                    write_status = backend.ExperienceWriteStatus }));
            Action<ExperienceDraftDto, ValidatedExperienceAppendResult, string> settlement = (draft, result, value) =>
            {
                if (result != null || draft == null) return;
                Safe(() => Record("experience", "experience_append_settled", draft.observer_id, draft.trace_id,
                    draft.request_id, null, draft.source_event_id, null, new { status = value, persisted_confirmed = false }));
            };
            backend.ConnectionStatusChanged += status;
            backend.CognitiveRequestFailed += failure;
            backend.CognitiveResponseReceived += response;
            backend.ValidatedExperienceAppendResultReceived += experience;
            backend.ExperienceAppendSettled += settlement;
            unsubscribe.Add(() => { if (backend == null) return;
                backend.ConnectionStatusChanged -= status;
                backend.CognitiveRequestFailed -= failure;
                backend.CognitiveResponseReceived -= response;
                backend.ValidatedExperienceAppendResultReceived -= experience;
                backend.ExperienceAppendSettled -= settlement;
            });
            status(backend.ConnectionStatus);
        }

        private void RecordAgentEvent(AgentEvent value)
        {
            if (value == null) return;
            Record("agent_event", "agent_event_received", value.TargetEntityId, value.TraceId,
                null, null, value.EventId, null,
                new { event_type = value.EventType, source_entity_id = value.SourceEntityId,
                    target_entity_id = value.TargetEntityId, area_entity_id = value.AreaEntityId,
                    source_observation_id = value.SourceObservationId, world_revision = value.WorldRevision,
                    payload = SafePayload(value.Payload) });
        }

        private void RecordObservation(ObservationApplyResult result)
        {
            Observation value = result.Observation;
            if (!result.IsApplied || value == null) return;
            if (value.SourceType == ObservationSourceType.ContinuousScan)
            {
                string key = value.ObserverEntityId + "|" + value.SubjectEntityId + "|" + value.Channel;
                if (visualStates.TryGetValue(key, out PerceptionLifecycleState previous) &&
                    previous == value.LifecycleState) return;
                visualStates[key] = value.LifecycleState;
            }
            Record("perception", "observation_applied", value.ObserverEntityId, value.TraceId,
                null, null, value.SourceEventId, null,
                new { observation_id = value.ObservationId, source_type = value.SourceType.ToString(),
                    channel = value.Channel.ToString(), subject_entity_id = value.SubjectEntityId,
                    lifecycle_state = value.LifecycleState.ToString(), perception_revision = result.PerceptionRevision,
                    world_revision = value.WorldRevision });
        }

        private void RecordRequest(CognitiveRequest value)
        {
            if (value == null) return;
            CognitiveContextSnapshot context = value.context;
            Record("cognition", "cognitive_request_created", value.run?.agent_entity_id,
                value.trace_id, value.request_id, value.run?.run_id, context?.current_event?.event_id, null,
                new { trigger_id = value.trigger?.trigger_id, trigger_type = value.trigger?.trigger_type,
                    current_event = context?.current_event == null ? null : new {
                        event_id = context.current_event.event_id, event_type = context.current_event.event_type,
                        source_entity_id = context.current_event.source_entity_id,
                        target_entity_id = context.current_event.target_entity_id,
                        source_observation_id = context.current_event.source_observation_id,
                        payload = SafePayload(context.current_event.payload) },
                    behavior_mode = context?.behavior_mode?.mode_id,
                    scene_facts = context?.scene_facts,
                    relevant_player = context?.relevant_player == null ? null : new {
                        entity_id = context.relevant_player.player_entity_id,
                        display_name = context.relevant_player.display_name },
                    capability_action_ids = context?.capabilities?.available_actions?
                        .Where(action => action != null).Select(action => action.action_id).ToArray(),
                    perception = context?.perception == null ? null : new {
                        perception_revision = context.perception.perception_revision,
                        entities = context.perception.entities?.Where(entity => entity != null).Select(entity => new {
                            entity_id = entity.entity_id, kind = entity.kind, display_name = entity.display_name,
                            semantic_tags = entity.semantic_tags, visibility = entity.visibility,
                            capabilities = entity.capabilities }).ToArray() } });
        }

        private void RecordResponse(CognitiveResponse value)
        {
            if (value == null) return;
            CognitiveDecision decision = value.decision;
            Record("cognition", "cognitive_response", value.agent_entity_id, value.trace_id,
                value.request_id, value.run_id, null, null,
                new { decision_kind = decision?.decision_kind, episode_directive = decision?.episode_directive,
                    utterance = Text(decision?.utterance), emotion = decision?.emotion,
                    gesture = decision?.gesture, look_at = decision?.look_at,
                    action_id = decision?.action_proposal?.action_id,
                    bindings = decision?.action_proposal?.bindings,
                    action_control = decision?.action_control,
                    validated_goal = value.validated_goal == null ? null : new {
                        type = value.validated_goal.type, target_entity_id = value.validated_goal.target_entity_id,
                        destination_entity_id = value.validated_goal.destination_entity_id } });
        }

        private void RecordPresented(PresentedCognitiveUtterance value)
        {
            if (value == null) return;
            Record("cognition", "utterance_presented", value.OwnerAgentEntityId, value.TraceId,
                value.RequestId, value.RunId, value.SourceEventId, null,
                new { player_entity_id = value.PlayerEntityId, utterance = Text(value.Utterance),
                    source_event_id = value.SourceEventId, conversation_id = value.ConversationId,
                    episode_directive = value.EpisodeDirective });
        }

        private void RecordOutcome(GoalOutcome value) => Record("action", "goal_outcome",
            value.ActorEntityId, value.TraceId, null, null, null, value.ProposalId,
            new { outcome_id = value.OutcomeId, proposal_revision = value.ProposalRevision,
                goal_type = value.GoalType, target_entity_id = value.TargetEntityId,
                destination_entity_id = value.DestinationEntityId, status = value.Status.ToString(),
                failure_code = value.FailureCode, world_revision = value.WorldRevision });

        private void RecordSkill(SkillResult value) => Record("action", "skill_result",
            value.ActorEntityId, value.TraceId, null, null, null, null,
            new { skill_id = value.SkillId, skill_type = value.SkillType,
                target_entity_id = value.TargetEntityId, destination_entity_id = value.DestinationEntityId,
                status = value.Status.ToString(), failure_code = value.FailureCode,
                world_revision_before = value.WorldRevisionBefore,
                world_revision_after = value.WorldRevisionAfter });

        private object SafePayload(IReadOnlyDictionary<string, string> payload)
        {
            if (payload == null) return new { };
            payload.TryGetValue(AgentEventPayloadKeys.Text, out string text);
            payload.TryGetValue(AgentEventPayloadKeys.ConversationId, out string conversationId);
            return new { text = Text(text), conversation_id = conversationId };
        }

        private object SafePayload(CognitiveKeyValueDto[] payload)
        {
            if (payload == null) return new { };
            string text = payload.FirstOrDefault(pair => pair?.key == AgentEventPayloadKeys.Text)?.value;
            string conversationId = payload.FirstOrDefault(pair => pair?.key == AgentEventPayloadKeys.ConversationId)?.value;
            return new { text = Text(text), conversation_id = conversationId };
        }

        private string Text(string value) => !includeDialogueText || value == null
            ? null : value.Length <= 500 ? value : value[..500];

        private void Record(string category, string kind, string entityId, string traceId, string requestId,
            string runId, string eventId, string proposalId, object data)
        {
            if (writer == null) return;
            if (writer.TryWrite(category, kind, entityId, traceId, requestId, runId, eventId,
                    proposalId, data, DateTime.UtcNow, Time.realtimeSinceStartupAsDouble))
            {
                string writtenLine = writer.LastWrittenLine;
                Action<string> listeners = TraceLineRecorded;
                if (listeners != null)
                    foreach (Delegate listener in listeners.GetInvocationList())
                        try { ((Action<string>)listener)(writtenLine); }
                        catch (Exception) { /* A live observer cannot stop recording or gameplay. */ }
                return;
            }
            DisableAfterFailure(writer.LastError);
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception error) { DisableAfterFailure(error.GetType().Name); }
        }

        private void DisableAfterFailure(string reason)
        {
            UnavailableReason = reason;
            Debug.LogWarning($"[RuntimeTrace] recorder stopped after {reason}; gameplay continues.", this);
            UnsubscribeAll();
            writer?.Dispose();
            writer = null;
        }

        private void UnsubscribeAll()
        {
            foreach (Action remove in unsubscribe)
                try { remove(); } catch (Exception) { /* Debug listeners cannot affect publishers. */ }
            unsubscribe.Clear();
            trackedRuns.Clear();
            visualStates.Clear();
        }
    }
}
