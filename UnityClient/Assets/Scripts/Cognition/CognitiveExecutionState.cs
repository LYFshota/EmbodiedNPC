using System;
using EmbodiedNPC.Memory;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>One immutable-on-capture wire projection, not a task store or execution command.</summary>
    [Serializable]
    public sealed class CognitiveExecutionState
    {
        public string source = "unity_execution_snapshot";
        public string actor_entity_id, player_entity_id, source_session_id, scene_instance_id;
        public double captured_at;
        public int world_revision;
        public string status = "UNKNOWN";
        public string proposal_id, trace_id, action_id, stage, step, drink_type;
        public int proposal_revision;
        public string outcome_id, failure_code, receipt_id;
        public int outcome_world_revision;
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public string[] allowed_controls;

        public static CognitiveExecutionState Unknown(string actor, string player, string scene, int revision) => new()
        {
            actor_entity_id = actor, player_entity_id = player, scene_instance_id = scene,
            source_session_id = ClientSessionIdentity.Current, captured_at = Time.realtimeSinceStartupAsDouble,
            world_revision = Math.Max(0, revision),
        };

        public bool IsValid => source == "unity_execution_snapshot" && !string.IsNullOrWhiteSpace(actor_entity_id) &&
            !string.IsNullOrWhiteSpace(source_session_id) && !double.IsNaN(captured_at) && !double.IsInfinity(captured_at) &&
            captured_at >= 0 && world_revision >= 0 && proposal_revision >= 0 && outcome_world_revision >= 0 &&
            (allowed_controls == null || allowed_controls.Length == 0 ||
                status == "IN_PROGRESS" && allowed_controls.Length == 1 && allowed_controls[0] == "cancel_active_goal") &&
            (status == "UNKNOWN" ? proposal_id == null && trace_id == null && action_id == null && stage == null &&
                step == null && drink_type == null && outcome_id == null && failure_code == null && receipt_id == null &&
                proposal_revision == 0 && outcome_world_revision == 0 :
                (status == "IN_PROGRESS" || status == "SUCCEEDED" || status == "FAILED" || status == "CANCELLED") &&
                !string.IsNullOrWhiteSpace(player_entity_id) && !string.IsNullOrWhiteSpace(scene_instance_id) &&
                !string.IsNullOrWhiteSpace(proposal_id) && !string.IsNullOrWhiteSpace(trace_id) &&
                !string.IsNullOrWhiteSpace(action_id) && !string.IsNullOrWhiteSpace(stage) &&
                (status == "IN_PROGRESS" ? outcome_id == null && receipt_id == null && failure_code == null &&
                    outcome_world_revision == 0 && stage != "terminal" :
                    stage == "terminal" && !string.IsNullOrWhiteSpace(outcome_id) && outcome_world_revision <= world_revision &&
                    (status != "SUCCEEDED" || action_id != "prepare_drink" || !string.IsNullOrWhiteSpace(receipt_id))));

        // Time/revision/step may advance normally. A different lifecycle or owner invalidates a reply,
        // without reading its prose, retrying Cognition, or cancelling the independent world action.
        public bool SameExecution(CognitiveExecutionState other) => other != null &&
            source_session_id == other.source_session_id && scene_instance_id == other.scene_instance_id &&
            actor_entity_id == other.actor_entity_id && player_entity_id == other.player_entity_id &&
            status == other.status && proposal_id == other.proposal_id && proposal_revision == other.proposal_revision &&
            trace_id == other.trace_id && outcome_id == other.outcome_id;
    }
}
