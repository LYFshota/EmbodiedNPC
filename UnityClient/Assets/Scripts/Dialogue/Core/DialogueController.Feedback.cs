using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Dialogue.Presentation;

namespace EmbodiedNPC.Dialogue
{
    public sealed partial class DialogueController
    {
        private CognitiveRunDriver feedbackDriver;
        private string feedbackRequestId, feedbackTraceId, replyFeedback;
        private bool submittingForFeedback, inputConfirmed, responseExpected, responseConfirmed;
        private bool feedbackSettled;
        private readonly HashSet<string> unconfirmedRecords = new();
        private bool additionalUnconfirmedRecords;
        private DialogueBubbleManager waitingBubbles;
        private string waitingSpeaker, waitingRequest, waitingTrace, waitingConversation;

        private void ClearWaitingBubble(string request = null, string trace = null, string conversation = null)
        {
            if (request != null && (request != waitingRequest || trace != waitingTrace || conversation != waitingConversation)) return;
            waitingBubbles?.ClearWaiting(waitingSpeaker, waitingRequest, waitingTrace, waitingConversation);
            waitingBubbles = null; waitingSpeaker = waitingRequest = waitingTrace = waitingConversation = null;
        }

        private void BindFeedbackDriver(CognitiveRunDriver driver)
        {
            if (feedbackDriver == driver) return;
            UnbindFeedbackDriver();
            feedbackDriver = driver;
            if (driver == null) return;
            driver.RequestFeedback += HandleRequestFeedback;
            driver.UtterancePresented += HandleFeedbackPresentation;
        }

        private void UnbindFeedbackDriver()
        {
            ClearWaitingBubble();
            if (feedbackDriver != null)
            {
                feedbackDriver.RequestFeedback -= HandleRequestFeedback;
                feedbackDriver.UtterancePresented -= HandleFeedbackPresentation;
            }
            feedbackDriver = null;
        }

        private void ResetRequestFeedback()
        {
            ClearWaitingBubble();
            feedbackRequestId = feedbackTraceId = replyFeedback = null;
            inputConfirmed = responseExpected = responseConfirmed = false;
            feedbackSettled = false;
            unconfirmedRecords.Clear(); additionalUnconfirmedRecords = false;
        }

        private void TrackAcceptedFeedback(AcceptedDialogueTurn turn)
        {
            // A response receipt may precede presentation/ACK. Only player input starts a wait.
            if (turn == null || turn.IsResponse) return;
            bool alreadySettled = feedbackRequestId == turn.RequestId && feedbackSettled;
            feedbackRequestId = turn.RequestId; feedbackTraceId = turn.TraceId;
            if (!alreadySettled) replyFeedback = "WAITING";
            ClearWaitingBubble();
            if (replyFeedback != "WAITING" || turn.IsResponse || !setupReady || !HasActiveSession ||
                backendClient == null || !backendClient.IsConnected || turn.ConversationId != CurrentConversationId ||
                turn.TargetAgentId != selectedTarget?.Entity?.EntityId) return;
            var manager = DialogueBubbleManager.Active;
            if (manager == null || !manager.ShowWaiting(turn.TargetAgentId, turn.RequestId, turn.TraceId, turn.ConversationId)) return;
            waitingBubbles = manager; waitingSpeaker = turn.TargetAgentId; waitingRequest = turn.RequestId;
            waitingTrace = turn.TraceId; waitingConversation = turn.ConversationId;
        }

        private void HandleRequestFeedback(CognitiveRequestFeedback value)
        {
            if (value == null || !HasActiveSession || value.AgentEntityId != selectedTarget?.Entity?.EntityId ||
                value.ConversationId != CurrentConversationId || value.EventType != AgentEventTypes.PlayerUtterance ||
                (!submittingForFeedback && (value.RequestId != feedbackRequestId || value.TraceId != feedbackTraceId))) return;
            feedbackRequestId = value.RequestId; feedbackTraceId = value.TraceId;
            feedbackSettled = true;
            replyFeedback = value.Status == "response_received" ? null : value.Reason ?? value.Status;
            if (value.Status != "response_received" ||
                (feedbackDriver?.PendingRequest == null && !responseExpected))
                ClearWaitingBubble(value.RequestId, value.TraceId, value.ConversationId);
            RefreshRequestFeedback();
        }

        private void HandleFeedbackPresentation(PresentedCognitiveUtterance value)
        {
            if (value == null || value.RequestId != feedbackRequestId || value.TraceId != feedbackTraceId ||
                value.ConversationId != CurrentConversationId || value.OwnerAgentEntityId != selectedTarget?.Entity?.EntityId) return;
            responseExpected = true;
            ClearWaitingBubble(value.RequestId, value.TraceId, value.ConversationId);
            RefreshRequestFeedback();
        }

        private void HandleExperienceSettled(ExperienceDraftDto draft, ValidatedExperienceAppendResult result, string status)
        {
            if (draft == null || !HasActiveSession || draft.conversation_id != CurrentConversationId ||
                draft.observer_id != selectedTarget?.Entity?.EntityId || draft.source_type != "dialogue" ||
                draft.event_type is not ("dialogue_input" or "dialogue_response")) return;
            string key = draft.request_id + ":" + draft.event_type + ":" + draft.source_event_id;
            if (result?.IsPersisted == true) unconfirmedRecords.Remove(key);
            else if (unconfirmedRecords.Count < 128) unconfirmedRecords.Add(key);
            else additionalUnconfirmedRecords = true;
            bool current = feedbackRequestId != null && draft.request_id == ExperienceIds.ProtocolId(feedbackRequestId, "request") &&
                draft.trace_id == ExperienceIds.ProtocolId(feedbackTraceId, "trace");
            if (current && result?.IsPersisted == true)
            {
                if (draft.event_type == "dialogue_input") inputConfirmed = true;
                else responseConfirmed = true;
            }
            RefreshRequestFeedback();
        }

        private bool RefreshRequestFeedbackIfAny()
        {
            if (feedbackRequestId == null && unconfirmedRecords.Count == 0 && !additionalUnconfirmedRecords) return false;
            RefreshRequestFeedback(); return true;
        }

        private void RefreshRequestFeedback()
        {
            if (!HasActiveSession || backendClient != null && !backendClient.IsConnected) return;
            string text = "";
            if (replyFeedback != null && replyFeedback != "WAITING")
            {
                var message = P0FeedbackText.Dialogue(replyFeedback);
                text = SettingsUiLanguage.T(message.Chinese, message.English);
            }
            bool unknown = unconfirmedRecords.Count > 0 || additionalUnconfirmedRecords ||
                replyFeedback != null && replyFeedback != "WAITING" && !inputConfirmed;
            if (unknown)
            {
                var message = P0FeedbackText.Dialogue("ACK_UNKNOWN");
                text = text.Length == 0 ? SettingsUiLanguage.T(message.Chinese, message.English) :
                    text + SettingsUiLanguage.T(" 保存未确认，请检查连接，勿重复提交。", " Storage unconfirmed; check the connection, do not resubmit.");
            }
            SetStatus(text);
            if (statusText != null)
            {
                statusText.raycastTarget = false;
                statusText.enableAutoSizing = true;
                statusText.fontSizeMin = 12;
                statusText.fontSizeMax = 20;
            }
        }
    }
}
