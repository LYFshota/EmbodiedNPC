"""Small shared lifecycle primitives for existing in-process memory workers."""
import asyncio
import threading
from functools import partial

async def drain_task(task):
    """Cancellation never detaches a native store operation or owned attempt."""
    cancelled = False
    while not task.done():
        try: await asyncio.shield(task)
        except asyncio.CancelledError:
            if task.done() and task.cancelled(): raise
            cancelled = True
    result = task.result()
    if cancelled: raise asyncio.CancelledError
    return result

async def store_call(fn, *args, **kwargs):
    return await drain_task(asyncio.create_task(asyncio.to_thread(partial(fn, *args, **kwargs))))

async def owned_attempt(factory):
    """Cancel inference promptly; first join claim, then join all native cleanup."""
    claimed = asyncio.Event()
    task = asyncio.create_task(factory(claimed))
    try:
        return await asyncio.shield(task)
    except asyncio.CancelledError:
        async def cancel_and_join():
            await claimed.wait()
            task.cancel()
            try: await task
            except asyncio.CancelledError: pass
        await drain_task(asyncio.create_task(cancel_and_join()))
        raise

class ActiveAttempts:
    """Single-process ownership; empty on restart, persistent generations still rule.

    Claim and stale recovery use the same lock. Expiration is enforced by the
    owning coroutine, never by removing protection from a still-running write.
    """
    def __init__(self):
        self.lock = threading.RLock()
        self._attempts = {}
    def add(self, job, scope=None):
        key = (job['job_id'], job['attempt_generation']) if isinstance(job, dict) else (job.job_id, job.attempt_generation)
        with self.lock: self._attempts[key] = scope
    def discard(self, job):
        key = (job['job_id'], job['attempt_generation']) if isinstance(job, dict) else (job.job_id, job.attempt_generation)
        with self.lock: self._attempts.pop(key, None)
    def contains(self, job_id, generation):
        with self.lock: return (job_id, generation) in self._attempts
    def exclusions(self, alias=''):
        """Bounded SQL exclusions shared by Maintenance and DerivedInsight."""
        with self.lock:
            scopes = set(s for s in self._attempts.values() if s is not None)
        prefix = alias + '.' if alias else ''
        clause, args = '', []
        for owner, subject in sorted(scopes):
            clause += f' AND NOT ({prefix}owner_json=? AND {prefix}subject_json=?)'
            args.extend((owner, subject))
        return clause, args

async def run_pool(process_once, stop, *, concurrency, interval, on_error):
    """Fixed lanes, bounded claims, and wake idle lanes when a dependency clears."""
    changed = asyncio.Condition()
    revision = 0

    async def lane():
        nonlocal revision
        while not stop.is_set():
            observed = revision
            try:
                if await process_once():
                    async with changed:
                        revision += 1
                        changed.notify_all()
                    continue
            except Exception:
                on_error()
            async with changed:
                try:
                    await asyncio.wait_for(changed.wait_for(
                        lambda: stop.is_set() or revision != observed), interval)
                except TimeoutError:
                    pass

    async def notify_stop():
        await stop.wait()
        async with changed:
            changed.notify_all()

    async def supervise():
        notifier = asyncio.create_task(notify_stop())
        try:
            await asyncio.gather(*(lane() for _ in range(concurrency)))
        finally:
            notifier.cancel()
            await asyncio.gather(notifier, return_exceptions=True)

    await drain_task(asyncio.create_task(supervise()))
