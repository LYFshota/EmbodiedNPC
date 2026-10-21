using System;
using System.Collections;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class PlayerEmoteHistoryTests : PlayerEmoteSocialFixture
    {
        private DialogueHistoryPanel history;
        private DialogueController dialogue;
        [UnitySetUp] public IEnumerator PrepareHistory()
        {
            history = Object.FindAnyObjectByType<DialogueHistoryPanel>(FindObjectsInactive.Include);
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            foreach (var component in new Component[] { history, dialogue })
            {
                var fields = new SerializedObject(component);
                fields.FindProperty("backendClient").objectReferenceValue = transports[merchant.EntityId];
                fields.ApplyModifiedPropertiesWithoutUndo();
            }
            history.enabled = true;
            yield return null;
        }
        private void ShowHistory(WorldEntity owner)
        {
            Assert.That(dialogue.SelectTarget(owner.GetComponent<DialogueParticipant>()), Is.True);
            if (!history.IsOpen) history.Toggle();
            Assert.That(history.IsOpen, Is.True);
        }
        private CognitiveResponse Reply(WorldEntity owner, string text)
        {
            var request = transports[owner.EntityId].Requests.Last();
            return new CognitiveResponse { message_type = "cognitive_response", schema_version = "1.0",
                request_id = request.request_id, trace_id = request.trace_id,
                run_id = request.run.run_id, agent_entity_id = owner.EntityId,
                decision = new CognitiveDecision { decision_kind = text == null ? CognitiveDecisionKinds.NoAction :
                    CognitiveDecisionKinds.DirectResponse, utterance = text } };
        }
        private void AcceptReply(WorldEntity owner, string text)
        {
            Assert.That(transports[owner.EntityId].Reply(Reply(owner, text)), Is.True);
            Assert.That(owner.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
            if (text != null)
                Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(owner.EntityId).Last().Text, Is.EqualTo(text));
        }
        private static int Count(string text, string marker) => text.Split(new[] { marker }, StringSplitOptions.None).Length - 1;

        [UnityTest] public IEnumerator T1_Wave_MerchantHistoryContainsActualReplyOnce()
        {
            player.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            yield return Perform("wave");
            var response = Reply(merchant, "这次挥手我看见啦。脚本甲");
            Assert.That(transports[merchant.EntityId].Reply(response), Is.True);
            Assert.That(transports[merchant.EntityId].Reply(response), Is.True); // Duplicate transport delivery is ignored by Driver.
            ShowHistory(merchant);
            Assert.That(Count(history.DisplayText, "表情[挥手]"), Is.EqualTo(1));
            Assert.That(Count(history.DisplayText, response.decision.utterance), Is.EqualTo(1));
            Assert.That(history.DisplayText.IndexOf("表情[挥手]"), Is.LessThan(history.DisplayText.IndexOf(response.decision.utterance)));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Last().Text, Is.EqualTo(response.decision.utterance));
            Bridge(merchant).SendMessage("HandleEmote", starts.Single());
            Assert.That(Count(history.DisplayText, "表情[挥手]"), Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator T1_GlobalDialogue_NpcSwitchKeepsAllMessagesAndNames()
        {
            var companion = observers.Single(b => b.Observer.EntityId == "npc_cafe_companion_01").Observer;
            companion.transform.position = origin + Vector3.right * 2;
            var owners = new[] { customer, merchant, companion };
            var names = new[] { "沈知意", "林薇薇", "温予安" };
            var sequences = new long[] { 1000, 1, 50 }; // These counters cannot order different NPCs.
            for (int index = 0; index < owners.Length; index++)
            {
                var owner = owners[index];
                player.transform.rotation = Quaternion.LookRotation(owner.transform.position - player.transform.position);
                owner.GetComponent<CognitiveDialogueSubmissionHandler>().Configure(owner,
                    owner.GetComponent<AgentOrchestrator>(), Object.FindAnyObjectByType<EmbodiedNPC.Events.AgentEventRouter>(),
                    transports[owner.EntityId]);
                ShowHistory(owner);
                string input = "全局玩家消息" + index, reply = "实际脚本回复" + index;
                Assert.That(Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.Input.TextUtteranceInput>().SubmitText(input), Is.True);
                AcceptReply(owner, reply);
                var page = new DialogueHistoryPage { observer_id = owner.EntityId, conversation_id = dialogue.CurrentConversationId,
                    next_after_seq = sequences[index] + 1, items = new[] {
                        new DialogueHistoryItem { event_id = "global_input_" + index, experience_seq = sequences[index], role = "player", text = input, timestamp = 100 },
                        new DialogueHistoryItem { event_id = "global_reply_" + index, experience_seq = sequences[index] + 1, role = "npc", text = reply, timestamp = 100 } } };
                Assert.That(history.AppendPage(page), Is.True);
                Assert.That(history.AppendPage(page), Is.False);
                Assert.That(history.DisplayText, Does.Contain(names[index]).And.Contain(input).And.Contain(reply));
            }
            string before = history.DisplayText;
            Assert.That(before.IndexOf("实际脚本回复0"), Is.LessThan(before.IndexOf("全局玩家消息1")));
            Assert.That(before.IndexOf("实际脚本回复1"), Is.LessThan(before.IndexOf("全局玩家消息2")));
            Assert.That(dialogue.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(history.IsOpen, Is.True);
            Assert.That(history.DisplayText, Is.EqualTo(before));
            history.Toggle(); history.Toggle();
            Assert.That(history.DisplayText, Is.EqualTo(before));
            foreach (var transport in transports.Values) Assert.That(transport.ConnectionAttempts, Is.Zero);
            yield return null;
        }
        [UnityTest] public IEnumerator T3_Expressions_NoActionCanceledOrUnshownRepliesNeverInventHistory()
        {
            yield return Perform("cheer");
            WorldEntity owner = observers.Single(b => b.Observer.EntityId == starts.Last().ResponderEntityId).Observer;
            AcceptReply(owner, "庆祝动作的实际回复丙");
            ShowHistory(owner);
            Assert.That(history.DisplayText, Does.Contain("表情[庆祝]").And.Contain("庆祝动作的实际回复丙"));
            dialogue.ClearSession(); owner.GetComponent<SpeechPresentationLifecycle>().Cancel(); emote.Cancel();
            yield return Wait(.15f);
            yield return Perform("proud");
            owner = observers.Single(b => b.Observer.EntityId == starts.Last().ResponderEntityId).Observer;
            AcceptReply(owner, null);
            ShowHistory(owner);
            Assert.That(Count(history.DisplayText, "表情[得意]"), Is.EqualTo(1));
            string before = history.DisplayText;
            dialogue.ClearSession(); emote.Cancel(); yield return Wait(.15f);
            yield return Perform("proud");
            owner = observers.Single(b => b.Observer.EntityId == starts.Last().ResponderEntityId).Observer;
            var canceled = Reply(owner, "取消后的旧回复不得出现");
            owner.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            Assert.That(owner.GetComponent<CognitiveRunDriver>().TryConsumeResponse(canceled), Is.False);
            ShowHistory(owner);
            Assert.That(history.DisplayText, Does.Not.Contain(canceled.decision.utterance));
            dialogue.ClearSession(); emote.Cancel(); yield return Wait(.15f);
            yield return Perform("proud");
            owner = observers.Single(b => b.Observer.EntityId == starts.Last().ResponderEntityId).Observer;
            owner.GetComponent<DialogueBubbleCognitivePresenter>().enabled = false;
            var unshown = Reply(owner, "未成功展示的回复不得出现");
            Assert.That(owner.GetComponent<CognitiveRunDriver>().TryConsumeResponse(unshown), Is.False);
            ShowHistory(owner);
            Assert.That(history.DisplayText, Does.Not.Contain(unshown.decision.utterance));
        }
        [UnityTest] public IEnumerator T2_GlobalEmote_ReopenRefreshAndHearingKeepOneInteraction()
        {
            player.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            yield return Perform("wave"); AcceptReply(merchant, "重开保留的真实回复丁");
            ShowHistory(merchant);
            var page = new DialogueHistoryPage { observer_id = merchant.EntityId, conversation_id = dialogue.CurrentConversationId,
                next_after_seq = 1, items = new[] { new DialogueHistoryItem { experience_seq = 1, role = "player", text = "普通文字记录", timestamp = 1 } } };
            Assert.That(history.AppendPage(page), Is.True);
            Assert.That(history.AppendPage(page), Is.False);
            string before = history.DisplayText;
            history.Toggle(); history.Toggle();
            Assert.That(history.DisplayText, Is.EqualTo(before));
            ShowHistory(customer); Assert.That(history.DisplayText, Is.EqualTo(before));
            ShowHistory(merchant); Assert.That(history.DisplayText, Is.EqualTo(before));
            var observed = new Observation("history_heard", customer.EntityId, ObservationSourceType.GameplayEvent,
                "history_speech", ObservationChannel.Audio, string.Empty,
                new System.Collections.Generic.Dictionary<string, string> { ["event_type"] = "heard_speech",
                    ["observer_entity_id"] = customer.EntityId, ["social_role"] = "bystander", ["text"] = "旁听不应成为额外聊天消息" },
                Array.Empty<string>(), 1f, world.WorldRevision, 1, 5, PerceptionLifecycleState.Detected,
                "history_speech", 1, traceId: "history_heard_trace");
            Assert.That(customer.GetComponent<PerceivedStateStore>().Apply(observed).IsApplied, Is.True);
            Assert.That(history.DisplayText, Is.EqualTo(before));
            Bridge(merchant).SendMessage("HandleEmote", starts.Single());
            Assert.That(history.AppendPage(page), Is.False);
            Assert.That(Count(history.DisplayText, "表情[挥手]"), Is.EqualTo(1));
            Assert.That(Count(history.DisplayText, "重开保留的真实回复丁"), Is.EqualTo(1));
            foreach (var transport in transports.Values) Assert.That(transport.ConnectionAttempts, Is.Zero);
        }
    }
}
