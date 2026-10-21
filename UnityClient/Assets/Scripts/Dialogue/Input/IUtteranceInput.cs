using System;

namespace EmbodiedNPC.Dialogue.Input
{
    /// <summary>Produces user utterances without knowing transport or planning.</summary>
    public interface IUtteranceInput
    {
        event Action<UserUtterance> Submitted;
        void BeginInput(DialogueContext context);
        void CancelInput();
    }
}
