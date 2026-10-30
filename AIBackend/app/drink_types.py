"""Strict drink parameters shared by contracts, tasks and action metadata; no I/O."""
from typing import Literal, get_args

DrinkType = Literal[
    "latte", "cappuccino", "americano", "espresso", "flat_white", "mocha",
    "caramel_macchiato", "hot_chocolate", "matcha_latte", "black_tea",
]
DRINK_TYPE_IDS: tuple[DrinkType, ...] = get_args(DrinkType)
VALID_DRINK_TYPES = frozenset(DRINK_TYPE_IDS)
