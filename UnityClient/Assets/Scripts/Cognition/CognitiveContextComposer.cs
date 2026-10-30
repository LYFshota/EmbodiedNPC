using System;
using System.Collections.Generic;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using EmbodiedNPC.Routines;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Snapshots bounded observer-local evidence and capabilities when a run starts; Persona/settings do not grant world authority.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    [RequireComponent(typeof(AgentOrchestrator))]
    public sealed class CognitiveContextComposer : MonoBehaviour
    {
        [SerializeField] private WorldEntity agentEntity;
        [SerializeField] private CharacterDefinition characterDefinition;
        [SerializeField] private AgentOrchestrator orchestrator;
        [SerializeField] private WakeCognitiveTriggerBridge triggerSource;
        [SerializeField] private AgentEventReceiver eventReceiver;
        [SerializeField] private CurrentBehaviorMode behaviorMode;
        [SerializeField] private PerceptionContextProvider perceptionProvider;
        [SerializeField] private PlayerIdentityBinding playerIdentity;
        [SerializeField] private AgentActionCapabilityProfile actionCapabilities;
        private EntityRegistry entityRegistry;
        private CharacterOverrideStore characterOverrides;
        public EmbodiedNPC.Dialogue.DialogueContinuationScheduler ContinuationScheduler { get; private set; }
        public void SetContinuationScheduler(EmbodiedNPC.Dialogue.DialogueContinuationScheduler scheduler)
            => ContinuationScheduler = scheduler;

        private bool isSubscribed;

        public CharacterDefinition CharacterDefinition => characterDefinition;
        /// <summary>Own character snapshot, shared by cognition and bounded failure presentation.</summary>
        public CognitiveCharacterDto CaptureCharacterProfile()
        {
            var profile = CharacterProfileResolver.Resolve(characterDefinition,
                characterOverrides ?? CharacterOverrideStore.Shared);
            return profile?.IsValid == true ? profile : null;
        }
        private readonly ReplyLanguageSession replyLanguageSession = new();

        public CognitiveRequest LastRequest { get; private set; }
        public event Action<CognitiveRequest> CognitiveRequestCreated;

        private void Awake() => ResolveLocalReferences();
        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();

        public void Configure(WorldEntity agent, CharacterDefinition definition, AgentOrchestrator runOrchestrator,
            WakeCognitiveTriggerBridge source, AgentEventReceiver receiver, CurrentBehaviorMode mode,
            PerceptionContextProvider perception, PlayerIdentityBinding player,
            AgentActionCapabilityProfile capabilities = null, CharacterOverrideStore overrides = null)
        {
            Unsubscribe();
            agentEntity = agent;
            characterDefinition = definition;
            orchestrator = runOrchestrator;
            triggerSource = source;
            eventReceiver = receiver;
            behaviorMode = mode;
            perceptionProvider = perception;
            playerIdentity = player;
            actionCapabilities = capabilities;
            characterOverrides = overrides;
            Subscribe();
        }

        public bool TryValidateConfiguration(out string error)
        {
            if (agentEntity == null || agentEntity.EntityKind != EntityKind.Agent ||
                string.IsNullOrWhiteSpace(agentEntity.EntityId))
            {
                error = "CognitiveContextComposer requires an Agent WorldEntity.";
                return false;
            }
            if (characterDefinition == null)
            {
                error = "CognitiveContextComposer requires CharacterDefinition.";
                return false;
            }
            if (!characterDefinition.TryValidateConfiguration(out error)) return false;
            if (orchestrator == null || triggerSource == null || eventReceiver == null)
            {
                error = "CognitiveContextComposer requires the existing run, trigger and event chain.";
                return false;
            }
            if (perceptionProvider == null)
            {
                error = "CognitiveContextComposer requires PerceptionContextProvider.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        public bool TryBuildRequest(CognitiveRun run, CognitiveTrigger trigger, out CognitiveRequest request)
        {
            request = null;
            if (run == null || trigger == null || !TryValidateConfiguration(out _) ||
                !string.Equals(run.TriggerId, trigger.TriggerId, StringComparison.Ordinal) ||
                !string.Equals(run.AgentEntityId, agentEntity.EntityId, StringComparison.Ordinal)) return false;

            AgentEvent currentEvent = null;
            if (!string.IsNullOrWhiteSpace(trigger.SourceEventId) &&
                !eventReceiver.TryGetRecentEvent(trigger.SourceEventId, out currentEvent)) return false;
            if (currentEvent?.EventType == AgentEventTypes.DialogueContinue &&
                (ContinuationScheduler == null || !ContinuationScheduler.TryClaimEvent(currentEvent))) return false;
            if (currentEvent?.EventType == AgentEventTypes.PlayerDwelledInServiceZone &&
                !(GetComponent<EmbodiedNPC.Routines.ServiceZoneDwellCognitionSource>()?.TryClaim(currentEvent, run) ?? false)) return false;

            if (currentEvent?.EventType == AgentEventTypes.CounterNeedsCleaning &&
                !(GetComponent<EmbodiedNPC.Routines.CounterCleaningCognitionSource>()?.TryClaim(currentEvent, run) ?? false)) return false;
            string actualUtterance = string.Empty;
            if (currentEvent != null && currentEvent.EventType == AgentEventTypes.PlayerUtterance)
                currentEvent.TryGetPayload(AgentEventPayloadKeys.Text, out actualUtterance);
            ContextSnapshot perception = perceptionProvider.BuildSnapshot(actualUtterance ?? string.Empty);
            if (perception == null) return false;

            CognitivePlayerDto player = BuildPlayerProjection(currentEvent);
            CognitiveCapabilityManifestDto capabilityManifest = null;
            if (actionCapabilities != null &&
                !actionCapabilities.TryBuildManifest(out capabilityManifest)) return false;
            CognitiveTriggerDto triggerDto = CognitiveTriggerDto.From(trigger);
            var context = new CognitiveContextSnapshot
            {
                run_id = run.RunId,
                agent_entity_id = run.AgentEntityId,
                trigger = triggerDto,
                current_event = CognitiveEventDto.From(currentEvent),
                character = CaptureCharacterProfile(),
                behavior_mode = behaviorMode == null ? null : new CognitiveBehaviorModeDto
                    { mode_id = behaviorMode.CurrentModeId },
                daily_behavior = agentEntity.GetComponent<DailyBehaviorController>()?.CaptureCognitiveContext(run.RunId, run.IsServiceZoneConsideration),
                relevant_player = player,
                perception = perception.ToPerceptionDto(),
                scene_facts = perception.SceneFacts,
                counter_work = run.IsCounterCleaningConsideration
                    ? agentEntity.GetComponent<EmbodiedNPC.Routines.CounterCleaningCognitionSource>()?.Capture(run)
                    : agentEntity.GetComponent<EmbodiedNPC.Actions.CounterCleaningCapability>()?.Capture(),
                capabilities = actionCapabilities == null ? null : capabilityManifest,
                runtime_state = GetComponent<EmbodiedNPC.Planning.GoalPlanRunner>() is { } runner
                    ? runner.CaptureExecutionState(player?.player_entity_id)
                    : CognitiveExecutionState.Unknown(agentEntity.EntityId, player?.player_entity_id,
                        EmbodiedNPC.Planning.GoalPlanRunner.SceneInstance(this), currentEvent?.WorldRevision ?? 0),
            };
            if (playerIdentity?.CurrentIdentity != null)
            {
                string profileEntityId = playerIdentity.GetComponent<WorldEntity>()?.EntityId;
                foreach (var perceived in context.perception.entities)
                    if (perceived.kind == "player" && perceived.entity_id == profileEntityId)
                        perceived.display_name = playerIdentity.CurrentIdentity.DisplayName;
            }

            request = new CognitiveRequest
            {
                request_id = $"cognitive-request-{Guid.NewGuid():N}",
                trace_id = !string.IsNullOrWhiteSpace(currentEvent?.TraceId)
                    ? currentEvent.TraceId : $"cognitive-trace-{Guid.NewGuid():N}",
                run = CognitiveRunDto.From(run),
                trigger = triggerDto,
                context = context,
                recall_identity = BuildRecallIdentity(context),
            };
            // Snapshot after identity/context validation. Language never rewrites player text or facts.
            var identity = request.recall_identity;
            string conversation = null;
            currentEvent?.TryGetPayload("conversation_id", out conversation);
            string playerId = context.relevant_player?.player_entity_id;
            string languageText = currentEvent?.EventType == AgentEventTypes.PlayerUtterance &&
                currentEvent.SourceEntityId == playerId && currentEvent.TargetEntityId == run.AgentEntityId ? actualUtterance : null;
            var playerIdentityWire = identity?.current_subjects == null ? null :
                System.Array.Find(identity.current_subjects, p => p.entity_id == playerId && p.kind == "player");
            string key = identity?.observer?.IsValid == true && playerIdentityWire?.IsValid == true &&
                !string.IsNullOrEmpty(identity.observer.persistent_id) && !string.IsNullOrEmpty(playerIdentityWire.persistent_id) &&
                !string.IsNullOrEmpty(conversation)
                ? Newtonsoft.Json.JsonConvert.SerializeObject(new[] { run.AgentEntityId, playerId, identity.observer.identity_namespace, identity.observer.identity_type,
                    identity.observer.persistent_id, playerIdentityWire.identity_namespace, playerIdentityWire.identity_type,
                    playerIdentityWire.persistent_id, conversation }) : null;
            request.reply_language = replyLanguageSession.Resolve(key, languageText, EmbodiedNPC.Settings.NpcReplyLanguageSettings.DefaultLanguage);
            if(currentEvent?.EventType==AgentEventTypes.CounterNeedsCleaning)
                FindAnyObjectByType<TaskRuntimeBridge>()?.IncludeCurrentPlayer(request.recall_identity);
            if ((currentEvent?.EventType == AgentEventTypes.PlayerUtterance || currentEvent?.EventType==AgentEventTypes.CounterNeedsCleaning) &&
                FindAnyObjectByType<TaskRuntimeBridge>() is { } taskBridge &&
                taskBridge.TryCapture(request.recall_identity, out var taskRuntime))
            { request.schema_version = "1.1"; request.task_runtime = taskRuntime; }
            if(currentEvent?.EventType==AgentEventTypes.TaskConsideration &&
                !(GetComponent<TaskConsiderationSource>()?.TryBind(request,run) ?? false)) return false;
            return request.IsValid;
        }

        private CognitiveRecallIdentityDto BuildRecallIdentity(CognitiveContextSnapshot context)
        {
            var observer = CognitiveRecallSubjectIdentityDto.From(agentEntity);
            if (observer == null) return null;
            if (entityRegistry == null) entityRegistry = FindAnyObjectByType<EntityRegistry>();
            var subjects = new List<CognitiveRecallSubjectIdentityDto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string entityId)
            {
                if (entityRegistry == null || string.IsNullOrWhiteSpace(entityId) || !seen.Add(entityId) ||
                    subjects.Count >= 16 || !entityRegistry.TryGetEntity(entityId, out WorldEntity entity)) return;
                CognitiveRecallSubjectIdentityDto identity = CognitiveRecallSubjectIdentityDto.From(entity);
                if (identity != null) subjects.Add(identity);
            }
            string requester = context.current_event?.source_entity_id;
            bool requesterIsRelevant = context.relevant_player != null &&
                string.Equals(requester, context.relevant_player.player_entity_id, StringComparison.Ordinal);
            bool requesterIsPerceived = false;
            foreach (PerceivedEntityDto perceived in context.perception.entities)
                if (perceived != null && (perceived.kind == "player" || perceived.kind == "npc") &&
                    string.Equals(perceived.entity_id, requester, StringComparison.Ordinal))
                    requesterIsPerceived = true;
            if (!requesterIsRelevant && !requesterIsPerceived) requester = null;
            Add(requester);
            Add(context.relevant_player?.player_entity_id);
            foreach (PerceivedEntityDto perceived in context.perception.entities)
                if (perceived != null && (perceived.kind == "player" || perceived.kind == "npc"))
                    Add(perceived.entity_id);
            return new CognitiveRecallIdentityDto
            {
                observer = observer,
                requester_entity_id = requester,
                current_subjects = subjects.ToArray(),
            };
        }

        private CognitivePlayerDto BuildPlayerProjection(AgentEvent currentEvent)
        {
            if (currentEvent == null || playerIdentity == null || playerIdentity.CurrentIdentity == null) return null;
            WorldEntity playerEntity = playerIdentity.GetComponent<WorldEntity>();
            if (playerEntity == null || !string.Equals(currentEvent.SourceEntityId, playerEntity.EntityId,
                    StringComparison.Ordinal))
            {
                if (playerEntity == null || (currentEvent.EventType != AgentEventTypes.DialogueContinue && currentEvent.EventType != AgentEventTypes.TaskConsideration) ||
                    currentEvent.SourceEntityId != agentEntity.EntityId ||
                    !currentEvent.TryGetPayload("player_entity_id", out string playerId) ||
                    playerId != playerEntity.EntityId) return null;
            }
            return new CognitivePlayerDto
            {
                player_entity_id = playerEntity.EntityId,
                display_name = playerIdentity.CurrentIdentity.DisplayName,
            };
        }

        private void HandleRunStarted(CognitiveRun run)
        {
            CognitiveTrigger trigger = triggerSource == null ? null : triggerSource.LastTrigger;
            if (trigger == null || !string.Equals(run.TriggerId, trigger.TriggerId, StringComparison.Ordinal))
                return;
            if (!TryBuildRequest(run, trigger, out CognitiveRequest request))
            {
                if (ReferenceEquals(orchestrator?.CurrentRun, run)) orchestrator.FailCurrentRun();
                return;
            }
            LastRequest = request;
            if (request.context.current_event?.event_type == AgentEventTypes.DialogueContinue)
                ContinuationScheduler?.RegisterRequest(request);
            CognitiveRequestCreated?.Invoke(request);
        }

        internal bool PublishOutcomeRequest(CognitiveRequest request)
        {
            if (!isActiveAndEnabled || request?.action_feedback == null || !request.IsValid ||
                orchestrator?.CurrentRun?.RunId != request.run.run_id) return false;
            LastRequest = request;
            CognitiveRequestCreated?.Invoke(request);
            return true;
        }

        public bool HasCurrentSceneFacts(CognitiveRequest request)
        {
            ContextSnapshot current = perceptionProvider?.BuildSnapshot(string.Empty);
            return current != null && JsonUtility.ToJson(current.SceneFacts) == JsonUtility.ToJson(request.context.scene_facts);
        }

        public bool HasCurrentParticipantIdentities(CognitiveRequest request)
        {
            if (request?.recall_identity?.observer == null || entityRegistry == null) return false;
            bool Matches(CognitiveRecallSubjectIdentityDto identity)
            {
                if (identity == null || !entityRegistry.TryGetEntity(identity.entity_id, out WorldEntity entity)) return false;
                var current = CognitiveRecallSubjectIdentityDto.From(entity);
                return current != null && current.identity_namespace == identity.identity_namespace &&
                       current.identity_type == identity.identity_type && current.persistent_id == identity.persistent_id &&
                       !string.IsNullOrWhiteSpace(current.persistent_id);
            }
            if (!Matches(request.recall_identity.observer)) return false;
            foreach (var subject in request.recall_identity.current_subjects)
                if (subject.entity_id == request.context.relevant_player?.player_entity_id) return Matches(subject);
            return false;
        }

        private void ResolveLocalReferences()
        {
            if (agentEntity == null) agentEntity = GetComponent<WorldEntity>();
            if (orchestrator == null) orchestrator = GetComponent<AgentOrchestrator>();
            if (triggerSource == null) triggerSource = GetComponent<WakeCognitiveTriggerBridge>();
            if (eventReceiver == null) eventReceiver = GetComponent<AgentEventReceiver>();
            if (behaviorMode == null) behaviorMode = GetComponent<CurrentBehaviorMode>();
            if (perceptionProvider == null) perceptionProvider = GetComponent<PerceptionContextProvider>();
            if (actionCapabilities == null) actionCapabilities = GetComponent<AgentActionCapabilityProfile>();
        }

        private void Subscribe()
        {
            if (!isActiveAndEnabled || isSubscribed || orchestrator == null) return;
            orchestrator.RunStarted += HandleRunStarted;
            isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!isSubscribed || orchestrator == null) return;
            orchestrator.RunStarted -= HandleRunStarted;
            isSubscribed = false;
        }

        private void OnValidate() => ResolveLocalReferences();
    }
}
