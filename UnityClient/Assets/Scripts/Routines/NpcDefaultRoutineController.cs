using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    /// <summary>Local fallback admission; never submits cognition or formal work.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class NpcDefaultRoutineController : MonoBehaviour
    {
        [SerializeField] private WorldEntity owner;
        [SerializeField] private GoalPlanRunner goalPlanRunner;
        [SerializeField] private AgentOrchestrator orchestrator;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private ExpressiveController expressive;
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private MonoBehaviour defaultRoutineBehaviour;
        [SerializeField, Min(0f)] private float engagementHoldSeconds = 8f;
        [SerializeField, Min(0f)] private float resumeDelay = 1f;
        private INpcDefaultRoutine defaultRoutine;
        private double engagementUntil;
        private float quietSeconds;
        public bool IsEngagementHeld => Time.unscaledTimeAsDouble < engagementUntil;
        public string LastEngagementReason { get; private set; } = string.Empty;
        // The originating player Run and its conversation may change situation, but cannot bypass task occupancy.
        public bool IsCognitiveModeTransitionBlocked(CognitiveRun ownRun) => IsOwnedCognitionBlocked(ownRun, false);
        // The driver installs outcome ownership before the original handoff. Only that
        // same task Run may cross Running -> WaitingActionResult; all live motion guards stay.
        internal bool IsTaskHandoffBlocked(CognitiveRun ownRun) => IsOwnedCognitionBlocked(ownRun, true);
        private bool IsOwnedCognitionBlocked(CognitiveRun ownRun, bool taskHandoff) => goalPlanRunner == null ||
            orchestrator == null || motionChannels == null || ownRun == null ||
            !ReferenceEquals(orchestrator.CurrentRun, ownRun) ||
            !(ownRun.State == CognitiveRunState.Running || taskHandoff && ownRun.IsTaskConsideration && ownRun.State == CognitiveRunState.WaitingActionResult) ||
            goalPlanRunner.HasActiveTask || motionChannels.IsEmergencyMotionActive ||
            motionChannels.IsInteractionMotionActive || motionChannels.IsFormalTaskMotionActive ||
            motionChannels.IsCarryPresentationActive || motionChannels.ActiveGestureId != 0 ||
            motionChannels.IsNavigationActive || (expressive != null && expressive.Speech != null && expressive.Speech.IsSpeaking);
        public bool IsDailyActivityBlocked => goalPlanRunner == null || orchestrator == null || motionChannels == null ||
            goalPlanRunner.HasActiveTask || orchestrator.HasActiveRun || IsEngagementHeld ||
            motionChannels.IsEmergencyMotionActive || motionChannels.IsInteractionMotionActive ||
            motionChannels.IsFormalTaskMotionActive || motionChannels.IsCarryPresentationActive ||
            motionChannels.ActiveGestureId != 0 ||
            (motionChannels.IsNavigationActive && defaultRoutineBehaviour is ReturnToHomeRoutine navigation && !navigation.OwnsPath) ||
            (expressive != null && expressive.Speech != null && expressive.Speech.IsSpeaking) ||
            (dialogue != null && dialogue.IsPresentationVisible && dialogue.SelectedTarget != null && dialogue.SelectedTarget.Entity == owner);

        private void Awake()
        {
            defaultRoutine = defaultRoutineBehaviour as INpcDefaultRoutine;
            if (defaultRoutine == null || owner == null || goalPlanRunner == null || orchestrator == null ||
                motionChannels == null || expressive == null || dialogue == null || entityRegistry == null)
            {
                Debug.LogWarning("[DefaultRoutine] Invalid routine or required references; execution disabled.", this);
                enabled = false;
            }
        }
        private void OnEnable()
        {
            if (dialogue != null) dialogue.DialogueSubmissionAccepted += OnAcceptedUtterance;
            if (goalPlanRunner != null) goalPlanRunner.GoalOutcomePublished += OnGoalOutcome;
            if (orchestrator != null) orchestrator.RunStarted += OnRunStarted;
        }
        private void OnDisable()
        {
            if (dialogue != null) dialogue.DialogueSubmissionAccepted -= OnAcceptedUtterance;
            if (goalPlanRunner != null) goalPlanRunner.GoalOutcomePublished -= OnGoalOutcome;
            if (orchestrator != null) orchestrator.RunStarted -= OnRunStarted;
            defaultRoutine?.SuspendRoutine();
            quietSeconds = 0f;
            ReleaseEngagement();
        }
        public void RefreshEngagement(string reason)
        {
            engagementUntil = Time.unscaledTimeAsDouble + engagementHoldSeconds;
            LastEngagementReason = reason ?? string.Empty;
            quietSeconds = 0f;
        }
        public void ReleaseEngagement() => engagementUntil = 0;
        private void OnRunStarted(CognitiveRun run)
        {
            // Release only our path before a same-frame action admission reads navigation busy state.
            quietSeconds = 0f;
            defaultRoutine?.SuspendRoutine();
        }
        private bool IsPlayer(string id) => entityRegistry != null &&
            entityRegistry.TryGetEntity(id, out WorldEntity entity) && entity.IsAgent(AgentRole.Player);
        private void OnAcceptedUtterance(UserUtterance utterance)
        {
            if (utterance != null && owner != null && utterance.targetAgentId == owner.EntityId &&
                IsPlayer(utterance.speakerEntityId)) RefreshEngagement("player_utterance");
        }
        private void OnGoalOutcome(GoalOutcome outcome)
        {
            if (owner != null && outcome.ActorEntityId == owner.EntityId &&
                outcome.Status == PlanExecutionStatus.Succeeded &&
                (IsPlayer(outcome.TargetEntityId) || IsPlayer(outcome.DestinationEntityId)))
                RefreshEngagement("player_directed_goal");
        }
        private void LateUpdate()
        {
            if (defaultRoutine == null || defaultRoutineBehaviour == null || !defaultRoutineBehaviour.isActiveAndEnabled) return;
            // Navigation is deliberately excluded: the routine's own path is ordinary locomotion.
            bool blocked = IsDailyActivityBlocked;
            if (blocked || IsEngagementHeld)
            {
                quietSeconds = 0f;
                defaultRoutine.SuspendRoutine();
                return;
            }
            quietSeconds += Time.unscaledDeltaTime;
            if (quietSeconds >= resumeDelay) defaultRoutine.TickRoutine(Time.deltaTime);
        }
    }
}
