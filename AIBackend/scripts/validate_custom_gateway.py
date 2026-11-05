"""Explicit, bounded gateway acceptance. Credentials exist only in process env."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import httpx
from validate_model_compatibility import EvidenceTransport, code
from app.model_api_config import MainModelConfig
from app.model_compatibility import ProbeBudget
from app.model_api_config_api import _test_main
from app.model_config_runtime import build_main_provider
from app.cognitive_entry import CognitiveBackendEntry
from app.model_profile_probes import synthetic_request, contract_probe
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions

async def run(args):
    key=os.environ.pop("EMBODIEDNPC_VALIDATION_API_KEY")
    if args.list_only:
        async with httpx.AsyncClient(timeout=20,trust_env=False) as client:
            response=await client.get(args.base_url.rstrip("/")+"/models",headers={"Authorization":"Bearer "+key})
        result={"status":response.status_code,"models":[]}
        if response.status_code==200:
            try:result["models"]=[r["id"] for r in response.json()["data"] if isinstance(r.get("id"),str)][:400]
            except (ValueError,KeyError,TypeError):result["error_code"]="MODEL_DISCOVERY_PROTOCOL_ERROR"
        print(json.dumps(result,ensure_ascii=True));return
    if not args.confirm_paid or not args.model or not args.report or args.report.exists():raise ValueError("NEW_REPORT_AND_PAID_CONFIRMATION_REQUIRED")
    evidence=dict(max_http_calls=args.max_calls,requests=[],checks=[],player_data_used=False)
    transport=EvidenceTransport(evidence)
    def save():args.report.write_text(json.dumps(evidence,ensure_ascii=False,indent=2),encoding="utf-8")
    try:
        for model in args.model[:3]:
            config=MainModelConfig(display_name="Isolated gateway validation",provider_type="openai_compatible",base_url=args.base_url,api_key=key,model=model,thinking=False,max_output_tokens=512,context_length=32768,timeout_seconds=30.0,schema_profile=args.schema_profile,token_parameter="auto",thinking_parameter="auto",send_temperature=None)
            start=len(evidence["requests"])
            try:result=dict(success=True,basis="explicit_confirmed_transport") if args.cognition_only else await contract_probe(config,transport,probe_budget=ProbeBudget()) if args.contracts_only else await _test_main(config,transport,probe_budget=ProbeBudget())
            except Exception as error:result=dict(success=False,error_code=code(error))
            if not args.cognition_only:evidence["checks"].append(dict(model=model,case="contracts" if args.contracts_only else "connection",**result))
            save()
            if not result["success"]:continue
            executor=PrioritizedModelExecutor()
            try:
                provider=build_main_provider(config,transport)
                result=await CognitiveBackendEntry(provider,executor,inference_options=InferenceOptions(think=False,max_output_tokens=512),context_length=32768).run(synthetic_request())
                evidence["checks"].append(dict(model=model,case="cognitive_run",success=True,decision_kind=result.decision.decision_kind,world_execution="NOT_RUN"))
            except Exception as error:evidence["checks"].append(dict(model=model,case="cognitive_run",success=False,error_code=code(error)))
            finally:await executor.close();save()
    finally:evidence["actual_http_calls"]=len(evidence["requests"]);save()
    print(json.dumps(dict(actual_http_calls=evidence["actual_http_calls"],checks=evidence["checks"]),ensure_ascii=True))
if __name__=="__main__":
    p=argparse.ArgumentParser();p.add_argument("--base-url",required=True);p.add_argument("--list-only",action="store_true");p.add_argument("--confirm-paid",action="store_true");p.add_argument("--contracts-only",action="store_true");p.add_argument("--cognition-only",action="store_true");p.add_argument("--schema-profile",choices=("auto","json_object"),default="auto");p.add_argument("--model",action="append");p.add_argument("--report",type=Path);p.add_argument("--max-calls",type=int,default=12,choices=range(1,13));asyncio.run(run(p.parse_args()))
