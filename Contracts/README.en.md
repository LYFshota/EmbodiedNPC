[简体中文](README.md) | [English](README.en.md)

# EmbodiedNPC Wire Contracts

Unity and Python Backend exchange cognition, experience, and task messages over local HTTP and WebSocket. The contracts emphasize **request correlation, structured decisions, and trusted world outcomes**. The authoritative definitions are in [Backend contracts.py](../AIBackend/app/contracts.py) and Unity's `Cognition/` DTOs.

## Cognitive messages

CognitiveRequest/Response supports **1.0**, with persistent-task extension **1.1**. Responses preserve the originating `request_id`, `trace_id`, `run_id`, NPC identity and protocol version.

`CognitiveRequest` carries the current event, character, local perception, relevant player, behavior mode, capabilities and execution state. Persona fields are `character_id`, `display_name`, `role_summary`, `backstory`, `personality`, `speaking_style`, and `boundaries`.

Ordinary final `CognitiveDecision` variants:

| Kind | Purpose |
| --- | --- |
| `DIRECT_RESPONSE` | Reply to the player |
| `NO_ACTION` | End the current run without an action |
| `ACTION_PROPOSAL` | Propose an action ID and bound arguments |

Restricted system events also admit validated `DAILY_MODE_INTENT` and `AUTONOMOUS_ACTION_INTENT`. Backend validates action proposals; Unity independently checks live-world admission. `GoalOutcome` and matching receipts certify executed effects.

## WebSocket and HTTP

WebSocket `/ws` carries `cognitive_request`, `cognitive_cancel`, `experience_append_request`, `task_runtime_sync`, `task_control`, responses and heartbeats.

HTTP provides `/live`, protected `/health`, model configuration, runtime status, knowledge management, context policy and experience export. Protected local endpoints use Backend session credentials; a model API key is used separately for a model provider.

`ExperienceAppendRequest/Result 1.0` acknowledges a ledger append or deduplication. Episode closure and long-term consolidation run independently in the background.

## Backend-internal context reads

The Backend prefetches authorized World Knowledge before ordinary Step 0. The model can then return a final decision or request one internal batch:

| Internal kind | Purpose |
| --- | --- |
| `CONTEXT_READ` | Optional `knowledge_plan`, `memory_plan` and task-aware `task_plan` |
| `MEMORY_RECALL` | Compatible memory-only read form |
| `TASK_READ` | Compatible bounded task read form |

These steps run inside Backend, not as arbitrary Unity tools. At least one read plan must be nonempty. Queries specify topics while identity and permission scopes are resolved from trusted Backend context.

Example task-aware Step 0 read plan:

```json
{
  "decision_kind": "CONTEXT_READ",
  "knowledge_plan": {"semantic_query": "Flat white compared with latte"},
  "memory_plan": {
    "semantic_query": "The current player's explicit past drink preference",
    "subject_role": "requester",
    "memory_type": "SEMANTIC_FACT"
  },
  "task_plan": {"op": "query_tasks", "statuses": ["waiting"], "limit": 3}
}
```

Each read returns its own outcome and bounded evidence, fitted into the shared final context budget. Step 1 must produce a terminal decision. Ordinary cognition uses at most two model inferences.

## Tasks and outcome correlation

Task-aware 1.1 adds task runtime context, persistence receipts, consideration opportunities and execution tickets. Execution retains player, NPC, visit, task-version and attempt identities; cancellation, deferral and settlement use TaskService.

Action feedback retains original Trace and authoritative outcome correlation. A bounded recovery decision may be requested under the action-feedback contract; Unity checks current world conditions and original execution ownership. See [Persistent Tasks](../Docs/NPC_TASK_SYSTEM.en.md).

## Presentation metadata and management HTTP

Ordinary decisions may include `emotion`, `gesture`, `look_at` and `expression_icon` for Unity's dialogue and expression presentation. Presentation hints do not modify world execution or memory truth.

Knowledge management uses the [Management API](../AIBackend/app/knowledge_management_api.py). Protected `GET /v1/experience-export` reads current persistent records; Unity combines them with local settings data.

## Related documents

[Architecture](../Docs/ARCHITECTURE.en.md) · [Backend](../AIBackend/README.en.md) · [Memory](../Docs/MEMORY.en.md) · [Tasks](../Docs/NPC_TASK_SYSTEM.en.md)
