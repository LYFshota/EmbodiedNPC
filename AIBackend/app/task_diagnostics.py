"""Bounded technical projection into the existing Backend logger; never task authority."""
import hashlib
import json
import logging
import os
import sqlite3
from threading import Lock

logger = logging.getLogger('embodiednpc.backend')
_lock = Lock()
_count = _bytes = 0
_limited = False
MAX_RECORDS, MAX_BYTES = 512, 256 * 1024
STAGES = set('protocol decision read persistence visit gate sync opportunity consideration admission execution result error limit'.split())
STATES = set(('entered prepared validated attempted committed failed recovered snapshot_received requested sent send_failed rejected '
    'established not_ready admitted not_admitted runtime_reconfigured source_disabled transport_changed player_session '
    'merged_into_player_run syncing sync_timeout sync_expired no_eligible_tasks offered considering considered '
    'condition_expired route_failed response_rejected execution_handoff not_started not_executed cancelled '
    'reserved awaiting_begin permitted executing settlement_pending settled effect_unknown settlement_rejected '
    'gate_changed response_received succeeded completed blocked uncertain timed_out limit_reached sync_send_failed unknown').split())
OPERATIONS = set('none create update edit defer cancel reserve begin settle not_started unknown'.split())
DECISIONS = set('DIRECT_RESPONSE NO_ACTION ACTION_PROPOSAL TASK_READ MEMORY_RECALL DAILY_MODE_INTENT AUTONOMOUS_ACTION_INTENT'.split())
ERRORS = set(('SCHEMA_ERROR MODEL_ERROR TASK_REJECTED STORE_ERROR OPERATION_REJECTED TASK_NOT_SAVED '
    'TASK_CONSIDERATION_STALE TASK_RUNTIME_CONFLICT TASK_RUNTIME_SYNC_FAILED TASK_EXECUTION_REJECTED '
    'TASK_EXECUTION_STORE_UNAVAILABLE TASK_NOT_STARTED TASK_START_REJECTED TASK_EXECUTION_TICKET_INVALID '
    'TASK_SNAPSHOT_REJECTED TASK_RUNTIME_NOT_READY TASK_OUTCOME_MISMATCH COGNITIVE_REQUEST_FAILED COGNITIVE_ADMISSION_REJECTED INVALID_ACTION_PROPOSAL SEND_FAILED CLIENT_TIMEOUT unknown').split())
ID_FIELDS = set('request_id trace_id run_id event_id command_id visit_id scene_instance_id task_id attempt_id proposal_id agent_id'.split())
BOOL_FIELDS = set(('task_supported persistence_attempted receipt_committed runtime_valid bridge_initialized transport_connected player_visible '
    'counter_opportunity dialogue_visible run_busy goal_busy returning engagement_held routine_blocked gate_open '
    'offer_tasks is_idle opportunity_offered start_permitted accepted receipt_present').split())
ENUMS = dict(task_stage=STAGES, task_state=STATES, operation=OPERATIONS, decision_kind=DECISIONS,
    future_commitment={'true','false','na'}, error_code=ERRORS,
    task_status={'waiting','executing','blocked','completed','cancelled'},
    receipt_outcome={'created','updated','deduplicated','recovered','cancelled','cancellation_requested','failed'},
    phase={'reserved','started','succeeded','failed','cancelled','uncertain','timed_out'},
    source={'backend','unity'})

def correlation(value):
    if not isinstance(value,str) or not value or len(value)>512: return None
    return 'h_' + hashlib.sha256(value.encode('utf-8')).hexdigest()[:24]

def operation(intent):
    if intent is None: return 'none'
    return 'create' if intent.op=='create_task' else getattr(intent,'change','update')

def emit(stage, *, request=None, decision=None, **fields):
    """Best-effort observer: no exception, model call, new file, or unbounded buffer."""
    global _count, _bytes, _limited
    try:
        fields.update(task_stage=stage,source='backend')
        if request is not None:
            runtime=getattr(request,'task_runtime',None)
            run=getattr(request,'run',None)
            fields.update(request_id=request.request_id,agent_id=getattr(run,'agent_entity_id',getattr(request,'agent_entity_id',None)),
                trace_id=getattr(request,'trace_id',None),run_id=getattr(run,'run_id',None),
                visit_id=getattr(runtime,'visit_id',None),scene_instance_id=getattr(runtime,'scene_instance_id',None),
                command_id=getattr(runtime,'command_id',None))
        if decision is not None:
            commitment=getattr(decision,'future_commitment',None)
            fields.update(decision_kind=decision.decision_kind,future_commitment='true' if commitment is True else 'false' if commitment is False else 'na',
                operation=operation(getattr(decision,'task_operation',None)))
        result={'task_diagnostic_version':1,'process_id':os.getpid()}
        for key,value in fields.items():
            if key in ID_FIELDS:
                value=correlation(value)
                if value is not None:result[key]=value
            elif key in BOOL_FIELDS and type(value) is bool:result[key]=value
            elif key in ENUMS and isinstance(value,str) and value in ENUMS[key]:result[key]=value
            elif key in ('task_count','eligible_count','waiting_count') and type(value) is int:result[key]=max(0,min(11,value))
            elif key=='task_ids' and isinstance(value,(tuple,list)):
                result[key]=[v for v in (correlation(x) for x in value[:3]) if v]
        if result.get('task_stage') not in STAGES:return
        text=json.dumps(result,separators=(',',':'))
        with _lock:
            if _count>=MAX_RECORDS or _bytes+len(text.encode('utf-8'))>MAX_BYTES:
                if not _limited:
                    _limited=True
                    logger.info('TASK_DIAGNOSTIC %s',json.dumps(dict(task_diagnostic_version=1,source='backend',process_id=os.getpid(),task_stage='limit',task_state='limit_reached')))
                return
            _count+=1;_bytes+=len(text.encode('utf-8'))
        logger.info('TASK_DIAGNOSTIC %s',text)
    except Exception:
        pass  # Diagnostics must never replace a decision or transaction result.

def failure(request,error,stage='error'):
    name=type(error).__name__
    code='SCHEMA_ERROR' if name=='ValidationError' else 'MODEL_ERROR' if name=='ProviderError' else 'STORE_ERROR' if isinstance(error,sqlite3.Error) else 'TASK_REJECTED' if name=='TaskError' else 'OPERATION_REJECTED'
    specific=getattr(error,'code',None)
    emit(stage,request=request,task_state='failed',error_code=specific if specific in ERRORS else code)
