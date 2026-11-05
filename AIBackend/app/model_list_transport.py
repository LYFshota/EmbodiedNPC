"""Bounded retries for read-only model lists; never used for generation."""
import asyncio
import logging
import ssl
import time

import httpx

_log = logging.getLogger(__name__)


def transport_error_suffix(error: BaseException) -> str:
    pending, seen, tls = [error], set(), False
    while pending and len(seen) < 24:
        current = pending.pop()
        if id(current) in seen:
            continue
        seen.add(id(current))
        if isinstance(current, ssl.SSLCertVerificationError):
            return "TLS_CERTIFICATE_FAILED"
        tls |= isinstance(current, ssl.SSLError)
        pending.extend(e for e in (current.__cause__, current.__context__) if e is not None)
    if tls:
        return "TLS_FAILED"
    if isinstance(error, httpx.TimeoutException):
        return "TIMEOUT"
    if isinstance(error, (httpx.NetworkError, httpx.RemoteProtocolError)):
        return "CONNECTION_FAILED"
    return "UNAVAILABLE"


async def get_model_list(client, url, *, headers=None, params=None, timeout_seconds=15):
    """At most two GETs within one deadline. HTTP and validation failures are not retried."""
    budget = max(.01, float(timeout_seconds))
    deadline = time.monotonic() + budget
    try:
        async with asyncio.timeout(budget):
            for attempt in (1, 2):
                remaining = max(.001, deadline - time.monotonic())
                try:
                    response = await client.get(url, headers=headers, params=params,
                        timeout=httpx.Timeout(remaining, connect=min(5., remaining / 2)))
                    if attempt == 2:
                        _log.info("MODEL_LIST_RECOVERED attempt=2")
                    return response
                except httpx.HTTPError as error:
                    code = transport_error_suffix(error)
                    _log.warning("MODEL_LIST_%s attempt=%d error_type=%s",
                                 code, attempt, type(error).__name__)
                    if attempt == 2 or code not in {"TLS_FAILED", "CONNECTION_FAILED", "TIMEOUT"}:
                        raise
                    await asyncio.sleep(min(.25, max(.001, (deadline - time.monotonic()) / 4)))
    except TimeoutError:
        raise httpx.ReadTimeout("MODEL_LIST_TIMEOUT") from None
