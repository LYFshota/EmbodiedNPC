"""Pair-bound maintenance through native admission, transactions and recall."""
import asyncio
from dataclasses import replace
import json
import unittest
from contextlib import closing

from app.local_model_scheduler import PrioritizedModelExecutor, PreemptedByForeground
from app.memory_maintenance_models import MaintenanceDecisionResponse, MaintenanceResponse
from app.memory_maintenance_worker import (
    MemoryMaintenanceWorker, MaintenanceOutputError, maintenance_request,
    parse_maintenance_response, bind_maintenance_decisions,
)
from app.providers import ModelResult
from tests import test_memory_maintenance as base


def judgments(context, changes=None):
    changes = changes or {}
    decisions = []
    for index, pair in enumerate(context.comparison_pairs):
        item = dict(pair_ref=f'p{index}', relation='NO_RELATION', direction='LEFT_TO_RIGHT', reason='Independent facts.')
        for (a, b), (relation, excerpt) in changes.items():
            if set(pair) == {a, b}:
                item.update(relation=relation, direction='LEFT_TO_RIGHT' if pair[0] == a else 'RIGHT_TO_LEFT')
                if excerpt:
                    record = next(r for r in context.records if r['memory_id'] == a)
                    item.update(source_excerpt=excerpt, evidence_event_ids=[record['excerpt_event_id']])
        decisions.append(item)
    return {'decisions': decisions}


class MemoryMaintenancePairTests(unittest.TestCase):
    setUp = base.MemoryMaintenanceTests.setUp
    add = base.MemoryMaintenanceTests.add
    batch = base.MemoryMaintenanceTests.batch
    read = base.MemoryMaintenanceTests.read

    def bind(self, context, body):
        return bind_maintenance_decisions(context, parse_maintenance_response(
            ModelResult(text=json.dumps(body)), MaintenanceDecisionResponse))

    def commit(self, context, body):
        bound, response = self.bind(context, body)
        self.assertTrue(self.repo.finalize(bound, response, self.now))
        return bound, response

    def test_program_binds_identity_and_no_relation_has_an_audit_without_a_link(self):
        self.add('我喜欢红茶。')
        self.add('我喜欢画画。')
        context = self.batch()
        request = maintenance_request(context)
        wire = request.model_dump_json()
        for memory_id in context.new_ids:
            self.assertNotIn(memory_id, wire)
        before = self.f.rows('SELECT * FROM memory_records ORDER BY memory_id')
        bound, response = self.commit(context, judgments(context))
        self.assertEqual(response.operations, ())
        self.assertEqual(before, self.f.rows('SELECT * FROM memory_records ORDER BY memory_id'))
        self.assertEqual([], self.f.rows('SELECT * FROM memory_relations'))
        run = self.f.rows('SELECT * FROM memory_maintenance_runs')[0]
        scope = json.loads(run['input_json'])['comparison_scope']
        self.assertEqual(scope['compared_pairs'], [list(p) for p in context.comparison_pairs])
        self.assertEqual(scope['decisions'][0]['relation'], 'NO_RELATION')
        self.assertFalse(json.loads(run['input_json'])['candidate_search_is_exhaustive'])
        self.assertFalse(self.repo.finalize(bound, response, self.now))

    def test_duplicate_refines_and_conditional_coexist_keep_original_evidence(self):
        a = self.add('我喜欢咖啡。')
        b = self.add('我喜欢喝咖啡。')
        c = self.add('我喜欢咖啡，尤其是热拿铁。')
        d = self.add('我晚上避免咖啡因，但白天可以喝咖啡。')
        context = self.batch()
        before = self.f.rows('SELECT * FROM memory_evidence ORDER BY memory_id')
        self.commit(context, judgments(context, {(b,a):('DUPLICATE',None),
            (c,a):('REFINES',None), (d,a):('COEXISTS',None)}))
        self.assertEqual({r['relation'] for r in self.f.rows('SELECT * FROM memory_relations')},
                         {'DUPLICATE','REFINES','COEXISTS'})
        self.assertEqual(before, self.f.rows('SELECT * FROM memory_evidence ORDER BY memory_id'))
        self.assertEqual({r['status'] for r in self.f.rows('SELECT * FROM memory_records')}, {'ACTIVE'})

    def test_new_new_direction_preserves_withdrawal_restoration_and_history(self):
        a = self.add('我喜欢拿铁。', occurred=10)
        b = self.add('现在不喜欢拿铁了。', occurred=20)
        c = self.add('现在又喜欢拿铁了。', occurred=30)
        context = self.batch()
        self.assertEqual(len(context.comparison_pairs), 3)
        self.commit(context, judgments(context, {(b,a):('SUPERSEDES','现在不喜欢拿铁了'),
            (c,b):('SUPERSEDES','现在又喜欢拿铁了')}))
        self.assertEqual([r.memory_id for r in self.read(context).items], [c])
        self.assertEqual({r.memory_id for r in self.read(context, history=True).items}, {a,b,c})
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_evidence')), 3)

    def test_conflicts_and_undetermined_do_not_choose_a_winner_or_invent_a_link(self):
        a = self.add('我在海边长大。')
        b = self.add('我在山里长大。')
        c = self.add('如果有时间，我打算去海边。')
        context = self.batch()
        self.commit(context, judgments(context, {(b,a):('CONFLICTS',None), (c,a):('UNDETERMINED',None)}))
        self.assertEqual([r['relation'] for r in self.f.rows('SELECT * FROM memory_relations')], ['CONFLICTS'])
        rows = {r['memory_id']:r for r in self.f.rows('SELECT * FROM memory_records')}
        self.assertEqual(rows[c]['summary'], '如果有时间，我打算去海边。')
        self.assertEqual(rows[a]['status'], 'CONFLICTED')
        self.assertEqual(rows[b]['status'], 'CONFLICTED')

    def test_bad_shape_reference_membership_and_evidence_are_rejected_not_repaired(self):
        self.add('我喜欢红茶。')
        self.add('我喜欢画画。')
        context = self.batch()
        good = judgments(context)
        d = good['decisions'][0]
        cases = [{}, {'operations':[]}, {'decisions':[]}, {'decisions':[d,d]},
            {'decisions':[{**d,'pair_ref':'p7'}]}, {'decisions':[{**d,'new_memory_id':context.new_ids[0]}]},
            {'decisions':[{**d,'old_memory_id':context.new_ids[1]}]},
            {'decisions':[{**d,'evidence_event_ids':['foreign_event']}]},
            {'decisions':[{**d,'reason':' '}]}, {'decisions':[{**d,'relation':'DELETE'}]}]
        for body in cases:
            with self.subTest(body=body), self.assertRaises(MaintenanceOutputError):
                self.bind(context, body)
        with self.assertRaisesRegex(MaintenanceOutputError, 'TRUNCATED'):
            parse_maintenance_response(ModelResult(text=json.dumps(good),finish_reason='length'), MaintenanceDecisionResponse)
        self.assertEqual([], self.f.rows('SELECT * FROM memory_maintenance_runs'))

    def drain(self):
        while (job := self.repo.claim(self.now,self.policy)) is not None:
            context = self.repo.prepare(job,self.policy)
            self.repo.finalize(context,MaintenanceResponse(operations=()),self.now)

    def test_old_endpoint_cannot_become_new_via_direction_or_evidence(self):
        old = self.add('我喜欢拿铁。')
        self.drain()
        new = self.add('现在不喜欢拿铁了。')
        context = self.batch()
        good = judgments(context)
        self.assertEqual(context.comparison_pairs, ((new,old),))
        bad = {'decisions':[{**good['decisions'][0], 'direction':'RIGHT_TO_LEFT'}]}
        with self.assertRaisesRegex(MaintenanceOutputError, 'DIRECTION_INVALID'):
            self.bind(context,bad)
        old_event = next(r for r in context.records if r['memory_id']==old)['excerpt_event_id']
        bad = {'decisions':[{**good['decisions'][0], 'evidence_event_ids':[old_event]}]}
        with self.assertRaisesRegex(MaintenanceOutputError, 'EVIDENCE_INVALID'):
            self.bind(context,bad)

    def test_wire_contract_survives_existing_schema_profiles(self):
        from jsonschema import Draft202012Validator
        from app.model_schema_projection import project_schema, unwrap_text
        self.add('我喜欢红茶。'); self.add('我喜欢画画。')
        context=self.batch()
        original=maintenance_request(context).response_schema
        body=MaintenanceDecisionResponse.model_validate(judgments(context)).model_dump(mode='json')
        for profile in ('raw','portable_union','openai_strict','anthropic'):
            with self.subTest(profile=profile):
                schema,wrapped=project_schema(original,profile)
                Draft202012Validator.check_schema(schema)
                self.assertTrue(Draft202012Validator(schema).is_valid(body))
                restored=unwrap_text(json.dumps(body),wrapped,original,profile)
                response=parse_maintenance_response(ModelResult(text=restored),MaintenanceDecisionResponse)
                self.assertEqual(bind_maintenance_decisions(context,response)[1].operations,())

    def test_repository_still_rejects_valid_record_outside_selected_pair_plan(self):
        from app.memory_maintenance_models import MaintenanceOperation, MaintenanceAdmissionError
        self.add('我喜欢红茶。'); self.add('我喜欢画画。'); self.add('我喜欢散步。')
        context=self.batch()
        a,b=context.comparison_pairs[1]
        narrowed=replace(context,comparison_pairs=context.comparison_pairs[:1])
        response=MaintenanceResponse(operations=(MaintenanceOperation(new_memory_id=a,old_memory_id=b,
            relation='COEXISTS',reason='not a supplied pair'),))
        with self.assertRaisesRegex(MaintenanceAdmissionError,'OPERATION_OUTSIDE_INPUT'):
            self.repo.finalize(narrowed,response,self.now)
        self.assertEqual([],self.f.rows('SELECT * FROM memory_maintenance_runs'))

    def test_eight_pair_boundary_records_omitted_scope_and_rejects_ninth_output(self):
        for i in range(12):
            self.add(f'我喜欢咖啡，第{i}种口味也愿意尝试。')
        self.drain()
        self.add('我现在仍然喜欢咖啡。')
        context = self.batch()
        self.assertEqual(len(context.comparison_pairs), 8)
        self.assertGreater(len(context.candidate_pairs), 8)
        body = judgments(context)
        body['decisions'].append(dict(body['decisions'][0]))
        with self.assertRaises(MaintenanceOutputError):
            self.bind(context,body)
        body = judgments(context)
        for d in body['decisions']:
            d['relation']='COEXISTS'  # Scripted representation test, not a semantic claim.
        self.commit(context,body)
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_relations')),8)
        scope = json.loads(self.f.rows('SELECT * FROM memory_maintenance_runs ORDER BY completed_at DESC,attempt_generation DESC')[0]['input_json'])['comparison_scope']
        self.assertGreater(scope['omitted_pair_count'],0)
        self.assertEqual(scope['completion_means'],'bounded_sample_only')

    def test_ranker_keeps_important_older_change_candidate_ahead_of_recent_noise(self):
        target = self.add('我喜欢拿铁咖啡。')
        for text in ('我喜欢画画。','我明年打算学陶艺。','我周末会散步。','我喜欢缝小册子。','我昨天读了散文。'):
            self.add(text)
        self.drain()
        new = self.add('我现在不喜欢拿铁咖啡了。')
        context = self.batch()
        self.assertIn((new,target),context.comparison_pairs)

    def test_partial_bad_judgment_and_missing_supersession_anchor_roll_back_entire_batch(self):
        a = self.add('我喜欢拿铁。')
        b = self.add('现在不喜欢拿铁了。')
        self.add('我明年想学陶艺。')
        context = self.batch()
        self.repo.requeue(context.job_id,context.generation,self.now,'test')
        before = self.f.rows('SELECT * FROM memory_records ORDER BY memory_id')
        body = judgments(context,{(b,a):('SUPERSEDES',None)})
        p = base.Provider(); p.output=json.dumps(body)
        worker = MemoryMaintenanceWorker(self.repo,p,PrioritizedModelExecutor(),policy=self.policy,clock=lambda:self.now)
        asyncio.run(worker.process_once())
        self.assertEqual(before,self.f.rows('SELECT * FROM memory_records ORDER BY memory_id'))
        self.assertEqual([],self.f.rows('SELECT * FROM memory_maintenance_runs'))
        self.assertTrue(all(r['processed_at'] is None for r in self.f.rows('SELECT * FROM memory_maintenance_items')))
        self.assertEqual(self.f.rows('SELECT * FROM memory_maintenance_jobs')[0]['error_code'], 'SUPERSEDES_REQUIRES_CHANGE_EVIDENCE')
        p.output=None; self.now+=100
        asyncio.run(worker.process_once())
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_maintenance_runs')),1)

    def test_revision_change_and_foreground_preemption_do_not_commit_or_spend_failure_budget(self):
        self.add('我喜欢咖啡。'); self.add('我晚上避免咖啡因。')
        repo, fixture = self.repo, self.f
        class Changing(base.Provider):
            async def generate(inner, request):
                with closing(fixture.database.connect()) as db, db:
                    db.execute('UPDATE memory_records SET revision=revision+1')
                return await super().generate(request)
        worker=MemoryMaintenanceWorker(repo,Changing(),PrioritizedModelExecutor(),policy=self.policy,clock=lambda:self.now)
        asyncio.run(worker.process_once())
        self.assertEqual([],self.f.rows('SELECT * FROM memory_maintenance_runs'))
        job=self.f.rows('SELECT * FROM memory_maintenance_jobs')[0]
        self.assertEqual((job['error_code'],job['failures']),('STALE_INPUT',0))
        class Preempted:
            async def execute_preemptible_background(inner,*args):
                raise PreemptedByForeground()
        worker.executor=Preempted();worker.provider=base.Provider()
        asyncio.run(worker.process_once())
        job=self.f.rows('SELECT * FROM memory_maintenance_jobs')[0]
        self.assertEqual((job['error_code'],job['failures']),('PREEMPTED',0))
        worker.executor=PrioritizedModelExecutor()
        asyncio.run(worker.process_once())
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_maintenance_runs')),1)


if __name__ == '__main__':
    unittest.main()
