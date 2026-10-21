"""Independent lexical/dense branches over an already-authorized canonical set."""

from dataclasses import dataclass
import logging
import math
import time

from .canonical_embeddings import CanonicalEmbeddingIndex, normalized

logger = logging.getLogger("embodiednpc.backend.canonical_retrieval")


def tie_key(row):
    return (-row["confidence"], -(row["importance"] or 0), -row["learned_at"], row["memory_id"])


def rrf(lexical, dense, by_id):
    scores = {}
    for branch in (lexical, dense):
        for rank, memory_id in enumerate(branch, 1):
            scores[memory_id] = scores.get(memory_id, 0) + 1 / (60 + rank)
    return sorted(scores, key=lambda key: (-scores[key], *tie_key(by_id[key])))


@dataclass(frozen=True)
class CanonicalRanking:
    ids: tuple[str, ...]
    truncated: bool
    embedding_unavailable: bool = False


class CanonicalMemoryRanker:
    def __init__(self, provider=None):
        self.provider = provider

    def rank(self, rows, db, semantic_query, filters, budget):
        from .canonical_memory_read_repository import lexical_terms
        terms = lexical_terms(semantic_query)
        by_id = {row["memory_id"]: row for row in rows}
        lexical_scores = {row["memory_id"]: len(terms & lexical_terms(row["summary"])) for row in rows}
        lexical = sorted((key for key in by_id if lexical_scores[key] > 0),
                         key=lambda key: (-lexical_scores[key], *tie_key(by_id[key])))
        dense = []
        vector_ready = False
        fallback_reason = "provider_unavailable" if self.provider is None else "empty_query"
        latency = 0.0
        if self.provider is not None and semantic_query.strip():
            try:
                vectors = CanonicalEmbeddingIndex.load_valid(db, rows, self.provider.model_key)
                fallback_reason = "index_incomplete"
                if vectors:
                    started = time.perf_counter()
                    query = normalized(getattr(self.provider, "embed_query", self.provider.embed)(semantic_query))
                    latency = (time.perf_counter() - started) * 1000
                    expected_dimension = getattr(self.provider, "dimensions", None)
                    scores = {key: sum(a * b for a, b in zip(query, vector))
                              for key, vector in vectors.items() if len(query) == len(vector) and expected_dimension in (None, len(query))}
                    complete = len(vectors) == len(by_id)
                    compatible = complete and len(scores) == len(by_id)
                    valid = compatible and all(math.isfinite(value) for value in scores.values())
                    vector_ready = valid and any(value > 0 for value in scores.values())
                    fallback_reason = ("index_incomplete" if not complete else "dimension_incompatible" if not compatible
                                       else "invalid_similarity" if not valid else "no_positive_similarity")
                    # Non-positive cosine supplies no semantic support.
                    dense = sorted((key for key in scores if scores[key] > 0),
                                   key=lambda key: (-scores[key], *tie_key(by_id[key])))
                    if not all(math.isfinite(value) for value in scores.values()):
                        dense = []
            except Exception as exc:
                # A derived-index or provider failure never makes canonical recall fatal.
                fallback_reason = "query_or_index_failed"
                dense = []
                logger.warning("Canonical dense fallback error=%s", type(exc).__name__)
        limit = budget.candidate_limit
        fused = dense[:limit] if vector_ready else rrf(lexical[:limit], dense[:limit], by_id)
        logger.info("retrieval_mode=%s eligible_count=%d lexical_count=%d dense_count=%d fused_count=%d "
                    "query_embedding_latency_ms=%.1f fallback_reason=%s", "vector_first" if vector_ready else "hybrid_fallback" if dense else "lexical_fallback",
                    len(rows), min(len(lexical), limit), min(len(dense), limit), len(fused), latency, "none" if vector_ready else fallback_reason)
        unavailable = bool(rows) and fallback_reason in {
            "index_incomplete", "dimension_incompatible", "invalid_similarity", "query_or_index_failed"}
        return CanonicalRanking(tuple(fused), len(dense) > limit if vector_ready else
                                len(lexical) > limit or len(dense) > limit, unavailable)
