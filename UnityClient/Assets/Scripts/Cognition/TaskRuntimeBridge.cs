using System;
using System.Linq;
using EmbodiedNPC.Memory;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    // One process session binds one stable Player identity. Reconnects keep it;
    // a new Cafe component has a new scene_instance_id, not a new Player Visit.
    public sealed class GameVisit
    {
        public string VisitId { get; private set; }
        private string playerKey;
        public bool Matches(string clientSession, string stablePlayerKey) => VisitId != null && VisitId == clientSession && playerKey == stablePlayerKey;
        public bool TryStart(string clientSession, string stablePlayerKey)
        {
            if (!TaskRuntimeContextDto.IsToken(clientSession) || string.IsNullOrWhiteSpace(stablePlayerKey)) return false;
            if (VisitId != null) return Matches(clientSession, stablePlayerKey);
            VisitId = clientSession; playerKey = stablePlayerKey; return true;
        }
    }
    [Serializable]
    public sealed class TaskRuntimeContextDto
    {
        public string command_id, client_session_id, visit_id, scene_instance_id;
        public string lifecycle_protocol;
        public string player_entity_id;
        public bool IsValid => IsToken(command_id) && IsToken(client_session_id) &&
            visit_id == client_session_id && IsToken(scene_instance_id) && (lifecycle_protocol==null || lifecycle_protocol=="1.0" && IsToken(player_entity_id));
        internal static bool IsToken(string value) => !string.IsNullOrEmpty(value) && value.Length <= 160 &&
            value.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-');
    }

    [Serializable]
    public sealed class TaskIdentityDto
    {
        public string identity_namespace, identity_type, persistent_id;
        public bool Matches(CognitiveRecallSubjectIdentityDto value) => value != null &&
            !string.IsNullOrWhiteSpace(persistent_id) && identity_namespace == value.identity_namespace &&
            identity_type == value.identity_type && persistent_id == value.persistent_id;
    }

    [Serializable]
    public sealed class CognitiveTaskReceiptDto
    {
        public string schema_version, request_id, trace_id, run_id, agent_entity_id, command_id;
        public TaskIdentityDto owner, player;
        public string visit_id, scene_instance_id, reply_language, operation, outcome;
        public bool committed, recovered;
        public string task_id, task_status, error_code;
        public int? task_version;

        public bool IsValid => schema_version == "1.1" && TaskRuntimeContextDto.IsToken(command_id) &&
            owner != null && player != null && (operation == "create" || operation == "update" || operation == "cancel") &&
            (recovered ? committed && outcome == "recovered" : committed ?
                operation == "create" && (outcome == "created" || outcome == "deduplicated") ||
                operation == "update" && outcome == "updated" || operation == "cancel" && (outcome == "cancelled" || outcome == "cancellation_requested") : outcome == "failed") &&
            (committed ? TaskRuntimeContextDto.IsToken(task_id) && task_version > 0 && error_code == null &&
                (task_status == "waiting" || task_status == "executing" || task_status == "blocked" ||
                 task_status == "completed" || task_status == "cancelled") :
                task_id == null && task_version == null && task_status == null && error_code == "TASK_NOT_SAVED");
    }

    public static class TaskReceiptValidator
    {
        public static bool Matches(CognitiveResponse response, CognitiveRequest request)
        {
            if (request == null || response == null || response.schema_version != request.schema_version) return false;
            var receipt = response.task_receipt;
            if (receipt == null) return true;
            var runtime = request.task_runtime;
            var identity = request.recall_identity;
            bool counter = request.context.current_event?.event_type=="counter.needs_cleaning" && runtime?.lifecycle_protocol=="1.0";
            var player = identity?.current_subjects?.FirstOrDefault(p => p.kind == "player" &&
                p.entity_id == (counter?runtime.player_entity_id:request.context.relevant_player?.player_entity_id));
            return receipt.IsValid && runtime?.IsValid == true && identity != null &&
                (counter || request.context.current_event?.event_type == "player.utterance" || request.context.current_event?.event_type == "npc.task_consideration" && request.task_consideration!=null) &&
                (counter ? response.decision.decision_kind==CognitiveDecisionKinds.NoAction : response.decision.decision_kind == CognitiveDecisionKinds.DirectResponse) &&
                response.decision.target_daily_mode == null && response.validated_goal == null &&
                receipt.request_id == request.request_id && receipt.trace_id == request.trace_id &&
                receipt.run_id == request.run.run_id && receipt.agent_entity_id == request.run.agent_entity_id &&
                receipt.command_id == runtime.command_id && receipt.visit_id == runtime.visit_id &&
                receipt.scene_instance_id == runtime.scene_instance_id && receipt.reply_language == request.reply_language &&
                receipt.owner.Matches(identity.observer) && receipt.player.Matches(player);
        }
    }

    /// <summary>One process Visit, one token per admitted Cafe scene. No wakeup or execution.</summary>
    public sealed partial class TaskRuntimeBridge : MonoBehaviour
    {
        private static GameVisit processVisit = new();
        private EntityRegistry registry;
        private WorldEntity player;
        private bool admitted;
        private readonly string sceneInstanceId = "scene_" + Guid.NewGuid().ToString("N");
        private string diagnosticAdmission;
        public bool DiagnosticInitialized=>admitted;
        public string DiagnosticVisitId=>processVisit.VisitId;
        public string DiagnosticSceneInstanceId=>sceneInstanceId;
        private bool RecordAdmission(bool accepted)
        {
            try {string signature=accepted+":"+processVisit.VisitId;
                if(signature!=diagnosticAdmission) {diagnosticAdmission=signature;
                    EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("visit",new Newtonsoft.Json.Linq.JObject {
                        ["task_state"]=accepted?"established":"not_ready",["bridge_initialized"]=admitted,["runtime_valid"]=accepted,
                        ["visit_id"]=processVisit.VisitId,["scene_instance_id"]=sceneInstanceId });}}
            catch(Exception) { }
            return accepted;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetProcess() { processVisit = new GameVisit(); ResetRetained(); }

        public bool Admit(EntityRegistry entities, WorldEntity currentPlayer)
        {
            if (entities == null || !entities.IsReady || currentPlayer == null ||
                !entities.TryGetEntity(currentPlayer.EntityId, out var registered) || registered != currentPlayer ||
                currentPlayer.AgentRole != AgentRole.Player || !currentPlayer.TryGetPersistentIdentityKey(out var key)) return RecordAdmission(false);
            string stable = Newtonsoft.Json.JsonConvert.SerializeObject(new[] { key.Namespace, key.IdentityType, key.PersistentId });
            string session = ClientSessionIdentity.Current;
            if (!processVisit.TryStart(session, stable)) return RecordAdmission(false);
            registry = entities; player = currentPlayer; admitted = true;
            return RecordAdmission(true);
        }

        public bool TryCapture(CognitiveRecallIdentityDto identity, out TaskRuntimeContextDto context)
        {
            context = null;
            if (!admitted || registry == null || !registry.IsReady || player == null ||
                !registry.TryGetEntity(player.EntityId, out var current) || current != player ||
                !player.TryGetPersistentIdentityKey(out var key)) return false;
            if (!processVisit.Matches(ClientSessionIdentity.Current, Newtonsoft.Json.JsonConvert.SerializeObject(
                    new[] { key.Namespace, key.IdentityType, key.PersistentId }))) return false;
            var participant = identity?.current_subjects?.FirstOrDefault(p => p.kind == "player" && p.entity_id == player.EntityId);
            if (identity?.observer == null || string.IsNullOrEmpty(identity.observer.persistent_id) || participant == null ||
                participant.identity_namespace != key.Namespace || participant.identity_type != key.IdentityType ||
                participant.persistent_id != key.PersistentId) return false;
            context = new TaskRuntimeContextDto { command_id = "task_command_" + Guid.NewGuid().ToString("N"),
                client_session_id = processVisit.VisitId, visit_id = processVisit.VisitId, scene_instance_id = sceneInstanceId,
                lifecycle_protocol="1.0",player_entity_id=player.EntityId };
            return context.IsValid;
        }

        public bool IsCurrent(TaskRuntimeContextDto context) => admitted && context?.IsValid == true &&
            (context.lifecycle_protocol==null || context.player_entity_id==player?.EntityId) &&
            context.visit_id == processVisit.VisitId && context.scene_instance_id == sceneInstanceId && registry != null && registry.IsReady &&
            player != null && registry.TryGetEntity(player.EntityId, out var current) && current == player &&
            player.TryGetPersistentIdentityKey(out var key) && processVisit.Matches(ClientSessionIdentity.Current,
                Newtonsoft.Json.JsonConvert.SerializeObject(new[] { key.Namespace, key.IdentityType, key.PersistentId }));
    }
}
