import copy
import json
import unittest
from unittest.mock import patch

from tools import evaluate_memory_recall as evaluation


class RecallEvaluationTests(unittest.TestCase):
    def test_metrics_use_first_gold_and_exclude_authority(self):
        self.assertEqual(evaluation.first_gold_rank(['noise', 'b', 'a'], ['a', 'b']), 2)
        self.assertIsNone(evaluation.first_gold_rank(['noise'], ['a']))
        rows = [dict(category='semantic', rank=r) for r in (1, 3, 5, None)]
        rows += [dict(category='exact', rank=2), dict(category='authority', rank=1)]
        result = evaluation.metrics(rows)
        self.assertEqual(result['count'], 5)
        self.assertEqual([result[k] for k in ('R@1', 'R@3', 'R@5')], [.2, .6, .8])
        self.assertAlmostEqual(result['MRR'], (1 + 1/3 + 1/5 + 1/2) / 5)
        self.assertEqual(evaluation.metrics(rows, 'semantic')['R@3'], .5)

    def test_fixture_references_and_real_seed_lifecycles(self):
        data = evaluation.validate_fixture(json.loads(evaluation.FIXTURE.read_text(encoding='utf-8')))
        self.assertEqual((len(data['memories']), len(data['queries'])), (48, 24))
        self.assertEqual(sum(m['memory_type']=='SEMANTIC_FACT' for m in data['memories']), 14)
        self.assertTrue(all(m['expected_status']=='ACTIVE' for m in data['memories']))
        retired = copy.deepcopy(data)
        retired['queries'][0]['filters'] = {'attribute_key': 'favorite_color'}
        with self.assertRaises(ValueError):
            evaluation.validate_fixture(retired)
        for mutation in ('duplicate', 'empty_gold', 'unknown_observer', 'unknown_memory'):
            invalid = copy.deepcopy(data)
            if mutation == 'duplicate':
                invalid['queries'][1]['query_id'] = invalid['queries'][0]['query_id']
            elif mutation == 'empty_gold':
                invalid['queries'][0]['gold_memory_ids'] = []
            elif mutation == 'unknown_observer':
                invalid['queries'][0]['observer'] = 'unknown'
            else:
                invalid['queries'][0]['gold_memory_ids'] = ['unknown']
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                evaluation.validate_fixture(invalid)
        helper = evaluation.seed_helpers.CanonicalRetrievalTests(methodName='runTest')
        helper.setUp()
        try:
            mapping = evaluation.seed_corpus(helper, data)
            self.assertEqual(len(set(mapping.values())), 48)
        finally:
            helper.doCleanups()

    def test_modes_share_production_retrieval_and_ranker(self):
        connect = object()
        provider = object()
        real_ranker = evaluation.CanonicalMemoryRanker
        with patch.object(evaluation, 'CanonicalMemoryRanker', wraps=real_ranker) as ranker:
            lexical = evaluation.production_retrieval(connect)
            hybrid = evaluation.production_retrieval(connect, provider)
        self.assertIsInstance(lexical, evaluation.CanonicalMemoryRetrieval)
        self.assertIsInstance(hybrid, evaluation.CanonicalMemoryRetrieval)
        self.assertEqual([call.args for call in ranker.call_args_list], [(None,), (provider,)])


if __name__ == '__main__':
    unittest.main()
