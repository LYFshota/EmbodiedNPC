using System;
using System.Collections.Generic;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    /// <summary>A bounded local demo condition source; one timed break per scene, with manual replay.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(DailyBehaviorController))]
    public sealed class DailyBehaviorDemoTrigger : MonoBehaviour
    {
        [SerializeField] private AgentEventRouter router;
        [SerializeField] private bool automaticDemo = false;
        [SerializeField, Min(1)] private float breakStartsAfterSeconds = 15f;
        [SerializeField, Min(1)] private float breakDurationSeconds = 35f;
        private WorldEntity owner;
        private AgentEventReceiver receiver;
        private AgentEvent currentEvent;
        private double sceneStartedAt, breakEndsAt;
        private bool automaticAttempted;
        private bool timedBreak;
        private int conditionRevision;
        public int ConditionRevision => conditionRevision;
        internal void SynchronizeCommittedMode(DailyBehaviorMode mode)
        {
            BreakRequested = mode == DailyBehaviorMode.Rest;
            timedBreak = false; currentEvent = null; conditionRevision++;
            automaticAttempted = true;
        }
        public bool BreakRequested { get; private set; }
        public int PublishedEventCount { get; private set; }
        public AgentEvent LastEvent => currentEvent;
        public void ConfigureDemo(bool automatic, float startAfter, float duration)
        { automaticDemo = automatic; breakStartsAfterSeconds = startAfter; breakDurationSeconds = duration; }

        private void Awake()
        {
            owner = GetComponent<WorldEntity>(); receiver = GetComponent<AgentEventReceiver>();
            sceneStartedAt = Time.unscaledTimeAsDouble;
        }
        private void OnDisable() { currentEvent = null; BreakRequested = false; }
        private void Update()
        {
            double now = Time.unscaledTimeAsDouble;
            if (automaticDemo && !automaticAttempted && now - sceneStartedAt >= breakStartsAfterSeconds)
            { automaticAttempted = true; BeginBreak(); }
            if (BreakRequested && timedBreak && now >= breakEndsAt) EndBreak();
        }
        [ContextMenu("Demo/Begin window break")]
        private void BeginBreakMenu() => BeginBreak();
        [ContextMenu("Demo/End window break")]
        private void EndBreakMenu() => EndBreak();
        public bool BeginBreak()
        {
            if (!Application.isPlaying || !isActiveAndEnabled || BreakRequested ||
                !float.IsFinite(breakDurationSeconds) || breakDurationSeconds <= 0) return false;
            return Publish(true, true);
        }
        public bool EndBreak()
        {
            if (!Application.isPlaying || !isActiveAndEnabled || !BreakRequested) return false;
            return Publish(false, false);
        }
        // An explicit local request lasts until changed by the user; the controller still owns admission.
        public bool RequestMode(DailyBehaviorMode mode)
        {
            if (!Application.isPlaying || !isActiveAndEnabled ||
                (mode != DailyBehaviorMode.Work && mode != DailyBehaviorMode.Rest)) return false;
            var controller = GetComponent<DailyBehaviorController>();
            if (controller == null || !controller.isActiveAndEnabled || !controller.Profile.Allows(mode)) return false;
            automaticAttempted = true;
            bool begin = mode == DailyBehaviorMode.Rest;
            if (begin == BreakRequested) { timedBreak = false; return true; }
            return Publish(begin, false);
        }
        private bool Publish(bool begin, bool timed)
        {
            if (owner == null || receiver == null || router == null || !router.isActiveAndEnabled) return false;
            string type = begin ? AgentEventTypes.WorkBreakStarted : AgentEventTypes.WorkBreakEnded;
            var value = new AgentEvent("agent_event_" + Guid.NewGuid().ToString("N"), type,
                owner.EntityId, owner.EntityId, string.Empty,
                new Dictionary<string, string> { ["producer"] = "daily_behavior_demo",
                    ["condition_revision"] = (conditionRevision + 1).ToString() }, Time.unscaledTimeAsDouble);
            // A misconfigured profile must not turn this purely local demo into a cognitive request.
            var decision = EventInterestEvaluator.Evaluate(value, owner.EntityId, receiver.InterestProfile,
                receiver.CurrentBehaviorMode == null ? string.Empty : receiver.CurrentBehaviorMode.CurrentModeId,
                receiver.ModeInterestOverrides);
            if (decision == EventInterestDecision.Wake || decision == EventInterestDecision.FastPath) return false;
            var previousEvent = currentEvent;
            bool previousRequested = BreakRequested, previousTimed = timedBreak;
            double previousEnd = breakEndsAt;
            BreakRequested = begin; timedBreak = begin && timed; conditionRevision++;
            if (begin) breakEndsAt = Time.unscaledTimeAsDouble + breakDurationSeconds;
            currentEvent = value;
            int delivered = router.Route(value);
            if (delivered == 1) PublishedEventCount++;
            else
            {
                currentEvent = previousEvent; BreakRequested = previousRequested;
                timedBreak = previousTimed; breakEndsAt = previousEnd; conditionRevision--;
            }
            return delivered == 1;
        }
        public bool TryResolveCurrentEvent(AgentEvent value, out DailyBehaviorMode target)
        {
            target = BreakRequested ? DailyBehaviorMode.Rest : DailyBehaviorMode.Work;
            return isActiveAndEnabled && ReferenceEquals(value, currentEvent) && value != null &&
                value.SourceEntityId == owner.EntityId && value.TargetEntityId == owner.EntityId &&
                value.EventType == (BreakRequested ? AgentEventTypes.WorkBreakStarted : AgentEventTypes.WorkBreakEnded) &&
                (!BreakRequested || !timedBreak || Time.unscaledTimeAsDouble < breakEndsAt);
        }
    }
}
