# Release Checklist

## Pre-Release

- [ ] All CI checks pass on the release branch
- [ ] `dotnet build Workflow.sln` succeeds
- [ ] `dotnet test` passes for all test projects
- [ ] `npm run lint` passes with zero errors
- [ ] `npm run test` passes
- [ ] `npm run test:types` passes
- [ ] Version in `version.json` is updated
- [ ] CHANGELOG.md (or release notes) is prepared

## Build Artifacts

- [ ] Container image builds successfully (`docker build .`)
- [ ] Self-contained exe publishes for win-x64
- [ ] Self-contained exe publishes for linux-x64
- [ ] UI assets are embedded in published artifacts

## Validation

- [ ] Container starts and `/api/health` returns 200
- [ ] Self-contained exe starts and `/api/health` returns 200
- [ ] `/api/version` returns the correct version
- [ ] UI loads in browser
- [ ] Engine parity tests pass against the release engine version

## Release

- [ ] Git tag created matching version (`v{version}`)
- [ ] GitHub Release created with changelog
- [ ] Container image pushed to registry
- [ ] Artifacts uploaded to release

## Post-Release

- [ ] Release notes reviewed and published
- [ ] Documentation site updated
- [ ] Smoke test on released artifacts
- [ ] Version bump in `version.json` for next development cycle
