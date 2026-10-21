using System;

namespace EmbodiedNPC.Cognition
{
    /// <summary>One bounded cognition execution identity with a minimal deterministic lifecycle.</summary>
    public sealed class CognitiveRun
    {
        public CognitiveRun(string runId, string agentEntityId, string triggerId, string triggerType,
            double startedAt, bool isServiceZoneConsideration = false, bool isCounterCleaningConsideration = false, bool isTaskConsideration = false)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("RunId is required.", nameof(runId));
            if (string.IsNullOrWhiteSpace(agentEntityId))
                throw new ArgumentException("AgentEntityId is required.", nameof(agentEntityId));
            if (string.IsNullOrWhiteSpace(triggerId))
                throw new ArgumentException("TriggerId is required.", nameof(triggerId));
            if (string.IsNullOrWhiteSpace(triggerType))
                throw new ArgumentException("TriggerType is required.", nameof(triggerType));

            RunId = runId;
            IsServiceZoneConsideration = isServiceZoneConsideration;
            IsCounterCleaningConsideration = isCounterCleaningConsideration;
            IsTaskConsideration = isTaskConsideration;
            AgentEntityId = agentEntityId;
            TriggerId = triggerId;
            TriggerType = triggerType;
            State = CognitiveRunState.Running;
            StartedAt = Math.Max(0d, startedAt);
        }

        public string RunId { get; }
        public bool IsServiceZoneConsideration { get; }
        public bool IsCounterCleaningConsideration { get; }
        public bool IsTaskConsideration { get; }
        public bool IsLowPrioritySystemConsideration => IsServiceZoneConsideration || IsCounterCleaningConsideration || IsTaskConsideration;
        public string AgentEntityId { get; }
        public string TriggerId { get; }
        public string TriggerType { get; }
        public CognitiveRunState State { get; private set; }
        public double StartedAt { get; }
        public double? EndedAt { get; private set; }
        public bool IsTerminal => State == CognitiveRunState.Completed ||
                                  State == CognitiveRunState.Cancelled ||
                                  State == CognitiveRunState.Failed;

        internal bool TryWaitForActionResult()
        {
            if (State != CognitiveRunState.Running) return false;
            State = CognitiveRunState.WaitingActionResult;
            return true;
        }

        internal bool TryFinish(CognitiveRunState terminalState, double endedAt)
        {
            if (IsTerminal || terminalState == CognitiveRunState.Running ||
                terminalState == CognitiveRunState.WaitingActionResult) return false;

            State = terminalState;
            EndedAt = Math.Max(StartedAt, endedAt);
            return true;
        }
    }
}
