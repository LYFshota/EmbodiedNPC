using System;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    [RequireComponent(typeof(WorldEntity))]
    public sealed class PerceivedStateStore : MonoBehaviour
    {
        [SerializeField] private WorldEntity observer;
        private PerceptionAggregator aggregator;
        private ObservationExperienceAdapter observationExperience;
        private GoalOutcomeExperienceAdapter goalExperience;
        private SkillResultExperienceAdapter skillExperience;
        private DialogueExperienceAdapter dialogueExperience;
        private GoalPlanRunner goalPlanRunner;
        private CognitiveRunDriver cognitiveRunDriver;
        private WorldStateService worldState;
        private bool experienceSubscribed;
        public event Action<ObservationApplyResult> ObservationApplied;

        public PerceptionAggregator Aggregator => aggregator;
        public SpatialWorkingMemory SpatialMemory { get; private set; }
        public string ObserverEntityId => observer == null ? string.Empty : observer.EntityId;

        private void Awake()
        {
            SpatialMemory = new SpatialWorkingMemory();
            if (observer == null) observer = GetComponent<WorldEntity>();
            if (observer == null || string.IsNullOrWhiteSpace(observer.EntityId))
            {
                Debug.LogError("[Perception] PerceivedStateStore requires a registered WorldEntity.", this);
                return;
            }
            aggregator = new PerceptionAggregator(observer.EntityId);
            InitializeExperienceAdapters();
        }

        private void Start() => InitializeExperienceAdapters();

        private void OnEnable() => SubscribeExperienceAdapters();

        private void OnDisable()
        {
            UnsubscribeExperienceAdapters();
            // Component disable may be temporary; retain observer-local evidence until its original TTL.
            SpatialMemory?.RecordLifecycle("observer_disabled_keep", Time.realtimeSinceStartupAsDouble);
        }

        private void OnDestroy()
        {
            UnsubscribeExperienceAdapters();
            SpatialMemory?.Clear("observer_destroyed", Time.realtimeSinceStartupAsDouble);
        }

        private void InitializeExperienceAdapters()
        {
            if (observationExperience != null || string.IsNullOrWhiteSpace(ObserverEntityId)) return;
            BackendWebSocketClient backend = FindAnyObjectByType<BackendWebSocketClient>();
            if (backend == null) return;
            Action<ExperienceDraft> sink = draft => backend.EnqueueExperience(draft);
            EntityRegistry entityRegistry = FindAnyObjectByType<EntityRegistry>();
            observationExperience = new ObservationExperienceAdapter(sink, entityRegistry: entityRegistry);
            dialogueExperience = new DialogueExperienceAdapter(sink, entityRegistry);
            cognitiveRunDriver = GetComponent<CognitiveRunDriver>();
            goalPlanRunner = GetComponent<GoalPlanRunner>();
            worldState = FindAnyObjectByType<WorldStateService>();
            if (goalPlanRunner != null)
                goalExperience = new GoalOutcomeExperienceAdapter(ObserverEntityId, sink, entityRegistry, worldState);
            if (worldState != null)
                skillExperience = new SkillResultExperienceAdapter(ObserverEntityId, sink, entityRegistry);
            SubscribeExperienceAdapters();
        }

        private void SubscribeExperienceAdapters()
        {
            if (experienceSubscribed || !isActiveAndEnabled || observationExperience == null) return;
            ObservationApplied += observationExperience.Handle;
            if (cognitiveRunDriver != null)
                cognitiveRunDriver.UtterancePresented += dialogueExperience.Handle;
            if (goalExperience != null) goalPlanRunner.GoalOutcomePublished += goalExperience.Handle;
            if (skillExperience != null) worldState.SkillResultPublished += skillExperience.Handle;
            experienceSubscribed = true;
        }

        private void UnsubscribeExperienceAdapters()
        {
            if (!experienceSubscribed) return;
            ObservationApplied -= observationExperience.Handle;
            if (cognitiveRunDriver != null && dialogueExperience != null)
                cognitiveRunDriver.UtterancePresented -= dialogueExperience.Handle;
            if (goalExperience != null && goalPlanRunner != null)
                goalPlanRunner.GoalOutcomePublished -= goalExperience.Handle;
            if (skillExperience != null && worldState != null)
                worldState.SkillResultPublished -= skillExperience.Handle;
            experienceSubscribed = false;
        }

        public void ResetSpatialMemory(string reason = "explicit_reset")
        {
            SpatialMemory?.Clear(reason, Time.realtimeSinceStartupAsDouble);
        }

        public ObservationApplyResult Apply(Observation observation)
        {
            ObservationApplyResult result = aggregator == null
                ? new ObservationApplyResult(ObservationApplyCode.Invalid, observation, 0)
                : aggregator.Apply(observation);
            if (result.IsApplied) ObservationApplied?.Invoke(result);
            return result;
        }

        /// <summary>Used only by continuous sensors. Immutable event observations must use Apply.</summary>
        public ObservationApplyResult ApplyOrRefresh(Observation observation)
        {
            ObservationApplyResult result = aggregator == null
                ? new ObservationApplyResult(ObservationApplyCode.Invalid, observation, 0)
                : aggregator.ApplyOrRefresh(observation);
            if (result.IsApplied) ObservationApplied?.Invoke(result);
            return result;
        }
    }
}
