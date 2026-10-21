from __future__ import annotations

import unittest

from app.action_catalog import (
    ActionCatalog,
    BRING_OBJECT_REGISTRATION,
    BUILT_IN_ACTION_CATALOG,
    MOVE_TO_ENTITY_REGISTRATION,
    OPEN_DOOR_REGISTRATION,
)


class ActionCatalogTests(unittest.TestCase):
    def test_builtin_catalog_registers_bring_object_with_bounded_metadata(self) -> None:
        registration = BUILT_IN_ACTION_CATALOG.try_get("bring_object")

        self.assertIs(BRING_OBJECT_REGISTRATION, registration)
        self.assertEqual(("target", "destination"), tuple(
            binding.name for binding in registration.descriptor.binding_schema
        ))
        self.assertEqual(("item", "pick_up", "delivery_destination"),
                         registration.descriptor.target_requirements)

    def test_builtin_catalog_registers_move_to_entity_with_bounded_metadata(self) -> None:
        registration = BUILT_IN_ACTION_CATALOG.try_get("move_to_entity")

        self.assertIs(MOVE_TO_ENTITY_REGISTRATION, registration)
        self.assertEqual(("target",), tuple(
            binding.name for binding in registration.descriptor.binding_schema
        ))
        self.assertIn("reachable_approach", registration.descriptor.target_requirements)

    def test_unknown_action_query_returns_none_without_nearest_match(self) -> None:
        self.assertIsNone(BUILT_IN_ACTION_CATALOG.try_get("bring_objects"))
        self.assertIsNone(BUILT_IN_ACTION_CATALOG.try_get("open_doors"))

    def test_builtin_catalog_registers_open_door_with_bounded_metadata(self) -> None:
        registration = BUILT_IN_ACTION_CATALOG.try_get("open_door")

        self.assertIs(OPEN_DOOR_REGISTRATION, registration)
        self.assertEqual(("target",), tuple(
            binding.name for binding in registration.descriptor.binding_schema
        ))
        self.assertEqual(("door", "open", "reachable_approach"),
                         registration.descriptor.target_requirements)

    def test_duplicate_registration_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "ACTION_ALREADY_REGISTERED:bring_object"):
            ActionCatalog((BRING_OBJECT_REGISTRATION, BRING_OBJECT_REGISTRATION))

    def test_available_for_intersects_client_capabilities_in_catalog_order(self) -> None:
        registrations = BUILT_IN_ACTION_CATALOG.available_for(("move_to_entity", "unregistered"))

        self.assertEqual(("move_to_entity",), tuple(
            registration.descriptor.action_id for registration in registrations
        ))
        self.assertEqual(("bring_object", "move_to_entity", "open_door", "prepare_drink", "clean_counter"),
                         BUILT_IN_ACTION_CATALOG.available_action_ids)
