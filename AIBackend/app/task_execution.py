"""Local trusted Unity bridge. Decisions remain in cognition; state stays in TaskService.

The reservation is durable. A begin acknowledgement is delivered at most once:
loss/reconnect is deliberately not permission to start again. Results may retry.
"""
from threading import RLock
from typing import Literal
from uuid import uuid4
from .contracts import CognitiveRequest
from .cognitive_task_models import TaskExecutionTicket
from .task_models import TaskModel, TaskIdentity, ExecutionRequest, TaskError, TrustedTaskResult
from .task_repository import digest
from . import task_diagnostics as task_diagnostic


def proposal_id(request_id):
    prefix = '' if request_id.startswith('proposal_') else 'proposal_'
    return (prefix + ''.join(c if c.isalnum() or c in '_-' else '_' for c in request_id))[:120]


class TaskExecutionControl(TaskModel):
    message_type: Literal['task_control']
    schema_version: Literal['1.1']
    operation: Literal['begin_execution', 'not_started', 'settle_execution', 'mark_execution_unknown']
    original_request: CognitiveRequest
    ticket: TaskExecutionTicket
    result: TrustedTaskResult | None = None


class TaskExecutions:
    def __init__(self, tasks):
        self.tasks = tasks
        self.service = tasks.service
        self._lock = RLock()

    @staticmethod
    def ticket(request, reserved):
        participant=next(p for p in request.recall_identity.current_subjects if p.kind=='player' and
                         p.entity_id==request.context.relevant_player.player_entity_id)
        return TaskExecutionTicket(request_id=request.request_id,trace_id=request.trace_id,
            run_id=request.run.run_id,agent_entity_id=request.run.agent_entity_id,
            command_id=request.task_runtime.command_id,owner=reserved.owner,
            player=TaskIdentity(identity_namespace=participant.identity_namespace,identity_type=participant.identity_type,persistent_id=participant.persistent_id),
            visit_id=request.task_runtime.visit_id,scene_instance_id=request.task_runtime.scene_instance_id,
            task_id=reserved.task_id,task_version=reserved.version,attempt_id=reserved.execution.attempt_id,
            goal=reserved.goal,execution_request=reserved.execution.request)

    def reserve(self, request, context, decision):
        focus = getattr(decision, 'task_focus', None)
        if context is None or not context.automatic or focus is None or decision.target_daily_mode is not None:
            raise TaskError('TASK_EXECUTION_SOURCE_REQUIRED')
        if not context.execution_enabled:
            raise TaskError('TASK_EXECUTION_CLIENT_UNSUPPORTED')
        task_id = context.references.get(focus.task_ref)
        if task_id not in context.task_ids:
            raise TaskError('TASK_REF_NOT_AUTHORIZED')
        task = self.service.get_task(context.scope,task_id)
        proposal = decision.action_proposal
        args = dict(expected_version=focus.expected_version,scene_instance_id=request.task_runtime.scene_instance_id,
            proposal_id=proposal_id(request.request_id),trace_id=request.trace_id,
            actor_entity_id=request.run.agent_entity_id,
            world_revision=request.context.current_event.world_revision)
        if not request.context.current_event.has_world_revision:
            raise TaskError('TASK_WORLD_REVISION_REQUIRED')
        if task.goal.kind=='serve_drink':
            if proposal.action_id!='prepare_drink' or proposal.bindings!={'drink_type':task.goal.drink_type} or task.player!=context.scope.player:
                raise TaskError('TASK_ACTION_GOAL_MISMATCH')
            args['player_entity_id']=request.context.relevant_player.player_entity_id
        else:
            counter=request.context.counter_work
            if (proposal.action_id!='clean_counter' or counter is None or not counter.needs_cleaning or
                    proposal.bindings!={'target':counter.target_entity_id}):
                raise TaskError('TASK_ACTION_GOAL_MISMATCH')
            args.update(target_entity_id=counter.target_entity_id,condition_revision=counter.condition_revision)
        reserved=self.service.reserve_execution(context.scope,request.task_runtime.command_id,task_id,
            ExecutionRequest.model_validate(args),is_idle=context.is_idle,binding_hash=context.binding_hash).task
        task_diagnostic.emit('execution',request=request,task_state='reserved',operation='reserve',task_id=reserved.task_id,
            attempt_id=reserved.execution.attempt_id,task_status=reserved.status,phase=reserved.execution.phase,start_permitted=False)
        return self.ticket(request,reserved)

    def control(self, control):
        request,ticket=control.original_request,control.ticket
        context=self.tasks.prepare(request,recovery=True)
        if context is None or not context.automatic:
            raise TaskError('TASK_EXECUTION_SOURCE_REQUIRED')
        with self._lock:
            saved=self.service.get_command(context.scope,request.task_runtime.command_id,binding_hash=context.binding_hash)
            if saved.operation!='reserve' or self.ticket(request,saved.task)!=ticket:
                raise TaskError('TASK_EXECUTION_TICKET_INVALID')
            current=self.service.get_task(context.scope,ticket.task_id)
            if current.execution is None or current.execution.attempt_id!=ticket.attempt_id:
                raise TaskError('TASK_ATTEMPT_MISMATCH')
            operation=control.operation
            if operation=='begin_execution':
                if control.result is not None:
                    raise TaskError('TASK_CONTROL_RESULT_FORBIDDEN')
                # A new command ID per incoming begin, plus storage CAS, ensures
                # even concurrent requests cannot replay a historical start permit.
                command=self.service.start_execution(context.scope,'begin_'+uuid4().hex,ticket.task_id,
                    expected_version=ticket.task_version,attempt_id=ticket.attempt_id)
            elif operation=='mark_execution_unknown':
                if control.result is not None:
                    raise TaskError('TASK_CONTROL_RESULT_FORBIDDEN')
                if current.execution.result is not None:
                    return self.response(control,current,False)
                if current.execution.phase=='uncertain':
                    return self.response(control,current,False)
                command=self.service.mark_execution_unknown(context.scope,'unknown_'+ticket.attempt_id,
                    ticket.task_id,expected_version=current.version,attempt_id=ticket.attempt_id,reason='UNITY_EFFECT_UNKNOWN')
            else:
                result=control.result
                if result is None or result.attempt_id!=ticket.attempt_id:
                    raise TaskError('TASK_CONTROL_RESULT_REQUIRED')
                if operation=='not_started' and (result.status!='failed' or result.effect!='not_committed' or
                        result.failure_code!='TASK_NOT_STARTED'):
                    raise TaskError('TASK_NOT_STARTED_PROOF_INVALID')
                if operation=='settle_execution' and current.execution.phase=='reserved':
                    raise TaskError('TASK_EXECUTION_NOT_STARTED')
                if operation=='not_started' and current.execution.cancel_requested:
                    result=result.model_copy(update={'status':'cancelled'})
                command=self.service.settle_execution(context.scope,'result_'+digest([ticket.attempt_id,result.model_dump(mode='json')]),
                    ticket.task_id,result)
            return self.response(control,command.task,operation=='begin_execution')

    @staticmethod
    def response(control,task,permit):
        task_diagnostic.emit('execution' if permit else 'result',request=control.original_request,task_state='permitted' if permit else 'settled',
            task_id=task.task_id,attempt_id=control.ticket.attempt_id,task_status=task.status,phase=task.execution.phase,
            start_permitted=permit,accepted=True,operation='begin' if permit else 'not_started' if control.operation=='not_started' else 'settle' if control.operation=='settle_execution' else 'unknown')
        return dict(message_type='task_execution_result',schema_version='1.1',operation=control.operation,
            ticket=control.ticket.model_dump(mode='json'),accepted=True,start_permitted=permit,
            task_status=task.status,task_version=task.version,phase=task.execution.phase)
