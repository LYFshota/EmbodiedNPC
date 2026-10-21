"""Additive schema 19 for Demo tasks. Never initializes or opens a database."""

import sqlite3
import time

TASK_SCHEMA_VERSION = 19

_STATEMENTS = (
    """CREATE TABLE npc_tasks (
        task_id TEXT PRIMARY KEY,
        owner_key TEXT NOT NULL, player_key TEXT,
        source_kind TEXT NOT NULL CHECK(source_kind IN ('player_request','self_initiated')),
        source_ref_json TEXT NOT NULL CHECK(json_valid(source_ref_json)),
        goal_json TEXT NOT NULL CHECK(json_valid(goal_json)),
        trigger_kind TEXT NOT NULL CHECK(trigger_kind IN ('next_visit','when_idle')),
        created_visit_id TEXT NOT NULL,
        status TEXT NOT NULL CHECK(status IN ('waiting','executing','blocked','completed','cancelled')),
        version INTEGER NOT NULL CHECK(version>0),
        dedup_key TEXT NOT NULL,
        schedule_json TEXT NOT NULL CHECK(json_valid(schedule_json)),
        consideration_json TEXT CHECK(consideration_json IS NULL OR (json_valid(consideration_json) AND length(consideration_json)<=2048)),
        execution_json TEXT CHECK(execution_json IS NULL OR (json_valid(execution_json) AND length(execution_json)<=16384)),
        created_at_utc REAL NOT NULL, updated_at_utc REAL NOT NULL,
        CHECK(updated_at_utc>=created_at_utc),
        CHECK(status NOT IN ('executing','blocked','completed') OR execution_json IS NOT NULL),
        CHECK((source_kind='player_request' AND player_key IS NOT NULL)
           OR (source_kind='self_initiated' AND player_key IS NULL)))""",
    """CREATE UNIQUE INDEX npc_tasks_active_dedup ON npc_tasks(owner_key,dedup_key)
        WHERE status IN ('waiting','executing','blocked')""",
    "CREATE INDEX npc_tasks_lookup ON npc_tasks(owner_key,status,player_key,created_at_utc)",
    """CREATE TABLE npc_task_commands (
        command_id TEXT PRIMARY KEY,
        task_id TEXT NOT NULL REFERENCES npc_tasks(task_id), owner_key TEXT NOT NULL,
        operation TEXT NOT NULL CHECK(operation IN
          ('create','update','cancel','reserve','start','settle','mark_unknown','requeue')),
        operation_key TEXT UNIQUE,
        payload_hash TEXT NOT NULL, source_ref_json TEXT NOT NULL CHECK(json_valid(source_ref_json)),
        result_json TEXT NOT NULL CHECK(json_valid(result_json) AND length(result_json)<=32768),
        created_at_utc REAL NOT NULL)""",
    "CREATE INDEX npc_task_commands_by_task ON npc_task_commands(task_id,created_at_utc)",
    """CREATE TRIGGER npc_task_commands_immutable_update BEFORE UPDATE ON npc_task_commands
        BEGIN SELECT RAISE(ABORT,'TASK_COMMAND_IMMUTABLE'); END""",
    """CREATE TRIGGER npc_task_commands_immutable_delete BEFORE DELETE ON npc_task_commands
        BEGIN SELECT RAISE(ABORT,'TASK_COMMAND_IMMUTABLE'); END""",
    """CREATE TRIGGER npc_tasks_identity_immutable BEFORE UPDATE ON npc_tasks
        WHEN NEW.owner_key IS NOT OLD.owner_key OR NEW.player_key IS NOT OLD.player_key
          OR NEW.source_kind IS NOT OLD.source_kind OR NEW.source_ref_json IS NOT OLD.source_ref_json
          OR NEW.created_visit_id IS NOT OLD.created_visit_id OR NEW.created_at_utc IS NOT OLD.created_at_utc
        BEGIN SELECT RAISE(ABORT,'TASK_IDENTITY_IMMUTABLE'); END""",
)


def migrate_tasks(connection: sqlite3.Connection) -> None:
    """One short transaction; old schema, data and foreign keys are left intact."""
    connection.execute("BEGIN IMMEDIATE")
    try:
        version = connection.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0] or 0
        if version > TASK_SCHEMA_VERSION:
            raise sqlite3.IntegrityError("UNSUPPORTED_FUTURE_TASK_SCHEMA")
        if connection.execute("SELECT 1 FROM schema_migrations WHERE version=?", (TASK_SCHEMA_VERSION,)).fetchone():
            connection.commit()
            return
        if version != TASK_SCHEMA_VERSION - 1:
            raise sqlite3.IntegrityError("TASK_SCHEMA_PREDECESSOR_REQUIRED")
        for statement in _STATEMENTS:
            connection.execute(statement)
        if connection.execute("PRAGMA foreign_key_check").fetchone():
            raise sqlite3.IntegrityError("TASK_SCHEMA_FOREIGN_KEY_CHECK_FAILED")
        connection.execute("INSERT INTO schema_migrations VALUES(?,?)", (TASK_SCHEMA_VERSION, time.time()))
        connection.commit()
    except BaseException:
        connection.rollback()
        raise
