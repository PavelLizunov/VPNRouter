#!/usr/bin/env python3
import json
import os
import time
from pathlib import Path

BASE_DIR = Path("/var/lib/dsh/Project/VPNRouter/plans/agent-map")
CHECKPOINT_FILE = BASE_DIR / "checkpoint.json"
EVENTS_FILE = BASE_DIR / "events.jsonl"
BATCHES_FILE = BASE_DIR / "inventory" / "batches.json"
MANIFEST_FILE = BASE_DIR / "inventory" / "manifest.json"

def log_event(event_type, data):
    event = {
        "timestamp": time.time(),
        "utc": time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()),
        "type": event_type,
        "data": data
    }
    with open(EVENTS_FILE, "a", encoding="utf-8") as f:
        f.write(json.dumps(event, ensure_ascii=False) + "\n")

def init_checkpoint():
    with open(MANIFEST_FILE, "r", encoding="utf-8") as f:
        manifest = json.load(f)
    with open(BATCHES_FILE, "r", encoding="utf-8") as f:
        batches = json.load(f)

    # Initialize batch statuses
    batches_state = {}
    for b_id, b in batches.items():
        batches_state[b_id] = {
            "batch_id": b_id,
            "module_id": b["module_id"],
            "status": "pending",
            "attempt": 0,
            "total_lines": b["total_lines"],
            "files_count": len(b["files"]),
            "artifact_hashes": []
        }

    checkpoint = {
        "schema_version": 1,
        "run_id": "run-agent-map-2026-09-26",
        "goal_id": "goal-7aa1c9c5-4b5b-473d-a734-b15ab8aeba5e",
        "revision": 1,
        "epoch": 1,
        "phase": "phase1_gemini_inventory",
        "created_at": time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()),
        "updated_at": time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()),
        "stats": {
            "total_batches": len(batches_state),
            "pending_batches": len(batches_state),
            "running_batches": 0,
            "accepted_batches": 0,
            "failed_batches": 0
        },
        "batches": batches_state
    }

    tmp_file = CHECKPOINT_FILE.with_suffix(".tmp")
    with open(tmp_file, "w", encoding="utf-8") as f:
        json.dump(checkpoint, f, indent=2, ensure_ascii=False)
    tmp_file.replace(CHECKPOINT_FILE)

    log_event("SUPERVISOR_INITIALIZED", {
        "run_id": checkpoint["run_id"],
        "total_batches": len(batches_state),
        "phase": checkpoint["phase"]
    })
    print("Checkpoint initialized successfully.")

def get_next_pending_batches(limit=10, filter_modules=None):
    if not CHECKPOINT_FILE.exists():
        init_checkpoint()

    with open(CHECKPOINT_FILE, "r", encoding="utf-8") as f:
        cp = json.load(f)

    pending = []
    for b_id, b in cp["batches"].items():
        if b["status"] == "pending":
            if filter_modules and b["module_id"] not in filter_modules:
                continue
            pending.append(b_id)
            if len(pending) >= limit:
                break
    return pending

def mark_batch_running(batch_id):
    with open(CHECKPOINT_FILE, "r", encoding="utf-8") as f:
        cp = json.load(f)

    b = cp["batches"][batch_id]
    b["status"] = "running"
    b["attempt"] += 1
    b["started_at"] = time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime())
    cp["revision"] += 1
    cp["updated_at"] = time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime())
    cp["stats"]["running_batches"] += 1
    cp["stats"]["pending_batches"] -= 1

    tmp = CHECKPOINT_FILE.with_suffix(".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cp, f, indent=2, ensure_ascii=False)
    tmp.replace(CHECKPOINT_FILE)

    log_event("BATCH_DISPATCHED", {"batch_id": batch_id, "attempt": b["attempt"]})

def mark_batch_accepted(batch_id, artifact_hashes=None):
    with open(CHECKPOINT_FILE, "r", encoding="utf-8") as f:
        cp = json.load(f)

    b = cp["batches"][batch_id]
    b["status"] = "accepted"
    b["finished_at"] = time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime())
    if artifact_hashes:
        b["artifact_hashes"] = artifact_hashes
    cp["revision"] += 1
    cp["updated_at"] = time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime())
    cp["stats"]["running_batches"] = max(0, cp["stats"]["running_batches"] - 1)
    cp["stats"]["accepted_batches"] += 1

    tmp = CHECKPOINT_FILE.with_suffix(".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cp, f, indent=2, ensure_ascii=False)
    tmp.replace(CHECKPOINT_FILE)

    log_event("BATCH_ACCEPTED", {"batch_id": batch_id, "artifacts": artifact_hashes})

if __name__ == "__main__":
    init_checkpoint()
