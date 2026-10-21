"""Connection-injected task storage. No default paths, bootstrap or module I/O."""

from __future__ import annotations

import hashlib
import json
import sqlite3
from collections.abc import Callable
from contextlib import closing

from .task_models import TaskCommandResult, TaskError, TaskPage, TaskQuery, TaskRecord, TaskScope, authorize


def canonical_json(value) -> str:
    return json.dumps(value, ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":"))


def digest(value) -> str:
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def _identity(key):
    if key is None:
        return None
    namespace, identity_type, persistent_id = json.loads(key)
    return dict(identity_namespace=namespace, identity_type=identity_type, persistent_id=persistent_id)


class TaskRepository:
    """Only this module issues task SQL; commands and state share one transaction."""

    def __init__(self, connection_factory: Callable[[], sqlite3.Connection]):
        self._connection_factory = connection_factory

    @staticmethod
    def _record(row) -> TaskRecord:
        # Persisted malformed data fails visibly, never becomes an empty task list.
        value = dict(row)
        value["owner"] = _identity(value.pop("owner_key"))
        value["player"] = _identity(value.pop("player_key"))
        for column, field in (("source_ref_json", "source"), ("goal_json", "goal"),
                              ("schedule_json", "schedule"), ("consideration_json", "consideration"),
                              ("execution_json", "execution")):
            data = value.pop(column)
            value[field] = json.loads(data) if data is not None else None
        return TaskRecord.model_validate_json(canonical_json(value))

    @staticmethod
    def _columns(task: TaskRecord) -> dict:
        task = TaskRecord.model_validate(task.model_dump())
        value = task.model_dump(mode="json")
        value.pop("owner")
        value.pop("player")
        value["owner_key"] = task.owner.key
        value["player_key"] = task.player.key if task.player else None
        for field, column in (("source", "source_ref_json"), ("goal", "goal_json"),
                              ("schedule", "schedule_json"), ("consideration", "consideration_json"),
                              ("execution", "execution_json")):
            data = value.pop(field)
            value[column] = canonical_json(data) if data is not None else None
        return value

    def _load(self, db, scope: TaskScope, task_id: str) -> TaskRecord:
        row = db.execute("SELECT * FROM npc_tasks WHERE task_id=? AND owner_key=?",
                         (task_id, scope.owner.key)).fetchone()
        if row is None:
            raise TaskError("TASK_NOT_FOUND_OR_NOT_AUTHORIZED")
        task = self._record(row)
        authorize(scope, task)
        return task

    def get(self, scope: TaskScope, task_id: str) -> TaskRecord:
        with closing(self._connection_factory()) as db:
            db.row_factory = sqlite3.Row
            return self._load(db, scope, task_id)

    def query(self, scope: TaskScope, query: TaskQuery) -> TaskPage:
        if scope.player is not None and query.player is not None and scope.player != query.player:
            raise TaskError("TASK_PLAYER_FILTER_NOT_AUTHORIZED")
        conditions = ["owner_key=?", "status IN (" + ",".join("?" for _ in query.statuses) + ")"]
        values = [scope.owner.key, *query.statuses]
        if query.player is not None:
            conditions.append("player_key=?")
            values.append(query.player.key)
        elif scope.player is not None:
            conditions.append("(player_key=? OR player_key IS NULL)")
            values.append(scope.player.key)
        with closing(self._connection_factory()) as db:
            db.row_factory = sqlite3.Row
            rows = db.execute("SELECT * FROM npc_tasks WHERE " + " AND ".join(conditions) +
                              " ORDER BY created_at_utc,task_id LIMIT ? OFFSET ?",
                              (*values, query.limit + 1, query.offset)).fetchall()
            return TaskPage(tasks=tuple(self._record(row) for row in rows[:query.limit]),
                            has_more=len(rows) > query.limit)

    def get_command(self, scope: TaskScope, command_id: str, *, binding_hash: str | None = None) -> TaskCommandResult:
        with closing(self._connection_factory()) as db:
            db.row_factory = sqlite3.Row
            row = db.execute("SELECT * FROM npc_task_commands WHERE command_id=? AND owner_key=?",
                             (command_id, scope.owner.key)).fetchone()
            if row is None:
                raise TaskError("TASK_COMMAND_NOT_FOUND_OR_NOT_AUTHORIZED")
            if binding_hash is not None and json.loads(row["source_ref_json"]).get("binding_hash") != binding_hash:
                raise TaskError("TASK_COMMAND_PAYLOAD_CONFLICT")
            result = TaskCommandResult.model_validate_json(row["result_json"])
            authorize(scope, result.task)
            return result

    def considerable(self, scope, *, is_idle, now):
        """Bounded storage qualification, never an action or model decision."""
        with closing(self._connection_factory()) as db:
            db.row_factory = sqlite3.Row
            rows = db.execute("""SELECT * FROM npc_tasks WHERE owner_key=? AND status='waiting'
                AND (player_key=? OR player_key IS NULL)
                AND ((trigger_kind='next_visit' AND created_visit_id<>?
                      AND json_extract(schedule_json,'$.anchor_visit_id')<>?)
                     OR (trigger_kind='when_idle' AND ?))
                AND (consideration_json IS NULL OR json_extract(consideration_json,'$.visit_id')<>?
                     OR (json_extract(consideration_json,'$.automatic_count')<3
                         AND json_extract(consideration_json,'$.next_allowed_at_utc')<=?))
                ORDER BY created_at_utc,task_id LIMIT 3""",
                (scope.owner.key, scope.player.key if scope.player else None, scope.visit_id,
                 scope.visit_id, is_idle, scope.visit_id, now)).fetchall()
            return tuple(self._record(row) for row in rows)

    def record_consideration(self, scope, task_ids, wake_key, now, eligible):
        """Persist resource counters atomically in the existing metadata slot.

        No business status/goal is chosen here. Versions advance so a concurrent
        model update must use the fresh snapshot. No execution slot is acquired.
        """
        with closing(self._connection_factory()) as db:
            db.row_factory = sqlite3.Row
            db.execute('BEGIN IMMEDIATE')
            try:
                accepted = []
                for task_id in task_ids:
                    task = self._load(db, scope, task_id)
                    previous = task.consideration
                    same = previous is not None and previous.visit_id == scope.visit_id
                    seen = tuple(dict.fromkeys((*previous.wake_keys, previous.wake_key))) if same else ()
                    if (not eligible(task) or same and (wake_key in seen or previous.automatic_count >= 3
                            or now < previous.next_allowed_at_utc)):
                        continue
                    value = task.model_dump()
                    value.update(version=task.version+1, updated_at_utc=max(now,task.updated_at_utc), consideration=dict(
                        visit_id=scope.visit_id, wake_key=wake_key, wake_keys=(*seen,wake_key),
                        automatic_count=(previous.automatic_count if same else 0)+1, next_allowed_at_utc=now+30.0))
                    updated = TaskRecord.model_validate(value)
                    self._save(db,updated,task.version)
                    accepted.append(updated)
                db.commit()
                return tuple(accepted)
            except BaseException:
                db.rollback()
                raise

    def _insert(self, db, task: TaskRecord):
        values = self._columns(task)
        # Identifiers come only from our fixed column mapping, never caller data.
        db.execute("INSERT INTO npc_tasks (" + ",".join(values) + ") VALUES (" +
                   ",".join("?" for _ in values) + ")", tuple(values.values()))

    def _save(self, db, task: TaskRecord, expected_version: int):
        values = self._columns(task)
        values.pop("task_id")
        cursor = db.execute("UPDATE npc_tasks SET " + ",".join(key + "=?" for key in values) +
                            " WHERE task_id=? AND owner_key=? AND version=?",
                            (*values.values(), task.task_id, task.owner.key, expected_version))
        if cursor.rowcount != 1:
            raise TaskError("TASK_VERSION_CONFLICT")

    def _duplicate(self, db, owner_key, dedup_key):
        row = db.execute("SELECT * FROM npc_tasks WHERE owner_key=? AND dedup_key=? "
                         "AND status IN ('waiting','executing','blocked')", (owner_key, dedup_key)).fetchone()
        return self._record(row) if row is not None else None

    @staticmethod
    def _count_open(db, owner_key, source_kind=None):
        sql = "SELECT COUNT(*) FROM npc_tasks WHERE owner_key=? AND status IN ('waiting','executing','blocked')"
        args = (owner_key,)
        if source_kind is not None:
            sql += " AND source_kind=?"
            args += (source_kind,)
        return db.execute(sql, args).fetchone()[0]

    def _run_command(self, scope: TaskScope, command_id: str, operation: str, payload: dict,
                     now: float, apply: Callable) -> TaskCommandResult:
        """apply returns (validated task, outcome, optional globally unique effect key).

        BEGIN IMMEDIATE serializes checks with changes; CAS remains explicit. No
        successful result escapes before commit, including when the journal insert
        or commit itself fails. The callback is private trusted application code.
        """
        payload_hash = digest({"operation": operation, "owner": scope.owner.key,
                               "player": scope.player.key if scope.player else None,
                               "visit_id": scope.visit_id, "payload": payload})
        with closing(self._connection_factory()) as db:
            db.row_factory = sqlite3.Row
            try:
                db.execute("BEGIN IMMEDIATE")
                existing = db.execute("SELECT * FROM npc_task_commands WHERE command_id=?", (command_id,)).fetchone()
                if existing is not None:
                    if existing["owner_key"] != scope.owner.key:
                        raise TaskError("TASK_COMMAND_NOT_FOUND_OR_NOT_AUTHORIZED")
                    if existing["payload_hash"] != payload_hash:
                        raise TaskError("TASK_COMMAND_PAYLOAD_CONFLICT")
                    result = TaskCommandResult.model_validate_json(existing["result_json"])
                    authorize(scope, result.task)
                else:
                    task, outcome, operation_key = apply(db)
                    authorize(scope, task)
                    result = TaskCommandResult(command_id=command_id, operation=operation, outcome=outcome, task=task)
                    if operation_key is not None and db.execute(
                            "SELECT 1 FROM npc_task_commands WHERE operation_key=?", (operation_key,)).fetchone():
                        raise TaskError("TASK_EXECUTION_KEY_CONFLICT")
                    db.execute("INSERT INTO npc_task_commands "
                               "(command_id,task_id,owner_key,operation,operation_key,payload_hash,source_ref_json,result_json,created_at_utc) "
                               "VALUES (?,?,?,?,?,?,?,?,?)",
                               (command_id, task.task_id, scope.owner.key, operation, operation_key, payload_hash,
                                canonical_json({"visit_id": scope.visit_id, "source": task.source.model_dump(mode="json"),
                                                "binding_hash": payload.get("binding_hash")}),
                                result.model_dump_json(), now))
                db.commit()
                return result
            except BaseException:
                db.rollback()
                raise
