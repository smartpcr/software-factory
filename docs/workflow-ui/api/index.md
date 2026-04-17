# API Reference

> **Generated from OpenAPI** — The canonical API specification is served at
> `/swagger/v1/swagger.json` by the running server.  The documentation below
> is derived from that spec.  In development mode the interactive Swagger UI
> is available at `/swagger`.

The Workflow UI Server exposes the following REST API endpoints.

## Health

### `GET /api/health`
Returns the health status of the server.

**Response:** `200 OK`
```json
{ "status": "ok", "timestamp": "2025-01-01T00:00:00Z" }
```

## Version

### `GET /api/version`
Returns build version information. Anonymous-accessible.

**Response:** `200 OK`
```json
{
  "version": "0.6.1",
  "commitSha": "abcdef0",
  "buildUtc": "2025-01-01T00:00:00Z"
}
```

## Engine Version

### `GET /api/engine/version`
Returns the execution engine assembly version.

**Response:** `200 OK`
```json
{ "engineAssemblyVersion": "1.0.0.0" }
```

## Workflows

### `GET /api/workflows`
Lists all workflow summaries.

**Response:** `200 OK` — Array of `WorkflowSummary`

### `GET /api/workflows/{id}`
Gets a workflow by ID. Supports slash-delimited nested IDs.

**Response:** `200 OK` — `WorkflowRecord` | `404 Not Found`

### `PUT /api/workflows/{id}`
Creates or updates a workflow. Requires Editor role.

**Headers:** `If-Match` (optional ETag for optimistic concurrency)

**Body:**
```json
{ "yaml": "...", "sidecarJson": "{}" }
```

**Response:** `201 Created` | `200 OK` | `412 Precondition Failed`

### `DELETE /api/workflows/{id}`
Deletes a workflow. Requires Editor role.

**Headers:** `If-Match` (required ETag)

**Response:** `204 No Content` | `404 Not Found`

## File Operations

### `GET /api/files`
Returns the workspace tree structure.

**Response:** `200 OK` — `TreeNode`

### `POST /api/files`
Creates a new empty workflow.

**Body:**
```json
{ "id": "my-workflow" }
```

**Response:** `201 Created` | `409 Conflict`

### `PATCH /api/files/{id}`
Renames a workflow.

**Body:**
```json
{ "newId": "new-name" }
```

**Response:** `200 OK` | `404 Not Found` | `409 Conflict`

### `DELETE /api/files/{id}`
Deletes a workflow file.

**Response:** `204 No Content` | `404 Not Found`

### `POST /api/files/duplicate`
Duplicates a workflow with optional target ID and template placement.

**Body:**
```json
{ "sourceId": "original", "targetId": null }
```

**Response:** `201 Created` | `404 Not Found`

### `POST /api/files/export`
Exports workflows as a zip archive.

**Body:**
```json
{ "ids": ["wf1", "wf2"] }
```

**Response:** `200 OK` — `application/zip`

### `POST /api/files/import`
Imports workflows from a zip archive.

**Body:** Raw zip file bytes

**Response:** `200 OK`
```json
{ "imported": ["wf1", "wf2"] }
```

## Validation

### `POST /api/workflows/validate`
Validates a workflow YAML document.

## SignalR Hub

### `/hubs/telemetry`
Real-time telemetry events for workflow runs.

**Methods:**
- `AttachRun(runId)` — Subscribe to run events
- `DetachRun(runId)` — Unsubscribe from run events
- `Replay(runId, fromSeq)` — Replay buffered events

## Response Headers

All responses include:
- `X-Workflow-Ui-Version` — Current server version
- `X-Correlation-Id` — Request correlation identifier

## Authentication

All endpoints except `/api/health` and `/api/version` require authentication.
In development mode with `AllowDevBypass=true`, loopback requests are auto-authenticated.
