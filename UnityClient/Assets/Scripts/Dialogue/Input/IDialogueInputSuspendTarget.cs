namespace EmbodiedNPC.Dialogue.Input
{
    /// <summary>Presentation/input boundary for gameplay consumers paused by an active dialogue session.</summary>
    public interface IDialogueInputSuspendTarget
    {
        void SetDialogueInputSuspended(bool suspended);
    }

    /// <summary>Cursor presentation is independent of movement and look suspension.</summary>
    public interface IDialogueCursorTarget
    {
        void SetDialogueCursorMode(bool active);
    }
}
