namespace EmbodiedNPC.Interaction
{
    /// <summary>Minimal capability contract exposed by an interactable entity.</summary>
    public interface IInteractable
    {
        string EntityId { get; }
        bool SupportsPickUp { get; }
        bool SupportsGive { get; }
    }
}
