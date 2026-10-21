"""Bounded speech windows and local continuity, after ledger source verification."""

from dataclasses import dataclass


@dataclass(frozen=True)
class RecentDialoguePolicy:
    """Internal injection only; None keeps the capacity-based 8/16 defaults."""

    target_messages: int | None = None
    boundary_overlap: int = 2

    def __post_init__(self):
        if (self.target_messages is not None and
                (type(self.target_messages) is not int or not 1 <= self.target_messages <= 64)):
            raise ValueError("RECENT_DIALOGUE_TARGET_INVALID")
        if type(self.boundary_overlap) is not int or not 0 <= self.boundary_overlap <= 2:
            raise ValueError("RECENT_DIALOGUE_OVERLAP_INVALID")

    def limits(self, capacity):
        large = capacity >= 16384
        return dict(limit=self.target_messages or (16 if large else 8),
                    max_chars=2400 if large else 1200,
                    timeline=True, boundary_overlap=self.boundary_overlap)


def dependencies(message):
    """Reply/anchor are verified lineage; context_seq is only local continuity."""
    return tuple(message[key] for key in ("reply_to_seq", "anchor_seq", "context_seq")
                 if key in message)


class RecentDialogueTimeline(list[dict]):
    """Model-visible speech plus internal original-source metadata, never durable state."""

    def __init__(self, messages=(), *, sources=None, truncated=False, target=8, expanded=0):
        super().__init__(messages)
        self.sources = sources or {}
        self.truncated = truncated
        self.target_messages = target
        self.expanded_messages = expanded

    @classmethod
    def select(cls, messages, sources, target, char_budget, overlap, *, truncated=False):
        ordered = sorted(messages, key=lambda m: m["seq"])
        start = max(0, len(ordered) - target)
        floor = max(0, start - overlap)
        available = {m["seq"]: m for m in ordered[floor:]}
        selected = {m["seq"] for m in ordered[start:]}
        # CurrentTask owns the current accepted input. Its nearest displayed NPC
        # predecessor may be just outside the requested history window.
        latest_npc = next((m for m in reversed(ordered) if m["role"] == "npc"), None)
        if latest_npc and latest_npc["seq"] in available:
            selected.add(latest_npc["seq"])
        # Only the existing 0..2 prefix slots can expand the window. No semantic
        # classifier, unbounded parent walk, or cross-session clock sort.
        for _ in range(overlap):
            selected.update(ref for seq in tuple(selected)
                            for ref in dependencies(available[seq]) if ref in available)
        if selected:
            # Extending to a predecessor also keeps intervening verified speech,
            # rather than silently skipping a short utterance between two links.
            first = next(i for i, m in enumerate(ordered[floor:]) if m["seq"] in selected)
            selected.update(m["seq"] for m in ordered[floor+first:])
        kept = []
        remaining = char_budget
        for message in reversed(ordered[floor:]):
            if message["seq"] not in selected:
                continue
            cost = len(message["text"])
            if cost > remaining:
                truncated = True
                continue  # An older long utterance cannot evict a newer short one.
            kept.append(message)
            remaining -= cost
        kept.reverse()
        retained = {m["seq"] for m in kept}
        base = {m["seq"] for m in ordered[start:]}
        return cls(kept, sources={seq: sources[seq] for seq in retained},
                   truncated=truncated or len(kept) < len(ordered), target=target,
                   expanded=len(retained - base))

    def budget_groups(self):
        """Small dependency blocks, with the nearest NPC block first.

        Speech is counted individually, while budget selection protects local
        dependencies together. A two-hop bound avoids chaining all history.
        """
        by_seq = {m["seq"]: m for m in self}
        assigned = set()

        def block(root):
            selected = {root}
            frontier = {root}
            for _ in range(2):
                frontier = {ref for seq in frontier for ref in dependencies(by_seq[seq])
                            if ref in by_seq and ref not in assigned and ref not in selected}
                selected.update(frontier)
            return selected - assigned

        groups = []
        latest_npc = next((m for m in reversed(self) if m["role"] == "npc"), None)
        if latest_npc:
            focus = block(latest_npc["seq"])
            for message in self[-2:]:
                if message.get("context_seq") == latest_npc["seq"]:
                    focus.add(message["seq"])
            groups.append((tuple(sorted(focus)), True))
            assigned.update(focus)
        for message in reversed(self):
            if message["seq"] in assigned:
                continue
            selected = block(message["seq"])
            groups.append((tuple(sorted(selected)), not groups))
            assigned.update(selected)
        return tuple(groups)

    def visible(self, retained):
        """Recheck local dependencies after final budget selection; expose fragments."""
        selected = set(retained)
        by_seq = {m["seq"]: m for m in self}
        result = []
        for message in self:
            if message["seq"] not in selected:
                continue
            required = set(dependencies(message))
            for ref in tuple(required):
                if ref in by_seq:
                    required.update(dependencies(by_seq[ref]))
            incomplete = bool(required - selected)
            result.append({**message, **({"context_incomplete": True} if incomplete else {})})
        return result
