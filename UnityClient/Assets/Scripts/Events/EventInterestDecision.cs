namespace EmbodiedNPC.Events
{
    /// <summary>Finite runtime handling result for one AgentEvent at one receiver.</summary>
    public enum EventInterestDecision
    {
        Ignore,
        Record,
        Wake,
        FastPath,
    }

    /// <summary>Optional static relevance constraint for an authored interest rule.</summary>
    public enum EventInterestRelevance
    {
        Any,
        TargetSelf,
        SourceSelf,
        SocialResponseCandidate,
    }
}
