"""Bounded local runtime recovery and explicit player cancellation; no cognition or scheduler."""
from typing import Literal
from .contracts import TaskRuntimeSyncRequest
from .task_models import TaskModel, TaskIdentity, TaskScope, TaskQuery, TaskError, Identifier, PositiveInt
from .task_repository import digest


class TaskCancelControl(TaskModel):
    message_type: Literal['task_control']
    schema_version: Literal['1.1']
    operation: Literal['cancel_task']
    runtime_request: TaskRuntimeSyncRequest
    task_id: Identifier
    expected_version: PositiveInt
    attempt_id: Identifier | None = None


def runtime_scope(tasks, request):
    wire = request.recall_identity
    player = next((p for p in wire.current_subjects if p.kind == 'player' and
                   p.entity_id == wire.requester_entity_id), None)
    if (wire.observer.kind != 'npc' or wire.observer.entity_id != request.agent_entity_id or
            player is None or player.persistent_id != tasks.expected_player_uid or
            request.task_runtime.lifecycle_protocol=='1.0' and request.task_runtime.player_entity_id!=player.entity_id):
        raise TaskError('TASK_CURRENT_IDENTITY_REQUIRED')
    def identity(value):
        return TaskIdentity(identity_namespace=value.identity_namespace, identity_type=value.identity_type,
                            persistent_id=value.persistent_id)
    return TaskScope(owner=identity(wire.observer), player=identity(player), visit_id=request.task_runtime.visit_id)


def summary(task):
    execution = task.execution
    return dict(task_id=task.task_id, version=task.version, status=task.status,
        source=task.source_kind, goal_kind=task.goal.kind,
        attempt_id=execution.attempt_id if execution else None,
        phase=execution.phase if execution else None,
        cancel_requested=execution.cancel_requested if execution else False,
        diagnostic=(execution.reason if execution else task.schedule.reason),
        condition=task.schedule.condition.model_dump(mode='json') if task.schedule.condition else None)


def lifecycle_snapshot(tasks, request, scope):
    """Reconcile lost worlds/stale conditions before projecting current summaries.

    A lost reserved/started attempt becomes uncertain/blocked, never a new
    execution permit. This snapshot can write task state; it cannot restart
    actions or infer success from a missing client.
    """
    service, runtime = tasks.service, request.task_runtime
    active = []
    for offset in (0, 5):
        active.extend(service.query_tasks(scope, TaskQuery(limit=5, offset=offset)).tasks)
    for task in active:
        execution = task.execution
        if execution and execution.phase in ('reserved', 'started') and (
                execution.visit_id != runtime.visit_id or execution.request.scene_instance_id != runtime.scene_instance_id):
            service.mark_execution_unknown(scope, 'lost_world_' + execution.attempt_id, task.task_id,
                expected_version=task.version, attempt_id=execution.attempt_id, reason='WORLD_INSTANCE_LOST')
        elif (task.status == 'waiting' and task.goal.kind == 'clean_assigned_counter' and
              task.schedule.condition is not None and request.counter_observed and task.schedule.condition != request.counter_condition):
            service.cancel_task(scope, 'stale_' + task.task_id + '_' + str(task.version), task.task_id,
                expected_version=task.version, reason='COUNTER_CONDITION_STALE')
    refreshed = [service.get_task(scope, task.task_id) for task in active]
    active_ids = {t.task_id for t in refreshed}
    terminal = tuple(t for t in service.query_tasks(scope, TaskQuery(statuses=('completed','cancelled'), limit=3)).tasks if t.task_id not in active_ids)
    return [summary(task) for task in (*refreshed, *terminal)]


def cancel(tasks, control):
    request, service = control.runtime_request, tasks.service
    if request.task_runtime.lifecycle_protocol != '1.0':
        raise TaskError('TASK_LIFECYCLE_UNSUPPORTED')
    scope = runtime_scope(tasks, request)
    task = service.get_task(scope, control.task_id)
    if task.player != scope.player or task.source_kind != 'player_request':
        raise TaskError('TASK_PLAYER_CANCELLATION_FORBIDDEN')
    execution = task.execution
    if (execution.attempt_id if execution else None) != control.attempt_id:
        raise TaskError('TASK_ATTEMPT_MISMATCH')
    command_id = request.task_runtime.command_id
    binding = digest(control.model_dump(mode='json'))
    try:
        saved = service.get_command(scope, command_id, binding_hash=binding)
    except TaskError as error:
        if error.code != 'TASK_COMMAND_NOT_FOUND_OR_NOT_AUTHORIZED':
            raise
        saved = None
    if saved is None:
        if control.expected_version > task.version:
            raise TaskError('TASK_VERSION_CONFLICT')
        # While the same immutable attempt is running, start/result acknowledgements
        # may advance the version. Cancellation targets that attempt, never a new one.
        expected = task.version if task.status in ('completed','cancelled') or execution is not None and task.status in ('executing','blocked') and control.expected_version<=task.version else control.expected_version
        service.cancel_task(scope, command_id, task.task_id, expected_version=expected,
                            reason='PLAYER_CANCEL_REQUESTED', binding_hash=binding, terminal_ack=True)
    task = service.get_task(scope, task.task_id)
    outcome = ('too_late' if task.status == 'completed' else 'cancelled' if task.status == 'cancelled'
               else 'cancellation_requested')
    return dict(message_type='task_cancel_result', schema_version='1.1', request_id=request.request_id,
        command_id=command_id, agent_entity_id=request.agent_entity_id,
        task_runtime=request.task_runtime.model_dump(mode='json', exclude_none=True),
        owner=scope.owner.model_dump(mode='json'), player=scope.player.model_dump(mode='json'),
        accepted=True, outcome=outcome, task=summary(task), error_code=None)
