import asyncio
import ssl
import time
import unittest
import httpx
from app.model_list_transport import get_model_list, transport_error_suffix
from app.model_api_config_api import DiscoverModelsRequest, _discover
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.runtime_settings_api import _SAFE_HEALTH_DETAILS

SECRET = "synthetic-private-error-do-not-export"

def tls_error(certificate=False):
    error = httpx.ConnectError(SECRET)
    error.__cause__ = ssl.SSLCertVerificationError(1, SECRET) if certificate else ssl.SSLEOFError(8, SECRET)
    return error

class ModelListTransportTests(unittest.IsolatedAsyncioTestCase):
    async def discover(self, handler):
        return await _discover(DiscoverModelsRequest(provider_type="openai_compatible",
            base_url="https://fixture.invalid/v1", api_key="synthetic-key", timeout_seconds=1),
            httpx.MockTransport(handler))

    async def test_transient_tls_recovers_once_without_logging_secrets(self):
        calls = []
        def handler(req):
            calls.append(req)
            if len(calls) == 1: raise tls_error()
            return httpx.Response(200, json={"data": [{"id": "fixture-model"}]})
        with self.assertLogs("app.model_list_transport", level="INFO") as logs:
            result = await self.discover(handler)
        self.assertTrue(result["success"])
        self.assertEqual(["GET", "GET"], [r.method for r in calls])
        self.assertNotIn(SECRET, str(logs.output))
        self.assertIn("MODEL_LIST_RECOVERED", str(logs.output))

    async def test_permanent_tls_stops_after_two_gets_with_safe_code(self):
        calls = []
        def handler(req):
            calls.append(req); raise tls_error()
        result = await self.discover(handler)
        self.assertEqual(2, len(calls))
        self.assertEqual("MODEL_DISCOVERY_TLS_FAILED", result["error_code"])
        self.assertNotIn(SECRET, str(result))

    async def test_certificate_failure_is_not_retried(self):
        calls = []
        def handler(req):
            calls.append(req); raise tls_error(True)
        result = await self.discover(handler)
        self.assertEqual(1, len(calls))
        self.assertEqual("MODEL_DISCOVERY_TLS_CERTIFICATE_FAILED", result["error_code"])

    async def test_http_failures_and_bad_json_are_not_retried(self):
        for status, code in ((401,"AUTH_FAILED"), (403,"AUTH_FAILED"), (404,"ENDPOINT_NOT_FOUND"),
                             (429,"RATE_LIMITED"), (500,"HTTP_FAILED"), (200,"PROTOCOL_ERROR")):
            calls = []
            def handler(req):
                calls.append(req); return httpx.Response(status, json={})
            with self.subTest(status=status):
                result = await self.discover(handler)
                self.assertEqual(1, len(calls))
                self.assertEqual("MODEL_DISCOVERY_" + code, result["error_code"])

    async def test_deadline_includes_slow_transport(self):
        async def handler(req):
            await asyncio.sleep(1)
            return httpx.Response(200)
        start = time.monotonic()
        async with httpx.AsyncClient(transport=httpx.MockTransport(handler)) as client:
            with self.assertRaises(httpx.TimeoutException):
                await get_model_list(client, "https://fixture.invalid/models", timeout_seconds=.05)
        self.assertLess(time.monotonic() - start, .3)

    async def test_health_classification_is_allowlisted(self):
        for error, code in ((tls_error(),"TLS_FAILED"), (tls_error(True),"TLS_CERTIFICATE_FAILED"),
                            (httpx.ConnectError(SECRET),"CONNECTION_FAILED"), (httpx.ReadTimeout(SECRET),"TIMEOUT")):
            def handler(req): raise error
            health = await DeepSeekOfficialResponsesProvider(base_url="https://api.deepseek.com",
                api_key="synthetic-key", model="deepseek-flash", transport=httpx.MockTransport(handler)).health()
            self.assertEqual("DEEPSEEK_" + code, health.detail)
            self.assertIn(health.detail, _SAFE_HEALTH_DETAILS)
            self.assertFalse(health.available)
            self.assertNotIn(SECRET, health.model_dump_json())

    def test_exception_cycles_are_bounded(self):
        error = httpx.ConnectError(SECRET); error.__cause__ = error
        self.assertEqual("CONNECTION_FAILED", transport_error_suffix(error))


    async def test_generation_post_is_not_retried(self):
        from app.openai_compatible_provider import OpenAICompatibleProvider
        from app.providers import ModelMessage, ModelRequest, ProviderError
        for kind in ("deepseek", "compatible"):
            calls = []
            def handler(req):
                calls.append(req); raise tls_error()
            transport = httpx.MockTransport(handler)
            provider = (DeepSeekOfficialResponsesProvider(base_url="https://api.deepseek.com",
                api_key="synthetic-key", model="deepseek-flash", transport=transport) if kind == "deepseek"
                else OpenAICompatibleProvider(base_url="https://fixture.invalid/v1",
                    api_key="synthetic-key", model="fixture-model", transport=transport))
            with self.assertRaises(ProviderError):
                await provider.generate(ModelRequest(messages=(ModelMessage(role="user", content="synthetic"),)))
            self.assertEqual(["POST"], [r.method for r in calls])

    async def test_compatible_rate_limit_is_distinct_and_one_get(self):
        from app.openai_compatible_provider import OpenAICompatibleProvider
        for status, code in ((400,"BAD_REQUEST"),(401,"AUTH_FAILED"),(404,"ENDPOINT_NOT_FOUND"),(429,"RATE_LIMITED")):
            calls = []
            def handler(req):
                calls.append(req); return httpx.Response(status, text=SECRET)
            health = await OpenAICompatibleProvider(base_url="https://fixture.invalid/v1",
                api_key="synthetic-key", model="fixture-model", transport=httpx.MockTransport(handler)).health()
            self.assertEqual("OPENAI_COMPATIBLE_" + code, health.detail)
            self.assertIn(health.detail,_SAFE_HEALTH_DETAILS)
            self.assertEqual(1,len(calls))
