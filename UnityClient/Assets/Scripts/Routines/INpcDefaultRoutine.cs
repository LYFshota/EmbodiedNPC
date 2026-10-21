namespace EmbodiedNPC.Routines
{
    public interface INpcDefaultRoutine
    {
        bool IsRunning { get; }
        void TickRoutine(float deltaTime);
        void SuspendRoutine();
    }
}
