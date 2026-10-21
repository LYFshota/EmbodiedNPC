"""Current-run eligibility for proposing a new gameplay world action."""

from __future__ import annotations

from .contracts import CognitiveRequest

COUNTER_CLEANING_EVENT = "counter.needs_cleaning"


class WorldActionNotEligibleError(ValueError):
    """Safe terminal refusal before action-specific validation or handoff."""

    def __init__(self) -> None:
        super().__init__("WORLD_ACTION_NOT_ELIGIBLE")


def may_propose_new_world_action(request: CognitiveRequest) -> bool:
    """Derive Cafe v1 eligibility only from the owned current Event Run."""

    run = request.run
    trigger = request.trigger
    context = request.context
    event = context.current_event
    player = context.relevant_player
    return (
        run.trigger_type == "event"
        and trigger.trigger_type == run.trigger_type
        and run.trigger_id == trigger.trigger_id == context.trigger.trigger_id
        and run.agent_entity_id == trigger.agent_entity_id == context.agent_entity_id
        and context.perception.observer_entity_id == run.agent_entity_id
        and event is not None
        and bool(trigger.source_event_id.strip())
        and trigger.source_event_id == event.event_id
        and event.event_type == "player.utterance"
        and event.target_entity_id == run.agent_entity_id
        and player is not None
        and bool(event.source_entity_id.strip())
        and event.source_entity_id == player.player_entity_id
        and (event.trace_id is None or not event.trace_id.strip() or event.trace_id == request.trace_id)
    )


def is_counter_cleaning_consideration(request: CognitiveRequest) -> bool:
    """Validate the Unity authority projection against the event, never event name/payload alone."""
    event = request.context.current_event
    counter = request.context.counter_work
    daily = request.context.daily_behavior
    if event is None or event.event_type != COUNTER_CLEANING_EVENT or counter is None or daily is None:
        return False
    payload = {item.key: item.value for item in event.payload}
    if len(payload) != len(event.payload) or set(payload) != {"target_entity_id", "condition_token", "condition_revision"}:
        return False
    return (request.run.trigger_type == request.trigger.trigger_type == "event"
        and request.run.trigger_id == request.trigger.trigger_id == request.context.trigger.trigger_id
        and request.run.agent_entity_id == request.context.agent_entity_id == request.trigger.agent_entity_id == "npc_cafe_01"
        and request.trigger.source_event_id == event.event_id == counter.event_id
        and event.trace_id == request.trace_id == counter.trace_id
        and request.context.relevant_player is None and event.source_observation_id is None
        and event.target_entity_id == "npc_cafe_01" and event.area_entity_id == "area_cafe_counter"
        and counter.target_entity_id == event.source_entity_id == payload.get("target_entity_id")
        and counter.needs_cleaning and counter.condition_revision > 0
        and str(counter.condition_revision) == payload.get("condition_revision")
        and counter.condition_token is not None and len(counter.condition_token) == 32
        and all(c in "0123456789abcdef" for c in counter.condition_token)
        and counter.condition_token == payload.get("condition_token")
        and event.has_world_revision and event.world_revision > 0 and daily.current_mode == "work")


def may_propose_autonomous_action(request: CognitiveRequest, proposal) -> bool:
    return (is_counter_cleaning_consideration(request) and proposal is not None
        and proposal.action_id == "clean_counter"
        and proposal.bindings == {"target": request.context.counter_work.target_entity_id})
