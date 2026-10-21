using System.Collections;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Validation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeDialogueBoundaryTests : PlayerPresentationFixture
    {
        [UnityTest]
        public IEnumerator LeavingRange_HidesPresentationAndRetainsConversation()
        {
            ControlledPerception();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            var interactor = player.GetComponent<PlayerDialogueInteractor>();
            var input = Object.FindAnyObjectByType<TextUtteranceInput>();
            var npc = merchant.GetComponent<DialogueParticipant>();
            Place(new Vector3(-.8f, .02f, 1.25f));
            Assert.That(dialogue.SelectTarget(npc), Is.True);
            string conversation = dialogue.CurrentConversationId;
            Assert.That(player.IsInputSuspended, Is.True);
            Place(merchant.transform.position + Vector3.right * 20f);
            interactor.RefreshCandidate();
            yield return null;
            Assert.That(dialogue.HasActiveSession, Is.True);
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            Assert.That(input.IsVisible, Is.False);
            Assert.That(player.IsInputSuspended, Is.False);
            Place(new Vector3(-.8f, .02f, 1.25f));
            Assert.That(dialogue.SelectTarget(npc), Is.True);
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
            // Hiding retains the session; explicitly selecting an ineligible target clears it.
            Place(merchant.transform.position + Vector3.right * 20f);
            Assert.That(dialogue.SelectTarget(npc), Is.False);
            Assert.That(dialogue.LastEligibility.FailureCode,
                Is.EqualTo(DialogueEligibilityFailureCode.TargetOutOfDialogueRange));
            Assert.That(dialogue.HasActiveSession, Is.False);
        }

        [UnityTest]
        public IEnumerator TransportStatusLoss_RotatesConversationOnceAndPreservesLocalSession()
        {
            ControlledPerception();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            var input = Object.FindAnyObjectByType<TextUtteranceInput>();
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            Place(new Vector3(-.8f, .02f, 1.25f));
            backend.SendMessage("SetStatus", "CONNECTED");
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            string initial = dialogue.CurrentConversationId;
            backend.SendMessage("SetStatus", "DISCONNECTED");
            yield return null;
            string rotated = dialogue.CurrentConversationId;
            Assert.That(dialogue.HasActiveSession, Is.True);
            Assert.That(input.IsVisible, Is.True);
            Assert.That(input.IsSubmissionReady, Is.False);
            Assert.That(rotated, Is.Not.EqualTo(initial));
            backend.SendMessage("SetStatus", "RETRY_WAIT");
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(rotated));
            backend.SendMessage("SetStatus", "CONNECTED");
            Assert.That(dialogue.HasActiveSession, Is.True);
            Assert.That(input.IsSubmissionReady, Is.True);
        }
    }
}
