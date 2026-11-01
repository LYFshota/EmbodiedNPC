"""Persistent scope queue, optimistic atomic maintenance and reversible relations."""
from contextlib import closing
from .worker_runtime import ActiveAttempts
import json

from .canonical_memory_ranking import CanonicalMemoryRanker
from .canonical_memory_read_repository import CanonicalMemoryReadRepository
from .canonical_recall_models import CanonicalReadFilter, CanonicalRecallBudget
from .experience_ledger import ExperienceLedger
from .memory_consolidation_admission import identity_key, semantic_id, source_texts, NPC_SELF_STATEMENT_PREFIX
from .memory_consolidation_context import TrustedPersistentIdentity
from .memory_maintenance_models import (
    MAINTENANCE_VERSION, MaintenancePolicy, MaintenanceInput, MaintenanceResponse,
    MaintenanceAdmissionError, MaintenanceStale,
)


def encoded(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def enqueue_fact(db, record, now):
    """Called in canonical commit or the additive migration, never via an LLM."""
    if record['memory_type'] != 'SEMANTIC_FACT':
        return
    owner = tuple(record[k] for k in ('identity_namespace','identity_type','persistent_id'))
    subjects = list(db.execute("SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects "
                               "WHERE memory_id=?", (record['memory_id'],)))
    if not all(owner) or len(subjects) != 1:
        raise MaintenanceAdmissionError('MAINTENANCE_SCOPE_INVALID')
    subject = tuple(subjects[0])
    job_id = semantic_id('maintenance_', [MAINTENANCE_VERSION, owner, subject])
    db.execute("INSERT INTO memory_maintenance_jobs(job_id,owner_json,subject_json,policy_version,"
               "pending_since,updated_at) VALUES(?,?,?,?,?,?) ON CONFLICT(job_id) DO NOTHING",
               (job_id, encoded(owner), encoded(subject), MAINTENANCE_VERSION, now, now))
    added = db.execute("INSERT INTO memory_maintenance_items VALUES(?,?,?,NULL) ON CONFLICT(memory_id) DO NOTHING",
                       (record['memory_id'], job_id, now)).rowcount
    if added:
        db.execute("UPDATE memory_maintenance_jobs SET status=CASE WHEN status='RUNNING' THEN status ELSE 'PENDING' END,"
                   "pending_since=COALESCE(pending_since,?),updated_at=?,failures=0,next_retry_at=NULL,error_code=NULL "
                   "WHERE job_id=?", (now, now, job_id))


class MemoryMaintenanceRepository:
    def __init__(self, connect, embedding_provider=None, *, expected_player_uid=None):
        self.connect = connect
        self.active_attempts = ActiveAttempts()
        self.ranker = CanonicalMemoryRanker(embedding_provider)
        self.certificates = CanonicalMemoryReadRepository(connect, expected_player_uid=expected_player_uid)

    def claim(self, now, policy: MaintenancePolicy, *, activate=False):
        with self.active_attempts.lock:
            job = self._claim(now, policy)
            if job is not None and activate:
                self.active_attempts.add(job, (job["owner_json"], job["subject_json"]))
            return job

    def recover_stale(self, db, now, stale_seconds, version):
        rows = db.execute("SELECT job_id,attempt_generation FROM memory_maintenance_jobs "
            "WHERE status='RUNNING' AND started_at<? AND policy_version=?", (now-stale_seconds, version)).fetchall()
        for row in rows:
            if not self.active_attempts.contains(row["job_id"], row["attempt_generation"]):
                db.execute("UPDATE memory_maintenance_jobs SET status='PENDING',started_at=NULL,"
                    "error_code='RECOVERED_STALE' WHERE job_id=? AND attempt_generation=? AND status='RUNNING'",
                    (row["job_id"], row["attempt_generation"]))

    def _claim(self, now, policy):
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            self.recover_stale(db, now, policy.stale_seconds, MAINTENANCE_VERSION)
            exclusion, args = self.active_attempts.exclusions('j')
            row = db.execute("SELECT j.* FROM memory_maintenance_jobs j WHERE j.status='PENDING' "
                "AND j.policy_version=? AND (j.next_retry_at IS NULL OR j.next_retry_at<=?) "
                "AND ((SELECT COUNT(*) FROM (SELECT 1 FROM memory_maintenance_items i WHERE i.job_id=j.job_id "
                "AND i.processed_at IS NULL LIMIT ?))>=? OR j.pending_since<=?) "
                + exclusion + " ORDER BY j.pending_since,j.job_id LIMIT 1",
                (MAINTENANCE_VERSION,now,policy.backlog_threshold,policy.backlog_threshold,now-policy.max_wait_seconds,*args)).fetchone()
            if row is None:
                return None
            db.execute("UPDATE memory_maintenance_jobs SET status='RUNNING',attempt_generation=attempt_generation+1,"
                       "started_at=?,updated_at=? WHERE job_id=?", (now,now,row['job_id']))
            return dict(db.execute('SELECT * FROM memory_maintenance_jobs WHERE job_id=?',(row['job_id'],)).fetchone())

    @staticmethod
    def identities(job):
        return (TrustedPersistentIdentity(*json.loads(job['owner_json'])),
                TrustedPersistentIdentity(*json.loads(job['subject_json'])))

    def _fact(self, db, memory_id, owner, subject):
        row = db.execute("SELECT r.*,f.source_excerpt,f.excerpt_event_id,f.temporal_precision,f.valid_from,f.valid_to,"
            "f.supersedes_memory_id FROM memory_records r JOIN memory_semantic_facts f USING(memory_id) "
            "WHERE r.memory_id=? AND r.memory_type='SEMANTIC_FACT' AND r.scope='NPC_PRIVATE' "
            "AND r.identity_namespace=? AND r.identity_type=? AND r.persistent_id=? "
            "AND r.status IN ('ACTIVE','CONFLICTED','SUPERSEDED')", (memory_id,*identity_key(owner))).fetchone()
        subjects = list(db.execute('SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?', (memory_id,)))
        if row is None or len(subjects)!=1 or tuple(subjects[0])!=identity_key(subject):
            raise MaintenanceAdmissionError('MAINTENANCE_SCOPE_INVALID')
        label = self.certificates._source_certificate(db,memory_id,owner)
        if label is None or label == 'SOURCE_UNVERIFIED':
            raise MaintenanceAdmissionError('MAINTENANCE_SOURCE_INVALID')
        links = db.execute("SELECT e.*,m.experience_seq AS linked_seq FROM memory_evidence m "
            "JOIN experience_events e ON e.event_id=m.experience_event_id WHERE m.memory_id=? "
            "ORDER BY (e.event_id=?) DESC,m.experience_seq LIMIT 65", (memory_id,row['excerpt_event_id'])).fetchall()
        if not links or len(links)>64:
            raise MaintenanceAdmissionError('MAINTENANCE_EVIDENCE_INVALID')
        events = [ExperienceLedger._row_to_event(e) for e in links]
        for link,event in zip(links,events):
            if (link['linked_seq']!=event.experience_seq or event.observer_id!=row['observer_entity_id']
                or not any((p.identity_namespace,p.identity_type,p.persistent_id)==identity_key(subject)
                           for p in event.persistent_participants)):
                raise MaintenanceAdmissionError('MAINTENANCE_EVIDENCE_INVALID')
        if not any(e.event_id==row['excerpt_event_id'] and any(row['source_excerpt'] in text for text in source_texts(e)) for e in events):
            raise MaintenanceAdmissionError('MAINTENANCE_ANCHOR_INVALID')
        return dict(memory_id=memory_id,revision=row['revision'],status=row['status'],fact=row['summary'],
            provenance_label=label or row['provenance_label'],occurred_start=row['occurred_start'],occurred_end=row['occurred_end'],
            temporal_precision=row['temporal_precision'],valid_from=row['valid_from'],valid_to=row['valid_to'],
            supersedes_memory_id=row['supersedes_memory_id'],source_excerpt=row['source_excerpt'],excerpt_event_id=row['excerpt_event_id'],
            evidence_digest=semantic_id('',[(e.event_id,e.experience_seq) for e in events]),
            evidence=[dict(event_id=e.event_id,occurred_at=e.occurred_at,clock_domain=e.clock_domain,
                source_session_id=e.source_session_id,provenance=e.provenance,
                content=[text[:1000] for text in source_texts(e)][:8]) for e in events[:4]])

    def prepare(self, job, policy: MaintenancePolicy):
        owner,subject = self.identities(job)
        with closing(self.connect()) as db:
            db.execute('BEGIN')
            new_ids = tuple(r[0] for r in db.execute("SELECT memory_id FROM memory_maintenance_items "
                "WHERE job_id=? AND processed_at IS NULL ORDER BY enqueued_at,memory_id LIMIT ?",(job['job_id'],policy.new_limit)))
            records = [self._fact(db,i,owner,subject) for i in new_ids]
            # SQL LIMIT precedes ranking and hydration. This is a bounded recent
            # window, not a claim to search all memories or prove no contradiction.
            rows = [dict(r) for r in db.execute("SELECT r.memory_id,r.memory_type,r.summary,r.confidence,r.importance,r.learned_at "
                "FROM memory_records r JOIN memory_subjects s USING(memory_id) JOIN memory_semantic_facts f USING(memory_id) "
                "WHERE r.identity_namespace=? AND r.identity_type=? AND r.persistent_id=? AND r.scope='NPC_PRIVATE' "
                "AND r.memory_type='SEMANTIC_FACT' AND r.status IN ('ACTIVE','CONFLICTED','SUPERSEDED') "
                "AND s.identity_namespace=? AND s.identity_type=? AND s.persistent_id=? "
                "AND NOT EXISTS (SELECT 1 FROM memory_maintenance_items i WHERE i.memory_id=r.memory_id AND i.processed_at IS NULL) "
                "ORDER BY r.learned_at DESC,r.memory_id LIMIT ?", (*identity_key(owner),*identity_key(subject),policy.scan_limit))]
            new_rows = [dict(db.execute("SELECT memory_id,memory_type,summary,confidence,importance,learned_at "
                                       "FROM memory_records WHERE memory_id=?", (i,)).fetchone()) for i in new_ids]
            ranked_peers = []
            for record in records:
                peers = [r for r in (*new_rows, *rows) if r['memory_id'] != record['memory_id']]
                # Attribution is authority metadata, not a shared semantic topic.
                query = record['fact'].removeprefix(NPC_SELF_STATEMENT_PREFIX)
                ranked = self.ranker.rank(peers, db, query, CanonicalReadFilter(),
                    CanonicalRecallBudget(candidate_limit=policy.related_limit, top_k=policy.related_limit))
                ids = list(ranked.ids[:policy.related_limit])
                # Retain the existing small recent fallback, after ranked peers.
                for peer in peers[:4]:
                    if peer['memory_id'] not in ids and len(ids) < policy.related_limit:
                        ids.append(peer['memory_id'])
                ranked_peers.append(ids)
            # Round-robin gives each new fact its best candidate before taking
            # lower-ranked peers. Bind unordered pairs once, including new/new.
            candidates, seen = [], set()
            for rank in range(policy.related_limit):
                for memory_id, peers in zip(new_ids, ranked_peers):
                    if rank >= len(peers):
                        continue
                    pair = (memory_id, peers[rank])
                    key = frozenset(pair)
                    if key not in seen:
                        seen.add(key)
                        candidates.append(pair)
            selected = tuple(candidates[:8])
            wanted = {i for pair in selected for i in pair} - set(new_ids)
            for memory_id in dict.fromkeys(i for pair in selected for i in pair if i in wanted):
                try:
                    records.append(self._fact(db,memory_id,owner,subject))
                except MaintenanceAdmissionError:
                    continue
            hydrated = {r['memory_id'] for r in records}
            selected = tuple(pair for pair in selected if set(pair) <= hydrated)
        return MaintenanceInput(job['job_id'],job['attempt_generation'],owner,subject,new_ids,tuple(records),
                                selected, tuple(candidates))

    def requeue(self, job_id, generation, now, code, *, failed=False, policy=MaintenancePolicy()):
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            row=db.execute("SELECT failures FROM memory_maintenance_jobs WHERE job_id=? AND status='RUNNING' "
                           "AND attempt_generation=?",(job_id,generation)).fetchone()
            if row is None:
                return
            failures=row[0]+int(failed)
            db.execute("UPDATE memory_maintenance_jobs SET status=?,failures=?,started_at=NULL,updated_at=?,"
                       "next_retry_at=?,error_code=? WHERE job_id=?",
                       ('FAILED' if failures>=policy.max_failures else 'PENDING',failures,now,
                        now+min(300,30*2**failures) if failed else now,code,job_id))

    def finalize(self, context: MaintenanceInput, response: MaintenanceResponse, now):
        response=MaintenanceResponse.model_validate(response.model_dump())
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            if db.execute('SELECT 1 FROM memory_maintenance_runs WHERE job_id=? AND attempt_generation=?',
                          (context.job_id,context.generation)).fetchone():
                return False
            job=db.execute('SELECT * FROM memory_maintenance_jobs WHERE job_id=?',(context.job_id,)).fetchone()
            if job is None or job['status']!='RUNNING' or job['attempt_generation']!=context.generation:
                raise MaintenanceStale('STALE_ATTEMPT')
            if job['policy_version']!=MAINTENANCE_VERSION or job['task_kind']!='MAINTENANCE':
                raise MaintenanceAdmissionError('MAINTENANCE_TASK_INVALID')
            if self.identities(job)!=(context.owner,context.subject):
                raise MaintenanceAdmissionError('MAINTENANCE_SCOPE_INVALID')
            if (not 1<=len(context.new_ids)<=8 or not 1<=len(context.records)<=24
                or len(set(r['memory_id'] for r in context.records))!=len(context.records)
                or len(set(context.new_ids))!=len(context.new_ids)
                or not set(context.new_ids).issubset(r['memory_id'] for r in context.records)):
                raise MaintenanceAdmissionError('MAINTENANCE_INPUT_INVALID')
            for memory_id in context.new_ids:
                if not db.execute("SELECT 1 FROM memory_maintenance_items WHERE memory_id=? AND job_id=? "
                                  "AND processed_at IS NULL",(memory_id,context.job_id)).fetchone():
                    raise MaintenanceStale('PENDING_INPUT_CHANGED')
            current=tuple(self._fact(db,r['memory_id'],context.owner,context.subject) for r in context.records)
            if encoded(current)!=encoded(context.records):
                raise MaintenanceStale('MEMORY_REVISION_CHANGED')
            by_id={r['memory_id']:r for r in current}
            planned = None if context.comparison_pairs is None else {frozenset(p) for p in context.comparison_pairs}
            if planned is not None and (len(planned) != len(context.comparison_pairs) or len(planned) > 8
                or any(len(p) != 2 or not p.issubset(by_id) or not p.intersection(context.new_ids) for p in planned)):
                raise MaintenanceAdmissionError('MAINTENANCE_PAIR_PLAN_INVALID')
            pairs=set()
            for op in response.operations:
                pair=(op.new_memory_id,op.old_memory_id)
                if (op.new_memory_id not in context.new_ids or op.old_memory_id not in by_id
                    or pair[0]==pair[1] or frozenset(pair) in pairs
                    or (planned is not None and frozenset(pair) not in planned)):
                    raise MaintenanceAdmissionError('OPERATION_OUTSIDE_INPUT')
                pairs.add(frozenset(pair))
                new=by_id[op.new_memory_id]
                evidence={e['event_id']:e for e in new['evidence']}
                if len(set(op.evidence_event_ids))!=len(op.evidence_event_ids) or any(i not in evidence for i in op.evidence_event_ids):
                    raise MaintenanceAdmissionError('OPERATION_EVIDENCE_INVALID')
                if op.relation=='SUPERSEDES':
                    # Literal evidence is a necessary anchor, not a semantic proof.
                    # The LLM is responsible for explicit correction/change entailment.
                    if (not op.source_excerpt or not op.evidence_event_ids
                        or not any(op.source_excerpt in text for i in op.evidence_event_ids
                            for text in (*evidence[i]['content'],new['source_excerpt'] if i==new['excerpt_event_id'] else ''))
                        or new['status']=='SUPERSEDED' or by_id[op.old_memory_id]['status']=='SUPERSEDED'):
                        raise MaintenanceAdmissionError('SUPERSEDES_REQUIRES_CHANGE_EVIDENCE')
            # Allow an explicit chronological chain, but never a cycle. Preserve
            # every phase as its own record rather than compressing its wording.
            superseded={op.old_memory_id for op in response.operations if op.relation=='SUPERSEDES'}
            edges={}
            for op in response.operations:
                if op.relation=='SUPERSEDES':
                    edges.setdefault(op.new_memory_id,[]).append(op.old_memory_id)
            def visit(node,path):
                if node in path:
                    raise MaintenanceAdmissionError('SUPERSEDES_CYCLE')
                for child in edges.get(node,()):
                    visit(child,path|{node})
            for node in edges:
                visit(node,set())
            run_id=semantic_id('maintenance_run_', [context.job_id,context.generation])
            db.execute('INSERT INTO memory_maintenance_runs VALUES(?,?,?,?,?,?,?,NULL)',
                       (run_id,context.job_id,context.generation,encoded(context.model_payload()),response.model_dump_json(),'[]',now))
            touched=set()
            for index,op in enumerate(response.operations):
                if op.relation=='UNDETERMINED':
                    continue
                db.execute('INSERT INTO memory_relations VALUES(?,?,?,?,?,?,?,?,1)',
                    (semantic_id('relation_', [run_id,index]),run_id,op.new_memory_id,op.old_memory_id,op.relation,
                     op.reason,encoded(op.evidence_event_ids),op.source_excerpt))
                touched.update((op.new_memory_id,op.old_memory_id))
            receipts=[]
            # Apply planned supersessions first so conflict state is independent
            # of memory ID ordering within this transaction.
            db.executemany("UPDATE memory_records SET status='SUPERSEDED' WHERE memory_id=?",((i,) for i in superseded))
            for memory_id in sorted(touched):
                before=by_id[memory_id]
                status='SUPERSEDED' if memory_id in superseded else before['status']
                conflict=db.execute("SELECT 1 FROM memory_relations l JOIN memory_records a ON a.memory_id=l.new_memory_id "
                    "JOIN memory_records b ON b.memory_id=l.old_memory_id WHERE l.active=1 AND l.relation='CONFLICTS' "
                    "AND (l.new_memory_id=? OR l.old_memory_id=?) AND a.status<>'SUPERSEDED' AND b.status<>'SUPERSEDED' LIMIT 1",
                    (memory_id,memory_id)).fetchone()
                if status!='SUPERSEDED' and conflict:
                    status='CONFLICTED'
                receipts.append(dict(memory_id=memory_id,before_status=before['status'],after_status=status,
                                     before_revision=before['revision'],after_revision=before['revision']+1))
                db.execute('UPDATE memory_records SET status=?,revision=revision+1 WHERE memory_id=?',(status,memory_id))
            db.execute('UPDATE memory_maintenance_runs SET changes_json=? WHERE run_id=?',(encoded(receipts),run_id))
            db.executemany('UPDATE memory_maintenance_items SET processed_at=? WHERE memory_id=?',((now,i) for i in context.new_ids))
            from .memory_reflection_repository import enqueue_reflection
            for memory_id in context.new_ids:
                enqueue_reflection(db, dict(db.execute('SELECT * FROM memory_records WHERE memory_id=?',
                                                      (memory_id,)).fetchone()), now)
            pending=db.execute('SELECT MIN(enqueued_at) FROM memory_maintenance_items WHERE job_id=? AND processed_at IS NULL',
                               (context.job_id,)).fetchone()[0]
            db.execute("UPDATE memory_maintenance_jobs SET status=?,pending_since=?,started_at=NULL,updated_at=?,"
                       "failures=0,next_retry_at=NULL,error_code=NULL WHERE job_id=?",
                       ('IDLE' if pending is None else 'PENDING',pending,now,context.job_id))
        return True

    def retry_failed(self, job_id, now):
        """Explicit operator retry; never an automatic infinite model-call loop."""
        with closing(self.connect()) as db, db:
            job=db.execute('SELECT task_kind,policy_version FROM memory_maintenance_jobs WHERE job_id=?',(job_id,)).fetchone()
            if job and job['task_kind']=='REFLECTION':
                from .memory_reflection_models import REFLECTION_VERSION
                if job['policy_version']!=REFLECTION_VERSION:
                    return False
                return db.execute("UPDATE memory_maintenance_jobs SET status='PENDING',failures=0,next_retry_at=NULL,error_code=NULL,updated_at=? WHERE job_id=? AND status='FAILED' AND EXISTS (SELECT 1 FROM memory_reflection_items i WHERE i.job_id=memory_maintenance_jobs.job_id AND i.processed_at IS NULL)",(now,job_id)).rowcount==1
            return db.execute("UPDATE memory_maintenance_jobs SET status='PENDING',failures=0,"
                "next_retry_at=NULL,error_code=NULL,updated_at=? WHERE job_id=? AND status='FAILED' "
                "AND policy_version=? AND EXISTS (SELECT 1 FROM memory_maintenance_items i "
                "WHERE i.job_id=memory_maintenance_jobs.job_id AND i.processed_at IS NULL)",
                (now,job_id,MAINTENANCE_VERSION)).rowcount == 1

    def undo_run(self, run_id, now):
        """Explicit reversible operation; refuses to overwrite later maintenance."""
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            run=db.execute('SELECT * FROM memory_maintenance_runs WHERE run_id=?',(run_id,)).fetchone()
            if run is None:
                raise MaintenanceAdmissionError('MAINTENANCE_RUN_NOT_FOUND')
            if run['undone_at'] is not None:
                return False
            receipts=json.loads(run['changes_json'])
            for item in receipts:
                row=db.execute('SELECT revision,status FROM memory_records WHERE memory_id=?',(item['memory_id'],)).fetchone()
                if tuple(row)!=(item['after_revision'],item['after_status']):
                    raise MaintenanceStale('UNDO_REVISION_CHANGED')
            for item in receipts:
                db.execute('UPDATE memory_records SET status=?,revision=revision+1 WHERE memory_id=?',
                           (item['before_status'],item['memory_id']))
            db.execute('UPDATE memory_relations SET active=0 WHERE run_id=?',(run_id,))
            db.execute('UPDATE memory_maintenance_runs SET undone_at=? WHERE run_id=?',(now,run_id))
        return True
