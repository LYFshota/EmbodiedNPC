"""Thinking-control boundaries; synthetic transports only, no paid requests."""
import json
import unittest
import httpx
from app.model_compatibility import _CACHE, ProbeBudget, initial_policy, policy_for
from app.model_config_runtime import build_main_provider
from app.providers import InferenceOptions, ModelRequest, ProviderError
from app.task_capacity import task_options, task_policy
from tests.test_model_compatibility import config, good, REQUEST


class ThinkingControlContractTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        _CACHE.clear()

    async def test_disable_mapping_and_no_unknown_gateway_inference(self):
        for style, expected in (("auto", {}), ("thinking", {"thinking": {"type": "disabled"}}),
                                ("enable_thinking", {"enable_thinking": False}),
                                ("reasoning_effort", {"reasoning_effort": "none"})):
            seen = []
            def send(request):
                seen.append(json.loads(request.content))
                return good()
            c = config(model="kimi-k3", thinking_parameter=style)
            await build_main_provider(c, httpx.MockTransport(send)).generate(REQUEST)
            self.assertEqual({k: seen[0][k] for k in ("thinking", "enable_thinking", "reasoning_effort") if k in seen[0]}, expected)
            self.assertEqual(seen[0]["max_tokens"], 128)
            self.assertEqual(c.thinking_parameter, style)

    async def test_reasoning_with_disabled_intent_is_not_accepted_even_with_total_field(self):
        for style, code in (("auto", "THINKING_CONTROL_UNVERIFIED"), ("thinking", "THINKING_DISABLE_NOT_HONORED"),
                            ("reasoning_effort", "THINKING_DISABLE_NOT_HONORED")):
            c = config(token_parameter="max_completion_tokens", thinking_parameter=style)
            p = build_main_provider(c, httpx.MockTransport(lambda r: good(reasoning_content="synthetic")))
            with self.assertRaisesRegex(ProviderError, code):
                await p.generate(REQUEST)

    async def test_explicit_enabled_total_budget_is_supported_without_double_reservation(self):
        c = config(thinking=True, token_parameter="max_completion_tokens", thinking_parameter="reasoning_effort", reasoning_effort="low")
        seen = []
        def send(request):
            seen.append(json.loads(request.content))
            return good(reasoning_content="synthetic")
        p = build_main_provider(c, httpx.MockTransport(send))
        for _ in range(2):
            options = p.cognitive_options(InferenceOptions(think=True, max_output_tokens=128), 32768)
            self.assertEqual(options.output_token_reserve, 128)
            await p.generate(REQUEST.model_copy(update={"inference_options": options}))
        self.assertEqual(len(seen), 2)
        self.assertEqual(seen[0]["max_completion_tokens"], 128)
        self.assertEqual(seen[0]["reasoning_effort"], "low")
        self.assertNotIn("max_tokens", seen[0])

    async def test_enabled_thinking_with_visible_only_budget_never_calls_transport(self):
        seen = []
        p = build_main_provider(config(thinking=True), httpx.MockTransport(lambda r: seen.append(r) or good()))
        with self.assertRaisesRegex(ProviderError, "TOTAL_GENERATION_BOUND_UNVERIFIED"):
            p.cognitive_options(InferenceOptions(think=True, max_output_tokens=128), 32768)
        with self.assertRaisesRegex(ProviderError, "TOTAL_GENERATION_BOUND_UNVERIFIED"):
            await p.generate(REQUEST.model_copy(update={"inference_options": InferenceOptions(think=True, max_output_tokens=128)}))
        self.assertFalse(seen)

    async def test_usage_reasoning_without_reasoning_text_still_honors_disabled_intent(self):
        for usage in ({"completion_tokens": 8, "completion_tokens_details": {"reasoning_tokens": 2}},
                      {"completion_tokens": 8, "completion_thinking_tokens": 2}):
            response = httpx.Response(200, json={"choices": [{"message": {"content": '{"ok":true}'}, "finish_reason": "stop"}], "usage": usage})
            with self.assertRaisesRegex(ProviderError, "THINKING_DISABLE_NOT_HONORED"):
                await build_main_provider(config(thinking_parameter="thinking"), httpx.MockTransport(lambda r: response)).generate(REQUEST)

    async def test_reasoning_usage_is_a_subset_not_added_twice(self):
        response = httpx.Response(200, json={"choices": [{"message": {"content": '{"ok":true}', "reasoning_content": "synthetic"}, "finish_reason": "stop"}],
            "usage": {"completion_tokens": 128, "completion_tokens_details": {"reasoning_tokens": 100}}})
        p = build_main_provider(config(thinking=True, token_parameter="max_completion_tokens"), httpx.MockTransport(lambda r: response))
        options = p.cognitive_options(InferenceOptions(think=True, max_output_tokens=128), 32768)
        result = await p.generate(REQUEST.model_copy(update={"inference_options": options}))
        self.assertEqual((result.usage.completion_tokens, result.usage.reasoning_tokens), (128, 100))

    async def test_total_overflow_and_truncation_remain_distinct(self):
        for tokens, finish, code in ((129, "stop", "OUTPUT_RESERVE_VIOLATED"), (128, "length", "OUTPUT_TRUNCATED")):
            response = httpx.Response(200, json={"choices": [{"message": {"content": '{"ok":true}', "reasoning_content": "synthetic"}, "finish_reason": finish}], "usage": {"completion_tokens": tokens}})
            p = build_main_provider(config(thinking=True, token_parameter="max_completion_tokens"), httpx.MockTransport(lambda r: response))
            options = p.cognitive_options(InferenceOptions(think=True, max_output_tokens=128), 32768)
            with self.assertRaisesRegex(ProviderError, code):
                await p.generate(REQUEST.model_copy(update={"inference_options": options}))

    async def test_failed_probe_reasoning_state_does_not_poison_new_runtime_or_draft(self):
        c = config()
        budget = ProbeBudget()
        failed = build_main_provider(c, httpx.MockTransport(lambda r: good(reasoning_content="synthetic")), probe_budget=budget)
        with self.assertRaisesRegex(ProviderError, "THINKING_CONTROL_UNVERIFIED"):
            await failed.generate(REQUEST)
        with self.assertRaisesRegex(ProviderError, "THINKING_CONTROL_UNVERIFIED"):
            failed.cognitive_options(REQUEST.inference_options, 32768)
        self.assertEqual(budget.calls, 1)
        self.assertFalse(_CACHE)
        for new_config in (c, c.model_copy(update={"model": "glm-5.2"}), c.model_copy(update={"thinking_parameter": "thinking"})):
            fresh = build_main_provider(new_config, httpx.MockTransport(lambda r: good()))
            fresh.cognitive_options(REQUEST.inference_options, 32768)
            await fresh.generate(REQUEST)
            self.assertEqual(policy_for(new_config), initial_policy(new_config))

    def test_background_tasks_do_not_silently_inherit_enabled_reasoning(self):
        p = build_main_provider(config(thinking=True, token_parameter="max_completion_tokens", thinking_parameter="reasoning_effort"))
        policy = task_policy(p, "consolidation", 32768)
        options = task_options(p, policy)
        self.assertFalse(options.think)
        self.assertEqual((options.max_output_tokens, options.output_token_reserve), (4096, 4096))
        self.assertEqual(p.budget_payload(REQUEST.model_copy(update={"inference_options": options}))["reasoning_effort"], "none")

    def test_context_reserve_still_blocks_before_network(self):
        p = build_main_provider(config(thinking=True, token_parameter="max_completion_tokens"))
        with self.assertRaisesRegex(ProviderError, "OUTPUT_RESERVE_EXCEEDS_CONTEXT"):
            p.cognitive_options(InferenceOptions(think=True, max_output_tokens=128), 640)
