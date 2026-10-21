from __future__ import annotations

import unittest

from pydantic import ValidationError

from app.action_contracts import (
    ActionBindingDescriptor,
    ActionDescriptor,
    ActionRegistration,
    ExecutionAuthorization,
)
from app.contracts import CognitiveRequest, CognitiveResponse


def descriptor() -> ActionDescriptor:
    return ActionDescriptor(
        action_id="bring_object",
        version="1.0",
        description="Bring one validated object to a validated destination.",
        binding_schema=(
            ActionBindingDescriptor(name="target", kind="entity_ref"),
            ActionBindingDescriptor(name="destination", kind="entity_ref"),
        ),
        target_requirements=("item", "pick_up"),
        default_timeout_seconds=30,
        interruption_policy="cancel",
        confirmation_policy="on_rebind",
    )


class ActionContractTests(unittest.TestCase):
    def test_registration_exposes_a_bounded_descriptor_without_runtime_handlers(self) -> None:
        registration = ActionRegistration(descriptor())

        self.assertEqual("bring_object", registration.descriptor.action_id)
        self.assertEqual(("target", "destination"), tuple(
            binding.name for binding in registration.descriptor.binding_schema
        ))

    def test_descriptor_rejects_executable_or_unbounded_binding_shapes(self) -> None:
        invalid_bindings = (
            {"name": "callback", "kind": "unity_method"},
            {"name": "choice", "kind": "enum", "enum_values": ()},
            {"name": "count", "kind": "number"},
            {"name": "count", "kind": "number", "minimum": 5, "maximum": 1},
            {"name": "text", "kind": "string"},
            {"name": "target", "kind": "entity_ref", "max_length": 20},
        )
        for binding in invalid_bindings:
            with self.subTest(binding=binding), self.assertRaises(ValidationError):
                ActionBindingDescriptor.model_validate(binding)

    def test_descriptor_rejects_unknown_fields_and_duplicate_bindings(self) -> None:
        payload = descriptor().model_dump()
        payload["unity_method"] = "OpenDoor"
        with self.assertRaises(ValidationError):
            ActionDescriptor.model_validate(payload)
        duplicate_payload = descriptor().model_dump()
        duplicate_payload["binding_schema"] = (
            ActionBindingDescriptor(name="target", kind="entity_ref"),
            ActionBindingDescriptor(name="target", kind="entity_ref"),
        )
        with self.assertRaises(ValidationError):
            ActionDescriptor.model_validate(duplicate_payload)

    def test_execution_authorization_is_action_and_entity_scoped(self) -> None:
        authorization = ExecutionAuthorization(
            action_id="bring_object",
            selection_mode="any",
            authorized_entity_ids=("cup_01", "cup_02"),
            confirmation_policy="before_execution",
            proposal_revision=2,
            world_revision=9,
        )

        self.assertTrue(authorization.authorizes("bring_object", "cup_02"))
        self.assertFalse(authorization.authorizes("move_to_entity", "cup_02"))
        self.assertFalse(authorization.authorizes("bring_object", "book_01"))

    def test_exact_authorization_and_duplicate_scope_fail_closed(self) -> None:
        base = {
            "action_id": "bring_object",
            "selection_mode": "exact",
            "authorized_entity_ids": ("cup_01", "cup_02"),
            "confirmation_policy": "before_execution",
            "proposal_revision": 0,
            "world_revision": 0,
        }
        with self.assertRaises(ValidationError):
            ExecutionAuthorization.model_validate(base)
        with self.assertRaises(ValidationError):
            ExecutionAuthorization.model_validate({
                **base,
                "selection_mode": "any",
                "authorized_entity_ids": ("cup_01", "cup_01"),
            })

    def test_internal_catalog_types_do_not_expand_cognitive_wire_protocol(self) -> None:
        for contract in (CognitiveRequest, CognitiveResponse):
            properties = contract.model_json_schema().get("properties", {})
            self.assertNotIn("action_descriptor", properties)
            self.assertNotIn("execution_authorization", properties)


if __name__ == "__main__":
    unittest.main()
