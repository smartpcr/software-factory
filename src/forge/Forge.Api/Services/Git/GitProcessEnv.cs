// -----------------------------------------------------------------------
// <copyright file="GitProcessEnv.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

using System.Diagnostics;

/// <summary>
/// 2026-05-12 follow-up — fail-fast environment variables applied to
/// EVERY <c>git</c> subprocess Forge.Api spawns. Without these, a
/// network op (push, fetch, ls-remote, clone) against a remote whose
/// cached credentials have expired pops <c>git-credential-manager get</c>,
/// which **hangs waiting for an interactive UI prompt** that never
/// arrives (Forge.Api has no terminal). Observed 2026-05-12 on
/// <c>msazure.visualstudio.com</c>: <c>git push origin --delete ws/...</c>
/// spawned from <see cref="Forge.Api.Endpoints.StoriesEndpoints"/>'s
/// Phase 50 respawn purge stayed hung indefinitely, freezing
/// <c>POST /api/stories/{id}/spawn</c>.
///
/// <para>The three vars together cover every credential-prompt path Git
/// supports:
/// <list type="bullet">
/// <item><c>GIT_TERMINAL_PROMPT=0</c> — rejects terminal prompts.</item>
/// <item><c>GCM_INTERACTIVE=Never</c> — disables Git Credential Manager's
///   GUI/IPC prompts.</item>
/// <item><c>GIT_ASKPASS=echo</c> — short-circuits the legacy askpass
///   path by returning empty input.</item>
/// </list>
/// With all three set, an expired credential fails the git command in
/// milliseconds with a non-zero exit code rather than blocking forever.</para>
///
/// <para>Apply this at every <c>new ProcessStartInfo("git")</c> call
/// site. Local-only ops (e.g. <c>git branch -D</c>) don't strictly
/// need it, but applying everywhere is harmless and prevents future
/// callers from accidentally introducing a credential-prompt hang.</para>
/// </summary>
internal static class GitProcessEnv
{
    /// <summary>
    /// Apply the fail-fast credential-prompt suppression env vars to
    /// <paramref name="psi"/>. Idempotent and side-effect free beyond
    /// the three environment entries.
    /// </summary>
    public static void ApplyFailFast(ProcessStartInfo psi)
    {
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "Never";
        psi.Environment["GIT_ASKPASS"] = "echo";
    }
}
