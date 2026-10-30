from __future__ import annotations

import json
import unittest
from unittest.mock import patch

import httpx

from app.providers import (
    InferenceOptions,
    ModelMessage,
    ModelRequest,
    ModelResult,
    ModelUsage,
    MockProvider,
    OllamaProvider,
)
from tests.test_action_feedback import make_request, reply
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor


class RecordingProvider:
    name = "recording"
    model = "recording-v1"

    def __init__(self, text: str) -> None:
        self.text = text
        self.requests: list[ModelRequest] = []

    async def generate(self, request: ModelRequest) -> ModelResult:
        self.requests.append(request)
        return ModelResult(text=self.text, finish_reason="stop")

    async def health(self):
        raise AssertionError("not used")


class ModelProviderTests(unittest.IsolatedAsyncioTestCase):
    async def test_mock_adapter_uses_the_same_model_request_and_result_contract(self) -> None:
        seen: list[ModelRequest] = []

        def respond(request: ModelRequest) -> ModelResult:
            seen.append(request)
            return ModelResult(
                text='{"ok":true}',
                finish_reason="stop",
                usage=ModelUsage(prompt_tokens=4, completion_tokens=2),
            )

        request = ModelRequest(
            messages=(ModelMessage(role="user", content="reply as JSON"),),
            response_schema={"type": "object"},
        )
        result = await MockProvider(responder=respond).generate(request)

        self.assertEqual([request], seen)
        self.assertEqual('{"ok":true}', result.text)
        self.assertEqual(4, result.usage.prompt_tokens)

    async def test_ollama_adapter_owns_http_payload_and_maps_protocol_metadata(self) -> None:
        captured: dict[str, object] = {}

        def handler(request: httpx.Request) -> httpx.Response:
            captured["url"] = str(request.url)
            captured["body"] = json.loads(request.content)
            return httpx.Response(
                200,
                json={
                    "message": {"content": '{"answer":"ok"}'},
                    "done_reason": "stop",
                    "prompt_eval_count": 11,
                    "eval_count": 5,
                },
            )

        request = ModelRequest(
            messages=(
                ModelMessage(role="system", content="structured output"),
                ModelMessage(role="user", content="hello"),
            ),
            response_schema={"type": "object", "additionalProperties": False},
            inference_options=InferenceOptions(temperature=0.25, think=False, max_output_tokens=512),
        )
        client_type = httpx.AsyncClient
        transport = httpx.MockTransport(handler)
        with patch(
            "app.providers.httpx.AsyncClient",
            side_effect=lambda **kwargs: client_type(transport=transport, **kwargs),
        ):
            result = await OllamaProvider(
                base_url="http://ollama.test",
                model="qwen-test",
                timeout_seconds=1,
                context_length=4096,
            ).generate(request)

        self.assertEqual("http://ollama.test/api/chat", captured["url"])
        body = captured["body"]
        self.assertEqual("qwen-test", body["model"])
        self.assertEqual(4096, body["options"]["num_ctx"])
        self.assertEqual(0.25, body["options"]["temperature"])
        self.assertEqual(512, body["options"]["num_predict"])
        self.assertEqual(request.response_schema, body["format"])
        self.assertEqual("stop", result.finish_reason)
        self.assertEqual(11, result.usage.prompt_tokens)
        self.assertEqual(5, result.usage.completion_tokens)


    async def test_failure_business_path_uses_unified_model_provider_contract(self):
        provider=RecordingProvider(json.dumps(reply(),ensure_ascii=False))
        result=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=32768).run(make_request())
        self.assertEqual(result.decision.utterance,reply()['utterance'])
        self.assertEqual(len(provider.requests),1)
        self.assertEqual(provider.requests[0].response_schema['title'],'CognitiveDecision')


if __name__ == "__main__":
    unittest.main()
