// -----------------------------------------------------------------------
// <copyright file="GitProcessEnv.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

using System.Diagnostics;

/// <summary>
/// Policy-driven environment variables applied to EVERY <c>git</c>
/// subprocess Forge.Api spawns. The default policy is <see cref="GitPromptPolicy.FailFast"/>
/// — credential prompts are suppressed so a network op against a remote
/// with expired/missing cached credentials fails in milliseconds instead
/// of hanging waiting for an interactive UI prompt that may never arrive
/// (Forge.Api as a Windows service / session-0 deployment has no desktop).
/// This default protects unattended deployments.
///
/// <para>For interactive desktop runs (e.g. <c>scripts/run-forge-ui.ps1</c>)
/// the operator IS sitting at the desktop and CAN answer GCM's browser /
/// system-credential prompt. To enable that flow, set the env var
/// <c>FORGE_GIT_PROMPT_POLICY=Interactive</c> before launching Forge.Api
/// — the dev script does this automatically. Even in Interactive mode,
/// <c>GIT_TERMINAL_PROMPT=0</c> stays set because Forge.Api has no TTY;
/// a TTY prompt would just EOF-fail anyway. The relevant relaxation is
/// dropping <c>GCM_INTERACTIVE=Never</c> so GCM can pop its GUI prompt.</para>
///
/// <para>The original 2026-05-12 hang (<c>git push origin --delete ws/...</c>
/// against <c>msazure.visualstudio.com</c> stalling
/// <c>POST /api/stories/{id}/spawn</c>) is still prevented by the default
/// FailFast policy. In Interactive mode the safety net shifts to per-call
/// timeouts (see <c>RealStoryGitOps.RunGitCaptureAsync</c>) — even if the
/// operator walks away, the git subprocess is killed after the configured
/// timeout instead of leaking.</para>
///
/// <para>Apply at every <c>new ProcessStartInfo("git")</c> call site via
/// <see cref="ApplyFailFast"/> (or its alias <see cref="Apply"/>); do not
/// hand-set <c>GIT_TERMINAL_PROMPT</c> / <c>GCM_INTERACTIVE</c> / <c>GIT_ASKPASS</c>
/// / <c>GCM_AZREPOS_CREDENTIALTYPE</c> directly — that bypasses the policy
/// and re-introduces inconsistency between Interactive and FailFast
/// deployments.</para>
/// </summary>
internal static class GitProcessEnv
{
    private const string PolicyEnvVar = "FORGE_GIT_PROMPT_POLICY";

    /// <summary>
    /// Resolves the active prompt policy. Reads
    /// <c>FORGE_GIT_PROMPT_POLICY</c> from the process environment;
    /// recognised values (case-insensitive) are <c>Interactive</c> and
    /// <c>FailFast</c>. Anything else (including unset) yields the safe
    /// default <see cref="GitPromptPolicy.FailFast"/>.
    /// </summary>
    public static GitPromptPolicy ResolvePolicy()
    {
        var raw = Environment.GetEnvironmentVariable(PolicyEnvVar);
        if (string.Equals(raw, "Interactive", StringComparison.OrdinalIgnoreCase))
        {
            return GitPromptPolicy.Interactive;
        }
        return GitPromptPolicy.FailFast;
    }

    /// <summary>
    /// Apply the active prompt policy (resolved from env) to
    /// <paramref name="psi"/>. Use this at every
    /// <c>new ProcessStartInfo("git")</c> call site so the policy is
    /// consistent across the whole process.
    /// </summary>
    public static void Apply(ProcessStartInfo psi) => ApplyInternal(psi, ResolvePolicy());

    /// <summary>
    /// Legacy name retained so the 14+ existing call sites keep
    /// compiling. Equivalent to <see cref="Apply"/> — both consult the
    /// env policy. Prefer <see cref="Apply"/> in new code; the name
    /// "FailFast" no longer accurately describes the behaviour in
    /// Interactive mode.
    /// </summary>
    public static void ApplyFailFast(ProcessStartInfo psi) => Apply(psi);

    /// <summary>
    /// Force a specific policy regardless of the env var. Use only when
    /// a specific call site must override (e.g. a background reconciler
    /// that must NEVER pop UI even when the rest of the process allows
    /// prompts).
    /// </summary>
    public static void ApplyExplicit(ProcessStartInfo psi, GitPromptPolicy policy) => ApplyInternal(psi, policy);

    private static void ApplyInternal(ProcessStartInfo psi, GitPromptPolicy policy)
    {
        // GIT_TERMINAL_PROMPT=0 is always set: Forge.Api has no TTY, so
        // a terminal prompt would just EOF-fail. Setting this to 0
        // makes the failure fast and clean instead of letting git try
        // to read from a closed stdin.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Azure DevOps: force Git Credential Manager to authenticate with
        // an Azure AD OAuth token instead of minting a short-lived PAT
        // (GCM's default credential type for the azure-repos host
        // provider). Microsoft-internal tenants such as `msazure` enforce
        // DisablePatCreationPolicy, so GCM's PAT mint is rejected with
        // "Failed to create PAT: DisablePatCreationPolicyViolation"; git
        // then falls back to a terminal username prompt that
        // GIT_TERMINAL_PROMPT=0 immediately EOF-fails. Observed 2026-05-24
        // on msazure.visualstudio.com/.../ASZ-UpdateService: every
        // `git fetch origin main` died at exit 128 and HALTED the spawn.
        // OAuth hands the AAD token straight to git so NO PAT is ever
        // created. This is the env-var equivalent of
        // `git config credential.azreposCredentialType oauth`, pinned here
        // so the behaviour holds regardless of the operator's global git
        // config. Honoured under both policies: in Interactive mode GCM can
        // pop its sign-in dialog on first use; in FailFast it uses a
        // cached / broker token silently and never attempts the (forbidden)
        // PAT path.
        psi.Environment["GCM_AZREPOS_CREDENTIALTYPE"] = "oauth";

        if (policy == GitPromptPolicy.FailFast)
        {
            // Service-mode / unattended deployments: refuse every
            // credential prompt path so a network op fails in
            // milliseconds instead of hanging waiting for a UI that
            // nobody can answer.
            psi.Environment["GCM_INTERACTIVE"] = "Never";
            psi.Environment["GIT_ASKPASS"] = "echo";
        }
        else
        {
            // Interactive desktop: allow GCM to pop its browser /
            // system credential dialog so the operator can sign in.
            // The per-call timeout in RealStoryGitOps.RunGitCaptureAsync
            // is the safety net against a missed prompt.
            //
            // Clear any inherited "Never" so a parent process that set
            // it in its environment can't override the policy here.
            psi.Environment.Remove("GCM_INTERACTIVE");
            psi.Environment.Remove("GIT_ASKPASS");
        }
    }
}

/// <summary>
/// Credential-prompt policy for Forge.Api's git subprocesses. See
/// <see cref="GitProcessEnv"/> for the semantics of each value.
/// </summary>
internal enum GitPromptPolicy
{
    /// <summary>Suppress every credential prompt (service-mode default).</summary>
    FailFast = 0,

    /// <summary>Allow GCM to pop its GUI prompt (interactive-desktop opt-in).</summary>
    Interactive = 1,
}
