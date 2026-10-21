using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class TextUtteranceInputTests
    {
        private GameObject gameObject;
        private TextUtteranceInput input;

        [SetUp]
        public void SetUp()
        {
            gameObject = new GameObject("TextUtteranceInputTests");
            input = gameObject.AddComponent<TextUtteranceInput>();
            input.BeginInput(new DialogueContext
            {
                conversationId = "player_01:npc_ybot_01",
                speakerEntityId = "player_01",
                targetAgentId = "npc_ybot_01",
            });
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(gameObject);

        [Test]
        public void SubmitText_EmitsOneNormalizedTextUtterance()
        {
            UserUtterance received = null;
            int eventCount = 0;
            input.Submitted += utterance => { received = utterance; eventCount++; };

            bool accepted = input.SubmitText("  帮我把书给我。  ");

            Assert.That(accepted, Is.True);
            Assert.That(eventCount, Is.EqualTo(1));
            Assert.That(received.text, Is.EqualTo("帮我把书给我。"));
            Assert.That(received.inputMode, Is.EqualTo("text"));
            Assert.That(received.speakerEntityId, Is.EqualTo("player_01"));
            Assert.That(received.targetAgentId, Is.EqualTo("npc_ybot_01"));
        }

        [Test]
        public void SubmitText_RejectsBlankTextWithoutEmitting()
        {
            int eventCount = 0;
            input.Submitted += _ => eventCount++;

            Assert.That(input.SubmitText("   "), Is.False);
            Assert.That(eventCount, Is.Zero);
        }

        [Test]
        public void SubmitText_WhenTransportUnavailable_DoesNotEmit()
        {
            int eventCount = 0;
            input.Submitted += _ => eventCount++;
            input.SetSubmissionReady(false, "offline");

            Assert.That(input.SubmitText("请保留这句话"), Is.False);
            Assert.That(eventCount, Is.Zero);
            Assert.That(input.HasActiveContext, Is.True);
        }

        [Test]
        public void CancelInput_NotifiesMockListener()
        {
            int eventCount = 0;
            input.Cancelled += () => eventCount++;

            input.CancelInput();

            Assert.That(eventCount, Is.EqualTo(1));
        }

        [Test]
        public void HideInput_PreservesBoundContextUntilEndInput()
        {
            input.ShowInput();
            Assert.That(input.IsVisible, Is.True);

            input.HideInput();
            Assert.That(input.IsVisible, Is.False);
            Assert.That(input.HasActiveContext, Is.True);

            input.ShowInput();
            Assert.That(input.IsVisible, Is.True);
            input.EndInput();
            Assert.That(input.HasActiveContext, Is.False);
            Assert.That(input.IsVisible, Is.False);
        }
    }
}
