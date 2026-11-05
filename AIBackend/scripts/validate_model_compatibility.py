"""Opt-in bounded official DeepSeek smoke, using synthetic identities and temp DBs.

Run from AIBackend: python scripts/validate_model_compatibility.py --confirm-paid
  --key-file <existing local model config> --report <new evidence path>
Never writes credentials, upstream response bodies or actual player data.
"""
import argparse
import asyncio
import json
import logging
import re
import sys
import time
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from app.model_api_config import MainModelConfig
from app.model_compatibility import ProbeBudget
from app.model_config_runtime import build_main_provider
from app.model_api_config_api import _test_main
from app.model_profile_probes import synthetic_request
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions, ProviderError
from app.base_memory_consolidator import BaseMemoryConsolidator
import httpx

class EvidenceTransport(httpx.AsyncBaseTransport):
    def __init__(self, evidence):self.evidence=evidence
    async def handle_async_request(self, request):
        if len(self.evidence["requests"]) >= self.evidence["max_http_calls"]:raise ProviderError("VALIDATION_CALL_LIMIT")
        body=json.loads(request.content)
        row=dict(endpoint=request.url.path,model=body.get("model"),format=(body.get("response_format") or (body.get("text") or {}).get("format") or {}).get("type"),output_limit=body.get("max_tokens",body.get("max_output_tokens",body.get("max_completion_tokens"))),temperature_sent="temperature" in body,thinking=body.get("thinking",body.get("reasoning")))
        self.evidence["requests"].append(row)
        start=time.monotonic()
        try:
            async with httpx.AsyncHTTPTransport(retries=0) as transport:
                response=await transport.handle_async_request(request)
                await response.aread()
            row["status"]=response.status_code
            try:
                body=response.json();usage=body.get("usage") or {}
                row["usage"]={k:v for k,v in usage.items() if k in ("prompt_tokens","completion_tokens","total_tokens","input_tokens","output_tokens","reasoning_tokens") and type(v) is int and v>=0} if isinstance(usage,dict) else {}
                if response.status_code >= 400:
                    error=body.get("error",{});message=str(error.get("message","")).casefold() if isinstance(error,dict) else str(error).casefold()
                    param=error.get("param") if isinstance(error,dict) else None
                    row["rejection_evidence"]={"param":param if param in ("response_format","response_format.type","temperature","max_tokens","max_completion_tokens") else "other_or_missing",
                        "terms":[term for term in ("response_format","json_schema","json_object","not supported","unsupported","not support","not allowed","must be","only support","invalid value","不支持","oneof","additionalproperties","pattern","max_tokens","max_completion_tokens","temperature") if term in message]}
                choices=body.get("choices") or []
                if choices:
                    row["reasoning_content_present"]=bool(choices[0].get("message",{}).get("reasoning_content"))
                    reason=choices[0].get("finish_reason")
                    row["finish_reason"]=reason if reason in ("stop","length","tool_calls","content_filter") else "other"
            except (ValueError,TypeError,AttributeError):pass
            return response
        except Exception as error:
            row["transport_error"]=type(error).__name__;raise
        finally:row["latency_ms"]=round((time.monotonic()-start)*1000)

def code(error):
    text=str(error)
    return text if re.fullmatch("[A-Z][A-Z0-9_]{2,80}",text) else type(error).__name__

async def run(args):
    logging.basicConfig(level=logging.WARNING)
    document=json.loads(args.key_file.read_text(encoding="utf-8"))
    configs=[p.get("config",{}) for p in document.get("main_profiles",[])]+[document.get("main_model",{}),(document.get("activation") or {}).get("main_model",{})]
    key=next(c["api_key"] for c in configs if c.get("base_url","").rstrip("/")=="https://api.deepseek.com" and c.get("api_key"))
    evidence=dict(max_http_calls=8,model="deepseek-flash",requests=[],checks=[],player_data_used=False)
    transport=EvidenceTransport(evidence)
    def save():args.report.write_text(json.dumps(evidence,ensure_ascii=False,indent=2),encoding="utf-8")
    try:
        for kind in ("custom","official"):
            config=MainModelConfig(display_name="Isolated validation",provider_type="openai_compatible" if kind=="custom" else "deepseek_official_responses",base_url="https://api.deepseek.com",api_key=key,model="deepseek-flash",thinking=False,max_output_tokens=512,context_length=32768,timeout_seconds=35.0,
                **(dict(schema_profile="auto",token_parameter="auto",thinking_parameter="auto",send_temperature=None) if kind=="custom" else {}))
            result=await _test_main(config,transport,probe_budget=ProbeBudget() if kind=="custom" else None)
            evidence["checks"].append(dict(adapter=kind,case="connection",**result));save()
            if not result["success"]:continue
            provider=build_main_provider(config,transport);executor=PrioritizedModelExecutor()
            start=len(evidence["requests"])
            try:
                entry=CognitiveBackendEntry(provider,executor,inference_options=InferenceOptions(think=False,max_output_tokens=512),context_length=32768)
                output=await entry.run(synthetic_request())
                evidence["checks"].append(dict(adapter=kind,case="cognitive_run",success=True,decision_kind=output.decision.decision_kind,http_calls=len(evidence["requests"])-start,world_execution="NOT_RUN"))
            except Exception as error:evidence["checks"].append(dict(adapter=kind,case="cognitive_run",success=False,error_code=code(error),http_calls=len(evidence["requests"])-start))
            finally:await executor.close();save()
            if kind=="custom" and evidence["checks"][-1]["success"]:
                from tests.test_consolidation_evidence_projection import ConsolidationEvidenceProjectionTests
                fixture=ConsolidationEvidenceProjectionTests();fixture.setUp();executor=PrioritizedModelExecutor()
                try:
                    job=fixture.fixture(observations=0,kinds=("dialogue_input",),core_text="我喜欢喝卡布奇诺。")
                    context=fixture.resolver.resolve(job)
                    proposal=await BaseMemoryConsolidator(provider,executor,context_length=32768).consolidate(context)
                    evidence["checks"].append(dict(adapter=kind,case="memory_consolidation_schema",success=True,proposal_type=type(proposal).__name__,canonical_memory_commit="NOT_RUN"))
                except Exception as error:evidence["checks"].append(dict(adapter=kind,case="memory_consolidation_schema",success=False,error_code=code(error)))
                finally:await executor.close();fixture.doCleanups();save()
    finally:
        evidence["actual_http_calls"]=len(evidence["requests"]);save()
    print(json.dumps(dict(actual_http_calls=evidence["actual_http_calls"],checks=evidence["checks"]),ensure_ascii=True))

if __name__=="__main__":
    parser=argparse.ArgumentParser();parser.add_argument("--confirm-paid",action="store_true");parser.add_argument("--key-file",type=Path,required=True);parser.add_argument("--report",type=Path,required=True);args=parser.parse_args()
    if not args.confirm_paid:parser.error("Explicit paid-call confirmation required")
    if args.report.exists():parser.error("Evidence output must be new")
    asyncio.run(run(args))
