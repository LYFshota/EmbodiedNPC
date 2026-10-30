"""Bounded observer/player-local execution evidence, never a new state store."""
import json
import re
from .drink_types import VALID_DRINK_TYPES

MAX_ROWS = 64
MAX_RESULTS = 4
MAX_AGE_SECONDS = 900
MAX_CHARS = 4800
MAX_GROUP_CHARS = 2000
_ID = re.compile(r'^[A-Za-z0-9_-]{1,160}$')
_ENTITY = re.compile(r'^[a-z][a-z0-9_]{0,119}$')
_STATUS = {'succeeded': 'Succeeded', 'failed': 'Failed', 'cancelled': 'Cancelled', 'timed_out': 'TimedOut'}


class RecentInteractionGroups(list):
    def __init__(self, groups=(), *, action_scope=None):
        super().__init__(groups)
        self.action_scope = action_scope


def _identities(event):
    return {p.entity_id: (p.identity_namespace, p.identity_type, p.persistent_id)
            for p in event.persistent_participants}


def _token(value, entity=False):
    return isinstance(value, str) and bool((_ENTITY if entity else _ID).fullmatch(value))


def read_action_results(ledger, connection, boundary, identities):
    """Only committed terminal records before this accepted input are eligible.

    A completed Skill is explicitly NOT a completed Goal. The Ledger has no
    authoritative ongoing-state carrier; neither an absent result nor a Skill
    can certify that a whole Goal is currently running or has not started.
    """
    observer, player = boundary.observer_id, boundary.actor_entity_id
    if (boundary.event_type != 'dialogue_input' or boundary.source_type != 'dialogue'
            or boundary.provenance != 'SYSTEM_CONFIRMED' or not _token(player, True)
            or boundary.target_entity_ids != (observer,) or set(identities) != {observer, player}
            or identities[observer][:2] != ('npc', 'Character')
            or identities[player][:2] != ('player', 'Player')
            or any(_identities(boundary).get(k) != identities[k] for k in (observer, player))):
        return [], None
    rows = connection.execute(
        "SELECT * FROM experience_events WHERE observer_id=? AND source_session_id=? "
        "AND clock_domain=? AND experience_seq<? AND source_type IN ('goal_outcome','skill_result') "
        "ORDER BY experience_seq DESC LIMIT ?",
        (observer, boundary.source_session_id, boundary.clock_domain, boundary.experience_seq, MAX_ROWS+1)).fetchall()
    if not rows:
        return [], None
    scope = dict(window_seconds=MAX_AGE_SECONDS, scan_limit=MAX_ROWS,
                 result_limit=MAX_RESULTS, read_limited=len(rows)>MAX_ROWS,
                 ongoing_execution_state='NOT_OBSERVED', historical_only=True)
    valid = []
    for row in rows[:MAX_ROWS]:
        try:
            event = ledger._row_to_event(row)
        except ValueError:
            continue
        payload = event.structured_payload
        suffix = event.event_type.removeprefix('goal_').removeprefix('skill_')
        prefix = 'goal_' if event.source_type == 'goal_outcome' else 'skill_'
        if (not event.event_type.startswith(prefix) or suffix not in _STATUS
                or payload.get('status') != _STATUS[suffix]
                or event.actor_entity_id != observer or event.provenance != 'EXECUTED_BY_SELF'
                or not _token(event.source_event_id) or not _token(event.trace_id)
                or player not in event.target_entity_ids
                or any(p.entity_id not in identities for p in event.persistent_participants)
                or any(_identities(event).get(k) != identities[k] for k in (observer, player))
                or event.observed_at > boundary.observed_at or event.occurred_at > boundary.occurred_at
                or boundary.observed_at-event.observed_at > MAX_AGE_SECONDS
                or boundary.occurred_at-event.occurred_at > MAX_AGE_SECONDS
                or event.occurred_at > event.observed_at
                or event.world_revision is None or boundary.world_revision is None
                or event.world_revision > boundary.world_revision
                or not isinstance(payload.get('failure_code',''), str)
                or (payload.get('failure_code') and not _token(payload['failure_code']))
                or (suffix == 'succeeded' and payload.get('failure_code'))):
            continue
        item = dict(kind='action_result', seq=event.experience_seq, actor_entity_id=observer,
                    player_entity_id=player, status=suffix.upper(),
                    result_level='goal' if prefix=='goal_' else 'skill',
                    target_entity_ids=list(event.target_entity_ids),
                    source_event_id=event.source_event_id, event_id=event.event_id,
                    trace_id=event.trace_id, world_revision=event.world_revision,
                    occurred_at=event.occurred_at, historical_only=True,
                    provenance='EXECUTED_BY_SELF', failure_code=payload.get('failure_code',''))
        if prefix == 'goal_':
            if (not _token(payload.get('goal_type'), True) or not _token(payload.get('proposal_id'))
                    or type(payload.get('proposal_revision')) is not int or payload['proposal_revision']<0):
                continue
            item.update(action_type=payload['goal_type'], proposal_id=payload['proposal_id'],
                        proposal_revision=payload['proposal_revision'])
            destination = payload.get('destination_entity_id')
            if destination:
                if not _token(destination, True) or destination not in event.target_entity_ids:
                    continue
                item['destination_entity_id'] = destination
            if payload['goal_type']=='prepare_drink' and suffix=='succeeded':
                if (not _token(payload.get('drink_receipt_id')) or destination!=player
                        or payload.get('drink_type') not in VALID_DRINK_TYPES
                        or event.world_revision<1):
                    continue
                item.update(drink_type=payload['drink_type'], receipt_id=payload['drink_receipt_id'])
        else:
            if not _token(payload.get('skill_id')) and not (
                    isinstance(payload.get('skill_id'),str) and len(payload['skill_id'])<=160
                    and all(_token(p) for p in payload['skill_id'].split(':'))):
                continue
            if not _token(payload.get('skill_type')):
                continue
            item.update(skill_id=payload['skill_id'], skill_type=payload['skill_type'],
                        goal_status='UNCONFIRMED')
        valid.append(item)
    goals, skills = {}, {}
    for item in valid:
        if item['result_level']=='goal':
            goals.setdefault(item['proposal_id'],[]).append(item)
        else:
            skills.setdefault(item['trace_id'],[]).append(item)
    groups = []
    for events in goals.values():
        latest_revision = max(x['proposal_revision'] for x in events)
        latest = sorted((x for x in events if x['proposal_revision']==latest_revision), key=lambda x:x['seq'])
        newest = latest[-1]
        signatures = {(x['status'],x['trace_id'],x.get('receipt_id'),x['action_type'],x.get('destination_entity_id'),
                       tuple(x['target_entity_ids'])) for x in latest}
        backwards = any(a['world_revision']>b['world_revision'] for a,b in zip(latest,latest[1:]))
        if len(signatures)>1 or backwards:
            # Never choose a convenient winner or keep success after a contradiction.
            newest = dict(newest, status='UNDETERMINED',
                          conflicting_statuses=sorted({x['status'] for x in latest}),
                          completion_confirmed=False)
            newest.pop('receipt_id',None)
        else:
            newest = dict(newest, completion_confirmed=newest['status']=='SUCCEEDED')
        newest['earlier_revision_present'] = any(x['proposal_revision']<latest_revision for x in events)
        groups.append([newest])
    goal_traces = {x['trace_id'] for events in goals.values() for x in events}
    for trace, events in skills.items():
        if trace not in goal_traces:
            # A substep is not an independent order and proves no final Goal success.
            groups.append([dict(max(events,key=lambda x:x['seq']), completion_confirmed=False)])
    groups.sort(key=lambda g:g[0]['seq'], reverse=True)
    retained, used = [], 0
    for group in groups:
        size = len(json.dumps(group,ensure_ascii=False,separators=(',',':')))
        if len(retained)>=MAX_RESULTS or size>MAX_GROUP_CHARS or used+size>MAX_CHARS:
            scope['read_limited'] = True
            continue
        retained.append(group);used += size
    scope['offered_results'] = len(retained)
    return sorted(retained,key=lambda g:g[0]['seq']), scope
