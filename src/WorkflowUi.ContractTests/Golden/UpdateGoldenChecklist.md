# Updating Golden Files

## When to Update

Golden files should be updated when:
- A new runtime kind is added to the engine
- An existing engine field is renamed or its type changes
- A new field is added to `NodeDefinition` or a subclass
- The mapper logic changes intentionally

## Checklist

1. **Identify the change** — What engine field or mapper behavior changed?
2. **Update the YAML fixture** — Edit the `.yaml` file to reflect the new canonical form
3. **Update the golden JSON** — Edit the `.golden.json` to match the expected mapper output
4. **Run golden tests** — `dotnet test src/WorkflowUi.ContractTests --filter "Category=Golden"`
5. **Review the diff** — Ensure only the intended fields changed
6. **Update sidecar if needed** — `.ui.json` files should rarely change (UI-only metadata)
7. **Verify manifest counts** — If adding/removing fixtures, update manifest assertions

## Review Expectations

- Golden PRs should show exactly which fields drifted
- Reviewers should verify the drift matches the intended engine change
- Accidental drift (e.g., formatting-only changes) should be investigated

## Line Ending Normalization

All comparisons normalize `\r\n` to `\n` to prevent cross-platform noise.
Fixtures should use LF line endings when possible.
