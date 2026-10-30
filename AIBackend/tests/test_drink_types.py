"""Independent parameter/schema expectations; no runtime startup or storage."""
import unittest
from typing import get_args
from pydantic import TypeAdapter, ValidationError
from app.drink_types import DrinkType, DRINK_TYPE_IDS
from app.action_catalog import PREPARE_DRINK_REGISTRATION
from app.contracts import GoalProposal, CognitiveExecutionState
from app.task_models import ServeDrinkGoal, DrinkReceipt

EXPECTED_IDS = (
    "latte", "cappuccino", "americano", "espresso", "flat_white", "mocha",
    "caramel_macchiato", "hot_chocolate", "matcha_latte", "black_tea",
)

class DrinkTypeTests(unittest.TestCase):
    def test_exact_order_and_strict_schemas_share_the_parameter_contract(self):
        self.assertEqual(get_args(DrinkType), EXPECTED_IDS)
        self.assertEqual(DRINK_TYPE_IDS, EXPECTED_IDS)
        binding = PREPARE_DRINK_REGISTRATION.descriptor.binding_schema[0]
        self.assertEqual((binding.name, binding.kind, binding.required), ("drink_type", "enum", True))
        self.assertEqual(binding.enum_values, EXPECTED_IDS)
        for model in (GoalProposal, CognitiveExecutionState, ServeDrinkGoal, DrinkReceipt):
            field = model.model_json_schema()["properties"]["drink_type"]
            schema = next((x for x in field.get("anyOf", ()) if x.get("type") == "string"), field)
            self.assertEqual(schema["enum"], list(EXPECTED_IDS), model.__name__)
        for value in ("unregistered_drink", "LATTE", "", None, 1, True, ["latte"]):
            with self.subTest(value=value), self.assertRaises(ValidationError):
                TypeAdapter(DrinkType).validate_python(value)
