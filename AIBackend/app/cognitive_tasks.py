"""Trusted adapter for one bounded cognition run; no scheduler or world executor."""
from dataclasses import dataclass, field
import time
from uuid import uuid4
from .contracts import CognitiveDecision
from .cognitive_task_models import CognitiveTaskReceipt, CreateTaskIntent
from .task_models import TaskScope, TaskIdentity, TaskSource, TaskCreate, TaskQuery, TaskUpdate, TaskError, TaskCondition
from .task_repository import digest
from . import task_diagnostics as task_diagnostic


@dataclass
class CognitiveTaskContext:
    scope: TaskScope
    binding_hash: str
    references: dict = field(default_factory=dict)
    automatic: bool = False
    opportunity: object = None
    task_ids: tuple = ()
    is_idle: bool = False
    execution_enabled: bool = False
    counter: bool = False
    counter_condition: object = None


class CognitiveTasks:
    def __init__(self, service, *, expected_player_uid):
        self.service = service
        self.expected_player_uid = expected_player_uid
        self._connections = {}
        self._opportunities = {}
        from .task_execution import TaskExecutions
        self.executions = TaskExecutions(self)

    def bind(self, connection, request, *, recovery=False):
        context = self.prepare(request, recovery=recovery)
        if context is None:
            return
        if context.automatic and not recovery and context.opportunity['connection'] is not connection:
            raise TaskError('TASK_RUNTIME_CONFLICT')
        key = context.scope.player.key
        previous = self._connections.get(key)
        if previous is not None and previous[1] != request.task_runtime.client_session_id:
            raise TaskError("TASK_RUNTIME_CONFLICT")
        self._connections[key] = (connection, request.task_runtime.client_session_id)

    def disconnect(self, connection):
        self._opportunities = {key: value for key,value in self._opportunities.items() if value['connection'] is not connection}
        for key in tuple(self._connections):
            if self._connections[key][0] is connection:
                del self._connections[key]

    def synchronize(self, connection, request):
        """Reconcile runtime state and offer bounded work, without model/world execution.

        lifecycle_protocol 1.0 can persist lost-world and stale-condition
        transitions via lifecycle_snapshot; this is not a read-only query.
        Opportunity tokens belong to this connection and expire on disconnect.
        """
        wire, runtime = request.recall_identity, request.task_runtime
        player = next((p for p in wire.current_subjects if p.kind == 'player' and p.entity_id == wire.requester_entity_id),None)
        if (wire.observer.entity_id != request.agent_entity_id or wire.observer.kind != 'npc' or player is None
                or player.persistent_id != self.expected_player_uid or runtime.lifecycle_protocol=='1.0' and runtime.player_entity_id!=player.entity_id):
            raise TaskError('TASK_CURRENT_IDENTITY_REQUIRED')
        def identity(value):
            return TaskIdentity(identity_namespace=value.identity_namespace,identity_type=value.identity_type,persistent_id=value.persistent_id)
        scope=TaskScope(owner=identity(wire.observer),player=identity(player),visit_id=runtime.visit_id)
        previous=self._connections.get(scope.player.key)
        if previous is not None and previous[1] != runtime.client_session_id:
            raise TaskError('TASK_RUNTIME_CONFLICT')
        self._connections[scope.player.key]=(connection,runtime.client_session_id)
        summaries = None
        if runtime.lifecycle_protocol == '1.0':
            from .task_lifecycle import lifecycle_snapshot
            summaries = lifecycle_snapshot(self, request, scope)
        # Filter before the three-item bound; absent-player commissions must not
        # starve an NPC's own work or become wake candidates.
        offered_scope=scope if request.player_visible else scope.model_copy(update={'player':None})
        tasks=self.service.query_considerable(offered_scope,is_idle=request.is_idle) if request.offer_tasks else ()
        key=(scope.owner.key,scope.player.key)
        if request.offer_tasks: self._opportunities.pop(key,None)
        token='task_opportunity_'+uuid4().hex if tasks else None
        if token is not None:
            if len(self._opportunities)>=128:
                self._opportunities.pop(next(iter(self._opportunities)))
            self._opportunities[key]=dict(connection=connection,token=token,runtime=runtime,
                task_ids=tuple(t.task_id for t in tasks),is_idle=request.is_idle,player_visible=request.player_visible,
                expires=time.monotonic()+15.0)
        reply = dict(message_type='task_runtime_snapshot',schema_version='1.1',request_id=request.request_id,
            agent_entity_id=request.agent_entity_id,task_runtime=runtime.model_dump(mode='json',exclude_none=True),
            owner=scope.owner.model_dump(mode='json'),player=scope.player.model_dump(mode='json'),
            opportunity_token=token,task_ids=[t.task_id for t in tasks])
        if summaries is not None: reply['tasks']=summaries
        if request.offer_tasks:
            task_diagnostic.emit('sync',request=request,task_state='snapshot_received',runtime_valid=True,
                player_visible=request.player_visible,offer_tasks=request.offer_tasks,eligible_count=len(tasks),
                task_ids=[t.task_id for t in tasks],opportunity_offered=token is not None)
        return reply

    def prepare(self, request, *, recovery=False):
        event, wire, runtime = request.context.current_event, request.recall_identity, request.task_runtime
        automatic=event is not None and event.event_type == 'npc.task_consideration'
        counter=event is not None and event.event_type == 'counter.needs_cleaning'
        if request.schema_version != "1.1" or runtime is None or event is None or event.event_type not in ("player.utterance",'npc.task_consideration','counter.needs_cleaning'):
            return None
        if counter:
            from .world_action_eligibility import is_counter_cleaning_consideration
            if runtime.lifecycle_protocol != '1.0' or not is_counter_cleaning_consideration(request):
                raise TaskError('TASK_SELF_SOURCE_INVALID')
        player = request.context.relevant_player
        participant = next((p for p in wire.current_subjects if p.kind == "player" and
                            (p.entity_id==runtime.player_entity_id if counter else player is not None and p.entity_id == player.player_entity_id)), None) if wire else None
        if (wire is None or participant is None or not counter and event.source_entity_id != (request.run.agent_entity_id if automatic else participant.entity_id)
                or event.target_entity_id != request.run.agent_entity_id
                or wire.observer.entity_id != request.run.agent_entity_id
                or participant.persistent_id != self.expected_player_uid):
            raise TaskError("TASK_CURRENT_IDENTITY_REQUIRED")
        if runtime.lifecycle_protocol=='1.0' and runtime.player_entity_id!=participant.entity_id:
            raise TaskError('TASK_CURRENT_IDENTITY_REQUIRED')
        def identity(value):
            return TaskIdentity(identity_namespace=value.identity_namespace, identity_type=value.identity_type,
                                persistent_id=value.persistent_id)
        actions = request.context.capabilities.available_actions if request.context.capabilities else ()
        kinds = tuple(kind for action, kind in (("prepare_drink", "serve_drink"), ("clean_counter", "clean_assigned_counter"))
                      if request.run.agent_entity_id == "npc_cafe_01" and any(a.action_id == action for a in actions))
        scope = TaskScope(owner=identity(wire.observer), player=identity(participant), visit_id=runtime.visit_id,
                          allowed_goal_kinds=kinds)
        binding_data={"request": request.model_dump(mode="json"), "identity": wire.model_dump(mode="json"),
                      "runtime": runtime.model_dump(mode="json", exclude_none=True)}
        if automatic: binding_data['consideration']=request.task_consideration.model_dump(mode='json',exclude_none=True)
        binding = digest(binding_data)
        context=CognitiveTaskContext(scope,binding,automatic=automatic,counter=counter,
            execution_enabled=automatic and request.task_consideration.execution_protocol=='1.0',
            counter_condition=self.condition(request) if request.context.counter_work is not None and request.context.counter_work.needs_cleaning else None)
        if automatic and not recovery:
            chance=self._opportunities.get((scope.owner.key,scope.player.key))
            visible=any(p.entity_id==participant.entity_id and p.visibility=='current_visible' for p in request.context.perception.entities)
            payload={p.key:p.value for p in event.payload}
            if (chance is None or chance['token'] != request.task_consideration.opportunity_token
                    or time.monotonic()>=chance['expires'] or chance['runtime'] != runtime or
                       not visible and (runtime.lifecycle_protocol!='1.0' or chance['player_visible'] or
                         any(self.service.get_task(scope,tid).player is not None or
                             self.service.get_task(scope,tid).schedule.condition != self.condition(request) for tid in chance['task_ids']))
                    or len(event.payload)!=1 or payload!={'player_entity_id':participant.entity_id} or event.source_observation_id is not None
                    or request.task_consideration.event_id != event.event_id or request.trigger.source_event_id != event.event_id):
                raise TaskError('TASK_CONSIDERATION_STALE')
            context.opportunity=chance
            context.task_ids=chance['task_ids']
            context.is_idle=chance['is_idle']
        return context

    @staticmethod
    def condition(request):
        counter = request.context.counter_work
        if counter is None or not counter.needs_cleaning:
            raise TaskError('TASK_COUNTER_CONDITION_REQUIRED')
        return TaskCondition(scene_instance_id=request.task_runtime.scene_instance_id,
            target_entity_id=counter.target_entity_id,condition_revision=counter.condition_revision)

    def has_counter_task(self, request, context):
        condition=self.condition(request)
        return any(t.goal.kind=='clean_assigned_counter' and t.schedule.condition==condition
            for offset in (0,5) for t in self.service.query_tasks(context.scope,TaskQuery(limit=5,offset=offset)).tasks)

    def begin_consideration(self, request, context):
        key=(context.scope.owner.key,context.scope.player.key)
        if self._opportunities.pop(key,None) is not context.opportunity:
            raise TaskError('TASK_CONSIDERATION_STALE')
        tasks=self.service.record_consideration(context.scope,context.task_ids,request.context.current_event.event_id,is_idle=context.is_idle)
        if not tasks:
            raise TaskError('TASK_CONSIDERATION_SUPPRESSED')
        context.task_ids=tuple(t.task_id for t in tasks)
        task_diagnostic.emit('consideration',request=request,task_state='entered',eligible_count=len(tasks),task_ids=list(context.task_ids))

    def projection(self, context, tasks):
        visible = []
        for task in tasks:
            ref = "t_" + digest([context.binding_hash, task.task_id])[:24]
            context.references[ref] = task.task_id
            visible.append(dict(task_ref=ref, version=task.version, goal=task.goal.model_dump(mode="json"),
                                source="self" if task.player is None else "requester", status=task.status,
                                trigger_kind=task.trigger_kind, trigger_ready=self.service.is_eligible(context.scope,task.task_id,is_idle=context.is_idle),
                                world_execution_available=context.execution_enabled and self.service.is_eligible(context.scope,task.task_id,is_idle=context.is_idle) and
                                    (task.goal.kind!='clean_assigned_counter' or context.counter_condition is not None and
                                     (task.schedule.condition is None or task.schedule.condition==context.counter_condition))))
        return visible

    def read(self, context, query=None):
        if context.automatic:
            tasks=tuple(self.service.get_task(context.scope,task_id) for task_id in context.task_ids)
            if query is not None and query.task_ref is not None:
                task_id=context.references.get(query.task_ref)
                if task_id not in context.task_ids: raise TaskError('TASK_REF_NOT_AUTHORIZED')
                tasks=tuple(t for t in tasks if t.task_id==task_id)
            return dict(status='OK',scope='current_opportunity',tasks=self.projection(context,tasks),has_more=False)
        if context.counter and (query is None or query.task_ref is None) and (query is None or set(query.statuses)<= {'waiting','executing','blocked'}):
            # At most eight open tasks, of which at most three are self plans.
            # Select those before applying the model-visible page limit.
            rows=tuple(t for offset in (0,5) for t in self.service.query_tasks(context.scope,TaskQuery(
                statuses=query.statuses if query else ('waiting','executing','blocked'),limit=5,offset=offset)).tasks if t.player is None)
            start,limit=(query.offset,query.limit) if query else (0,3)
            tasks,more=rows[start:start+limit],len(rows)>start+limit
        elif query is not None and query.task_ref is not None:
            task_id = context.references.get(query.task_ref)
            if task_id is None:
                raise TaskError("TASK_REF_NOT_AUTHORIZED")
            tasks = (self.service.get_task(context.scope, task_id),)
            more = False
        else:
            page = self.service.query_tasks(context.scope, TaskQuery(
                statuses=query.statuses if query else ("waiting", "executing", "blocked"),
                limit=query.limit if query else 3, offset=query.offset if query else 0))
            tasks, more = page.tasks, page.has_more
        if context.counter: tasks=tuple(t for t in tasks if t.player is None)
        return dict(status="OK", tasks=self.projection(context, tasks), has_more=more)

    def recover(self, request, context):
        try:
            command = self.service.get_command(context.scope, request.task_runtime.command_id,
                                               binding_hash=context.binding_hash)
        except TaskError as error:
            if error.code == "TASK_COMMAND_NOT_FOUND_OR_NOT_AUTHORIZED":
                return None
            raise
        task = self.service.get_task(context.scope, command.task.task_id)
        if command.operation == 'reserve':
            # Historical recovery must never replay a world-action permit.
            return CognitiveDecision(decision_kind='NO_ACTION'), None
        receipt = self.receipt(request, context, command.operation, task, recovered=True)
        if context.counter:
            return CognitiveDecision(decision_kind='NO_ACTION'), receipt
        if request.reply_language == "en-US":
            text = "The earlier task was saved. Its current status is: " + task.status + "."
        else:
            states = dict(waiting="等待中", executing="执行中", blocked="待核对", completed="已完成", cancelled="已取消")
            text = "此前的任务已保存，目前状态是" + states[task.status] + "。"
        return CognitiveDecision(decision_kind="DIRECT_RESPONSE", utterance=text), receipt

    def mutate(self, request, context, intent):
        command_id = request.task_runtime.command_id
        goal = getattr(intent, "goal", None)
        if goal is not None and goal.kind == "serve_drink":
            actions = request.context.capabilities.available_actions if request.context.capabilities else ()
            capability = next((a for a in actions if a.action_id == "prepare_drink"), None)
            binding = next((b for b in capability.bindings if b.name == "drink_type" and b.kind == "enum"), None) if capability else None
            if binding is None or goal.drink_type not in binding.enum_values:
                raise TaskError("TASK_GOAL_NOT_AUTHORIZED")
        if isinstance(intent, CreateTaskIntent):
            if (context.automatic or context.counter) and intent.source == 'requester':
                raise TaskError('TASK_NEW_COMMISSION_SOURCE_REQUIRED')
            # The host derives identities/source event; self plans only support cleaning.
            task_diagnostic.emit('persistence',request=request,task_state='attempted',operation='create',persistence_attempted=True)
            command = self.service.create_task(context.scope, command_id, TaskCreate(
                source_kind="player_request" if intent.source == "requester" else "self_initiated",
                source=TaskSource(event_id=request.context.current_event.event_id, request_id=request.request_id),
                goal=intent.goal, trigger_kind=intent.trigger_kind,
                condition=self.condition(request) if intent.goal.kind=='clean_assigned_counter' else None), binding_hash=context.binding_hash)
        else:
            task_id = context.references.get(intent.task_ref)
            if task_id is None:
                raise TaskError("TASK_REF_NOT_AUTHORIZED")
            if intent.change == "cancel":
                task_diagnostic.emit('persistence',request=request,task_state='attempted',operation='cancel',task_id=task_id,persistence_attempted=True)
                command = self.service.cancel_task(context.scope, command_id, task_id,
                    expected_version=intent.expected_version, reason=intent.reason, binding_hash=context.binding_hash)
            else:
                task_diagnostic.emit('persistence',request=request,task_state='attempted',operation=intent.change,task_id=task_id,persistence_attempted=True)
                command = self.service.update_task(context.scope, command_id, task_id, TaskUpdate(
                    expected_version=intent.expected_version, reason=intent.reason,
                    goal=intent.goal, trigger_kind=intent.trigger_kind), binding_hash=context.binding_hash)
        return self.receipt(request, context, command.operation, command.task, outcome=command.outcome)

    @staticmethod
    def receipt(request, context, operation, task=None, *, recovered=False, outcome=None):
        task_diagnostic.emit('persistence',request=request,task_state='recovered' if recovered else 'committed' if task else 'failed',
            operation=operation,receipt_committed=task is not None,task_id=task.task_id if task else None,
            task_status=task.status if task else None,error_code=None if task else 'TASK_NOT_SAVED',
            receipt_outcome='recovered' if recovered else outcome if task else 'failed')
        return CognitiveTaskReceipt(request_id=request.request_id, trace_id=request.trace_id,
            run_id=request.run.run_id, agent_entity_id=request.run.agent_entity_id,
            command_id=request.task_runtime.command_id, owner=context.scope.owner, player=context.scope.player,
            visit_id=request.task_runtime.visit_id, scene_instance_id=request.task_runtime.scene_instance_id,
            reply_language=request.reply_language, operation=operation, committed=task is not None, recovered=recovered,
            outcome="recovered" if recovered else outcome if task is not None else "failed",
            task_id=task.task_id if task else None, task_version=task.version if task else None,
            task_status=task.status if task else None, error_code=None if task else "TASK_NOT_SAVED")
