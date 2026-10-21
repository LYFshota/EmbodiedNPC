using EmbodiedNPC.Planning;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>
    /// Correlation-preserving adapter from one validated cognitive action proposal to Unity's
    /// existing authoritative goal runtime. It does not select actions, plan, or mutate world state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CognitiveActionHandoff : MonoBehaviour
    {
        [SerializeField] private GoalPlanRunner goalPlanRunner;

        public GoalPlanRunner GoalPlanRunner => goalPlanRunner;
        public string LastTraceId { get; private set; } = string.Empty;
        public string LastProposalId { get; private set; } = string.Empty;
        public int LastProposalRevision { get; private set; }
        public int LastWorldRevision { get; private set; }

        private void Awake() => ResolveReferences();

        internal bool TryPrevalidate(CognitiveRequest request, CognitiveResponse response, out string reason)
        {
            reason = "ACTION_HANDOFF_UNAVAILABLE";
            if (!isActiveAndEnabled || request == null || response == null || !response.IsValid ||
                goalPlanRunner == null) return false;
            int revision = request.context?.current_event?.has_world_revision == true
                ? request.context.current_event.world_revision : 0;
            if (response.validated_goal != null)
                response.validated_goal.RequestingPlayerId = request.context?.relevant_player?.player_entity_id;
            return goalPlanRunner.TryPrevalidateGoal(response.validated_goal, revision, out reason);
        }

        internal bool TryPrepareOwnership(CognitiveRequest request, CognitiveResponse response,
            out CognitiveActionOutcomeOwnership ownership)
        {
            ownership = null;
            if (request == null || response == null || response.validated_goal == null ||
                (response.decision?.decision_kind != CognitiveDecisionKinds.ActionProposal && response.decision?.decision_kind != CognitiveDecisionKinds.AutonomousActionIntent) ||
                goalPlanRunner == null || request.run == null) return false;
            ownership = new CognitiveActionOutcomeOwnership(request.run.run_id,
                GoalPlanRunner.NormalizeProposalId(response.request_id), 0, response.trace_id,
                request.run.agent_entity_id);
            return true;
        }

        // Preserve the original Run/proposal ownership through submission. Tasks
        // additionally consume their begin permit; model validation alone cannot
        // replace current admission or certify the later world Outcome.
        public bool TryHandoff(CognitiveRequest request, CognitiveResponse response)
        {
            if (request == null || response == null || response.validated_goal == null ||
                response.decision == null ||
                (response.decision.decision_kind != CognitiveDecisionKinds.ActionProposal && response.decision.decision_kind != CognitiveDecisionKinds.AutonomousActionIntent) ||
                goalPlanRunner == null)
                return false;

            int worldRevision = request.context?.current_event?.has_world_revision == true
                ? request.context.current_event.world_revision
                : 0;
            const int proposalRevision = 0;
            response.validated_goal.RequestingPlayerId = request.context?.relevant_player?.player_entity_id;
            var taskExecution=GetComponent<TaskExecutionBridge>();
            if(request.task_consideration!=null && (response.task_execution==null || taskExecution?.ConsumeStart(request,response)!=true)) return false;
            try
            {
            if (!goalPlanRunner.TrySubmitGoal(response.validated_goal, response.trace_id,
                    response.request_id, proposalRevision, worldRevision,
                    request.context?.relevant_player?.player_entity_id, request.reply_language))
            { if(request.task_consideration!=null) taskExecution.NotStarted();return false; }
            }
            catch(System.Exception)
            { if(request.task_consideration!=null) taskExecution.Unknown();throw; }

            LastTraceId = response.trace_id ?? string.Empty;
            LastProposalId = goalPlanRunner.ActiveProposalId;
            LastProposalRevision = proposalRevision;
            LastWorldRevision = worldRevision;
            return true;
        }

        private void ResolveReferences()
        {
            if (goalPlanRunner == null) goalPlanRunner = GetComponent<GoalPlanRunner>();
        }

        private void OnValidate() => ResolveReferences();
    }
}
