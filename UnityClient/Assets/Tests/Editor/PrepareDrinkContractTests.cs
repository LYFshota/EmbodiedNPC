using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Memory;
using EmbodiedNPC.World;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Planning.MiniGoap;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class PrepareDrinkContractTests
    {
        // Independent golden fixture keeps protocol order and translations reviewable.
        private static readonly string[][] DrinkCases =
        {
            new[]{"latte","拿铁","Latte"}, new[]{"cappuccino","卡布奇诺","Cappuccino"},
            new[]{"americano","美式","Americano"}, new[]{"espresso","浓缩咖啡","Espresso"},
            new[]{"flat_white","馥芮白","Flat White"}, new[]{"mocha","摩卡","Mocha"},
            new[]{"caramel_macchiato","焦糖玛奇朵","Caramel Macchiato"},
            new[]{"hot_chocolate","热巧克力","Hot Chocolate"}, new[]{"matcha_latte","抹茶拿铁","Matcha Latte"},
            new[]{"black_tea","红茶","Black Tea"},
        };
        private static IEnumerable<string> RegisteredDrinks => DrinkTypes.Ids;

        [Test] public void ManifestAndNamesMatchIndependentTenDrinkContract()
        {
            Assert.That(DrinkTypes.Ids,Is.EqualTo(DrinkCases.Select(d=>d[0])));
            BuiltInActionCatalog.Instance.TryGet("prepare_drink",out var registration);
            var binding=registration.Descriptor.BindingSchema.Single();
            Assert.That(binding.Name,Is.EqualTo("drink_type"));Assert.That(binding.Kind,Is.EqualTo(ActionBindingKind.Enum));
            Assert.That(binding.Required,Is.True);Assert.That(binding.EnumValues,Is.EqualTo(DrinkCases.Select(d=>d[0])));
            foreach(var drink in DrinkCases)
            {
                Assert.That(DrinkTypes.IsValid(drink[0]),Is.True);
                Assert.That(DrinkTypes.Name(drink[0]),Is.EqualTo(drink[1]));
                Assert.That(DrinkTypes.Name(drink[0],true),Is.EqualTo(drink[2]));
            }
        }

        [TestCase(false)][TestCase(true)] public void PreparationFeedbackUsesCurrentUiLanguage(bool english)
        {
            const string key="EmbodiedNPC.Settings.Language";
            bool had=PlayerPrefs.HasKey(key);int previous=PlayerPrefs.GetInt(key,0);
            try
            {
                PlayerPrefs.SetInt(key,english?1:0);
                foreach(var drink in DrinkCases)
                {
                    string name=drink[english?2:1];
                    foreach(var status in new[]{ActionExecutionStatus.Accepted,ActionExecutionStatus.Succeeded,
                        ActionExecutionStatus.Cancelled,ActionExecutionStatus.Failed,ActionExecutionStatus.TimedOut})
                    {
                        var result=new ActionExecutionResult("prepare_drink","p","t",status,"test_code",
                            new Dictionary<string,string>{{"drink_type",drink[0]}},0,1);
                        var text=new PrepareDrinkPresentationPolicy().Present(result);
                        Assert.That(text.Status,Is.EqualTo(status));Assert.That(text.Code,Is.EqualTo("test_code"));
                        if(status==ActionExecutionStatus.Accepted || status==ActionExecutionStatus.Succeeded)
                            Assert.That(text.Message,Does.Contain(name));
                        else Assert.That(text.Message,Does.Not.Contain(english?"is ready":"做好了"));
                    }
                    foreach(var phase in new[]{PlanProgressKind.Accepted,PlanProgressKind.Action})
                    {
                        var snapshot=new TaskFeedbackSnapshot(new TaskFeedbackKey("npc","p",0,"t"),"prepare_drink","station","player",
                            "player",0,1,new PlanActionProgress(phase,"plan",0,0,PlannerActionType.PrepareDrink),drinkType:drink[0]);
                        Assert.That(ActionFeedbackPresenter.FormatPhase(snapshot),Is.EqualTo(english
                            ? (phase==PlanProgressKind.Accepted?$"Preparing {name}…":$"Making {name}…")
                            : (phase==PlanProgressKind.Accepted?$"正在准备{name}…":$"正在制作{name}…")));
                    }
                }
            }
            finally { if(had)PlayerPrefs.SetInt(key,previous);else PlayerPrefs.DeleteKey(key); }
        }

        [TestCase(0f)][TestCase(-1f)][TestCase(float.NaN)][TestCase(float.PositiveInfinity)]
        public void InvalidInstancePreparationDurationIsRejected(float seconds) =>
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new PrepareDrinkActionRunner(seconds));

        private static string Wire(string drink) => "{\"message_type\":\"cognitive_response\",\"schema_version\":\"1.0\","+
            "\"request_id\":\"request_drink\",\"trace_id\":\"trace_drink\",\"run_id\":\"run_drink\",\"agent_entity_id\":\"npc_cafe_01\","+
            "\"decision\":{\"decision_kind\":\"ACTION_PROPOSAL\",\"action_proposal\":{\"action_id\":\"prepare_drink\",\"bindings\":{\"drink_type\":\""+drink+"\"}}},"+
            "\"validated_goal\":{\"type\":\"prepare_drink\",\"drink_type\":\""+drink+"\"}}";
        [TestCase("request_drink")][TestCase("proposal_request_drink")][TestCase("request with spaces")]
        public void ProposalNormalizationIsIdempotentAcrossRevisions(string id)
        { var canonical=GoalPlanRunner.NormalizeProposalId(id);Assert.That(GoalPlanRunner.NormalizeProposalId(canonical),Is.EqualTo(canonical)); }
        [TestCaseSource(nameof(RegisteredDrinks))]
        public void EnumAdapterPreservesTypedGoalWithoutEntitySpoof(string drink)
        {
            var proposal=new CognitiveActionProposal { action_id="prepare_drink",
                bindings=new Dictionary<string,string>{{"drink_type",drink}} };
            Assert.That(CognitiveActionProposalAdapter.TryMap(proposal,out var goal),Is.True);
            Assert.That(goal.drink_type,Is.EqualTo(drink));Assert.That(goal.target_entity_id,Is.Null);
            Assert.That(goal.destination_entity_id,Is.Null);
            Assert.That(CognitiveResponseValidator.TryParse(Wire(drink),out var response,out var error),Is.True,error);
            Assert.That(response.validated_goal.drink_type,Is.EqualTo(drink));
        }
        [TestCase("unregistered_drink")][TestCase("LATTE")][TestCase("")][TestCase(null)]
        public void IllegalEnumIsRejected(string drink)
        {
            var proposal=new CognitiveActionProposal { action_id="prepare_drink",bindings=new Dictionary<string,string>{{"drink_type",drink}} };
            Assert.That(CognitiveActionProposalAdapter.TryMap(proposal,out _),Is.False);
        }
        [Test] public void ExtraMissingAndNonStringWireBindingsAreRejected()
        {
            foreach(var bindings in new[]{new Dictionary<string,string>(),
                new Dictionary<string,string>{{"drink_type","latte"},{"recipient","player_01"}}})
                Assert.That(CognitiveActionProposalAdapter.TryMap(new CognitiveActionProposal{action_id="prepare_drink",bindings=bindings},out _),Is.False);
            foreach(var wrong in new[]{"1","true","[]","null"})
            {
                string wire=Wire("latte").Replace("\"drink_type\":\"latte\"","\"drink_type\":"+wrong);
                Assert.That(CognitiveResponseValidator.TryParse(wire,out _,out _),Is.False);
            }
            var entityBinding=new CognitiveActionBindingCapabilityDto {name="target",kind="entity_ref",required=true,enum_values=new[]{"latte"}};
            Assert.That(entityBinding.IsValid,Is.False);
            // Existing silent/direct responses explicitly serialize action_proposal:null.
            string silent="{\"message_type\":\"cognitive_response\",\"schema_version\":\"1.0\",\"request_id\":\"r\",\"trace_id\":\"t\",\"run_id\":\"run\",\"agent_entity_id\":\"npc_cafe_01\","+
                "\"decision\":{\"decision_kind\":\"NO_ACTION\",\"action_proposal\":null},\"validated_goal\":null}";
            Assert.That(CognitiveResponseValidator.TryParse(silent,out _,out _),Is.True);
        }
        [Test] public void SucceededWithoutAuthoritativeReceiptCannotShowPlusOne()
        {
            var actorRoot=new GameObject("receipt_test_actor");actorRoot.SetActive(false);
            actorRoot.AddComponent<WorldEntity>().Configure("npc_receipt_test","test.actor",EntityKind.Agent,AgentRole.Npc);
            var runner=actorRoot.AddComponent<GoalPlanRunner>();
            var playerRoot=new GameObject("receipt_test_player");
            var player=playerRoot.AddComponent<WorldEntity>();player.Configure("player_receipt_test","test.player",EntityKind.Agent,AgentRole.Player);
            var root=new GameObject("receipt_test_ui",typeof(RectTransform));var ui=root.AddComponent<ActionFeedbackPresenter>();
            try
            {
                ui.Configure(null,null,new[]{runner},player,null,null,null);
                var key=new TaskFeedbackKey("npc_receipt_test","proposal_receipt_test",0,"trace_receipt_test");
                ui.ObserveStarted(new TaskFeedbackSnapshot(key,"prepare_drink","","",player.EntityId,0,1,
                    new PlanActionProgress(PlanProgressKind.Accepted,"",0,0),drinkType:"latte"));
                ui.ObserveOutcome(new GoalOutcome("outcome_receipt_test",key.Proposal,0,key.Trace,"prepare_drink",key.Actor,"","",
                    PlanExecutionStatus.Succeeded,"",1,0));
                Assert.That(ui.PendingNotificationCount,Is.Zero);Assert.That(ui.LastResultText,Is.Empty);
                Assert.That(ui.ActiveTaskCount,Is.Zero);
            }
            finally {Object.DestroyImmediate(root);Object.DestroyImmediate(actorRoot);Object.DestroyImmediate(playerRoot);}
        }

        [Test] public void GoalExperienceUsesCommittedBeneficiaryIdentityAndPreservesEffectIdempotency()
        {
            using var f = new DrinkEvidenceFixture();
            SkillResult result = f.Commit();
            Assert.That(result.Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(f.World.TryGetDrinkReceipt(f.Actor.EntityId, "proposal_evidence", "trace_evidence", out var receipt), Is.True);
            Assert.That(receipt.PlayerIdentity.PersistentId, Is.EqualTo("beneficiary-evidence-player"));
            var drafts = new List<ExperienceDraft>();
            var adapter = new GoalOutcomeExperienceAdapter(f.Actor.EntityId, drafts.Add, f.Registry, f.World);
            // A proposed replacement recipient never becomes evidence, even when the real effect succeeded.
            GoalOutcome outcome = f.Outcome(target: "other_player", destination: "other_player");
            adapter.Handle(outcome);
            ExperienceDraft draft = drafts.Single();
            Assert.That(draft.IsValid, Is.True);
            Assert.That(draft.TargetEntityIds, Is.EqualTo(new[] { f.Player.EntityId }));
            Assert.That(draft.PersistentParticipants.Select(p => p.PersistentId), Is.EquivalentTo(
                new[] { "beneficiary-evidence-npc", "beneficiary-evidence-player" }));
            Assert.That(draft.StructuredPayload["drink_receipt_id"], Is.EqualTo(receipt.Id));
            Assert.That(draft.StructuredPayload["destination_entity_id"], Is.EqualTo(f.Player.EntityId));
            TestContext.Out.WriteLine("SKILL_EVIDENCE_JSON:" + ExperienceAppendRequest.FromDraft(
                SkillResultExperienceAdapter.Create(result, 1d, f.Registry)).Serialize());
            TestContext.Out.WriteLine("GOAL_EVIDENCE_JSON:" + ExperienceAppendRequest.FromDraft(draft).Serialize());
            Assert.That(f.Commit().Status, Is.EqualTo(SkillStatus.Succeeded));
            Assert.That(f.World.GetDrinkCount(f.Player.EntityId, "latte"), Is.EqualTo(1));
            // Unregistration/rebinding after the effect must not rewrite its durable beneficiary.
            f.Registry.Unregister(f.Player);
            f.Player.Configure("player_evidence", "test.player", EntityKind.Agent, AgentRole.Player,
                new PersistentIdentityRef("player", "Player", "replacement-player"));
            var after = GoalOutcomeExperienceAdapter.Create(outcome, f.Registry, f.World);
            Assert.That(after.PersistentParticipants.Single(p => p.EntityId == "player_evidence").PersistentId,
                Is.EqualTo("beneficiary-evidence-player"));
        }

        [TestCase("no_receipt")]
        [TestCase("other_actor")]
        [TestCase("other_proposal")]
        [TestCase("other_trace")]
        [TestCase("old_revision")]
        [TestCase("other_world")]
        [TestCase("missing_player_identity")]
        [TestCase("missing_actor_identity")]
        public void GoalExperienceCannotInventBeneficiary(string mismatch)
        {
            using var f = new DrinkEvidenceFixture(mismatch);
            if (mismatch != "no_receipt") Assert.That(f.Commit().Status, Is.EqualTo(SkillStatus.Succeeded));
            var outcome = new GoalOutcome("outcome_evidence", mismatch == "other_proposal" ? "other" : "proposal_evidence", 0,
                mismatch == "other_trace" ? "other" : "trace_evidence", "prepare_drink",
                mismatch == "other_actor" ? "other_npc" : f.Actor.EntityId, f.Player.EntityId, f.Player.EntityId,
                PlanExecutionStatus.Succeeded, "", mismatch == "old_revision" ? 0 : f.World.WorldRevision, 1d);
            WorldStateService world = mismatch == "other_world" ? f.Root.AddComponent<WorldStateService>() : f.World;
            var draft = GoalOutcomeExperienceAdapter.Create(outcome, f.Registry, world);
            Assert.That(draft.TargetEntityIds, Is.Empty);
            Assert.That(draft.PersistentParticipants.Any(p => p.IdentityType == "Player"), Is.False);
            Assert.That(draft.StructuredPayload.ContainsKey("drink_receipt_id"), Is.False);
        }

        [TestCase("prepare_drink", PlanExecutionStatus.Failed)]
        [TestCase("prepare_drink", PlanExecutionStatus.Cancelled)]
        [TestCase("prepare_drink", PlanExecutionStatus.TimedOut)]
        [TestCase("clean_counter", PlanExecutionStatus.Succeeded)]
        public void ReceiptDoesNotAddBeneficiaryToOtherGoalResults(string goal, PlanExecutionStatus status)
        {
            using var f = new DrinkEvidenceFixture();
            Assert.That(f.Commit().Status, Is.EqualTo(SkillStatus.Succeeded));
            var outcome = new GoalOutcome("outcome_evidence", "proposal_evidence", 0, "trace_evidence", goal,
                f.Actor.EntityId, goal == "clean_counter" ? f.Station.Entity.EntityId : "", "", status, "original_failure", 1, 1d);
            var draft = GoalOutcomeExperienceAdapter.Create(outcome, f.Registry, f.World);
            Assert.That(draft.PersistentParticipants.Any(p => p.IdentityType == "Player"), Is.False);
            Assert.That(draft.StructuredPayload["status"], Is.EqualTo(status.ToString()));
            Assert.That(draft.StructuredPayload["failure_code"], Is.EqualTo("original_failure"));
            Assert.That(draft.TargetEntityIds, Is.EqualTo(goal == "clean_counter" ? new[] { f.Station.Entity.EntityId } : new string[0]));
        }

        private sealed class DrinkEvidenceFixture : System.IDisposable
        {
            public readonly GameObject Root;
            public readonly EntityRegistry Registry;
            public readonly WorldStateService World;
            public readonly WorldEntity Actor, Player;
            public readonly DrinkPreparationStation Station;
            public DrinkEvidenceFixture(string mismatch = "")
            {
                Root = new GameObject("isolated_drink_evidence"); Root.SetActive(false);
                Actor = Entity("npc_cafe_01", EntityKind.Agent, AgentRole.Npc, mismatch == "missing_actor_identity" ? null :
                    new PersistentIdentityRef("npc", "Character", "beneficiary-evidence-npc"));
                Player = Entity("player_evidence", EntityKind.Agent, AgentRole.Player, mismatch == "missing_player_identity" ? null :
                    new PersistentIdentityRef("player", "Player", "beneficiary-evidence-player"));
                var station = Entity("station_evidence", EntityKind.Environment, AgentRole.None, null);
                Station = station.gameObject.AddComponent<DrinkPreparationStation>();
                Station.Configure(station.transform, station.transform);
                Actor.gameObject.AddComponent<AgentActionCapabilityProfile>().Configure("prepare_drink");
                Actor.gameObject.AddComponent<DrinkPreparationCapability>().Configure(Station);
                World = Root.AddComponent<WorldStateService>();
                Registry = Root.AddComponent<EntityRegistry>();
                var serialized = new SerializedObject(Registry);
                serialized.FindProperty("autoDiscoverSceneEntities").boolValue = false;
                var entities = serialized.FindProperty("entities"); entities.arraySize = 3;
                entities.GetArrayElementAtIndex(0).objectReferenceValue = Actor;
                entities.GetArrayElementAtIndex(1).objectReferenceValue = Player;
                entities.GetArrayElementAtIndex(2).objectReferenceValue = station;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Root.SetActive(true); Registry.RebuildRegistry();
                Assert.That(Registry.IsReady, Is.True);
            }
            private WorldEntity Entity(string id, EntityKind kind, AgentRole role, PersistentIdentityRef identity)
            {
                var root = new GameObject(id); root.transform.SetParent(Root.transform);
                var entity = root.AddComponent<WorldEntity>(); entity.Configure(id, "test." + id, kind, role, identity);
                return entity;
            }
            public SkillResult Commit() => World.TryCommitDrink("proposal_evidence", "trace_evidence", Registry,
                Actor, Station.Entity, Player, "latte");
            public GoalOutcome Outcome(string target = "", string destination = "") => new("outcome_evidence", "proposal_evidence", 0,
                "trace_evidence", "prepare_drink", Actor.EntityId, target, destination, PlanExecutionStatus.Succeeded, "", World.WorldRevision, 1d);
            public void Dispose() => Object.DestroyImmediate(Root);
        }
    }
}
