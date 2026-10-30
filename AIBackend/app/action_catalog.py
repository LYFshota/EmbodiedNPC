"""Static internal catalog for the currently implemented high-level actions."""

from __future__ import annotations

from types import MappingProxyType
from typing import Iterable, Mapping

from .action_contracts import ActionBindingDescriptor, ActionDescriptor, ActionRegistration
from .drink_types import DRINK_TYPE_IDS


class ActionCatalog:
    """Immutable registration lookup; it intentionally performs no scanning or hot loading."""

    def __init__(self, registrations: Iterable[ActionRegistration]) -> None:
        ordered = tuple(registrations)
        by_id: dict[str, ActionRegistration] = {}
        for registration in ordered:
            action_id = registration.descriptor.action_id
            if action_id in by_id:
                raise ValueError(f"ACTION_ALREADY_REGISTERED:{action_id}")
            by_id[action_id] = registration
        self._registrations = ordered
        self._by_id: Mapping[str, ActionRegistration] = MappingProxyType(by_id)

    @property
    def registrations(self) -> tuple[ActionRegistration, ...]:
        return self._registrations

    @property
    def available_action_ids(self) -> tuple[str, ...]:
        return tuple(
            registration.descriptor.action_id
            for registration in self._registrations
            if registration.descriptor.available
        )

    def try_get(self, action_id: str) -> ActionRegistration | None:
        return self._by_id.get(action_id)

    def available_for(self, capability_ids: Iterable[str]) -> tuple[ActionRegistration, ...]:
        allowed = frozenset(capability_ids)
        return tuple(
            registration
            for registration in self._registrations
            if registration.descriptor.available and registration.descriptor.action_id in allowed
        )


BRING_OBJECT_REGISTRATION = ActionRegistration(ActionDescriptor(
    action_id="bring_object",
    version="1.0",
    description=(
        "Pick up or deliver one validated item to a validated destination. "
        "Requires an explicit bring, carry, give, or delivery request."
    ),
    binding_schema=(
        ActionBindingDescriptor(name="target", kind="entity_ref"),
        ActionBindingDescriptor(name="destination", kind="entity_ref"),
    ),
    target_requirements=("item", "pick_up", "delivery_destination"),
    default_timeout_seconds=30,
    interruption_policy="cancel",
    confirmation_policy="on_rebind",
), model_usage=(
    'The item binding needs trusted item identity from perception.entities (kind=item); the delivery '
    'destination needs player identity from relevant_player or a disclosed kind=player entity. Player, '
    'npc and environment references cannot fill the item target role; unknown is not item evidence. A '
    "remembered item remains a usable reference for Unity's existing rediscovery path; current visibility "
    'is not required. Item identity does not prove PickUp capability or current executability; Unity '
    'remains authoritative. A past dialogue mention without a grounded item reference supplies no '
    'binding. If no trustworthy item can be bound, use DIRECT_RESPONSE to explain what cannot yet be '
    'confirmed or ask for a missing clue, without promising to search, pick up or deliver. Missing target '
    'evidence does not prove no item exists; bring_object is not a separate find skill.'
))

MOVE_TO_ENTITY_REGISTRATION = ActionRegistration(ActionDescriptor(
    action_id="move_to_entity",
    version="1.0",
    description=(
        "Move the NPC near one validated entity without picking it up or delivering it. "
        "Requires a reachable authoritative approach point."
    ),
    binding_schema=(ActionBindingDescriptor(name="target", kind="entity_ref"),),
    target_requirements=("entity", "reachable_approach"),
    default_timeout_seconds=30,
    interruption_policy="cancel",
    confirmation_policy="on_rebind",
))

OPEN_DOOR_REGISTRATION = ActionRegistration(ActionDescriptor(
    action_id="open_door",
    version="1.0",
    description=(
        "Walk to and open one validated door. Unity authoritatively decides whether the door is closed, "
        "unlocked and reachable."
    ),
    binding_schema=(ActionBindingDescriptor(name="target", kind="entity_ref"),),
    target_requirements=("door", "open", "reachable_approach"),
    default_timeout_seconds=30,
    interruption_policy="cancel",
    confirmation_policy="on_rebind",
))

PREPARE_DRINK_REGISTRATION = ActionRegistration(ActionDescriptor(
    action_id="prepare_drink", version="1.0",
    description=f"Prepare one of {len(DRINK_TYPE_IDS)} supported hot drinks from drink_type for the requesting player at the authorized station.",
    binding_schema=(ActionBindingDescriptor(name="drink_type", kind="enum",
                                           enum_values=DRINK_TYPE_IDS),),
    target_requirements=("authorized_drink_station", "requesting_player"),
    default_timeout_seconds=30, interruption_policy="cancel", confirmation_policy="on_rebind",
), model_usage=(
    'It serves the current requesting player at a Unity-validated station. Unsupported drinks need '
    'explanation or clarification. Acceptance never proves the drink has been made; only the real action '
    'outcome confirms completion.'
))

CLEAN_COUNTER_REGISTRATION = ActionRegistration(ActionDescriptor(
    action_id="clean_counter", version="1.0",
    description="Wipe the single authorized spare counter surface when it needs cleaning.",
    binding_schema=(ActionBindingDescriptor(name="target", kind="entity_ref"),),
    target_requirements=("authorized_counter", "needs_cleaning"),
    default_timeout_seconds=30, interruption_policy="cancel", confirmation_policy="on_rebind",
), model_usage=(
    'Use the configured counter from counter_work.target_entity_id only when counter_work.needs_cleaning '
    'is true. This is configured counter work knowledge, not perception or proof of completion. Unity '
    'validates the final condition and operation point.'
))

BUILT_IN_ACTION_CATALOG = ActionCatalog((
    BRING_OBJECT_REGISTRATION,
    MOVE_TO_ENTITY_REGISTRATION,
    OPEN_DOOR_REGISTRATION,
    PREPARE_DRINK_REGISTRATION,
    CLEAN_COUNTER_REGISTRATION,
))
