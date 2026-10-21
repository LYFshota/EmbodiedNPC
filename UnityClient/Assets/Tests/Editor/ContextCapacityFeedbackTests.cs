using System;
using System.Linq;
using EmbodiedNPC.Cognition;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ContextCapacityFeedbackTests
    {
        public static CapacityCheckDto Check(string state="error", char identity='a', string cause="budget") => new()
        {
            condition_id=new string(identity,64),configuration_key=new string('c',64),state=state,cause=cause,
            proof=new string('d',64),window=16384,input_cap=13824,required_estimate=15000,input_estimate=15000,
            output_reserve=1024,estimation_reserve=1536,counts=new(),provider_verified=state!="error"
        };
        public static CapacityStatusDto Status(params CapacityCheckDto[] checks) => new() { version=1,checks=checks };
        [Test] public void HardErrorDeduplicatesAndOnlyOriginalSourceOrVerifiedRecheckClearsIt()
        {
            var state=new ContextCapacityState();var error=Check();state.Observe(Status(error));state.Observe(Status(error));
            Assert.AreEqual(1,state.Errors.Count);
            state.Observe(Status(Check("ok",'b')));Assert.AreEqual(1,state.Errors.Count);
            state.Rechecked(new CapacityRecheckResult {version=1,checks=new[]{new CapacityRecheckDto{condition_id=error.condition_id,proof=error.proof,resolved=false}}});
            Assert.AreEqual(1,state.Errors.Count);
            state.Rechecked(new CapacityRecheckResult {version=1,checks=new[]{new CapacityRecheckDto{condition_id=error.condition_id,proof=new string('e',64),resolved=true}}});
            Assert.AreEqual(1,state.Errors.Count,"An old in-flight verification cannot clear a replaced error.");
            state.Rechecked(new CapacityRecheckResult {version=1,checks=new[]{new CapacityRecheckDto{condition_id=error.condition_id,proof=error.proof,resolved=true}}});
            Assert.IsEmpty(state.Errors);
            state.Observe(Status(Check(cause:"provider")));var unverified=Check("ok");unverified.provider_verified=false;
            state.Observe(Status(unverified));Assert.AreEqual(1,state.Errors.Count);
            state.Observe(Status(Check("ok")));Assert.IsEmpty(state.Errors);
        }
        [Test] public void WarningNeverWeakensHardErrorsAndRepeatedEventsDoNotStack()
        {
            var state=new ContextCapacityState();state.Observe(Status(Check("warning")));
            state.Observe(Status(Check("warning",'b')));Assert.IsTrue(state.WarningPending);
            state.Observe(Status(Check()));Assert.AreEqual(1,state.Errors.Count);Assert.IsTrue(state.WarningPending);
        }
        [TestCase("TIMEOUT")] [TestCase("UNAVAILABLE")] [TestCase("COGNITIVE_RESPONSE_INVALID")]
        public void OtherFailuresWithoutCapacityDiagnosticsCannotIntroduceCapacityErrors(string category)
        {
            var json=new JObject { ["message_type"]="error",["request_id"]="r",["trace_id"]="t",["run_id"]="run",["agent_entity_id"]="npc_cafe_01",["reason"]="COGNITIVE_REQUEST_FAILED",["diagnostic_category"]=category };
            Assert.IsTrue(CognitiveResponseValidator.TryParseFailure(json.ToString(),out var failure));
            Assert.IsNull(failure.ContextStatus);
            var state=new ContextCapacityState();state.Observe(failure.ContextStatus);Assert.IsEmpty(state.Errors);
        }
        [Test] public void CapacityWireIsStrictAndUnknownFieldsStillFail()
        {
            var json=new JObject { ["message_type"]="error",["request_id"]="r",["trace_id"]="t",["run_id"]="run",["agent_entity_id"]="npc_cafe_01",["reason"]="COGNITIVE_REQUEST_FAILED",["diagnostic_category"]="CONTEXT_BUDGET_EXCEEDED",["context_status"]=JToken.FromObject(Status(Check())) };
            Assert.IsTrue(CognitiveResponseValidator.TryParseFailure(json.ToString(),out var failure));Assert.IsTrue(failure.ContextStatus.IsValid);
            json["context_status"]["checks"][0]["window"]="16384";
            Assert.IsFalse(CognitiveResponseValidator.TryParseFailure(json.ToString(),out _));
            json["context_status"]["checks"][0]["window"]=16384;json["context_status"]["arbitrary"]=true;
            Assert.IsFalse(CognitiveResponseValidator.TryParseFailure(json.ToString(),out _));
        }
    }
}
