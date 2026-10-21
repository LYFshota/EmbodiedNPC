using System;
using System.Collections.Generic;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Planning.Recovery
{
    public enum FailureCategory
    {
        Transient,
        Recoverable,
        MissingInformation,
        Terminal,
        Infrastructure,
        Interrupted,
    }

    public enum RecoveryAction
    {
        RetryCurrentAction,
        RequestClarification,
        AbortGoal,
    }

    public static class FailureClassifier
    {
        public static FailureCategory Classify(string failureCode, SkillStatus status)
        {
            string code = (failureCode ?? string.Empty).Trim().ToUpperInvariant();

            if (status == SkillStatus.Cancelled || code.Contains("CANCELLED") || code.Contains("INTERRUPTED") ||
                code.Contains("THREAT"))
            {
                return FailureCategory.Interrupted;
            }

            return code switch
            {
                "TARGET_RESERVED" or "OWNERSHIP_CONFLICT" or "TRANSFER_CONFLICT" or
                "PATH_INVALID" or "PATH_PARTIAL" or "PLAYER_PATH_INVALID" or
                "DESTINATION_REJECTED" or "PLAYER_DESTINATION_REJECTED" or
                "PICKUP_TIMEOUT" or "GIVE_TIMEOUT" or "WALK_TIMEOUT" or
                "AGENT_NOT_ON_NAVMESH" => FailureCategory.Transient,

                "HAND_OCCUPIED" or "POSE_INVALID" or "ITEM_ALREADY_HELD" => FailureCategory.Recoverable,

                "TARGET_NOT_FOUND" or "TARGET_NOT_PERCEIVED" or "DESTINATION_NOT_FOUND" =>
                    FailureCategory.MissingInformation,

                "BACKEND_UNAVAILABLE" or "WORLD_STATE_UNAVAILABLE" or "PROVIDER_TIMEOUT" or
                "CONNECTION_LOST" => FailureCategory.Infrastructure,

                _ => FailureCategory.Terminal,
            };
        }
    }

    public readonly struct FailureSignature : IEquatable<FailureSignature>
    {
        public FailureSignature(string goalType, string actionType, string targetId, string failureCode,
            int worldRevision)
        {
            GoalType = goalType ?? string.Empty;
            ActionType = actionType ?? string.Empty;
            TargetId = targetId ?? string.Empty;
            FailureCode = failureCode ?? string.Empty;
            WorldRevision = worldRevision;
        }

        public string GoalType { get; }
        public string ActionType { get; }
        public string TargetId { get; }
        public string FailureCode { get; }
        public int WorldRevision { get; }

        public bool Equals(FailureSignature other) =>
            string.Equals(GoalType, other.GoalType, StringComparison.Ordinal) &&
            string.Equals(ActionType, other.ActionType, StringComparison.Ordinal) &&
            string.Equals(TargetId, other.TargetId, StringComparison.Ordinal) &&
            string.Equals(FailureCode, other.FailureCode, StringComparison.Ordinal) &&
            WorldRevision == other.WorldRevision;

        public override bool Equals(object obj) => obj is FailureSignature other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(GoalType);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ActionType);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(TargetId);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(FailureCode);
                return hash * 31 + WorldRevision;
            }
        }

        public override string ToString() =>
            $"{GoalType}|{ActionType}|{TargetId}|{FailureCode}|rev:{WorldRevision}";
    }

    public sealed class RetryBudget
    {
        private readonly int maxSameFailureRetries;
        private readonly int maxRecoveriesPerGoal;
        private readonly float deadlineAt;
        private readonly Dictionary<FailureSignature, int> retriesBySignature = new();
        private int recoveryCount;

        public RetryBudget(float startedAt, int maxSameFailureRetries = 1, int maxRecoveriesPerGoal = 2,
            float maxGoalDurationSeconds = 30f)
        {
            this.maxSameFailureRetries = Math.Max(0, maxSameFailureRetries);
            this.maxRecoveriesPerGoal = Math.Max(0, maxRecoveriesPerGoal);
            deadlineAt = startedAt + Math.Max(0f, maxGoalDurationSeconds);
        }

        public int RecoveryCount => recoveryCount;
        public bool IsExpired(float now) => now >= deadlineAt;

        public bool TryConsume(FailureSignature signature, float now, out string rejectionCode)
        {
            if (IsExpired(now))
            {
                rejectionCode = "GOAL_DEADLINE_EXCEEDED";
                return false;
            }

            if (recoveryCount >= maxRecoveriesPerGoal)
            {
                rejectionCode = "GOAL_RECOVERY_BUDGET_EXHAUSTED";
                return false;
            }

            retriesBySignature.TryGetValue(signature, out int sameFailureRetries);
            if (sameFailureRetries >= maxSameFailureRetries)
            {
                rejectionCode = "SAME_FAILURE_RETRY_EXHAUSTED";
                return false;
            }

            retriesBySignature[signature] = sameFailureRetries + 1;
            recoveryCount++;
            rejectionCode = string.Empty;
            return true;
        }
    }

    public readonly struct RecoveryDecision
    {
        public RecoveryDecision(FailureCategory category, RecoveryAction action, string reasonCode)
        {
            Category = category;
            Action = action;
            ReasonCode = reasonCode ?? string.Empty;
        }

        public FailureCategory Category { get; }
        public RecoveryAction Action { get; }
        public string ReasonCode { get; }
    }

    public static class RecoveryPolicy
    {
        public static RecoveryDecision Decide(SkillResult result, FailureSignature signature,
            RetryBudget budget, float now)
        {
            FailureCategory category = FailureClassifier.Classify(result.FailureCode, result.Status);
            if (category == FailureCategory.MissingInformation)
            {
                return new RecoveryDecision(category, RecoveryAction.RequestClarification, result.FailureCode);
            }

            if (category != FailureCategory.Transient && category != FailureCategory.Recoverable)
            {
                return new RecoveryDecision(category, RecoveryAction.AbortGoal, result.FailureCode);
            }

            string rejectionCode = string.Empty;
            if (budget != null && budget.TryConsume(signature, now, out rejectionCode))
            {
                return new RecoveryDecision(category, RecoveryAction.RetryCurrentAction, "RETRY_ALLOWED");
            }

            return new RecoveryDecision(category, RecoveryAction.AbortGoal,
                string.IsNullOrWhiteSpace(rejectionCode) ? "RECOVERY_BUDGET_UNAVAILABLE" : rejectionCode);
        }
    }
}
