# Forge — providers (ADO + GitHub)

Forge talks to remote sources through `IForgeProvider`. Two implementations
ship out of the box:

| Provider | Read surface | Write surface |
|----------|--------------|---------------|
| `ado`    | Work items, PRs, builds | Create/update WI, PRs, comments, votes, merge, queue pipeline |
| `github` | Issues, PRs, check-runs | Create/update issues, PRs, reviews, comments, merge, dispatch workflow |

All write paths require `X-Forge-User`. If `Forge:Auth:Secret` is set, the
client must also send `X-Forge-Secret`.

## Azure DevOps

1. **Create a PAT** with scopes:
   - Work Items (read & write)
   - Code (read & write)
   - Build (read & execute) if you want pipeline triggers
2. **Set keys**:
   ```pwsh
   cd src/forge/Forge.Api
   dotnet user-secrets set "Forge:Ado:OrgUrl"     "https://dev.azure.com/your-org"
   dotnet user-secrets set "Forge:Ado:Project"    "YourProject"
   dotnet user-secrets set "Forge:Ado:Repository" "your-repo"
   dotnet user-secrets set "Forge:Ado:Pat"        "<your pat>"
   ```
3. **Optional WIQL filter** — `Forge:Ado:WorkItemQuery`. Omit for the default
   "all work items in this project ordered by changed date".
4. **Webhooks** — point a Service Hook at `POST /api/webhooks/ado` and set
   `Forge:Ado:WebhookSecret` to the same secret you configure in the hook.

## GitHub

1. **Token**: a fine-grained PAT scoped to the repo (Issues, PRs, Actions
   read/write) or a classic PAT with `repo` scope.
2. **Set keys**:
   ```pwsh
   dotnet user-secrets set "Forge:GitHub:Repo" "owner/repo"
   dotnet user-secrets set "Forge:GitHub:Pat"  "<your pat>"
   ```
3. **Webhooks** — at `POST /api/webhooks/github`. Configure the GitHub webhook
   secret to match `Forge:GitHub:WebhookSecret`; HMAC is verified via
   `X-Hub-Signature-256`.
4. **Workflow dispatch** — `TriggerBuildAsync` POSTs `/dispatches`. Add a
   `forge-dispatch` workflow listening for the event_type to wire CI re-runs.

## Per-user PATs (Phase 6.11)

The server PAT is used for read-side sync. Writes can run on behalf of the
human user when these headers are present:

- `X-Forge-Ado-Pat: <pat>`
- `X-Forge-GitHub-Pat: <pat>`

The header overrides the server token for that single request — useful when
attribution matters (e.g. who closed this issue).

## Health

`GET /api/providers` returns the last-known health snapshot for every
provider; the Settings page polls this and surfaces a "test connection"
button per provider.

## When nothing is configured

Both providers report `IsConfigured = false`. The board / PR / workstream
views still render off the local SQLite cache (seeded with sample data on
first boot). Auto-merge is a no-op until at least one provider can be picked.
