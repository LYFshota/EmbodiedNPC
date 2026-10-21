namespace EmbodiedNPC.Dialogue
{
    public readonly struct DialogueSubmissionResult
    {
        private DialogueSubmissionResult(bool accepted, string status, AcceptedDialogueTurn acceptedTurn)
        {
            Accepted = accepted;
            Status = status ?? string.Empty;
            AcceptedTurn = acceptedTurn;
        }

        public bool Accepted { get; }
        public string Status { get; }
        public AcceptedDialogueTurn AcceptedTurn { get; }

        public static DialogueSubmissionResult Accept(string status = "COGNITIVE_REQUEST_SUBMITTED") =>
            new(true, status, null);

        public static DialogueSubmissionResult AcceptWithTurn(AcceptedDialogueTurn acceptedTurn,
            string status = "COGNITIVE_REQUEST_SUBMITTED") => new(true, status, acceptedTurn);

        public static DialogueSubmissionResult Reject(string status) => new(false, status, null);
    }

    /// <summary>Optional per-target boundary that may own a validated dialogue submission.</summary>
    public interface IDialogueSubmissionHandler
    {
        DialogueSubmissionResult Submit(UserUtterance utterance);
    }
}
