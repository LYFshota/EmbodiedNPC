"""Open Semantic Fact proposals for production consolidation.

The model interprets meaning. These contracts validate structure and exact source
text only: matching an excerpt does not prove that a proposed fact is entailed.
"""

from collections.abc import Mapping
from typing import Annotated

from pydantic import BaseModel, ConfigDict, Field, field_validator


SEMANTIC_FACT_CONTRACT_VERSION = "semantic-fact/1.0"
SEMANTIC_MEMORY_RECORD_VERSION = "memory-record/3.0"


class SemanticFactCandidate(BaseModel):
    """One atomic, source-qualified claim, without model-controlled authority.

Preserve negation, past/present distinctions and uncertainty. A preference is
not a favorite, a wish is not an outcome, and speech is not world verification.
These are semantic obligations for extraction, not string-based NLU rules.
"""

    model_config = ConfigDict(extra="forbid", frozen=True)

    subject_handle: str = Field(pattern=r"^participant_[1-9][0-9]*$", max_length=40)
    fact: str = Field(min_length=1, max_length=500)
    evidence_event_ids: tuple[Annotated[str, Field(min_length=1, max_length=160)], ...] = Field(
        min_length=1, max_length=64
    )
    source_excerpt: str = Field(min_length=1, max_length=1000)
    confidence: float = Field(ge=0, le=1, allow_inf_nan=False)

    @field_validator("fact", "source_excerpt")
    @classmethod
    def nonblank_text(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("SEMANTIC_FACT_TEXT_BLANK")
        # Retain the original excerpt, including its punctuation and spacing.
        return value

    @field_validator("evidence_event_ids")
    @classmethod
    def unique_evidence(cls, value: tuple[str, ...]) -> tuple[str, ...]:
        if any(not event_id.strip() for event_id in value):
            raise ValueError("EVIDENCE_ID_BLANK")
        if len(set(value)) != len(value):
            raise ValueError("DUPLICATE_EVIDENCE_ID")
        return value


def locate_source_excerpt(candidate: SemanticFactCandidate,
                          evidence_texts: Mapping[str, str | None]) -> str:
    """Return a cited event containing the literal excerpt, not a truth verdict.

    The caller must supply only authorized, model-visible raw evidence and must
    independently validate observer, subject and provenance. No fuzzy matching,
    normalization or keyword interpretation is used here.
    """
    if any(event_id not in evidence_texts for event_id in candidate.evidence_event_ids):
        raise ValueError("EVIDENCE_OUTSIDE_CONTEXT")
    for event_id in candidate.evidence_event_ids:
        text = evidence_texts[event_id]
        if isinstance(text, str) and candidate.source_excerpt in text:
            return event_id
    raise ValueError("SOURCE_EXCERPT_NOT_IN_CITED_EVIDENCE")
