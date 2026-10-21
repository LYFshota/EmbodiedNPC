"""One integration test of the real m3_main assembly, isolated from developer data.

Only model I/O is deterministic. Episodes/jobs/indexes are produced by lifespan
workers, not manually closed, claimed, finalized, or indexed by the test.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest


def dialogue_append(*, response=False, utterance='我最喜欢蓝色。', directive=None):
    from tests.test_memory_d2_cognitive_steps import OWNER, PLAYER
    payload = {'direction': 'npc_to_player' if response else 'player_to_npc'}
    if directive:
        payload['episode_directive'] = directive
    return dict(message_type='experience_append_request', schema_version='1.0',
        request_id='append_response' if response else 'append_input', trace_id='trace_readiness',
        draft=dict(schema_version='experience-draft/1.1', observer_id='npc_cafe_01',
            event_type='dialogue_response' if response else 'dialogue_input', source_type='dialogue',
            source_session_id='readiness_session', clock_domain='unity_realtime', source_event_id='utterance_01',
            actor_entity_id='npc_cafe_01' if response else 'player_01',
            target_entity_ids=['player_01' if response else 'npc_cafe_01'],
            persistent_participants=[dict(entity_id=entity, identity_namespace=identity.identity_namespace,
                identity_type=identity.identity_type, persistent_id=identity.persistent_id)
                for entity, identity in [('npc_cafe_01', OWNER), ('player_01', PLAYER)]],
            natural_language_payload=utterance, structured_payload=payload,
            occurred_at=11.0 if response else 10.0, observed_at=11.0 if response else 10.0,
            request_id='request_d2', trace_id='trace_d2', conversation_id='readiness_conversation',
            provenance='EXECUTED_BY_SELF' if response else 'SYSTEM_CONFIRMED'))


def cognitive_wire(text, suffix=''):
    from tests.test_memory_d2_cognitive_steps import cognitive_request
    request = cognitive_request(text, actions=False)
    wire = request.model_dump()
    # The trusted transport envelope is excluded from ordinary model serialization.
    wire['recall_identity'] = request.recall_identity.model_dump()
    wire['request_id'] += suffix
    wire['trace_id'] += suffix
    return wire


def wait_for(predicate, timeout=10):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(.02)
    raise AssertionError('Readiness state did not arrive before deadline')


def exercise_assembly():
    from contextlib import closing
    from threading import Event
    from unittest.mock import patch
    from fastapi.testclient import TestClient
    from app import m3_main as backend
    from app.providers import ModelResult, OllamaProvider, ProviderHealth
    from app.canonical_embeddings import OllamaEmbeddingProvider
    from app.consolidation_state import ConsolidationStateRepository
    from app.cognitive_step_models import MemoryRecallPlan
    from tests.test_memory_d2_cognitive_steps import recall, cognitive_request

    embedding_ready = Event()
    cognitive_calls = []

    async def generate(provider, request):
        if request.messages[0].content.startswith('Consolidate'):
            context = json.loads(request.messages[-1].content.split('\n', 1)[1])
            event = next(e for e in context['evidence_events'] if e['event_type'] == 'dialogue_input')
            output = dict(episodic_memories=[], semantic_facts=[dict(
                subject_handle=event['participant_handles']['player_01'], fact='玩家最喜欢蓝色。',
                source_excerpt=event['natural_language_payload'], evidence_event_ids=[event['event_id']], confidence=.9)])
        elif request.messages[0].content.startswith("You are interpreting one NPC's direct dialogue"):
            output = dict(outcome='NO_CHANGE', interpretation=None)
        else:
            cognitive_calls.append(request)
            if len(cognitive_calls) == 1:
                output = dict(decision_kind='DIRECT_RESPONSE', utterance='知道了。', episode_directive='END')
            elif len(cognitive_calls) == 2:
                output = recall(request)
            else:
                assert '蓝色' in request.messages[-1].content
                output = dict(decision_kind='DIRECT_RESPONSE', utterance='你之前说喜欢蓝色。')
        return ModelResult(text=json.dumps(output, ensure_ascii=False))

    async def health(provider):
        return ProviderHealth(provider=provider.name, model=provider.model, available=True)

    def embed(provider, text):
        if not embedding_ready.is_set():
            raise RuntimeError('test deliberately holds derived index pending')
        return (1.0, 0.0, 0.0)

    def rows(sql):
        with closing(backend.database.connect()) as db:
            return [dict(row) for row in db.execute(sql)]

    with patch.object(OllamaProvider, 'generate', generate), patch.object(OllamaProvider, 'health', health), \
            patch.object(OllamaEmbeddingProvider, 'embed', embed):
        with TestClient(backend.app) as client:
            assert client.get('/health').json()['status'] == 'ok'
            assert rows('SELECT version FROM schema_migrations WHERE version=13')
            owned = [backend.episode_processing_loop, backend.memory_consolidation_worker,
                     backend.canonical_embedding_indexer, backend.memory_maintenance_worker]
            assert all(w._task is not None and not w._task.done() for w in owned)
            assert backend.base_memory_consolidator.executor is backend.model_executor
            assert backend.cognitive_entry._model_executor is backend.model_executor
            assert backend.memory_maintenance_worker.executor is backend.model_executor
            assert backend.cognitive_entry._memory_recall._retrieval._repository._ranker.provider is backend.canonical_embedding_provider
            with client.websocket_connect('/ws') as ws:
                message = dialogue_append()
                ws.send_json(message)
                assert ws.receive_json()['status'] == 'inserted'
                ws.send_json(message)
                assert ws.receive_json()['status'] == 'already_exists'
                # The capacity-aware assembler now holds an unanswered input
                # until its displayed reply (or idle boundary), instead of
                # publishing half of the interaction as an OPEN episode.
                assert not rows("SELECT * FROM episodes WHERE status='OPEN'")
                assert not rows('SELECT * FROM memory_jobs')
                ws.send_json(cognitive_wire('我最喜欢蓝色。'))
                response = ws.receive_json()
                assert response['message_type'] == 'cognitive_response'
                # Simulate only Unity's presentation ACK adapter, retaining the model directive.
                ws.send_json(dialogue_append(response=True, utterance=response['decision']['utterance'],
                    directive=response['decision']['episode_directive']))
                assert ws.receive_json()['status'] == 'inserted'
                jobs = wait_for(lambda: rows("SELECT * FROM memory_jobs WHERE status='SUCCEEDED'"))
                assert len(jobs) == 1 and jobs[0]['extractor_version'] == 'memory-consolidator/3.0'
                assert rows('SELECT close_reason FROM episodes')[0]['close_reason'] == 'semantic_end'
                assert len(rows('SELECT * FROM experience_events')) == 2
                assert len(rows('SELECT * FROM memory_records')) == 1
                assert len(rows('SELECT * FROM memory_subjects')) == 1
                assert len(rows('SELECT * FROM memory_evidence')) == 1
                assert rows('SELECT * FROM memory_semantic_facts')[0]['source_excerpt'] == '我最喜欢蓝色。'
                assert not rows('SELECT * FROM memory_profile_facts')
                state = ConsolidationStateRepository(backend.database.connect).get('npc_cafe_01', 'memory-consolidator/3.0')
                assert state == 2
                assert not rows('SELECT * FROM memory_embeddings')
                adapter = backend.cognitive_entry._memory_recall
                catalog = adapter.prepare(cognitive_request(actions=False))
                pending = adapter.recall(catalog, MemoryRecallPlan(semantic_query='蓝色', memory_type='SEMANTIC_FACT'))
                assert pending.status == 'OK' and pending.evidence.items[0].summary == '玩家最喜欢蓝色。'
                embedding_ready.set()
                wait_for(lambda: rows('SELECT * FROM memory_embeddings'))
                ws.send_json(cognitive_wire('你记得我最喜欢什么颜色吗？', '_recall'))
                assert ws.receive_json()['decision']['decision_kind'] == 'DIRECT_RESPONSE'
                assert len(cognitive_calls) == 3
            tasks = [w._task for w in owned]
        assert all(t.done() for t in tasks)
        assert all(w._task is None for w in owned)


def read_after_restart():
    """Reconstruct the actual backend in a fresh process against the saved test DB."""
    from contextlib import closing
    from unittest.mock import patch
    from app import m3_main as backend
    from app.canonical_embeddings import OllamaEmbeddingProvider
    from app.cognitive_step_models import MemoryRecallPlan
    from tests.test_memory_d2_cognitive_steps import cognitive_request
    adapter = backend.cognitive_entry._memory_recall
    with patch.object(OllamaEmbeddingProvider, 'embed', return_value=(1.0, 0.0, 0.0)):
        outcome = adapter.recall(adapter.prepare(cognitive_request(actions=False)),
            MemoryRecallPlan(semantic_query='蓝色', memory_type='SEMANTIC_FACT'))
    assert outcome.status == 'OK'
    assert outcome.evidence.items[0].summary == '玩家最喜欢蓝色。'
    assert outcome.evidence.items[0].source_excerpt == '我最喜欢蓝色。'
    assert outcome.evidence.items[0].pipeline_version == 'memory-consolidator/3.0'
    with closing(backend.database.connect()) as db:
        assert db.execute('SELECT COUNT(*) FROM memory_embeddings').fetchone()[0] == 1
        assert not db.execute('PRAGMA foreign_key_check').fetchall()


class BackendMemoryReadinessTests(unittest.TestCase):
    def test_formal_lifespan_dialogue_to_automatic_canonical_index_and_d2(self):
        with tempfile.TemporaryDirectory() as directory:
            env = {**os.environ, 'EMBODIEDNPC_MEMORY_DB': str(Path(directory) / 'readiness.sqlite3'),
                'EMBODIEDNPC_PROVIDER': 'ollama',
                'OLLAMA_NUM_CTX': '16384',
                'EMBODIEDNPC_MODEL_CONFIG_FILE': str(Path(directory) / 'model-config.json'),
                'EMBODIEDNPC_SETTINGS_FILE': str(Path(directory) / 'settings.json'),
                'CANONICAL_EMBEDDING_ENABLED': 'true', 'CANONICAL_EMBEDDING_INTERVAL_SECONDS': '1',
                'EPISODE_POLL_INTERVAL_SECONDS': '.02', 'MEMORY_JOB_POLL_INTERVAL_SECONDS': '.02',
                }
            result = subprocess.run([sys.executable, '-m', 'tests.test_backend_memory_readiness', '--exercise'],
                cwd=Path(__file__).resolve().parents[1], env=env, capture_output=True, text=True,
                encoding='utf-8', errors='replace', timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            restarted = subprocess.run([sys.executable, '-m', 'tests.test_backend_memory_readiness', '--read-saved'],
                cwd=Path(__file__).resolve().parents[1], env=env, capture_output=True, text=True,
                encoding='utf-8', errors='replace', timeout=30)
            self.assertEqual(restarted.returncode, 0, restarted.stdout + restarted.stderr)


if __name__ == '__main__':
    if '--exercise' in sys.argv:
        exercise_assembly()
    elif '--read-saved' in sys.argv:
        read_after_restart()
    else:
        unittest.main()
