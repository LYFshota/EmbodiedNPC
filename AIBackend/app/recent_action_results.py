"""Bounded observer/player-local execution evidence, never a new state store."""
import json
import re
from contextlib import closing
from .recent_dialogue_context import RecentDialogueTimeline
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
    if not _accepted_input(boundary, identities):
        return [], None
    observer = boundary.observer_id
    rows = connection.execute(
        "SELECT * FROM experience_events WHERE observer_id=? AND source_session_id=? "
        "AND clock_domain=? AND experience_seq<? AND source_type IN ('goal_outcome','skill_result') "
        "ORDER BY experience_seq DESC LIMIT ?",
        (observer, boundary.source_session_id, boundary.clock_domain, boundary.experience_seq, MAX_ROWS+1)).fetchall()
    return _project_results(ledger, rows, boundary, identities)


def _accepted_input(boundary, identities):
    observer, player = boundary.observer_id, boundary.actor_entity_id
    if (boundary.event_type != 'dialogue_input' or boundary.source_type != 'dialogue'
            or boundary.provenance != 'SYSTEM_CONFIRMED' or not _token(player, True)
            or boundary.target_entity_ids != (observer,) or set(identities) != {observer, player}
            or identities[observer][:2] != ('npc', 'Character')
            or identities[player][:2] != ('player', 'Player')
            or any(_identities(boundary).get(k) != identities[k] for k in (observer, player))):
        return False
    return True


def _project_results(ledger, rows, boundary, identities, *, age_limited=True):
    """Shared terminal/receipt/conflict checks; only a verified dialogue join bypasses age."""
    observer, player = boundary.observer_id, boundary.actor_entity_id
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
                or (age_limited and boundary.observed_at-event.observed_at > MAX_AGE_SECONDS)
                or (age_limited and boundary.occurred_at-event.occurred_at > MAX_AGE_SECONDS)
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


def read_dialogue_linked_results(ledger, timeline, observer, conversation, request_id, event_id, identities):
    """Read terminal evidence for retained accepted inputs, never infer an order from speech.

    Reuse the original source session/clock and persistent pair. Cross-source-session
    history stays history-only speech; this bounded projection does not bridge visits.
    Each selected input has at most MAX_ROWS terminal candidates. Ambiguity or an
    incomplete revision/conflict scan cannot certify a convenient older success.
    """
    if not isinstance(timeline, RecentDialogueTimeline) or not timeline:
        return ()
    linked, seen = [], set()
    with closing(ledger._connection_factory()) as connection:
        rows = connection.execute(
            "SELECT * FROM experience_events WHERE observer_id=? AND conversation_id=? "
            "AND event_type='dialogue_input' AND request_id=? AND source_event_id=? LIMIT 2",
            (observer, conversation, request_id, event_id)).fetchall()
        if len(rows) != 1:
            return ()
        boundary = ledger._row_to_event(rows[0])
        if not _accepted_input(boundary, identities):
            return ()
        for message in timeline:
            source = timeline.sources.get(message['seq'], {})
            input_seq = source.get('input_seq')
            if type(input_seq) is not int or input_seq in seen or input_seq >= boundary.experience_seq:
                continue
            seen.add(input_seq)
            rows = connection.execute(
                "SELECT * FROM experience_events WHERE observer_id=? AND experience_seq=?",
                (observer, input_seq)).fetchall()
            if len(rows) != 1:
                continue
            order = ledger._row_to_event(rows[0])
            if (not _accepted_input(order, identities) or _identities(order) != identities
                    or not _token(order.request_id) or not _token(order.trace_id) or not order.source_event_id
                    or order.source_session_id != boundary.source_session_id
                    or order.clock_domain != boundary.clock_domain):
                continue
            matches = connection.execute(
                "SELECT experience_seq FROM experience_events WHERE observer_id=? "
                "AND source_session_id=? AND clock_domain=? AND trace_id=? "
                "AND event_type='dialogue_input' AND experience_seq<? LIMIT 2",
                (observer, order.source_session_id, order.clock_domain, order.trace_id,
                 boundary.experience_seq)).fetchall()
            if len(matches) != 1:
                continue  # A normalized/truncated proposal alone is not an order identity.
            proposal = (order.request_id if order.request_id.startswith('proposal_')
                        else 'proposal_' + order.request_id)[:120]
            rows = connection.execute(
                "SELECT * FROM experience_events WHERE observer_id=? AND source_session_id=? "
                "AND clock_domain=? AND trace_id=? AND source_type='goal_outcome' "
                "AND json_extract(payload_json,'$.structured_payload.proposal_id')=? "
                "AND experience_seq>? AND experience_seq<? ORDER BY experience_seq DESC LIMIT ?",
                (observer, order.source_session_id, order.clock_domain, order.trace_id,
                 proposal, input_seq, boundary.experience_seq, MAX_ROWS + 1)).fetchall()
            if len(rows) > MAX_ROWS:
                continue
            rows = [row for row in rows if row['occurred_at'] >= order.occurred_at
                    and row['observed_at'] >= order.observed_at and row['world_revision'] is not None
                    and (order.world_revision is None or row['world_revision'] >= order.world_revision)]
            groups, _ = _project_results(ledger, rows, boundary, identities, age_limited=False)
            speech_seqs = [m['seq'] for m in timeline
                           if timeline.sources.get(m['seq'], {}).get('input_seq') == input_seq]
            for group in groups:
                for item in group:
                    linked.append(dict(item, linked_input_seq=input_seq, linked_dialogue_seqs=speech_seqs,
                        original_request_id=order.request_id, original_input_event_id=order.source_event_id,
                        original_source_session_id=order.source_session_id,
                        original_conversation_id=order.conversation_id))
    return tuple(linked)
