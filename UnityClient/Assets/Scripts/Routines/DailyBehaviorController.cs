using System;
using System.Collections.Generic;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Events;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    /// <summary>One authoritative local life mode. The existing routine remains the only navigation writer.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(90)]
    [RequireComponent(typeof(WorldEntity))]
    public sealed class DailyBehaviorController : MonoBehaviour
    {
        [SerializeField] private DailyBehaviorProfile profile;
        [SerializeField] private Transform windowRestPoint;
        [SerializeField] private Transform windowLookPoint;
        [SerializeField] private bool allowCognitiveTransitions = true;
        private WorldEntity owner;
        private AgentEventReceiver receiver;
        private DailyBehaviorDemoTrigger systemSource;
        private NpcDefaultRoutineController admission;
        private ReturnToHomeRoutine routine;
        private AgentEvent pendingEvent;
        private double enteredAt, nextTransitionAt;
        private string lastConsumedEventId;
        private string profileSignature, capturedJson, capturedRunId;
        private int profileRevision;
        private DailyBehaviorProfile observedProfile;
        private CognitiveDailyBehaviorDto capturedContext;
        private double capturedAt;
        private Animator idleAnimator;
        private AgentOrchestrator activityOrchestrator;
        private GoalPlanRunner activityTasks;
        private DailyBehaviorProfile activityProfile;
        private int activityModeRevision = -1;
        private double nextActivityCheck;
        private DailyBehaviorActivity? unavailableNavigation;
        private Vector3 unavailableDestination;
        private bool activityWasBlocked;

        public DailyBehaviorActivity? CurrentActivity { get; private set; }
        public string ActivityReason { get; private set; } = "initializing";
        public bool IsActivitySuspended => !isActiveAndEnabled || IsActivityBlocked;
        public bool IsFormalTaskOccupied => activityTasks != null && activityTasks.HasActiveTask;
        public bool IsActivityFailureCoolingDown => unavailableNavigation.HasValue && routine != null && routine.IsNavigationCoolingDown;
        public bool HasUnavailableActivity => unavailableNavigation.HasValue;
        public bool AllowsEnvironmentObservation => isActiveAndEnabled && profile != null &&
            profile.AllowsActivity(CurrentMode, DailyBehaviorActivity.ObserveEnvironment) && !IsActivitySuspended &&
            CurrentActivity.HasValue && CurrentActivity != DailyBehaviorActivity.WindowIdle;

        public DailyBehaviorProfile Profile => profile;
        public DailyBehaviorMode CurrentMode { get; private set; }
        public int ModeRevision { get; private set; }
        public string LastTransitionReason { get; private set; } = "initialized";
        public bool HasPendingTransition => pendingEvent != null;
        public bool TryGetPendingMode(out DailyBehaviorMode mode)
        {
            mode = CurrentMode;
            return pendingEvent != null && systemSource != null &&
                systemSource.TryResolveCurrentEvent(pendingEvent, out mode) && mode != CurrentMode;
        }
        public Transform WindowRestPoint => windowRestPoint;
        public bool IsActivityBlocked => admission != null ? !admission.isActiveAndEnabled || admission.IsDailyActivityBlocked :
            (activityOrchestrator != null && activityOrchestrator.HasActiveRun) || IsFormalTaskOccupied;
        public bool IsRestingAtWindow => CurrentMode == DailyBehaviorMode.Rest && CurrentActivity == DailyBehaviorActivity.WindowIdle && routine != null &&
            windowRestPoint != null && windowLookPoint != null &&
            !routine.IsRunning && !routine.OwnsPath && !routine.IsWaitingForNavigation &&
            routine.IsAtDailyDestination && !IsActivityBlocked;

        private void Awake()
        {
            owner = GetComponent<WorldEntity>(); receiver = GetComponent<AgentEventReceiver>();
            systemSource = GetComponent<DailyBehaviorDemoTrigger>();
            admission = GetComponent<NpcDefaultRoutineController>(); routine = GetComponent<ReturnToHomeRoutine>();
            idleAnimator = GetComponentInChildren<Animator>();
            activityOrchestrator = GetComponent<AgentOrchestrator>(); activityTasks = GetComponent<GoalPlanRunner>();
            if (profile == null || !profile.IsValid || receiver == null ||
                (profile.Allows(DailyBehaviorMode.Rest) && (admission == null || systemSource == null)))
            {
                Debug.LogWarning("[DailyBehavior] Invalid profile or activity references; disabled.", this);
                enabled = false; return;
            }
            CurrentMode = profile.DefaultMode;
            enteredAt = Time.unscaledTimeAsDouble;
        }
        private void OnEnable()
        {
            if (receiver == null) receiver = GetComponent<AgentEventReceiver>();
            if (receiver != null) receiver.EventReceived += ReceiveSystemEvent;
        }
        private void OnDisable()
        {
            if (receiver != null) receiver.EventReceived -= ReceiveSystemEvent;
            pendingEvent = null; routine?.SuspendRoutine();
            capturedContext = null;
            CurrentActivity = null; unavailableNavigation = null; activityModeRevision = -1;
            nextActivityCheck = 0; ActivityReason = "disabled";
        }

        private void ReceiveSystemEvent(AgentEvent value)
        {
            if (!isActiveAndEnabled || systemSource == null || value.EventId == lastConsumedEventId ||
                value.SourceEntityId != owner.EntityId || value.TargetEntityId != owner.EntityId ||
                !systemSource.TryResolveCurrentEvent(value, out _)) return;
            pendingEvent = value;
            TryApplyPendingTransition();
        }

        // The pending intent is revalidated against the current producer condition, never blindly replayed.
        public bool TryApplyPendingTransition()
        {
            if (!isActiveAndEnabled || pendingEvent == null) return false;
            if (profile == null || !profile.IsValid)
            { ConsumePending("invalid_profile"); return false; }
            if (systemSource == null || !systemSource.TryResolveCurrentEvent(pendingEvent, out var target))
            { pendingEvent = null; LastTransitionReason = "expired_condition"; return false; }
            if (target == CurrentMode)
            { ConsumePending("already_current"); return false; }
            if (!profile.AllowsTransition(CurrentMode, target, pendingEvent.EventType))
            { ConsumePending("transition_not_allowed"); return false; }
            double now = Time.unscaledTimeAsDouble;
            if (IsActivityBlocked || admission == null || !admission.isActiveAndEnabled ||
                now < nextTransitionAt || now - enteredAt < profile.MinimumModeSeconds)
            { LastTransitionReason = "deferred_busy_or_cooldown"; return false; }
            routine?.SuspendRoutine();
            CurrentMode = target; ModeRevision++;
            enteredAt = now; nextTransitionAt = now + profile.TransitionCooldown;
            routine?.ResetNavigationRetry();
            ConsumePending("system_event_applied");
            return true;
        }
        private void ConsumePending(string reason)
        {
            lastConsumedEventId = pendingEvent.EventId; pendingEvent = null; LastTransitionReason = reason;
        }
        private void Update()
        {
            TryApplyPendingTransition();
            TickActivitySelection();
        }

        // Cheap occupancy edges are checked immediately; candidate evaluation is bounded to 1 Hz.
        // No timers/callbacks survive disable and no selection wakes cognition or grants world actions.
        private void TickActivitySelection()
        {
            if (!isActiveAndEnabled) return;
            bool changedMode = activityModeRevision != ModeRevision || activityProfile != profile;
            if (changedMode)
            {
                routine?.SuspendRoutine(); CurrentActivity = null; unavailableNavigation = null;
                activityModeRevision = ModeRevision; activityProfile = profile; nextActivityCheck = 0;
            }
            bool blocked = IsActivityBlocked;
            if (blocked)
            {
                routine?.SuspendRoutine(); activityWasBlocked = true;
                ActivityReason = IsFormalTaskOccupied ? "formal_task" : "interaction_or_cognition_busy";
                return;
            }
            if (activityWasBlocked)
            {
                activityWasBlocked = false; CurrentActivity = null; nextActivityCheck = 0;
            }
            double now = Time.unscaledTimeAsDouble;
            if (now < nextActivityCheck) return;
            nextActivityCheck = now + 1;
            DailyBehaviorActivity? selected = null;
            if (profile != null && profile.IsValid)
                foreach (var binding in profile.ActivitiesFor(CurrentMode))
                    if (!binding.IsOverlay && CanConsiderActivity(binding.activity) && CanResumeNavigation(binding.activity))
                    { selected = binding.activity; break; }
            if (selected != CurrentActivity)
            {
                routine?.SuspendRoutine(); CurrentActivity = selected;
            }
            ActivityReason = !selected.HasValue ? "safe_existing_idle" :
                unavailableNavigation.HasValue ? "navigation_unavailable_fallback" : "profile_preference_conditions_met";
        }

        private bool CanConsiderActivity(DailyBehaviorActivity activity)
        {
            bool hasIdle = idleAnimator != null && idleAnimator.isActiveAndEnabled && idleAnimator.runtimeAnimatorController != null;
            switch (activity)
            {
                case DailyBehaviorActivity.HomeIdle: return routine != null && routine.isActiveAndEnabled;
                case DailyBehaviorActivity.WindowIdle: return routine != null && routine.isActiveAndEnabled && TryGetRestDestination(out _, out _);
                case DailyBehaviorActivity.NaturalIdle: return hasIdle && routine != null && routine.isActiveAndEnabled;
                case DailyBehaviorActivity.SeatedIdle: return hasIdle && CurrentMode == DailyBehaviorMode.Leisure && routine == null;
                default: return false;
            }
        }

        private bool CanResumeNavigation(DailyBehaviorActivity activity)
        {
            if (unavailableNavigation != activity) return true;
            if (routine == null || routine.IsNavigationCoolingDown) return false;
            Transform target = activity == DailyBehaviorActivity.WindowIdle ? windowRestPoint : null;
            Vector3 point = target == null ? routine.HomePosition : target.position;
            // A failed path is not re-issued on a timer. Only a changed/reachable condition admits it again.
            if ((point - unavailableDestination).sqrMagnitude < .0001f && !routine.CanReachDailyDestination(target)) return false;
            unavailableNavigation = null;
            return true;
        }

        internal void NotifyActivityNavigationFailure()
        {
            if (!isActiveAndEnabled || !CurrentActivity.HasValue || routine == null) return;
            unavailableNavigation = CurrentActivity;
            unavailableDestination = routine.DailyDestination;
            nextActivityCheck = 0;
            TickActivitySelection();
        }

        private int CurrentProfileRevision()
        {
            string signature = profile == null ? string.Empty : JsonUtility.ToJson(profile);
            if (signature != profileSignature || !ReferenceEquals(profile, observedProfile))
            { profileRevision++; profileSignature = signature; observedProfile = profile; }
            return profileRevision;
        }
        internal bool HasCurrentModeProfile(CognitiveDailyBehaviorDto context) => isActiveAndEnabled && context != null &&
            context.current_mode == CurrentMode.ToString().ToLowerInvariant() && context.mode_revision == ModeRevision &&
            context.profile_revision == CurrentProfileRevision();
        public CognitiveDailyBehaviorDto CaptureCognitiveContext(string runId, bool systemConsideration = false)
        {
            if (!isActiveAndEnabled || profile == null || !profile.IsValid) return null;
            var goal = profile.GoalFor(CurrentMode);
            var candidates = new List<CognitiveDailyModeCandidateDto>();
            if (allowCognitiveTransitions)
                foreach (DailyBehaviorMode mode in Enum.GetValues(typeof(DailyBehaviorMode)))
                    if (profile.AllowsCognitiveTransition(CurrentMode, mode))
                        candidates.Add(new CognitiveDailyModeCandidateDto {
                            target_mode = mode.ToString().ToLowerInvariant(),
                            condition = systemConsideration ? "结合服务区停留事实与目标：" + profile.GoalFor(mode).objective + " 仅静默考虑模式，不构成服务请求或动作许可。" :
                                mode == DailyBehaviorMode.Work ? "根据店员职责和当前合理请求，自愿恢复工作；仍需现有动作许可。" :
                                "当前请求适合个人休闲，且没有冲突的正式任务或交互。" });
            capturedContext = new CognitiveDailyBehaviorDto {
                context_token = Guid.NewGuid().ToString("N"), current_mode = CurrentMode.ToString().ToLowerInvariant(),
                mode_revision = ModeRevision, profile_revision = CurrentProfileRevision(),
                condition_revision = systemSource == null ? 0 : systemSource.ConditionRevision,
                objective = goal.objective, tendency = goal.tendency,
                considered_activities = profile.ConsideredActivitiesFor(CurrentMode), candidates = candidates.ToArray(),
                constraints = new[] { "模式目标不构成当前世界事实或正式动作许可。", "活动自行检查位置和导航条件；不可用时安全待机。",
                    systemConsideration ? "仅当前可信服务区条件允许静默模式考虑；不是玩家请求，不代表视觉证据。" : "提议只用于当前玩家请求，任务/导航/Carry 冲突或状态变化会拒绝。",
                    systemConsideration ? "只允许 NO_ACTION 或 DAILY_MODE_INTENT；禁止回复、表现、正式动作和对话经历。" : "模式意图可附带普通回复或合法 ActionProposal；动作另行验证和接收，完成须有真实 ActionOutcome。" } };
            capturedJson = JsonUtility.ToJson(capturedContext); capturedRunId = runId;
            capturedAt = Time.unscaledTimeAsDouble;
            return capturedContext;
        }

        // Read-only prevalidation. The commit path repeats it against the current local state.
        public bool TryValidateCognitiveMode(CognitiveRequest request, CognitiveRun run, string targetMode, out string reason)
            => TryValidateMode(request, run, targetMode, false, out reason);

        private bool TryValidateMode(CognitiveRequest request, CognitiveRun run, string targetMode, bool system, out string reason)
        {
            reason = "stale_or_invalid_context";
            var snapshot = request?.context?.daily_behavior;
            var currentEvent = request?.context?.current_event;
            var registry = FindAnyObjectByType<EntityRegistry>();
            WorldEntity player = null;
            if (!isActiveAndEnabled || owner == null || !owner.isActiveAndEnabled || profile == null || !profile.IsValid || !allowCognitiveTransitions ||
                snapshot == null || capturedContext == null || !snapshot.IsValid ||
                snapshot.context_token != capturedContext.context_token || JsonUtility.ToJson(snapshot) != capturedJson ||
                run == null || run.State != CognitiveRunState.Running || capturedRunId != run.RunId ||
                request.run.run_id != run.RunId || request.context.run_id != run.RunId ||
                request.run.agent_entity_id != owner.EntityId || request.context.agent_entity_id != owner.EntityId ||
                !ReferenceEquals(GetComponent<AgentOrchestrator>()?.CurrentRun, run) ||
                currentEvent == null || (system ?
                    currentEvent.event_type != AgentEventTypes.PlayerDwelledInServiceZone || CurrentMode != DailyBehaviorMode.Rest || targetMode != "work" ||
                        !(GetComponent<ServiceZoneDwellCognitionSource>()?.Matches(request, run) ?? false) :
                    currentEvent.event_type != AgentEventTypes.PlayerUtterance || run.IsServiceZoneConsideration) ||
                currentEvent.target_entity_id != owner.EntityId || request.context.relevant_player == null ||
                currentEvent.source_entity_id != request.context.relevant_player.player_entity_id || registry == null ||
                !registry.TryGetEntity(currentEvent.source_entity_id, out player) || !player.isActiveAndEnabled || !player.IsAgent(AgentRole.Player) ||
                Time.unscaledTimeAsDouble - capturedAt > 30 || snapshot.mode_revision != ModeRevision ||
                snapshot.current_mode != CurrentMode.ToString().ToLowerInvariant() ||
                snapshot.profile_revision != CurrentProfileRevision() ||
                snapshot.condition_revision != (systemSource == null ? 0 : systemSource.ConditionRevision)) return false;
            if (!CognitiveDailyBehaviorDto.IsMode(targetMode) || !Enum.TryParse(targetMode, true, out DailyBehaviorMode target) ||
                !Array.Exists(snapshot.candidates, c => c.target_mode == targetMode) ||
                !profile.AllowsCognitiveTransition(CurrentMode, target))
            { reason = "target_not_allowed"; return false; }
            double now = Time.unscaledTimeAsDouble;
            if (HasPendingTransition || admission == null || !admission.isActiveAndEnabled ||
                admission.IsCognitiveModeTransitionBlocked(run) || now < nextTransitionAt || now - enteredAt < profile.MinimumModeSeconds)
            { reason = "conflicting_activity_or_cooldown"; return false; }
            reason = "daily_mode_valid";
            return true;
        }

        public bool TryCommitSystemMode(CognitiveRequest request, CognitiveRun run, string targetMode, out string reason)
        {
            if (!TryValidateMode(request, run, targetMode, true, out reason)) return false;
            CurrentMode = DailyBehaviorMode.Work; ModeRevision++; enteredAt = Time.unscaledTimeAsDouble;
            nextTransitionAt = enteredAt + profile.TransitionCooldown;
            FinishCognitiveCommit(CurrentMode); reason = "system_mode_committed"; return true;
        }

        // Commit only the checked life state. Dialogue and action presentation
        // have their own admission and do not roll this state back.
        public bool TryCommitCognitiveMode(CognitiveRequest request, CognitiveRun run, string targetMode, out string reason)
        {
            if (!TryValidateCognitiveMode(request, run, targetMode, out reason)) return false;
            Enum.TryParse(targetMode, true, out DailyBehaviorMode target);
            CurrentMode = target; ModeRevision++; enteredAt = Time.unscaledTimeAsDouble;
            nextTransitionAt = enteredAt + profile.TransitionCooldown;
            FinishCognitiveCommit(target);
            reason = "cognition_mode_applied";
            return true;
        }
        private void FinishCognitiveCommit(DailyBehaviorMode target)
        {
            routine?.SuspendRoutine(); routine?.ResetNavigationRetry();
            systemSource?.SynchronizeCommittedMode(target);
            capturedContext = null; LastTransitionReason = "cognition_mode_applied";
        }

        public bool TryGetRestDestination(out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = default;
            if (!isActiveAndEnabled || profile == null || !profile.AllowsActivity(CurrentMode, DailyBehaviorActivity.WindowIdle) ||
                windowRestPoint == null || windowLookPoint == null || !windowRestPoint.gameObject.activeInHierarchy ||
                !windowLookPoint.gameObject.activeInHierarchy) return false;
            position = windowRestPoint.position; rotation = windowRestPoint.rotation; return true;
        }
        public bool TryGetRestGazePoint(out Vector3 point)
        {
            point = default;
            if (!IsRestingAtWindow || windowLookPoint == null) return false;
            point = windowLookPoint.position; return true;
        }
    }
}
