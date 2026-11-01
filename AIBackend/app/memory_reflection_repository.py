"""Reflection tasks share B1 attempts/runs; only direct, unconsumed sources enter."""
from contextlib import closing
import json

from .canonical_recall_models import CanonicalReadFilter, CanonicalRecallBudget
from .experience_ledger import ExperienceLedger
from .memory_consolidation_admission import identity_key, semantic_id, source_texts, NPC_SELF_STATEMENT_LABEL
from .memory_maintenance_models import MaintenanceStale, MaintenanceAdmissionError
from .memory_maintenance_repository import encoded
from .memory_reflection_models import REFLECTION_VERSION, ReflectionInput, ReflectionResponse


def enqueue_reflection(db, record, now):
    if record['memory_type'] not in ('EPISODIC', 'SEMANTIC_FACT'):
        return
    owner = tuple(record[k] for k in ('identity_namespace','identity_type','persistent_id'))
    if not all(owner):
        return
    subjects = [tuple(s) for s in db.execute('SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?', (record['memory_id'],))]
    # A bounded one-subject reflection cannot safely expose a multi-party memory.
    # Observer participation is allowed; other durable subjects must be isolated.
    targets = [s for s in subjects if s != owner] or subjects
    if len(targets)!=1 or any(s not in (owner,targets[0]) for s in subjects):
        return
    subject = targets[0]
    job_id = semantic_id('reflection_', [REFLECTION_VERSION, owner, subject])
    db.execute("INSERT INTO memory_maintenance_jobs(job_id,owner_json,subject_json,policy_version,pending_since,updated_at,task_kind) VALUES(?,?,?,?,?,?,'REFLECTION') ON CONFLICT(job_id) DO NOTHING",
               (job_id, encoded(owner), encoded(subject), REFLECTION_VERSION, now, now))
    # Fact extraction and Episodic saving can arrive at different times. Once
    # an Episode has been acknowledged, a later memory from that same Episode
    # cannot resurrect the identical experience batch under a new memory ID.
    consumed = db.execute('SELECT 1 FROM memory_reflection_items i JOIN memory_records r USING(memory_id) WHERE i.job_id=? AND i.processed_at IS NOT NULL AND r.episode_id=? LIMIT 1',(job_id,record['episode_id'])).fetchone()
    added = db.execute('INSERT INTO memory_reflection_items VALUES(?,?,?,?) ON CONFLICT(job_id,memory_id) DO NOTHING', (job_id,record['memory_id'],now,now if consumed else None)).rowcount
    if added and not consumed:
        db.execute("UPDATE memory_maintenance_jobs SET status=CASE WHEN status='RUNNING' THEN status ELSE 'PENDING' END,pending_since=COALESCE(pending_since,?),updated_at=?,failures=0,next_retry_at=NULL,error_code=NULL WHERE job_id=?", (now,now,job_id))


class ReflectionTasks:
    """Repository extension, not a second worker or inference scheduler."""
    def __init__(self, maintenance):
        self.base = maintenance
        self.connect = maintenance.connect

    def claim(self, now, policy, stale_seconds, *, activate=False):
        with self.base.active_attempts.lock:
            job = self._claim(now, policy, stale_seconds)
            if job is not None and activate:
                self.base.active_attempts.add(job, (job["owner_json"], job["subject_json"]))
            return job

    def _claim(self, now, policy, stale_seconds):
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            self.base.recover_stale(db, now, stale_seconds, REFLECTION_VERSION)
            exclusion, args = self.base.active_attempts.exclusions('j')
            job = db.execute("SELECT j.* FROM memory_maintenance_jobs j WHERE j.task_kind='REFLECTION' AND j.policy_version=? AND j.status='PENDING' AND (j.next_retry_at IS NULL OR j.next_retry_at<=?) AND j.pending_since<=? AND (SELECT COUNT(DISTINCT episode_id) FROM (SELECT r.episode_id FROM memory_reflection_items i JOIN memory_records r USING(memory_id) WHERE i.job_id=j.job_id AND i.processed_at IS NULL AND r.status IN ('ACTIVE','CONFLICTED','SUPERSEDED') AND r.memory_type IN ('EPISODIC','SEMANTIC_FACT') ORDER BY i.enqueued_at,i.memory_id LIMIT ?))>=?" + exclusion + " ORDER BY j.pending_since,j.job_id LIMIT 1",
                (REFLECTION_VERSION,now,now-policy.min_wait_seconds,policy.scan_limit,policy.min_episodes,*args)).fetchone()
            if job is None:
                return None
            db.execute("UPDATE memory_maintenance_jobs SET status='RUNNING',attempt_generation=attempt_generation+1,started_at=?,updated_at=? WHERE job_id=?", (now,now,job['job_id']))
            return dict(db.execute('SELECT * FROM memory_maintenance_jobs WHERE job_id=?',(job['job_id'],)).fetchone())

    def direct(self, db, memory_id, owner, subject):
        row = db.execute("SELECT * FROM memory_records WHERE memory_id=? AND identity_namespace=? AND identity_type=? AND persistent_id=? AND scope='NPC_PRIVATE' AND memory_type IN ('EPISODIC','SEMANTIC_FACT') AND status IN ('ACTIVE','CONFLICTED','SUPERSEDED')", (memory_id,*identity_key(owner))).fetchone()
        subjects = {tuple(s) for s in db.execute('SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?',(memory_id,))}
        if row is None or identity_key(subject) not in subjects or not subjects.issubset({identity_key(owner),identity_key(subject)}):
            raise MaintenanceAdmissionError('REFLECTION_SCOPE_INVALID')
        if row['memory_type']=='SEMANTIC_FACT':
            result = self.base._fact(db,memory_id,owner,subject)
        else:
            label = self.base.certificates._source_certificate(db,memory_id,owner)
            if label is None or label=='SOURCE_UNVERIFIED':
                raise MaintenanceAdmissionError('REFLECTION_SOURCE_INVALID')
            result = dict(memory_id=memory_id,revision=row['revision'],status=row['status'],fact=row['summary'],provenance_label=label or row['provenance_label'])
        episode = db.execute('SELECT * FROM episodes WHERE episode_id=?',(row['episode_id'],)).fetchone()
        links = db.execute('SELECT e.*,m.experience_seq AS linked_seq FROM memory_evidence m JOIN experience_events e ON e.event_id=m.experience_event_id WHERE m.memory_id=? ORDER BY (e.event_id=?) DESC,m.experience_seq LIMIT 65',(memory_id,result.get('excerpt_event_id',''))).fetchall()
        if not episode or episode['status']!='CLOSED' or not 1<=len(links)<=64:
            raise MaintenanceAdmissionError('REFLECTION_EPISODE_INVALID')
        events = [ExperienceLedger._row_to_event(e) for e in links]
        # Certified NPC-only speech remains about the NPC, even when a Player
        # was its recipient. The source certificate still checks the full event
        # against the deployment UID; recipient presence cannot become Player
        # history or widen the persisted Insight subject.
        self_statement = owner==subject and result['provenance_label']==NPC_SELF_STATEMENT_LABEL
        for link,event in zip(links,events):
            participants = {(p.identity_namespace,p.identity_type,p.persistent_id) for p in event.persistent_participants}
            if (link['linked_seq']!=event.experience_seq or event.observer_id!=row['observer_entity_id']
                or event.observer_id!=episode['observer_id'] or event.source_session_id!=episode['source_session_id']
                or not episode['start_seq']<=event.experience_seq<=episode['end_seq']
                or not {identity_key(owner),identity_key(subject)}.issubset(participants)
                or not self_statement and any((p[0].casefold()=='player' or p[1].casefold()=='player') and p!=identity_key(subject) for p in participants)):
                raise MaintenanceAdmissionError('REFLECTION_EVIDENCE_INVALID')
        result.update(memory_type=row['memory_type'],episode_id=row['episode_id'],observer_entity_id=row['observer_entity_id'],
                      occurred_start=row['occurred_start'],occurred_end=row['occurred_end'],
                      evidence_digest=semantic_id('',[(e.event_id,e.experience_seq) for e in events]),
                      evidence=[dict(event_id=e.event_id,experience_seq=e.experience_seq,occurred_at=e.occurred_at,
                                     clock_domain=e.clock_domain,source_session_id=e.source_session_id,provenance=e.provenance,
                                     content=[t[:1000] for t in source_texts(e)][:8]) for e in events[:4]])
        return result

    def prepare(self, job, policy):
        owner,subject = self.base.identities(job)
        with closing(self.connect()) as db:
            db.execute('BEGIN')
            rows = [dict(r) for r in db.execute("SELECT r.memory_id,r.memory_type,r.summary,r.confidence,r.importance,r.learned_at,r.episode_id FROM memory_reflection_items i JOIN memory_records r USING(memory_id) WHERE i.job_id=? AND i.processed_at IS NULL AND r.memory_type IN ('EPISODIC','SEMANTIC_FACT') AND r.status IN ('ACTIVE','CONFLICTED','SUPERSEDED') ORDER BY i.enqueued_at,i.memory_id LIMIT ?",(job['job_id'],policy.scan_limit))]
            if not rows:
                raise MaintenanceStale('REFLECTION_INPUT_CHANGED')
            ranked = self.base.ranker.rank(rows,db,rows[0]['summary'][:512],CanonicalReadFilter(),
                CanonicalRecallBudget(candidate_limit=policy.scan_limit,top_k=policy.input_limit))
            ordered = sorted(rows,key=lambda r: ranked.ids.index(r['memory_id']) if r['memory_id'] in ranked.ids else len(ranked.ids)+rows.index(r))
            records,episodes = [],set()
            # First select distinct Episodes; remaining slots can add detail.
            for distinct_only in (True,False):
                for row in ordered:
                    if len(records)>=policy.input_limit:
                        break
                    if row['memory_id'] in {r['memory_id'] for r in records} or distinct_only and row['episode_id'] in episodes:
                        continue
                    record = self.direct(db,row['memory_id'],owner,subject)
                    records.append(record)
                    episodes.add(record['episode_id'])
        return ReflectionInput(job['job_id'],job['attempt_generation'],owner,subject,tuple(r['memory_id'] for r in records),tuple(records))

    def finalize(self, context, response, now):
        response = ReflectionResponse.model_validate(response.model_dump())
        with closing(self.connect()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            if db.execute('SELECT 1 FROM memory_maintenance_runs WHERE job_id=? AND attempt_generation=?',(context.job_id,context.generation)).fetchone():
                return False
            job = db.execute('SELECT * FROM memory_maintenance_jobs WHERE job_id=?',(context.job_id,)).fetchone()
            if (not job or job['task_kind']!='REFLECTION' or job['policy_version']!=REFLECTION_VERSION
                or job['status']!='RUNNING' or job['attempt_generation']!=context.generation):
                raise MaintenanceStale('STALE_ATTEMPT')
            if self.base.identities(job)!=(context.owner,context.subject):
                raise MaintenanceAdmissionError('REFLECTION_SCOPE_INVALID')
            if not 1<=len(context.records)<=8 or context.new_ids!=tuple(r['memory_id'] for r in context.records) or len(set(context.new_ids))!=len(context.new_ids):
                raise MaintenanceAdmissionError('REFLECTION_INPUT_INVALID')
            for memory_id in context.new_ids:
                if not db.execute('SELECT 1 FROM memory_reflection_items WHERE job_id=? AND memory_id=? AND processed_at IS NULL',(context.job_id,memory_id)).fetchone():
                    raise MaintenanceStale('PENDING_INPUT_CHANGED')
            current = tuple(self.direct(db,r['memory_id'],context.owner,context.subject) for r in context.records)
            if encoded(current)!=encoded(context.records):
                raise MaintenanceStale('MEMORY_REVISION_CHANGED')
            by_id = {r['memory_id']:r for r in current}
            sources = []
            for insight in response.insights:
                if (len(set(insight.source_memory_ids))!=len(insight.source_memory_ids)
                    or len(set(insight.evidence_event_ids))!=len(insight.evidence_event_ids)
                    or any(i not in by_id for i in insight.source_memory_ids)):
                    raise MaintenanceAdmissionError('INSIGHT_REFERENCE_INVALID')
                selected = [by_id[i] for i in insight.source_memory_ids]
                cited = set(insight.evidence_event_ids)
                events = {e['event_id']:e for r in selected for e in r['evidence']}
                if (not cited.issubset(events) or len({r['episode_id'] for r in selected})<2
                    or len({r['observer_entity_id'] for r in selected})!=1
                    or any(not cited.intersection(e['event_id'] for e in r['evidence']) for r in selected)
                    or len({r['episode_id'] for r in selected if cited.intersection(e['event_id'] for e in r['evidence'])})<2):
                    raise MaintenanceAdmissionError('INSIGHT_EVIDENCE_INVALID')
                sources.append((selected, [events[i] for i in insight.evidence_event_ids]))
            # Every operation is validated before writing. Interpretations cannot
            # modify the underlying direct memories, Persona, world or relations.
            run_id = semantic_id('reflection_run_', [context.job_id,context.generation])
            db.execute('INSERT INTO memory_maintenance_runs VALUES(?,?,?,?,?,?,?,NULL)',
                (run_id,context.job_id,context.generation,encoded(context.model_payload()),response.model_dump_json(),'[]',now))
            receipts = []
            for index,(insight,(selected,events)) in enumerate(zip(response.insights,sources)):
                memory_id = semantic_id('insight_', [run_id,index])
                times = [e['occurred_at'] for e in events]
                db.execute("INSERT INTO memory_records(memory_id,observer_entity_id,identity_namespace,identity_type,persistent_id,memory_type,scope,summary,importance,confidence,provenance_label,provenance_set_json,occurred_start,occurred_end,learned_at,episode_id,job_id,pipeline_version,admission_policy_version,status,schema_version,revision) VALUES(?,?,?,?,?,'DERIVED_INSIGHT','NPC_PRIVATE',?,NULL,0.5,'DERIVED_INSIGHT',?,?,?,?,NULL,NULL,?,?,'ACTIVE','memory-record/4.0',1)",
                    (memory_id,selected[0]['observer_entity_id'],*identity_key(context.owner),insight.insight,
                     encoded(sorted({e['provenance'] for e in events})),min(times),max(times),now,REFLECTION_VERSION,REFLECTION_VERSION))
                db.execute('INSERT INTO memory_subjects VALUES(?,?,?,?)',(memory_id,*identity_key(context.subject)))
                db.executemany('INSERT INTO memory_evidence VALUES(?,?,?)',((memory_id,e['event_id'],e['experience_seq']) for e in events))
                db.execute('INSERT INTO memory_derived_insights VALUES(?,?,?,?)',(memory_id,run_id,insight.derivation_type,insight.uncertainty))
                db.executemany('INSERT INTO memory_insight_sources VALUES(?,?,?,?)',((memory_id,r['memory_id'],r['revision'],r['status']) for r in selected))
                db.executemany('INSERT INTO memory_insight_episodes VALUES(?,?)',((memory_id,i) for i in sorted({r['episode_id'] for r in selected})))
                receipts.append(dict(memory_id=memory_id,before_status='SUPERSEDED',after_status='ACTIVE',after_revision=1,created=True))
            db.execute('UPDATE memory_maintenance_runs SET changes_json=? WHERE run_id=?',(encoded(receipts),run_id))
            db.executemany('UPDATE memory_reflection_items SET processed_at=? WHERE job_id=? AND memory_id IN (SELECT memory_id FROM memory_records WHERE episode_id=?)',
                          ((now,context.job_id,i) for i in {r['episode_id'] for r in context.records}))
            pending = db.execute('SELECT MIN(enqueued_at) FROM memory_reflection_items WHERE job_id=? AND processed_at IS NULL',(context.job_id,)).fetchone()[0]
            db.execute("UPDATE memory_maintenance_jobs SET status=?,pending_since=?,started_at=NULL,updated_at=?,failures=0,next_retry_at=NULL,error_code=NULL WHERE job_id=?",('IDLE' if pending is None else 'PENDING',pending,now,context.job_id))
        return True
