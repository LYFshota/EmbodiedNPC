"""Current Relationship projection fixtures shared by context-budget tests."""
from app.relationship_context import RelationshipContext
from app.relationship_summary_reader import RelationshipCurrentStateContext, RelationshipStateItemContext

def social_context(*texts):
    return RelationshipContext(current_state=RelationshipCurrentStateContext(
        items=tuple(RelationshipStateItemContext(kind='TENTATIVE',text=text) for text in texts),
        has_direct_interaction=True,coverage='CURRENT',late_episode_skipped=False),
        truncated=False,policy_version='test/current')
