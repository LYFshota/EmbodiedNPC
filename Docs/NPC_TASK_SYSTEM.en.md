[简体中文](NPC_TASK_SYSTEM.md) | [English](NPC_TASK_SYSTEM.en.md)

# NPC Persistent Tasks

Persistent Tasks let NPCs accept limited future commitments and reconsider them on a later visit or when idle. The model interprets requests and proposes operations; trusted Backend and Unity code manage storage, conditions, and authoritative completion.

## Scope and storage

CafeDemoV2 currently supports bounded goals `serve_drink` and `clean_assigned_counter`, with `next_visit` and `when_idle` triggers.

| Store | Responsibility |
| --- | --- |
| `npc_tasks` | Stable NPC/player identity, goal, trigger, status, version and execution occupancy |
| `npc_task_commands` | Create/update/defer/cancel commands with idempotency metadata |

`TaskService` provides task operations and outcome settlement using SQLite transactions, version checks and command deduplication.

## Future commitments in dialogue

The model may propose a structured `create_task` when a real future commitment is made. After storage succeeds, Backend returns a trusted `task_receipt`; Unity ties the confirmation to the original request, NPC, player and session.

An optional `task_plan` can participate in the ordinary cognition `CONTEXT_READ` batch under the task-aware schema. It shares the run's one supplementary read opportunity with knowledge and memory.

## GameVisit and task consideration

A `GameVisit` represents a valid game-process visit, separate from the current scene instance. A `next_visit` task created during one visit becomes eligible on a later one.

Unity's `TaskRuntimeBridge` considers presence, idle opportunity, cooldown and Run/Goal occupancy before issuing `npc.task_consideration`. This is a system event rather than player dialogue. The model may keep waiting or propose a permitted action.

## Execution and settlement

Backend checks identity, task version and trigger eligibility and claims one attempt. Unity independently verifies current world state, capability and execution ticket before passing the admitted goal to `GoalPlanRunner`, Mini-GOAP and the existing action runners.

Completion uses correlated `GoalOutcome` and corresponding `DrinkReceipt` or `CounterCleaningReceipt`. Task states cover waiting, executing, completed, cancelled, blocked and uncertain results.

## Cancellation and recovery

Players may request cancellation through dialogue or the current-task menu. In-progress cancellation uses the existing Runner; committed world effects remain authoritative.

After a disconnect or scene interruption, task correlation and uncertain attempts are retained for idempotent reconciliation against original receipts. The current demo operates locally with stable player identity; broader crash recovery can be built on these contracts.

## Source links

[Task Models](../AIBackend/app/task_models.py) · [TaskService](../AIBackend/app/task_service.py) · [Cognition Adapter](../AIBackend/app/cognitive_tasks.py) · [Unity TaskRuntimeBridge](../UnityClient/Assets/Scripts/Cognition/TaskRuntimeBridge.cs) · [Wire Contracts](../Contracts/README.en.md)
