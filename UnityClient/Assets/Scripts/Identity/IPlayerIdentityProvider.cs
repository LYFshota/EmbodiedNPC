namespace EmbodiedNPC.Identity
{
    /// <summary>Read-only adapter over the host game's authoritative player identity service.</summary>
    public interface IPlayerIdentityProvider
    {
        bool TryGetCurrentIdentity(out PlayerIdentitySnapshot snapshot);
    }
}
