using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EmbodiedNPC.Actions
{
    public enum ActionExecutionStatus
    {
        Accepted,
        Recovering,
        Rejected,
        Succeeded,
        Failed,
        Cancelled,
        TimedOut,
        RequiresConfirmation,
        Clarification,
    }

    /// <summary>Immutable Action-layer result data. It mirrors the existing lifecycle; it does not drive it.</summary>
    public readonly struct ActionExecutionResult
    {
        public ActionExecutionResult(string actionId, string proposalId, string traceId,
            ActionExecutionStatus status, string failureCode,
            IReadOnlyDictionary<string, string> validatedBindings,
            int worldRevisionBefore, int worldRevisionAfter, string sourceMessage = "")
        {
            if (!ActionContractRules.IsIdentifier(actionId))
                throw new ArgumentException("Action ID must be a bounded identifier.", nameof(actionId));
            if (worldRevisionBefore < 0) throw new ArgumentOutOfRangeException(nameof(worldRevisionBefore));
            if (worldRevisionAfter < 0) throw new ArgumentOutOfRangeException(nameof(worldRevisionAfter));

            ActionId = actionId;
            ProposalId = proposalId ?? string.Empty;
            TraceId = traceId ?? string.Empty;
            Status = status;
            FailureCode = failureCode ?? string.Empty;
            Dictionary<string, string> bindings = new(StringComparer.Ordinal);
            if (validatedBindings != null)
            {
                foreach (KeyValuePair<string, string> binding in validatedBindings)
                    bindings.Add(binding.Key, binding.Value);
            }
            ValidatedBindings = new ReadOnlyDictionary<string, string>(bindings);
            WorldRevisionBefore = worldRevisionBefore;
            WorldRevisionAfter = worldRevisionAfter;
            SourceMessage = sourceMessage ?? string.Empty;
        }

        public string ActionId { get; }
        public string ProposalId { get; }
        public string TraceId { get; }
        public ActionExecutionStatus Status { get; }
        public string FailureCode { get; }
        public IReadOnlyDictionary<string, string> ValidatedBindings { get; }
        public int WorldRevisionBefore { get; }
        public int WorldRevisionAfter { get; }
        public string SourceMessage { get; }
    }

    public readonly struct ActionPresentationInfo
    {
        public ActionPresentationInfo(ActionExecutionStatus status, string code, string message)
        {
            Status = status;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public ActionExecutionStatus Status { get; }
        public string Code { get; }
        public string Message { get; }
    }

    public interface IActionPresentationPolicy
    {
        ActionPresentationInfo Present(ActionExecutionResult result);
    }

    public sealed class OpenDoorPresentationPolicy : IActionPresentationPolicy
    {
        public ActionPresentationInfo Present(ActionExecutionResult result)
        {
            EnsureAction(result, "open_door");
            if (result.Status == ActionExecutionStatus.Rejected &&
                string.Equals(result.FailureCode,
                    nameof(EmbodiedNPC.Planning.GoalValidationFailureCode.TargetAlreadySatisfied),
                    StringComparison.Ordinal))
            {
                return new ActionPresentationInfo(ActionExecutionStatus.Accepted,
                    result.FailureCode, "门已经打开了。");
            }

            string message = result.Status switch
            {
                ActionExecutionStatus.Accepted => "好的，我去开门。",
                ActionExecutionStatus.Recovering => "我正在重新确认门的位置和状态。",
                ActionExecutionStatus.Rejected when result.FailureCode ==
                    nameof(EmbodiedNPC.Planning.GoalValidationFailureCode.TargetLocked) =>
                    "门被锁住了，我不能打开它。",
                ActionExecutionStatus.Rejected => "这个目标现在不能作为可打开的门。",
                ActionExecutionStatus.Succeeded => "门已经打开了。",
                ActionExecutionStatus.Cancelled => "我已经停止开门。",
                ActionExecutionStatus.TimedOut => "开门操作超时了。",
                ActionExecutionStatus.Failed when result.FailureCode == "TARGET_UNREACHABLE" =>
                    "我无法走到门前。",
                ActionExecutionStatus.Failed when result.FailureCode == "DOOR_LOCKED" =>
                    "门被锁住了，我不能打开它。",
                ActionExecutionStatus.Failed => "这次没能打开门。",
                ActionExecutionStatus.RequiresConfirmation => "这扇门已失效，需要你重新确认目标。",
                ActionExecutionStatus.Clarification => string.IsNullOrWhiteSpace(result.SourceMessage)
                    ? "请明确要打开哪扇门。" : result.SourceMessage,
                _ => result.SourceMessage,
            };
            return new ActionPresentationInfo(result.Status, result.FailureCode, message);
        }

        private static void EnsureAction(ActionExecutionResult result, string actionId)
        {
            if (!string.Equals(result.ActionId, actionId, StringComparison.Ordinal))
                throw new ArgumentException($"PRESENTATION_ACTION_MISMATCH:{result.ActionId}", nameof(result));
        }
    }

    public sealed class BringObjectPresentationPolicy : IActionPresentationPolicy
    {
        public ActionPresentationInfo Present(ActionExecutionResult result)
        {
            EnsureAction(result, "bring_object");
            string message = result.Status switch
            {
                ActionExecutionStatus.Accepted => "我会尝试把它拿给你。",
                ActionExecutionStatus.Succeeded => "给你。",
                ActionExecutionStatus.Failed or ActionExecutionStatus.Cancelled or ActionExecutionStatus.TimedOut =>
                    "这次没能把它拿给你。",
                _ => result.SourceMessage,
            };
            return new ActionPresentationInfo(result.Status, result.FailureCode, message);
        }

        private static void EnsureAction(ActionExecutionResult result, string actionId)
        {
            if (!string.Equals(result.ActionId, actionId, StringComparison.Ordinal))
                throw new ArgumentException($"PRESENTATION_ACTION_MISMATCH:{result.ActionId}", nameof(result));
        }
    }

    public sealed class MoveToEntityPresentationPolicy : IActionPresentationPolicy
    {
        public ActionPresentationInfo Present(ActionExecutionResult result)
        {
            EnsureAction(result, "move_to_entity");
            string message = result.Status switch
            {
                ActionExecutionStatus.Accepted => "好的，我过去看看。",
                ActionExecutionStatus.Succeeded => "我已经到目标附近了。",
                ActionExecutionStatus.Failed or ActionExecutionStatus.Cancelled or ActionExecutionStatus.TimedOut =>
                    "我没能到达目标附近。",
                _ => result.SourceMessage,
            };
            return new ActionPresentationInfo(result.Status, result.FailureCode, message);
        }

        private static void EnsureAction(ActionExecutionResult result, string actionId)
        {
            if (!string.Equals(result.ActionId, actionId, StringComparison.Ordinal))
                throw new ArgumentException($"PRESENTATION_ACTION_MISMATCH:{result.ActionId}", nameof(result));
        }
    }
}
