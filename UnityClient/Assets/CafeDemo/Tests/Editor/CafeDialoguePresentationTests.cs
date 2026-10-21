using System;
using System.Collections;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeDialoguePresentationTests
    {
        private const string ScenePath = "Assets/Scenes/CafeDemoV2.unity";
        private CafeDemoPlayerController player;
        private CafeDemoOrbitCamera orbit;
        private Keyboard keyboard;
        private InputSettings.BackgroundBehavior previousBackgroundBehavior;
        private InputSettings.EditorInputBehaviorInPlayMode previousEditorInputBehavior;
        private bool inputRoutingCaptured;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene(ScenePath);
            string endpoint = Environment.GetEnvironmentVariable("EMBODIEDNPC_TEST_BACKEND_URL");
            if (!string.IsNullOrWhiteSpace(endpoint))
                typeof(BackendWebSocketClient).GetField("serverUrl", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).SetValue(
                    Object.FindAnyObjectByType<BackendWebSocketClient>(), endpoint);
            yield return new EnterPlayMode();
            previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            previousEditorInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            inputRoutingCaptured = true;
            // Synthetic input must reach player updates even when the Editor owns focus.
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.ApplyStartupConfig(new ModelConfigView { configured = true });
            settings.Close();
            player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            orbit = Object.FindAnyObjectByType<CafeDemoOrbitCamera>();
            keyboard = InputSystem.AddDevice<Keyboard>("CafeDialoguePresentationKeyboard");
            yield return null;
            yield return null;
            Assert.That(settings.IsOpen, Is.False);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (InputDevice device in InputSystem.devices.Where(x => x.name.StartsWith("CafeDialoguePresentation")).ToArray())
                InputSystem.RemoveDevice(device);
            if (inputRoutingCaptured)
            {
                InputSystem.settings.backgroundBehavior = previousBackgroundBehavior;
                InputSystem.settings.editorInputBehaviorInPlayMode = previousEditorInputBehavior;
                inputRoutingCaptured = false;
            }
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator NpcPrompt_RebuildsAfterScriptReloadStateLoss_AndEStillOpensDialogue()
        {
            PlacePlayer(new Vector3(-0.8f, 0.02f, 1.25f));
            var interactor = player.GetComponent<PlayerDialogueInteractor>();
            yield return Wait(0.15f);
            var candidate = interactor.RefreshCandidate();
            Assert.That(candidate, Is.Not.Null);
            interactor.enabled = false;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            // Script reload restores Unity object references, but loses the plain C# presenter.
            typeof(PlayerDialogueInteractor).GetField("promptView", flags).SetValue(interactor, null);
            typeof(PlayerDialogueInteractor).GetField("currentCandidate", flags).SetValue(interactor, candidate);
            interactor.enabled = true;
            yield return null;
            Assert.That(typeof(PlayerDialogueInteractor).GetField("promptView", flags).GetValue(interactor), Is.Not.Null);
            Assert.That(interactor.IsPromptVisible, Is.True);
            yield return Tap(Key.E);
            Assert.That(Object.FindAnyObjectByType<DialogueController>().IsPresentationVisible, Is.True);
            Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>().IsVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator InputMode_EFocusesAndSuspendsGameplay_OneEscapeRestoresEverything()
        {
            PlacePlayer(new Vector3(-0.8f, 0.02f, 1.25f));
            PlayerDialogueInteractor interactor = player.GetComponent<PlayerDialogueInteractor>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            yield return Wait(0.15f);
            interactor.RefreshCandidate();
            Assert.That(interactor.IsPromptVisible, Is.True);
            Assert.That(interactor.PromptLabel, Does.StartWith("[E]"));

            yield return Tap(Key.E);
            yield return null;
            Assert.That(controller.HasActiveSession, Is.True);
            string conversationId = controller.CurrentConversationId;
            DialogueParticipant selectedTarget = controller.SelectedTarget;
            Assert.That(input.IsVisible, Is.True);
            Assert.That(input.IsInputFocused, Is.True);
            Assert.That(player.IsInputSuspended, Is.True);
            Assert.That(orbit.IsInputSuspended, Is.True);
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
            Assert.That(Cursor.visible, Is.True);

            Vector3 before = player.transform.position;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
            yield return Wait(0.2f);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null;
            Vector3 horizontal = Vector3.ProjectOnPlane(player.transform.position - before, Vector3.up);
            Assert.That(horizontal.magnitude, Is.LessThan(0.01f));

            yield return Tap(Key.Escape);
            yield return null;
            yield return null;
            Assert.That(controller.HasActiveSession, Is.True);
            Assert.That(controller.IsPresentationVisible, Is.False);
            Assert.That(controller.SelectedTarget, Is.SameAs(selectedTarget));
            Assert.That(controller.CurrentConversationId, Is.EqualTo(conversationId));
            Assert.That(input.IsVisible, Is.False);
            Assert.That(player.IsInputSuspended, Is.False);
            Assert.That(orbit.IsInputSuspended, Is.False);
            Assert.That(orbit.PointerCaptured, Is.True);
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
        }

        [UnityTest]
        public IEnumerator CancelButton_UsesExistingExitPathWithoutSubmission()
        {
            PlacePlayer(new Vector3(-0.8f, 0.02f, 1.25f));
            PlayerDialogueInteractor interactor = player.GetComponent<PlayerDialogueInteractor>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            CognitiveDialogueSubmissionHandler handler = DialogueHandler("npc_cafe_01");
            AgentEventReceiver receiver = handler.GetComponent<AgentEventReceiver>();
            CognitiveContextComposer composer = handler.GetComponent<CognitiveContextComposer>();
            BackendWebSocketClient backend = Object.FindAnyObjectByType<BackendWebSocketClient>();

            yield return Wait(0.15f);
            Assert.That(controller.SelectTarget(handler.GetComponent<DialogueParticipant>()), Is.True);
            yield return null;
            Assert.That(controller.HasActiveSession, Is.True);
            Assert.That(input.IsVisible, Is.True);

            AgentEvent previousEvent = receiver.LastEvent;
            CognitiveRequest previousRequest = composer.LastRequest;
            int cognitiveSends = backend.CognitiveRequestSendCount;
            Button cancel = GameObject.Find("CancelButton").GetComponent<Button>();
            Assert.That(cancel, Is.Not.Null);
            cancel.onClick.Invoke();
            yield return null;
            yield return null;

            Assert.That(controller.HasActiveSession, Is.True);
            Assert.That(controller.IsPresentationVisible, Is.False);
            Assert.That(input.IsVisible, Is.False);
            Assert.That(input.IsInputFocused, Is.False);
            Assert.That(player.IsInputSuspended, Is.False);
            Assert.That(orbit.IsInputSuspended, Is.False);
            Assert.That(orbit.PointerCaptured, Is.True);
            Assert.That(receiver.LastEvent, Is.SameAs(previousEvent));
            Assert.That(composer.LastRequest, Is.SameAs(previousRequest));
            Assert.That(backend.CognitiveRequestSendCount, Is.EqualTo(cognitiveSends));
        }

        [UnityTest]
        public IEnumerator BubbleHistory_ThreePerSpeaker_NoOverlap_AndMessageLifetime()
        {
            DialogueBubbleManager manager = Object.FindAnyObjectByType<DialogueBubbleManager>();
            DialogueBubbleAnchor[] anchors = Object.FindObjectsByType<DialogueBubbleAnchor>();
            Assert.That(manager.BubbleLifetimeSeconds, Is.EqualTo(15f));
            Vector3 sharedPoint = Camera.main.transform.position + Camera.main.transform.forward * 3f;
            foreach (DialogueBubbleAnchor anchor in anchors) anchor.AnchorPoint.position = sharedPoint;

            const double now = 10d;
            for (int index = 1; index <= 4; index++)
            {
                manager.PushMessage("player_01", "player " + index, DialogueSpeakerKind.Player, now);
                manager.PushMessage("npc_cafe_01", "merchant " + index, DialogueSpeakerKind.Npc, now);
            }
            manager.Refresh(now);

            CollectionAssert.AreEqual(new[] { "player 2", "player 3", "player 4" },
                manager.GetMessages("player_01").Select(x => x.Text));
            CollectionAssert.AreEqual(new[] { "merchant 2", "merchant 3", "merchant 4" },
                manager.GetMessages("npc_cafe_01").Select(x => x.Text));
            var rects = manager.GetVisibleScreenRects();
            Assert.That(rects.Count, Is.EqualTo(6));
            for (int left = 0; left < rects.Count; left++)
                for (int right = left + 1; right < rects.Count; right++)
                    Assert.That(rects[left].Overlaps(rects[right]), Is.False,
                        $"Bubble rects {left} and {right} overlap.");

            manager.Refresh(now + 15.01d);
            Assert.That(manager.GetMessages("player_01"), Is.Empty);
            Assert.That(manager.GetMessages("npc_cafe_01"), Is.Empty);
            yield return null;
        }

        [UnityTest]
        public IEnumerator BubbleHistory_SeparateSpeakers_KeepOwnHeight_OverlapOrdersAndSeparationRestores()
        {
            var manager = Object.FindAnyObjectByType<DialogueBubbleManager>();
            var anchors = Object.FindObjectsByType<DialogueBubbleAnchor>();
            orbit.enabled = false;
            // Independent head anchors let us exercise screen geometry without
            // teleporting gameplay entities or retaining their old animated head bones.
            var playerPoint = new GameObject("BubbleTestPlayerAnchor");
            var npcPoint = new GameObject("BubbleTestNpcAnchor");
            var playerAnchor = playerPoint.AddComponent<DialogueBubbleAnchor>();
            var npcAnchor = npcPoint.AddComponent<DialogueBubbleAnchor>();
            playerAnchor.Configure(anchors.Single(a => a.SpeakerEntityId == "player_01").GetComponent<WorldEntity>(),
                playerPoint.transform, DialogueSpeakerKind.Player);
            npcAnchor.Configure(anchors.Single(a => a.SpeakerEntityId == "npc_cafe_01").GetComponent<WorldEntity>(),
                npcPoint.transform, DialogueSpeakerKind.Npc);
            try
            {
                const double now = 10d;
                // Earlier player below later NPC is valid when rectangles do not overlap.
                // Repeat with the opposite speaker below, despite overlapping X ranges.
                foreach (bool playerBelow in new[] { true, false })
                {
                    manager.ClearMessages();
                    playerPoint.transform.position = Camera.main.ViewportToWorldPoint(new Vector3(.48f, playerBelow ? .25f : .8f, 3f));
                    npcPoint.transform.position = Camera.main.ViewportToWorldPoint(new Vector3(.52f, playerBelow ? .8f : .25f, 3f));
                    manager.PushMessage("player_01", "question", DialogueSpeakerKind.Player, now);
                    manager.PushMessage("npc_cafe_01", "answer", DialogueSpeakerKind.Npc, now);
                    manager.Refresh(now);
                    var natural = manager.GetVisibleScreenRects().ToArray();
                    Assert.That(natural.Length, Is.EqualTo(2));
                    Assert.That(natural[0].Overlaps(natural[1]), Is.False);
                    Assert.That(playerBelow ? natural[0].yMax < natural[1].yMin : natural[1].yMax < natural[0].yMin,
                        Is.True, "Different speakers must retain their natural screen heights.");

                    npcPoint.transform.position = playerPoint.transform.position;
                    manager.Refresh(now);
                    var colliding = manager.GetVisibleScreenRects();
                    Assert.That(colliding[0].yMin, Is.GreaterThanOrEqualTo(colliding[1].yMax),
                        "Actual overlap puts the older message above the newer one.");

                    npcPoint.transform.position = Camera.main.ViewportToWorldPoint(new Vector3(.52f, playerBelow ? .8f : .25f, 3f));
                    manager.Refresh(now);
                    var restored = manager.GetVisibleScreenRects();
                    for (int i = 0; i < 2; i++)
                        Assert.That(Vector2.Distance(restored[i].center, natural[i].center), Is.LessThan(.1f),
                            "Separating characters must remove the collision offset.");
                }
                foreach (float viewportY in new[] { .5f, .97f })
                {
                    manager.ClearMessages();
                    playerPoint.transform.position = npcPoint.transform.position =
                        Camera.main.ViewportToWorldPoint(new Vector3(.5f, viewportY, 3f));
                    for (int i = 0; i < 3; i++)
                    {
                        manager.PushMessage("player_01", "question " + i, DialogueSpeakerKind.Player, now);
                        manager.PushMessage("npc_cafe_01", "a longer answer with a different bubble height " + i, DialogueSpeakerKind.Npc, now);
                    }
                    manager.Refresh(now);
                    var rects = manager.GetVisibleScreenRects();
                    Assert.That(rects.Count, Is.EqualTo(6));
                    for (int i = 1; i < rects.Count; i++)
                        Assert.That(rects[i - 1].yMin, Is.GreaterThanOrEqualTo(rects[i].yMax - .1f),
                            "Colliding histories must stack in time order, including at the top edge.");
                }
            }
            finally
            {
                manager.ClearMessages();
                Object.DestroyImmediate(playerPoint);
                Object.DestroyImmediate(npcPoint);
                foreach (var anchor in anchors) manager.RegisterAnchor(anchor);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator BubbleHistory_AvoidsHeadsAtLowAndCloseCameraAngles()
        {
            PlacePlayer(new Vector3(0.24f, 0.02f, 3.35f));
            var manager = Object.FindAnyObjectByType<DialogueBubbleManager>();
            var anchors = Object.FindObjectsByType<DialogueBubbleAnchor>();
            var camera = Camera.main;
            orbit.enabled = false;
            var speakers = anchors.Where(a => a.SpeakerEntityId == "player_01" ||
                a.SpeakerEntityId == "npc_cafe_01").ToArray();
            Vector3 focus = speakers.Aggregate(Vector3.zero, (sum, a) => sum + a.AnchorPoint.position) / speakers.Length;
            foreach (Vector3 position in new[] { new Vector3(-0.3f, 0.65f, 2.5f),
                         new Vector3(-2f, 1.8f, 2f), new Vector3(-2f, 2.7f, 2f) })
            {
                camera.transform.position = position;
                camera.transform.LookAt(focus);
                // Preserve the angle while fitting both speakers in portrait and landscape Game Views.
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (speakers.All(a =>
                    {
                        Vector3 viewport = camera.WorldToViewportPoint(a.AnchorPoint.position);
                        return viewport.z > camera.nearClipPlane && viewport.x > .1f && viewport.x < .9f &&
                            viewport.y > .1f && viewport.y < .9f;
                    })) break;
                    camera.transform.position -= camera.transform.forward * .5f;
                }
                foreach (var speaker in speakers)
                {
                    Vector3 viewport = camera.WorldToViewportPoint(speaker.AnchorPoint.position);
                    Assert.That(viewport.z, Is.GreaterThan(camera.nearClipPlane));
                    Assert.That(viewport.x, Is.InRange(.1f, .9f));
                    Assert.That(viewport.y, Is.InRange(.1f, .9f));
                }
                manager.ClearMessages();
                manager.PushMessage("player_01", "What coffee do you recommend?", DialogueSpeakerKind.Player, 10d);
                manager.PushMessage("npc_cafe_01", "Try our fresh hand brewed coffee or a warm latte today.",
                    DialogueSpeakerKind.Npc, 10d);
                manager.Refresh(10d);
                var rects = manager.GetVisibleScreenRects();
                Assert.That(rects.Count, Is.EqualTo(2));
                foreach (DialogueBubbleView view in Object.FindObjectsByType<DialogueBubbleView>(FindObjectsSortMode.None))
                {
                    if (!manager.GetMessages(view.Message.SpeakerEntityId).Any(m => m.MessageId == view.Message.MessageId)) continue;
                    var owner = anchors.Single(a => a.SpeakerEntityId == view.Message.SpeakerEntityId);
                    Vector3 ownerScreen = camera.WorldToScreenPoint(owner.AnchorPoint.position);
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)manager.transform,
                        ownerScreen, null, out Vector2 ownerLocal);
                    Assert.That(view.ScreenRect.center.x, Is.EqualTo(ownerLocal.x).Within(0.1f),
                        "Bubble must remain horizontally centered above its own speaker.");
                    Assert.That(view.ScreenRect.yMin, Is.GreaterThan(ownerLocal.y));
                    Vector3 headScreen = camera.WorldToScreenPoint(owner.HeadPosition);
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)manager.transform,
                        headScreen, null, out Vector2 headLocal);
                    Assert.That(view.ScreenRect.Contains(headLocal), Is.False,
                        "A bubble must remain above its own character's head.");
                }
                Assert.That(rects[0].Overlaps(rects[1]), Is.False);
            }
            manager.ClearMessages();
            yield return null;
        }

        [UnityTest]
        public IEnumerator CafeMock_AcceptedPlayerAndNpcResponsesUseBubblePresenter()
        {
            BackendWebSocketClient backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            CognitiveDialogueSubmissionHandler handler = DialogueHandler("npc_cafe_01");
            DialogueBubbleManager manager = Object.FindAnyObjectByType<DialogueBubbleManager>();
            CognitiveRunDriver driver = handler.GetComponent<CognitiveRunDriver>();
            AgentOrchestrator orchestrator = handler.GetComponent<AgentOrchestrator>();
            TMP_Text status = GameObject.Find("DialogueStatusText").GetComponent<TMP_Text>();

            float connectionDeadline = Time.realtimeSinceStartup + 5f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < connectionDeadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            PlacePlayer(new Vector3(-0.8f, 0.02f, 1.25f));
            yield return Wait(0.1f);
            float setupDeadline = Time.realtimeSinceStartup + 5f;
            while (orchestrator.HasActiveRun && Time.realtimeSinceStartup < setupDeadline) yield return null;
            Assert.That(controller.SelectTarget(handler.GetComponent<DialogueParticipant>()), Is.True);

            Assert.That(input.SubmitText("你好"), Is.True);
            Assert.That(status.text, Does.Not.Contain("COGNITIVE_REQUEST_SUBMITTED"));
            Assert.That(manager.GetMessages("player_01").Single().Text, Is.EqualTo("你好"));
            float directDeadline = Time.realtimeSinceStartup + 5f;
            while (manager.GetMessages("npc_cafe_01").Count == 0 && Time.realtimeSinceStartup < directDeadline)
                yield return null;
            Assert.That(manager.GetMessages("npc_cafe_01").Last().Text, Is.Not.Empty);
            Assert.That(driver.Presenter, Is.TypeOf<DialogueBubbleCognitivePresenter>());
            while (orchestrator.HasActiveRun && Time.realtimeSinceStartup < directDeadline) yield return null;
            Assert.That(orchestrator.HasActiveRun, Is.False, "Direct response run must finish before the action utterance.");

            int npcBeforeAction = manager.GetMessages("npc_cafe_01").Count;
            Assert.That(input.SubmitText("把这个杯子拿给我"), Is.True);
            Assert.That(manager.GetMessages("player_01").Last().Text, Is.EqualTo("把这个杯子拿给我"),
                "The action utterance must cross the same accepted-submission presentation boundary.");
            float actionDeadline = Time.realtimeSinceStartup + 10f;
            while ((manager.GetMessages("npc_cafe_01").Count == npcBeforeAction ||
                    driver.LastValidatedGoalProposal == null) && Time.realtimeSinceStartup < actionDeadline)
                yield return null;
            Assert.That(driver.LastValidatedGoalProposal, Is.Not.Null);
            Assert.That(driver.LastValidatedGoalProposal.type, Is.EqualTo("bring_object"));
            Assert.That(manager.GetMessages("npc_cafe_01").Last().Text, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator DialogueUi_MouseClicksStayInside_OutsideRestoresGameplay()
        {
            PlacePlayer(new Vector3(-0.8f, 0.02f, 1.25f));
            yield return Wait(.15f);
            yield return Tap(Key.E);
            var controller = Object.FindAnyObjectByType<DialogueController>();
            var mode = Object.FindAnyObjectByType<DialogueInputModeController>();
            var history = Object.FindAnyObjectByType<DialogueHistoryPanel>();
            Assert.That(controller.HasActiveSession, Is.True);
            var mouse = InputSystem.AddDevice<Mouse>("CafeDialoguePresentationMouse");
            float yaw = orbit.Yaw, pitch = orbit.Pitch;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(400, 300), delta = new Vector2(180, 90) });
            yield return null;
            Assert.That(orbit.Yaw, Is.EqualTo(yaw));
            Assert.That(orbit.Pitch, Is.EqualTo(pitch));
            Assert.That(Cursor.visible, Is.True);
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
            yield return Click(mouse, Centre(GameObject.Find("TMP_InputField")));
            Assert.That(controller.HasActiveSession, Is.True);
            Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>().IsInputFocused, Is.True);
            yield return Click(mouse, Centre(GameObject.Find("ChatHistoryButton")));
            Assert.That(history.IsOpen, Is.True);
            GameObject panel = GameObject.Find("ChatHistoryPanel");
            Assert.That(Centre(panel).y, Is.GreaterThan(Centre(GameObject.Find("TMP_InputField")).y));
            Assert.That(Centre(panel).x, Is.LessThan(Screen.width * .4f));
            yield return Click(mouse, Centre(panel));
            Assert.That(controller.HasActiveSession, Is.True);
            yield return Click(mouse, Centre(GameObject.Find("CloseHistory")));
            Assert.That(history.IsOpen, Is.False);
            yield return Click(mouse, Centre(GameObject.Find("ChatHistoryButton")));
            Assert.That(history.IsOpen, Is.True);
            Vector2 outside = new Vector2(Screen.width * .9f, Screen.height * .8f);
            Assert.That(mode.IsOverDialogueUi(outside), Is.False);
            yield return Click(mouse, outside);
            yield return null;
            string conversationId = controller.CurrentConversationId;
            Assert.That(controller.HasActiveSession, Is.True);
            Assert.That(controller.IsPresentationVisible, Is.False);
            Assert.That(history.IsOpen, Is.False);
            Assert.That(player.IsInputSuspended, Is.False);
            Assert.That(orbit.IsInputSuspended, Is.False);
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
            Assert.That(Cursor.visible, Is.False);
            Vector3 before = player.transform.position;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
            yield return Wait(.15f);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            Assert.That(Vector3.Distance(before, player.transform.position), Is.GreaterThan(.02f));
            yield return Tap(Key.E);
            Assert.That(controller.HasActiveSession, Is.True);
            Assert.That(controller.IsPresentationVisible, Is.True);
            Assert.That(controller.CurrentConversationId, Is.EqualTo(conversationId));
            Assert.That(history.IsOpen, Is.False);
        }

        [UnityTest]
        public IEnumerator DialogueHistory_ScrollPreservesReadingPosition_RejectsStaleScope()
        {
            PlacePlayer(new Vector3(-0.8f, 0.02f, 1.25f));
            yield return Wait(.15f);
            yield return Tap(Key.E);
            var controller = Object.FindAnyObjectByType<DialogueController>();
            var history = Object.FindAnyObjectByType<DialogueHistoryPanel>();
            history.Toggle();
            yield return Wait(.6f);
            var scroll = GameObject.Find("ChatHistoryPanel").GetComponentInChildren<ScrollRect>();
            var mode = Object.FindAnyObjectByType<DialogueInputModeController>();
            var page = new DialogueHistoryPage {
                observer_id = controller.SelectedTarget.Entity.EntityId,
                conversation_id = controller.CurrentConversationId,
                items = Enumerable.Range(1, 30).Select(i => new DialogueHistoryItem {
                    experience_seq = i, role = "player", text = "只读记录 " + i + " <b>literal</b>"
                }).ToArray(), next_after_seq = 30
            };
            Assert.That(history.AppendPage(page), Is.True);
            Assert.That(scroll.verticalNormalizedPosition, Is.LessThan(.02f));
            Assert.That(scroll.content.GetComponent<TMP_Text>().richText, Is.False);
            var mouse = InputSystem.AddDevice<Mouse>("CafeDialoguePresentationScrollMouse");
            InputSystem.QueueStateEvent(mouse, new MouseState { position = Centre(scroll.viewport.gameObject), scroll = new Vector2(0, 120) });
            yield return null;
            yield return null;
            Assert.That(scroll.verticalNormalizedPosition, Is.GreaterThan(.02f));
            Assert.That(controller.HasActiveSession, Is.True);
            Assert.That(mode.IsOverDialogueUi(Centre(scroll.verticalScrollbar.gameObject)), Is.True);
            yield return Click(mouse, Centre(scroll.verticalScrollbar.gameObject));
            Assert.That(controller.HasActiveSession, Is.True);
            scroll.StopMovement();
            scroll.verticalNormalizedPosition = .8f;
            float previous = scroll.content.anchoredPosition.y;
            page.items = new[] { new DialogueHistoryItem { experience_seq = 31, role = "npc", text = "new reply" } };
            page.next_after_seq = 31;
            Assert.That(history.AppendPage(page), Is.True);
            Assert.That(scroll.content.anchoredPosition.y, Is.EqualTo(previous).Within(.5f));
            scroll.verticalNormalizedPosition = 0;
            page.items[0].experience_seq = 32;
            page.next_after_seq = 32;
            Assert.That(history.AppendPage(page), Is.True);
            Assert.That(scroll.verticalNormalizedPosition, Is.LessThan(.02f));
            page.observer_id = "other_npc";
            Assert.That(history.AppendPage(page), Is.False);
            controller.ClearSession();
            Assert.That(history.IsOpen, Is.False);
            Assert.That(history.DisplayText, Is.Empty);
            Assert.That(history.AppendPage(page), Is.False);
        }

        [UnityTest]
        public IEnumerator DialogueHistory_HttpLedger_SendWhileOpen_RetainsVisitedNpcsAndConversations()
        {
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            float deadline = Time.realtimeSinceStartup + 8;
            while (!backend.IsConnected && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            var controller = Object.FindAnyObjectByType<DialogueController>();
            var history = Object.FindAnyObjectByType<DialogueHistoryPanel>();
            var field = GameObject.Find("TMP_InputField").GetComponent<TMP_InputField>();
            var mouse = InputSystem.AddDevice<Mouse>("CafeDialoguePresentationHistoryMouse");
            var participants = Object.FindObjectsByType<CognitiveDialogueSubmissionHandler>()
                .OrderBy(x => x.GetComponent<WorldEntity>().EntityId).ToArray();
            Assert.That(participants.Length, Is.EqualTo(4));
            string previousMarker = "unused";
            foreach (var handler in participants)
            {
                PlacePlayer(handler.transform.position + Vector3.forward);
                yield return null;
                Assert.That(controller.SelectTarget(handler.GetComponent<DialogueParticipant>()), Is.True);
                // Let TMP complete deferred focus and let any scene-start greeting finish.
                yield return null;
                yield return null;
                deadline = Time.realtimeSinceStartup + 8;
                while (handler.GetComponent<AgentOrchestrator>().HasActiveRun &&
                       Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(handler.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
                history.Toggle();
                Assert.That(history.IsOpen, Is.True);
                string marker = "你好 " + handler.GetComponent<WorldEntity>().EntityId;
                field.text = marker;
                yield return Click(mouse, Centre(GameObject.Find("SendButton")));
                Assert.That(handler.LastAcceptedTurn, Is.Not.Null, "Send button must accept the input before reading history.");
                Assert.That(handler.LastAcceptedTurn.Text, Is.EqualTo(marker));
                Assert.That(controller.HasActiveSession, Is.True);
                Assert.That(history.IsOpen, Is.True);
                deadline = Time.realtimeSinceStartup + 10;
                while ((!history.DisplayText.Contains(marker) || !history.DisplayText.Contains("NPC: ")) &&
                       Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(history.IsOpen, Is.True, "The history drawer must remain open after sending.");
                Assert.That(history.DisplayText, Does.Contain(marker),
                    GameObject.Find("ChatHistoryPanel")?.transform.Find("Status")?.GetComponent<TMP_Text>()?.text);
                Assert.That(history.DisplayText, Does.Contain("NPC: "));
                if (previousMarker != "unused") Assert.That(history.DisplayText, Does.Not.Contain(previousMarker), "The drawer displays only the current NPC's history.");
                previousMarker = marker;
                controller.ClearSession();
                Assert.That(history.IsOpen, Is.False);
            }
            var last = participants.Last().GetComponent<DialogueParticipant>();
            Assert.That(controller.SelectTarget(last), Is.True);
            history.Toggle();
            yield return Wait(1f);
            Assert.That(history.DisplayText, Does.Contain(previousMarker), "A new conversation keeps visited NPC history.");
        }

        [UnityTest]
        public IEnumerator DialogueHistory_ReopenPreservesNpcScopes_RejectsUnvisitedScopeAndDuplicatePages()
        {
            var controller = Object.FindAnyObjectByType<DialogueController>();
            var history = Object.FindAnyObjectByType<DialogueHistoryPanel>();
            var participants = Object.FindObjectsByType<DialogueParticipant>().OrderBy(x => x.Entity.EntityId).ToArray();
            Assert.That(participants.Length, Is.EqualTo(4));
            for (int i = 0; i < participants.Length; i++)
            {
                PlacePlayer(participants[i].transform.position + Vector3.forward);
                yield return null;
                Assert.That(controller.SelectTarget(participants[i]), Is.True);
                history.Toggle();
                var page = new DialogueHistoryPage { observer_id = participants[i].Entity.EntityId,
                    conversation_id = controller.CurrentConversationId, next_after_seq = 100000+i,
                    items = new[] { new DialogueHistoryItem { experience_seq = 100000+i, timestamp = i,
                        role = "npc", text = "保留记录 " + i + " <b>literal</b>" } } };
                Assert.That(history.AppendPage(page), Is.True);
                Assert.That(history.AppendPage(page), Is.False, "A repeated page must not duplicate its messages.");
                page.observer_id = "never_visited";
                Assert.That(history.AppendPage(page), Is.False);
                controller.ClearSession();
            }
            PlacePlayer(participants[1].transform.position + Vector3.forward);
            yield return null;
            Assert.That(controller.SelectTarget(participants[1]), Is.True);
            history.Toggle();
            foreach (var participant in participants)
            {
                string name = CharacterProfileResolver.Resolve(participant.GetComponent<CognitiveContextComposer>().CharacterDefinition,
                    CharacterOverrideStore.Shared).display_name;
                Assert.That(history.DisplayText.Contains(name), Is.EqualTo(participant == participants[1]));
            }
            Assert.That(history.DisplayText, Does.Not.Contain("保留记录 0").And.Contain("保留记录 1"));
            Assert.That(GameObject.Find("ChatHistoryPanel").GetComponentsInChildren<TMP_Text>().Where(x => x.name == "Message").All(x => !x.richText), Is.True);
            var field = GameObject.Find("TMP_InputField").GetComponent<TMP_InputField>();
            Assert.That(((TMP_Text)field.placeholder).text, Is.EqualTo(SettingsUiLanguage.T("输入聊天信息", "Enter a message")));
            history.Toggle(); history.Toggle();
            Assert.That(history.DisplayText, Does.Not.Contain("保留记录 0").And.Contain("保留记录 1"));
        }

        private static Vector2 Centre(GameObject go)
        {
            Canvas.ForceUpdateCanvases();
            var rect = (RectTransform)go.transform;
            return RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
        }

        private static IEnumerator Click(Mouse mouse, Vector2 position)
        {
            InputSystem.QueueStateEvent(mouse, new MouseState { position = position });
            yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = position }.WithButton(MouseButton.Left));
            yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = position });
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator CafeSelection_PrefersCameraCenterWithinRange_ThenFallsBackToDistance()
        {
            var controller = Object.FindAnyObjectByType<DialogueController>();
            controller.ClearSession();
            orbit.enabled = false;
            PlacePlayer(Vector3.zero);
            Camera.main.transform.SetPositionAndRotation(new Vector3(0, 1, -2), Quaternion.identity);
            var merchant = DialogueHandler("npc_cafe_01").transform;
            var regular = DialogueHandler("npc_cafe_regular_01").transform;
            merchant.position = new Vector3(0, 0, 3);
            regular.position = new Vector3(1, 0, 1);
            var interactor = player.GetComponent<PlayerDialogueInteractor>();
            Assert.That(interactor.RefreshCandidate().Entity.EntityId, Is.EqualTo("npc_cafe_01"),
                "Centered Merchant must win over a nearer off-center customer.");
            regular.position = new Vector3(0, 0, 1);
            Assert.That(interactor.RefreshCandidate().Entity.EntityId, Is.EqualTo("npc_cafe_regular_01"));
            regular.position = new Vector3(1, 0, 1);
            merchant.position = new Vector3(0, 0, 8);
            Assert.That(interactor.RefreshCandidate().Entity.EntityId, Is.EqualTo("npc_cafe_regular_01"),
                "Center priority must not bypass the dialogue distance.");
            regular.position = new Vector3(0, 0, -3);
            Assert.That(interactor.RefreshCandidate(), Is.Null, "NPCs behind the camera cannot be selected.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator RegularCustomer_HasVisibleIdleModelBesideSofa_AndCanBeSelected()
        {
            var regular = DialogueHandler("npc_cafe_regular_01");
            var visual = regular.transform.Find("Visual");
            Assert.That(visual, Is.Not.Null);
            var animator = visual.GetComponentInChildren<Animator>();
            Assert.That(animator.isHuman, Is.True);
            Assert.That(animator.avatar.isValid, Is.True);
            Assert.That(animator.runtimeAnimatorController, Is.Not.Null);
            Assert.That(animator.applyRootMotion, Is.False);
            Assert.That(visual.GetComponentsInChildren<Renderer>().All(r => r.enabled), Is.True);
            Assert.That(visual.GetComponentsInChildren<SkinnedMeshRenderer>(), Is.Not.Empty);
            Assert.That(visual.GetComponentsInChildren<Collider>(), Is.Empty);
            Assert.That(Vector3.Distance(regular.transform.position, GameObject.Find("Sofa_01").transform.position),
                Is.LessThan(2.2f));
            PlacePlayer(regular.transform.position + Vector3.left * 1.5f);
            orbit.enabled = false;
            Camera.main.transform.SetPositionAndRotation(player.transform.position + Vector3.up * 1.3f,
                Quaternion.LookRotation(regular.transform.position + Vector3.up -
                                       (player.transform.position + Vector3.up * 1.3f)));
            var interactor = player.GetComponent<PlayerDialogueInteractor>();
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            Assert.That(Object.FindAnyObjectByType<DialogueController>().SelectedTarget.Entity.EntityId,
                Is.EqualTo("npc_cafe_regular_01"));
            yield return Wait(.15f);
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.GreaterThan(0));
        }

        private void PlacePlayer(Vector3 position)
        {
            player.Controller.enabled = false;
            player.transform.position = position;
            player.Controller.enabled = true;
            Physics.SyncTransforms();
        }

        private static CognitiveDialogueSubmissionHandler DialogueHandler(string entityId) =>
            Object.FindObjectsByType<CognitiveDialogueSubmissionHandler>(FindObjectsInactive.Exclude)
                .Single(value => value.GetComponent<WorldEntity>().EntityId == entityId);

        private IEnumerator Tap(Key key)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
            yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null;
        }

        private static IEnumerator Wait(float seconds)
        {
            float deadline = Time.time + seconds;
            while (Time.time < deadline) yield return null;
        }
    }
}
