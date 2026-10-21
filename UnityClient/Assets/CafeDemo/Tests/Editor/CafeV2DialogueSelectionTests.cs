using System.Collections;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeV2DialogueSelectionTests
    {
        private DialogueController dialogue;
        private PlayerDialogueInteractor interactor;
        private DialogueParticipant customerA;
        private DialogueParticipant customerB;
        private Camera camera;

        [UnitySetUp]
        public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            Object.FindAnyObjectByType<BackendWebSocketClient>().enabled = false;
            Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled = false;
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().enabled = false;
            Object.FindAnyObjectByType<CafeDemoOrbitCamera>().enabled = false;
            yield return new EnterPlayMode();
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            panel.ApplyStartupConfig(new ModelConfigView { configured = true });
            panel.Close();
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            interactor = Object.FindAnyObjectByType<PlayerDialogueInteractor>();
            var player = interactor.GetComponent<CafeDemoPlayerController>();
            player.enabled = false;
            player.Controller.enabled = false;
            player.transform.position = new Vector3(-3.68f, .03f, 1.6f);
            player.Controller.enabled = true;
            var participants = Object.FindObjectsByType<DialogueParticipant>();
            customerA = participants.Single(x => x.Entity.EntityId == "npc_cafe_regular_01");
            customerB = participants.Single(x => x.Entity.EntityId == "npc_cafe_companion_01");
            camera = Camera.main;
            camera.transform.position = new Vector3(-3.68f, 1.6f, 2.7f);
            dialogue.ClearSession();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Close()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private void AimAt(DialogueParticipant target)
        {
            camera.transform.LookAt(target.transform.position + Vector3.up);
            foreach (var other in new[] { customerA, customerB })
            {
                Assert.That(dialogue.CheckEligibility(other).IsEligible, Is.True);
                var viewport = camera.WorldToViewportPoint(other.transform.position + Vector3.up);
                Assert.That(viewport.z, Is.GreaterThan(0));
                Assert.That(viewport.x, Is.InRange(0, 1));
                Assert.That(viewport.y, Is.InRange(0, 1));
            }
        }

        [UnityTest]
        public IEnumerator BeforeAnySession_CameraAimSwitchesBetweenBothVisibleSeatedCustomers()
        {
            AimAt(customerA);
            Assert.That(interactor.RefreshCandidate(), Is.SameAs(customerA));
            AimAt(customerB);
            Assert.That(interactor.RefreshCandidate(), Is.SameAs(customerB));
            AimAt(customerA);
            Assert.That(interactor.RefreshCandidate(), Is.SameAs(customerA));
            yield return null;
        }

        [UnityTest]
        public IEnumerator HiddenSession_CameraAimSwitchesPrompt_AndECommitsNewTarget()
        {
            AimAt(customerA);
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            string originalConversation = dialogue.CurrentConversationId;
            interactor.EndDialogue();
            Assert.That(dialogue.HasActiveSession, Is.True);
            Assert.That(dialogue.IsPresentationVisible, Is.False);

            AimAt(customerB);
            Assert.That(interactor.RefreshCandidate(), Is.SameAs(customerB),
                "A retained hidden conversation must not pin the screen-center prompt to A.");
            Assert.That(interactor.PromptLabel, Does.Contain("温予安"));
            Assert.That(dialogue.SelectedTarget, Is.SameAs(customerA), "Aiming alone must not switch the real session.");
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(originalConversation));
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            Assert.That(dialogue.SelectedTarget, Is.SameAs(customerB));
            Assert.That(dialogue.CurrentConversationId, Is.Not.EqualTo(originalConversation));
            yield return null;
        }

        [UnityTest]
        public IEnumerator HiddenSession_AimingAtSameNpcResumesOriginalConversation()
        {
            AimAt(customerA);
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            string originalConversation = dialogue.CurrentConversationId;
            interactor.EndDialogue();
            AimAt(customerA);
            Assert.That(interactor.RefreshCandidate(), Is.SameAs(customerA));
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(originalConversation));
            Assert.That(dialogue.IsPresentationVisible, Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator LegacyDistanceMode_PreservesRetainedConversationPreference()
        {
            var serialized = new SerializedObject(interactor);
            serialized.FindProperty("preferScreenCenter").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(dialogue.SelectTarget(customerA), Is.True);
            interactor.EndDialogue();
            AimAt(customerB);
            Assert.That(interactor.RefreshCandidate(), Is.SameAs(customerA));
            yield return null;
        }
    }
}
