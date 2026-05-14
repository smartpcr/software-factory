"""
Phase 63 one-off — repair PR smartpcr/qq#14 state in the Forge DB.

Background: GitHubPrCommentSyncClient.IsConfigured returned false in this
deployment (no Forge:GitHub:Pat set; client lacked gh-CLI fallback), so
every sync of PR #14 failed silently with
LastSyncStatus='failed' / "Sync client for provider 'github' not configured".
The PR was auto-merged on GitHub at 2026-05-14T00:41:30Z but Forge's local
mirror never refreshed -> UI stayed on iter 4 ACTIVE.

This script applies the same mutations that PrSyncJob.ApplySnapshotAsync +
WorkstreamMergeAdvancer.AdvanceForMergedPrAsync would have written if sync
had succeeded. Safe to run while Forge.Api is up: SQLite WAL mode serializes
writes, and the only fields touched are the ones the sync path owns.

Run with:  python scripts/repair-pr14.py
"""
import sqlite3
import json
import datetime

DB_PATH = r'E:\forge\forge.db'
PR_ID = 'PR smartpcr/qq#14'
WS_ID = 'ws-qq-microsoft-teams-mess-phase-teams-bot-framework-core-stage-asp-net-core-bot-host'

# Authoritative state from GitHub REST GET /repos/smartpcr/qq/pulls/14
GH_STATE = dict(
    additions=11736,
    deletions=0,
    changed_files=112,
    commits=7,
    base_ref='main',
    head_ref='ws/qq-MICROSOFT-TEAMS-MESS/phase-teams-bot-framework-core-stage-asp-net-core-bot-host',
    merged_at='2026-05-14T00:41:30Z',
    updated_at='2026-05-14T00:41:30Z',
    closed_at='2026-05-14T00:41:30Z',
    merge_commit_sha='1b6c171fd1f13eb2f3b7f5133191d6f9ee541e09',
)

now_iso = datetime.datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%S.%fZ')

db = sqlite3.connect(DB_PATH)
db.row_factory = sqlite3.Row
db.execute('BEGIN IMMEDIATE')
try:
    # ----- snapshot before -----
    pr_before = dict(db.execute('SELECT * FROM prs WHERE Id = ?', (PR_ID,)).fetchone())
    ws_before = dict(db.execute('SELECT * FROM workstreams WHERE Id = ?', (WS_ID,)).fetchone())
    print('--- BEFORE ---')
    print(f"  prs.Col              = {pr_before['Col']}")
    print(f"  prs.IsActive         = {pr_before['IsActive']}")
    print(f"  prs.LastSyncStatus   = {pr_before['LastSyncStatus']}")
    print(f"  prs.LastSyncError    = {pr_before['LastSyncError']}")
    print(f"  prs.Additions        = {pr_before['Additions']}")
    print(f"  prs.Files            = {pr_before['Files']}")
    print(f"  prs.Commits          = {pr_before['Commits']}")
    print(f"  prs.Base             = {pr_before['Base']}")
    print(f"  workstreams.Status   = {ws_before['Status']}")
    print(f"  workstreams.PrsJson  = {ws_before['PrsJson']}")

    # ----- prs row -----
    db.execute("""
        UPDATE prs SET
            Col                  = 'merged',
            IsActive             = 0,
            Additions            = :additions,
            Deletions            = :deletions,
            Files                = :files,
            Commits              = :commits,
            Base                 = :base_ref,
            Branch               = :head_ref,
            LastSyncAt           = :now,
            LastSyncStatus       = 'idle',
            LastSyncError        = NULL,
            IsRunningSince       = NULL,
            LastUpstreamUpdatedAt= :updated_at,
            UpdatedAt            = :updated_at
        WHERE Id = :prid
    """, dict(
        additions=GH_STATE['additions'],
        deletions=GH_STATE['deletions'],
        files=GH_STATE['changed_files'],
        commits=GH_STATE['commits'],
        base_ref=GH_STATE['base_ref'],
        head_ref=GH_STATE['head_ref'],
        updated_at=GH_STATE['updated_at'],
        now=now_iso,
        prid=PR_ID,
    ))

    # ----- workstreams row -----
    # Mirror what AdvanceForMergedPrAsync would do: PrsJson must contain the
    # PR id BEFORE Status flips to 'complete'.
    prs_list = json.loads(ws_before['PrsJson'] or '[]')
    if PR_ID not in prs_list:
        prs_list.append(PR_ID)
    new_prs_json = json.dumps(prs_list)

    db.execute("""
        UPDATE workstreams SET
            Status   = 'complete',
            PrsJson  = :prs
        WHERE Id = :wsid
    """, dict(prs=new_prs_json, wsid=WS_ID))

    # ----- pr_comment_history audit row (mimics PrSyncJob writing
    # the state-transition row) -----
    db.execute("""
        INSERT INTO pr_comment_history (CommentId, Kind, FromValue, ToValue, ByUserId, At, PayloadJson)
        VALUES (0, 'pr-state-transition', :from_, 'merged', 'manual:repair-pr14', :now, :payload)
    """, dict(
        from_=pr_before['Col'],
        now=now_iso,
        payload=json.dumps(dict(prId=PR_ID, source='scripts/repair-pr14.py')),
    ))

    # ----- verify -----
    pr_after = dict(db.execute('SELECT * FROM prs WHERE Id = ?', (PR_ID,)).fetchone())
    ws_after = dict(db.execute('SELECT * FROM workstreams WHERE Id = ?', (WS_ID,)).fetchone())
    print('--- AFTER ---')
    print(f"  prs.Col              = {pr_after['Col']}")
    print(f"  prs.IsActive         = {pr_after['IsActive']}")
    print(f"  prs.LastSyncStatus   = {pr_after['LastSyncStatus']}")
    print(f"  prs.LastSyncError    = {pr_after['LastSyncError']}")
    print(f"  prs.Additions        = {pr_after['Additions']}")
    print(f"  prs.Files            = {pr_after['Files']}")
    print(f"  prs.Commits          = {pr_after['Commits']}")
    print(f"  prs.Base             = {pr_after['Base']}")
    print(f"  workstreams.Status   = {ws_after['Status']}")
    print(f"  workstreams.PrsJson  = {ws_after['PrsJson']}")

    db.commit()
    print('--- COMMIT OK ---')
except Exception as e:
    db.rollback()
    print('--- ROLLBACK ---', e)
    raise
finally:
    db.close()
