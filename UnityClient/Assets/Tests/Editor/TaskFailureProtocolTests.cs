using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Cognition;
using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.TestTools;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class TaskFailureProtocolTests
    {
        private double now;

        [Test]
        public void FailureCharacter_UsesOwnEffectiveProfileAndKeepsOptionalWireCompatibility()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "failure-character-" + System.Guid.NewGuid() + ".json");
            var root = new GameObject("FailureCharacter_Test");
            root.SetActive(false);
            var definition = ScriptableObject.CreateInstance<CharacterDefinition>();
            var otherDefinition = ScriptableObject.CreateInstance<CharacterDefinition>();
            try
            {
                definition.Configure("test.owner", "原名", "店员", "背景", "开朗", "轻快", "不造假");
                otherDefinition.Configure("test.other", "另一个人", "顾客", "背景", "安静", "简短", "不造假");
                var store = new CharacterOverrideStore(path);
                store.Save("test.owner", "本人的新名字", "店员", "背景", "直率", "直接", "不造假");
                store.Save("test.other", "别人的名字", "顾客", "背景", "安静", "简短", "不造假");
                var composer = root.AddComponent<CognitiveContextComposer>();
                composer.Configure(null, definition, null, null, null, null, null, null, overrides: store);
                var report = CreateReport();
                Assert.That(report.IsValid, Is.True); // Old senders may omit character.
                report.character = composer.CaptureCharacterProfile();
                var wire = report.Serialize();
                Assert.That(Newtonsoft.Json.Linq.JObject.Parse(wire)["failed_goal"]["drink_type"].Type,
                    Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.Null));
                Assert.That(wire, Does.Not.Contain("IsValid"));
                var roundTrip = JsonUtility.FromJson<TaskFailureReport>(wire);
                Assert.That(roundTrip.IsValid, Is.True);
                Assert.That(roundTrip.character.character_id, Is.EqualTo("test.owner"));
                Assert.That(roundTrip.character.display_name, Is.EqualTo("本人的新名字"));
                Assert.That(wire, Does.Not.Contain("别人的名字"));
                report.character.personality = new string('长', CharacterDefinition.PersonalityLimit + 1);
                Assert.That(report.IsValid, Is.False);
                Assert.That(composer.CaptureCharacterProfile().personality, Is.EqualTo("直率"));
                composer.Configure(null, otherDefinition, null, null, null, null, null, null, overrides: store);
                Assert.That(composer.CaptureCharacterProfile().display_name, Is.EqualTo("别人的名字"));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(definition);
                Object.DestroyImmediate(otherDefinition);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }

        [Test]
        public void Disconnect_InvalidatesOwnedTicketAndUnblocksNextGoal()
        {
            GameObject root = new("DisconnectedFailureTicket_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runner = CreatePendingRunner(root, backend, "failure-request-1");
                TaskFailureResponseInbox inbox = GetInbox(backend);
                Assert.That(inbox.Register(CreateReport()), Is.True);
                Assert.That(runner.HasActiveTask, Is.True);

                backend.ClearPendingRequests();

                Assert.That(inbox.PendingCount, Is.Zero);
                Assert.That(PendingRequestId(runner), Is.Null);
                Assert.That(runner.HasActiveTask, Is.False);
                Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4, out _, out string code), Is.False);
                Assert.That(code, Is.EqualTo("REQUEST_NOT_PENDING"));
                string rejection = null;
                runner.PresentationReady += value => rejection = value.Code;
                LogAssert.Expect(LogType.Warning, new Regex(@"\[GoalPlan\] REJECTED code="));
                runner.TrySubmitGoal(NewGoal(), "trace_new", "proposal_new", 0, 0);
                Assert.That(rejection, Is.Not.EqualTo("PLAN_ALREADY_EXECUTING"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ExpiredTicket_ReleasesRunnerOwnership()
        {
            GameObject root = new("ExpiredFailureTicket_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runner = CreatePendingRunner(root, backend, "failure-request-1");
                TaskFailureResponseInbox inbox = GetInbox(backend);
                Assert.That(inbox.Register(CreateReport(), 0.01d), Is.True);
                System.Threading.Thread.Sleep(30);

                Assert.That(backend.PendingRequestCount, Is.Zero);
                Assert.That(PendingRequestId(runner), Is.Null);
                Assert.That(runner.HasActiveTask, Is.False);
                Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4, out _, out string code), Is.False);
                Assert.That(code, Is.EqualTo("REQUEST_NOT_PENDING"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void InvalidatingOtherTicket_LeavesOnlyItsOwningRunnerReleased()
        {
            GameObject first = new("FirstFailureOwner_Test");
            GameObject second = new("SecondFailureOwner_Test");
            first.SetActive(false);
            second.SetActive(false);
            try
            {
                BackendWebSocketClient backend = first.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runnerA = CreatePendingRunner(first, backend, "failure-request-1");
                GoalPlanRunner runnerB = CreatePendingRunner(second, backend, "failure-request-2");
                TaskFailureResponseInbox inbox = GetInbox(backend);
                TaskFailureReport other = CreateReport();
                other.request_id = "failure-request-2";
                Assert.That(inbox.Register(CreateReport()), Is.True);
                Assert.That(inbox.Register(other), Is.True);

                backend.CancelPendingFailureRequest(other.request_id);

                Assert.That(PendingRequestId(runnerA), Is.EqualTo("failure-request-1"));
                Assert.That(runnerA.HasActiveTask, Is.True);
                Assert.That(PendingRequestId(runnerB), Is.Null);
                Assert.That(runnerB.HasActiveTask, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(first);
            }
        }

        [Test]
        public void ValidatedResponse_ForOtherRequestCannotConsumeCurrentOwnership()
        {
            GameObject root = new("OtherValidatedFailure_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runner = CreatePendingRunner(root, backend, "failure-request-2");
                TaskFailureResponseInbox inbox = new(() => now);
                Assert.That(inbox.Register(CreateReport()), Is.True);
                Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4,
                    out ValidatedTaskFailureResponse oldResponse, out _), Is.True);
                int presentations = 0;
                runner.PresentationReady += _ => presentations++;

                HandleResponse(runner, oldResponse);

                Assert.That(PendingRequestId(runner), Is.EqualTo("failure-request-2"));
                Assert.That(runner.ActiveProposalRevision, Is.Zero);
                Assert.That(presentations, Is.Zero);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase("clarify")]
        [TestCase("explain")]
        public void ValidatedResponse_ForOwnedRequestStillResolves(string resolution)
        {
            GameObject root = new("OwnedValidatedFailure_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runner = CreatePendingRunner(root, backend, "failure-request-1");
                TaskFailureResponseInbox inbox = new(() => now);
                Assert.That(inbox.Register(CreateReport()), Is.True);
                string json = ResponseJson("bring_book_01")
                    .Replace("\"resolution\":\"revise\"", $"\"resolution\":\"{resolution}\"")
                    .Replace("\"selected_alternative_id\":\"bring_book_01\"",
                        "\"selected_alternative_id\":\"\"");
                Assert.That(inbox.TryAccept(json, 4, out ValidatedTaskFailureResponse response,
                    out string code), Is.True, code);
                GoalPlanPresentation presentation = default;
                runner.PresentationReady += value => presentation = value;

                HandleResponse(runner, response);

                Assert.That(PendingRequestId(runner), Is.Null);
                Assert.That(runner.HasActiveTask, Is.False);
                Assert.That(presentation.Kind, Is.EqualTo(GoalPlanPresentationKind.Clarification));
                Assert.That(presentation.Code, Is.EqualTo("TASK_" + resolution.ToUpperInvariant()));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ValidatedRevise_ForOwnedRequestStillReachesProposalAdmission()
        {
            GameObject root = new("OwnedValidatedRevision_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runner = CreatePendingRunner(root, backend, "failure-request-1");
                TaskFailureResponseInbox inbox = new(() => now);
                Assert.That(inbox.Register(CreateReport()), Is.True);
                Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4,
                    out ValidatedTaskFailureResponse response, out string code), Is.True, code);
                string presentationCode = null;
                runner.PresentationReady += value => presentationCode = value.Code;
                LogAssert.Expect(LogType.Warning, new Regex(@"\[GoalPlan\] REJECTED code=GoalMissing"));

                HandleResponse(runner, response);

                Assert.That(PendingRequestId(runner), Is.Null);
                Assert.That(runner.ActiveProposalRevision, Is.EqualTo(1));
                Assert.That(presentationCode, Is.EqualTo("GoalMissing"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RunnerCancellation_InvalidatesOnlyItsReply_AndRejectsAlreadyValidatedLateRevision(bool disable)
        {
            GameObject root = new("CancelledProposal_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runner = root.AddComponent<GoalPlanRunner>();
                var safety = root.AddComponent<EmbodiedNPC.Interaction.InteractionSafetyController>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var inbox = (TaskFailureResponseInbox)typeof(BackendWebSocketClient)
                    .GetProperty("FailureInbox", flags).GetValue(backend);
                Assert.That(inbox.Register(CreateReport()), Is.True);
                TaskFailureReport other = CreateReport();
                other.request_id = "other-request";
                other.proposal_id = "other-proposal";
                Assert.That(inbox.Register(other), Is.True);
                TaskFailureResponseInbox lateInbox = new(() => now);
                lateInbox.Register(CreateReport());
                Assert.That(lateInbox.TryAccept(ResponseJson("bring_book_01"), 4, out var late, out _), Is.True);
                typeof(GoalPlanRunner).GetField("backendClient", flags).SetValue(runner, backend);
                typeof(GoalPlanRunner).GetField("activeProposalId", flags).SetValue(runner, "proposal_1");
                typeof(GoalPlanRunner).GetField("pendingFailureRequestId", flags).SetValue(runner, "failure-request-1");
                int presentations = 0;
                runner.PresentationReady += _ => presentations++;
                if (disable) typeof(GoalPlanRunner).GetMethod("OnDisable", flags).Invoke(runner, null);
                else safety.CancelCurrentSkill();

                Assert.That(inbox.PendingCount, Is.EqualTo(1));
                Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4, out _, out string code), Is.False);
                Assert.That(code, Is.EqualTo("REQUEST_NOT_PENDING"));
                typeof(GoalPlanRunner).GetMethod("HandleTaskFailureResponse", flags).Invoke(runner, new object[] { late });
                Assert.That(runner.IsExecuting, Is.False);
                Assert.That(presentations, Is.Zero);
                Assert.That((string)typeof(GoalPlanRunner).GetField("activeProposalId", flags).GetValue(runner), Is.Empty);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void Inbox_AcceptsOnlyTheExactUnityAuthoredAlternative()
        {
            TaskFailureResponseInbox inbox = new(() => now);
            TaskFailureReport report = CreateReport();
            Assert.That(report.IsValid, Is.True);
            Assert.That(inbox.Register(report), Is.True);

            string invented = ResponseJson("invented_target");
            Assert.That(inbox.TryAccept(invented, 4, out _, out string rejection), Is.False);
            Assert.That(rejection, Is.EqualTo("ALTERNATIVE_NOT_ALLOWED"));

            Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4,
                out ValidatedTaskFailureResponse accepted, out string code), Is.True, code);
            Assert.That(accepted.ProposalRevision, Is.EqualTo(1));
            Assert.That(accepted.SelectedGoal.target_entity_id, Is.EqualTo("book_01"));
            Assert.That(inbox.PendingCount, Is.Zero);
        }

        [Test]
        public void Inbox_RejectsStaleWorldAndSkippedRevision()
        {
            TaskFailureResponseInbox inbox = new(() => now);
            Assert.That(inbox.Register(CreateReport()), Is.True);

            Assert.That(inbox.TryAccept(ResponseJson("bring_book_01", revision: 2), 4,
                out _, out string skipped), Is.False);
            Assert.That(skipped, Is.EqualTo("PROPOSAL_REVISION_INVALID"));
            Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 5,
                out _, out string stale), Is.False);
            Assert.That(stale, Is.EqualTo("STALE_WORLD_REVISION"));
        }

        [Test]
        public void Inbox_NotifiesOnlyRemovedTickets_AndNeverAcceptedTickets()
        {
            TaskFailureResponseInbox inbox = new(() => now);
            List<string> invalidated = new();
            inbox.RequestInvalidated += invalidated.Add;
            inbox.Cancel("missing");
            inbox.Clear();
            Assert.That(invalidated, Is.Empty);

            TaskFailureReport first = CreateReport();
            TaskFailureReport second = CreateReport();
            second.request_id = "failure-request-2";
            Assert.That(inbox.Register(first, 1), Is.True);
            Assert.That(inbox.Register(second, 1), Is.True);
            now = 2;
            inbox.Expire();
            inbox.Expire();
            Assert.That(invalidated, Is.EquivalentTo(new[] { first.request_id, second.request_id }));

            Assert.That(inbox.Register(first), Is.True);
            Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4, out _, out string code), Is.True, code);
            Assert.That(invalidated, Has.Count.EqualTo(2));

            Assert.That(inbox.Register(first), Is.True);
            Assert.That(inbox.Register(second), Is.True);
            inbox.Cancel(first.request_id);
            inbox.Cancel(first.request_id);
            inbox.Clear();
            inbox.Clear();
            Assert.That(invalidated, Is.EqualTo(new[]
            {
                first.request_id, second.request_id, first.request_id, second.request_id,
            }));
        }

        [Test]
        public void BackendLazyInbox_ForwardsOneInvalidationPerTicket()
        {
            GameObject root = new("LazyFailureInbox_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                List<string> invalidated = new();
                backend.TaskFailureRequestInvalidated += invalidated.Add;
                TaskFailureResponseInbox inbox = GetInbox(backend);
                Assert.That(backend.PendingRequestCount, Is.Zero);
                Assert.That(GetInbox(backend), Is.SameAs(inbox));
                Assert.That(inbox.Register(CreateReport()), Is.True);

                backend.CancelPendingFailureRequest("failure-request-1");

                Assert.That(invalidated, Is.EqualTo(new[] { "failure-request-1" }));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void OldRevisionResponse_CannotClearNewRevisionTicket()
        {
            GameObject root = new("FailureRevisionOwnership_Test");
            root.SetActive(false);
            try
            {
                BackendWebSocketClient backend = root.AddComponent<BackendWebSocketClient>();
                GoalPlanRunner runner = CreatePendingRunner(root, backend, "failure-request-2");
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(GoalPlanRunner).GetField("activeProposalRevision", flags).SetValue(runner, 1);
                TaskFailureResponseInbox inbox = new(() => now);
                Assert.That(inbox.Register(CreateReport()), Is.True);
                Assert.That(inbox.TryAccept(ResponseJson("bring_book_01"), 4,
                    out ValidatedTaskFailureResponse oldResponse, out _), Is.True);

                HandleResponse(runner, oldResponse);

                Assert.That(PendingRequestId(runner), Is.EqualTo("failure-request-2"));
                Assert.That(runner.ActiveProposalRevision, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ReportAndPolicy_EnforceTwoRevisionAndSemanticBoundary()
        {
            TaskFailureReport report = CreateReport();
            report.proposal_revision = 2;
            Assert.That(report.IsValid, Is.False);
            Assert.That(TaskFailurePolicy.ShouldReport("planning", "NO_PLAN"), Is.True);
            Assert.That(TaskFailurePolicy.ShouldReport("execution", "GOAL_REPLAN_BUDGET_EXHAUSTED",
                PlanExecutionStatus.Failed), Is.True);
            Assert.That(TaskFailurePolicy.ShouldReport("execution", "PATH_INVALID",
                PlanExecutionStatus.Failed), Is.False);
            Assert.That(TaskFailurePolicy.ShouldReport("execution", "INTERRUPTED_BY_THREAT",
                PlanExecutionStatus.Cancelled), Is.False);
        }

        private static TaskFailureReport CreateReport() => new()
        {
            request_id = "failure-request-1",
            trace_id = "failure-trace-1",
            world_revision = 4,
            proposal_id = "proposal_1",
            proposal_revision = 0,
            failure_stage = "planning",
            failure_code = "NO_PLAN",
            detail = "目标路径不可达",
            failed_goal = new GoalProposal
            {
                type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01",
            },
            involved_entity_ids = new[] { "cup_01", "player_01" },
            retryable = true,
            allowed_alternatives = new[]
            {
                new TaskAlternative
                {
                    alternative_id = "bring_book_01",
                    description = "改为交付书",
                    goal = new GoalProposal
                    {
                        type = "bring_object", target_entity_id = "book_01",
                        destination_entity_id = "player_01",
                    },
                },
            },
        };

        private static string ResponseJson(string selected, int revision = 1) =>
            "{\"message_type\":\"task_failure_response\",\"schema_version\":\"1.0\"," +
            "\"request_id\":\"failure-request-1\",\"trace_id\":\"failure-trace-1\"," +
            "\"world_revision\":4,\"proposal_id\":\"proposal_1\"," +
            $"\"proposal_revision\":{revision},\"resolution\":\"revise\"," +
            "\"utterance\":\"可以换成书吗？\",\"emotion\":\"neutral\",\"gesture\":\"none\"," +
            $"\"selected_alternative_id\":\"{selected}\"}}";

        private static GoalPlanRunner CreatePendingRunner(GameObject root, BackendWebSocketClient backend,
            string requestId)
        {
            GoalPlanRunner runner = root.AddComponent<GoalPlanRunner>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(GoalPlanRunner).GetField("backendClient", flags).SetValue(runner, backend);
            typeof(GoalPlanRunner).GetField("activeProposalId", flags).SetValue(runner, "proposal_1");
            typeof(GoalPlanRunner).GetField("pendingFailureRequestId", flags).SetValue(runner, requestId);
            typeof(GoalPlanRunner).GetMethod("OnEnable", flags).Invoke(runner, null);
            return runner;
        }

        private static TaskFailureResponseInbox GetInbox(BackendWebSocketClient backend) =>
            (TaskFailureResponseInbox)typeof(BackendWebSocketClient)
                .GetProperty("FailureInbox", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(backend);

        private static string PendingRequestId(GoalPlanRunner runner) =>
            (string)typeof(GoalPlanRunner)
                .GetField("pendingFailureRequestId", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(runner);

        private static void HandleResponse(GoalPlanRunner runner, ValidatedTaskFailureResponse response) =>
            typeof(GoalPlanRunner)
                .GetMethod("HandleTaskFailureResponse", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(runner, new object[] { response });

        private static GoalProposal NewGoal() => new()
        {
            type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01",
        };
    }
}
