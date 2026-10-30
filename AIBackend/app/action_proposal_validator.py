"""Deterministic E6A validation of untrusted cognitive action proposals."""

from __future__ import annotations

from .action_catalog import BUILT_IN_ACTION_CATALOG, ActionCatalog
from .action_contracts import binding_contract_signature
from .contracts import CognitiveContextSnapshot, CognitiveDecision, GoalProposal


class ActionProposalValidationError(ValueError):
    """Safe terminal validation failure; details are intentionally not model-visible."""

    reason = "INVALID_ACTION_PROPOSAL"


class ActionProposalValidator:
    """Validates only structured action/binding legality, never raw language semantics."""

    def __init__(self, supported_actions: ActionCatalog = BUILT_IN_ACTION_CATALOG) -> None:
        self._supported_actions = supported_actions

    def validate_and_adapt(self, decision: CognitiveDecision,
                           context: CognitiveContextSnapshot) -> GoalProposal | None:
        if decision.decision_kind not in ("ACTION_PROPOSAL", "AUTONOMOUS_ACTION_INTENT"):
            return None
        proposal = decision.action_proposal
        manifest = context.capabilities
        if proposal is None or manifest is None:
            raise ActionProposalValidationError()

        runtime_action = next(
            (item for item in manifest.available_actions if item.action_id == proposal.action_id), None
        )
        supported = self._supported_actions.try_get(proposal.action_id)
        if runtime_action is None or supported is None or not supported.descriptor.available:
            raise ActionProposalValidationError()

        supported_bindings = binding_contract_signature(supported.descriptor.binding_schema)
        runtime_bindings = binding_contract_signature(runtime_action.bindings)
        if runtime_bindings != supported_bindings:
            raise ActionProposalValidationError()

        supplied = set(proposal.bindings)
        required = {name for name, (_, is_required, _) in supported_bindings.items() if is_required}
        if not required.issubset(supplied) or not supplied.issubset(supported_bindings):
            raise ActionProposalValidationError()

        observer_scope = {entity.entity_id for entity in context.perception.entities}
        if context.relevant_player is not None:
            observer_scope.add(context.relevant_player.player_entity_id)
        if proposal.action_id == "clean_counter":
            counter = context.counter_work
            if (context.agent_entity_id != "npc_cafe_01" or counter is None or not counter.needs_cleaning or
                    counter.condition_revision < 1 or proposal.bindings.get("target") != counter.target_entity_id):
                raise ActionProposalValidationError()
            observer_scope.add(counter.target_entity_id)
        for name, entity_id in proposal.bindings.items():
            if supported_bindings[name][0] == "entity_ref" and entity_id not in observer_scope:
                raise ActionProposalValidationError()
            if supported_bindings[name][0] == "enum" and entity_id not in supported_bindings[name][2]:
                raise ActionProposalValidationError()

        if proposal.action_id == "prepare_drink":
            if context.agent_entity_id != "npc_cafe_01" or context.relevant_player is None:
                raise ActionProposalValidationError()
            return GoalProposal(type="prepare_drink", drink_type=proposal.bindings["drink_type"])

        if proposal.action_id == "clean_counter":
            return GoalProposal(type="clean_counter", target_entity_id=proposal.bindings["target"])

        if proposal.action_id != "bring_object":
            raise ActionProposalValidationError()

        target = proposal.bindings["target"]
        destination = proposal.bindings["destination"]
        # Observer-local kinds are already supplied by Unity. A reference in scope is not
        # an item capability. Unknown kinds and remembered items still need final Unity
        # admission; absence of an observed capability is not proof that it is unavailable.
        player_id = context.relevant_player.player_entity_id if context.relevant_player else None
        if target == player_id or any(
            entity.entity_id == target and entity.kind not in ("item", "unknown")
            for entity in context.perception.entities
        ):
            raise ActionProposalValidationError()
        if any(
            entity.entity_id == destination and entity.kind not in ("player", "unknown")
            for entity in context.perception.entities
        ):
            raise ActionProposalValidationError()
        return GoalProposal(
            type="bring_object",
            target_entity_id=target,
            destination_entity_id=destination,
        )
