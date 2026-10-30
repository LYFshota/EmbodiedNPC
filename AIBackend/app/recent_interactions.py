"""Bounded read-only social and execution evidence over the existing ExperienceLedger."""
import json
from contextlib import closing

from .memory_consolidation_context import correlated_social_pair
from .experience_models import is_heard_speech
from .recent_action_results import RecentInteractionGroups, read_action_results


def _identities(event):
    return {p.entity_id: (p.identity_namespace, p.identity_type, p.persistent_id)
            for p in event.persistent_participants}


def read_recent_interactions(ledger, observer, conversation, request_id, event_id, identities):
    if not identities or observer not in identities:
        return []
    with closing(ledger._connection_factory()) as connection:
        rows = connection.execute(
            "SELECT * FROM experience_events WHERE observer_id=? AND conversation_id=? "
            "AND event_type='dialogue_input' AND request_id=? AND source_event_id=? LIMIT 2",
            (observer, conversation, request_id, event_id)).fetchall()
        if len(rows) != 1:
            return []
        boundary = ledger._row_to_event(rows[0])
        player = boundary.actor_entity_id
        if (boundary.target_entity_ids != (observer,) or player not in identities
                or any(_identities(boundary).get(k) != identities[k] for k in (observer, player))):
            return []
        rows = connection.execute(
            "SELECT * FROM experience_events WHERE observer_id=? AND source_session_id=? "
            "AND clock_domain=? AND experience_seq<? AND event_type='observation' "
            "AND ((json_extract(payload_json, '$.actor_entity_id')=? AND json_extract(payload_json, '$.structured_payload.known_fields.event_type')='player.emote') "
            "OR (json_extract(payload_json, '$.structured_payload.known_fields.event_type')='heard_speech' "
            "AND (json_extract(payload_json, '$.actor_entity_id')=? OR json_extract(payload_json, '$.structured_payload.known_fields.target_entity_id')=?))) "
            "ORDER BY experience_seq DESC LIMIT 16",
            (observer, boundary.source_session_id, boundary.clock_domain, boundary.experience_seq, player, player, player)).fetchall()
        actions, action_scope = read_action_results(ledger, connection, boundary, identities)
    result, seen, used = [], set(), 0
    for row in rows:
        action = ledger._row_to_event(row)
        if (action.source_event_id in seen or not action.trace_id
                or any(_identities(action).get(k) != identities[k] for k in (observer, player))):
            continue
        fields = action.structured_payload.get('known_fields', {})
        if fields.get('event_type') == 'heard_speech':
            if not is_heard_speech(action):
                continue
            target = fields.get('target_entity_id')
            speaker = action.actor_entity_id
            speaker_label = f'玩家（{player}）' if speaker == player else (speaker or '未辨识的说话者')
            target_label = f'玩家（{player}）' if target == player else target
            target_status = fields.get('target_epistemic_status', 'unknown')
            if target and target_status == 'identified_from_speech_event':
                addressed = f'对{target_label}'
                identity_note = '交谈对象由发言事件确认。'
            elif target and target_status == 'inferred_from_visible_interaction':
                addressed = f'似乎在对{target_label}'
                identity_note = '交谈对象仅为当时的视觉推断。'
            else:
                addressed = ''
                identity_note = '当时的直接对象未确认。'
            item = dict(kind='heard_speech', seq=action.experience_seq, observer_entity_id=observer,
                actor_entity_id=speaker, target_entity_id=target,
                target_epistemic_status=fields.get('target_epistemic_status', 'unknown'),
                social_role='bystander', text=fields['text'], historical_only=True, provenance='HEARD',
                source_event_id=action.source_event_id, parent_source_event_id=fields['parent_source_event_id'],
                occurrence_id=fields['occurrence_id'], event_id=action.event_id, trace_id=action.trace_id,
                request_id=fields.get('request_id'), run_id=fields.get('run_id'), occurred_at=action.occurred_at,
                model_content_is_not_world_truth=True,
                history_description=(f'历史旁听：你（{observer}）此前实际听到{speaker_label}{addressed}说了text字段中的内容。'
                    f'{identity_note}这不是对你说的话；只证明当时说过这些话。'))
            group = [item]
            size = len(json.dumps(group, ensure_ascii=False, separators=(',', ':')))
            if used + size <= 2400:
                result.append(group); seen.add(action.source_event_id); used += size
                if len(result) == 4:
                    break
            continue
        if fields.get('social_observer_entity_id') != observer or fields.get('emote_id') not in {'wave', 'cheer', 'flex'}:
            continue
        item = dict(kind='player_emote', seq=action.experience_seq, actor_entity_id=player,
                    observer_entity_id=observer, action=fields['emote_id'],
                    social_role=fields.get('social_role', 'bystander'),
                    meaning=fields.get('emote_meaning'), historical_only=True,
                    target_entity_id=fields.get('target_entity_id'),
                    target_epistemic_status=fields.get('target_epistemic_status', 'unknown'),
                    source_event_id=action.source_event_id, event_id=action.event_id,
                    trace_id=action.trace_id, occurred_at=action.occurred_at)
        action_label = {'wave': '挥手', 'cheer': '庆祝', 'flex': '展示肌肉'}[item['action']]
        # Describe only the observed action; inferred recipients stay in the typed fields.
        item['history_description'] = (
            f'历史观察（非玩家发言）：你（{observer}）此前亲眼看到玩家（{player}）做出{action_label}动作。')
        group = [item]
        reply = ledger.paired_response(action)
        if (reply and reply.experience_seq < boundary.experience_seq and correlated_social_pair(action, reply)
                and all(_identities(reply).get(k) == identities[k] for k in (observer, player))):
            group.append(dict(kind='npc_social_reply', seq=reply.experience_seq,
                              actor_entity_id=observer, reply_to_seq=action.experience_seq,
                              text=reply.natural_language_payload, event_id=reply.event_id,
                              source_event_id=reply.source_event_id, trace_id=reply.trace_id,
                              request_id=reply.request_id, run_id=reply.structured_payload['run_id'],
                              occurred_at=reply.occurred_at, model_content_is_not_world_truth=True,
                              history_description=(
                                  f'历史回复：随后，你（{observer}）对同一来源事件实际展示了 text 中的回复。')))
        size = len(json.dumps(group, ensure_ascii=False, separators=(',', ':')))
        if used + size > 2400:
            continue  # Never truncate an action/reply pair or turn omitted evidence into a fact.
        result.append(group); seen.add(action.source_event_id); used += size
        if len(result) == 4:
            break
    return RecentInteractionGroups(sorted(result + actions, key=lambda group: group[0]['seq']),
                                   action_scope=action_scope)
