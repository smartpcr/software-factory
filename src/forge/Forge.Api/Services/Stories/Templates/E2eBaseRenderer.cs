// -----------------------------------------------------------------------
// <copyright file="E2eBaseRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.Templates;

using System.Text;

/// <summary>
/// docs/forge/e2e.md Stage 7.3 — production-side renderer for two
/// deterministic qa-e2e scaffold files:
///
/// <list type="bullet">
///   <item><c>tests/e2e/{phaseSlug}/appsettings.e2e.json</c> — declares
///         the env-var names the qa-e2e suite reads at runtime, with
///         empty placeholder values. Per-phase so the operator can have
///         different vars per setup type without merge conflicts.</item>
///   <item><c>tests/Steps/{storyId}/E2eBase.cs</c> — Reqnroll-friendly
///         base class with a <c>RequireEnv</c> helper that throws
///         <c>SetupSkippedException</c> when a required var is unset.
///         Per-spawn (one E2eBase per story) — emitted by the FIRST
///         qa-e2e workstream to fire; subsequent qa-e2e workstreams
///         skip it (idempotency tracked by the strategy via a per-spawn
///         <c>ConcurrentDictionary</c>, NOT this renderer — the renderer
///         is pure).</item>
/// </list>
///
/// <para>The base class is intentionally framework-agnostic in its
/// shape: <c>SetupSkippedException</c> is a plain <c>Exception</c>
/// subclass so the local dev loop can yellow-skip via xUnit/Reqnroll
/// custom-skip plumbing, while CI can configure the test runner to
/// fail-on-skipped via <c>appsettings.e2e.json</c> (a CI-only override
/// file populates the env vars, so a hit on <c>RequireEnv</c> in CI
/// means a real missing secret — not an expected local skip).</para>
///
/// <para>Lives under <c>Forge.Api/Services/Stories/Templates/</c> so
/// BOTH the deterministic strategy AND the LLM-driven
/// <see cref="CrewStoryArtifactStrategy"/> consume the same renderer —
/// the appsettings + base class are deterministic templates that don't
/// benefit from LLM generation, and emitting them via a shared helper
/// guarantees byte-identical output across the two producers.</para>
/// </summary>
public static class E2eBaseRenderer
{
    /// <summary>docs/forge/e2e.md Stage 7.3 — per-phase appsettings path.</summary>
    public static string AppSettingsPathFor(string phaseSlug) =>
        $"tests/e2e/{phaseSlug}/appsettings.e2e.json";

    /// <summary>docs/forge/e2e.md Stage 7.3 — per-spawn E2eBase path.</summary>
    public static string E2eBasePathFor(string storyId) =>
        $"tests/Steps/{StoryPaths.Safe(storyId)}/E2eBase.cs";

    /// <summary>
    /// docs/forge/e2e.md Stage 7.3 — env-var names the qa-e2e suite for
    /// a given setup type expects. Exposed so test assertions, the LLM
    /// prompt section, and the appsettings renderer all agree on the
    /// canonical list per setup type.
    /// <list type="bullet">
    ///   <item><c>inline</c> — no vars; tests run in-process with no
    ///         external infra.</item>
    ///   <item><c>compose</c> — the two service-host vars the default
    ///         <c>app + redis</c> scaffold's tests read.</item>
    ///   <item><c>lab-wsfc</c> / <c>lab-bare-metal</c> — the three
    ///         <c>LAB_*</c> vars Key Vault / GH environment secrets
    ///         supply (matches <c>E2eLabRenderer</c> Stage 7.2 env
    ///         block).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> EnvVarNames(E2eSetupType setupType) =>
        setupType switch
        {
            E2eSetupType.Inline => Array.Empty<string>(),
            E2eSetupType.Compose => new[] { "COMPOSE_REDIS_HOST", "COMPOSE_REDIS_PORT" },
            E2eSetupType.LabWsfc => LabEnvVarNames,
            E2eSetupType.LabBareMetal => LabEnvVarNames,
            _ => Array.Empty<string>(),
        };

    private static readonly string[] LabEnvVarNames = new[]
    {
        "LAB_CLUSTER_FQDN",
        "LAB_ADMIN_USER",
        "LAB_ADMIN_PASSWORD",
    };

    /// <summary>
    /// docs/forge/e2e.md Stage 7.3 — render <c>appsettings.e2e.json</c>
    /// declaring every env-var the qa-e2e suite for
    /// <paramref name="setupType"/> reads, with empty-string values.
    /// Inline phases emit an empty-but-valid <c>{ }</c> object so the
    /// file's existence still hints at the convention.
    /// </summary>
    public static string RenderAppSettings(E2eSetupType setupType, string phaseSlug)
    {
        var vars = EnvVarNames(setupType);
        var setupWire = E2eScenariosSetupReader.ToWire(setupType);

        var sb = new StringBuilder()
            .AppendLine("{")
            .AppendLine($"  \"_comment\": \"docs/forge/e2e.md Stage 7.3 — appsettings template for phase '{phaseSlug}' (setup: {setupWire}). Populate values via env-vars on CI or the operator's secrets store; empty values trigger SetupSkippedException locally.\",")
            .AppendLine("  \"E2e\": {");

        if (vars.Count == 0)
        {
            sb.AppendLine("    \"_comment\": \"Inline setup — no external env-vars required.\"");
        }
        else
        {
            for (var i = 0; i < vars.Count; i++)
            {
                var comma = i == vars.Count - 1 ? string.Empty : ",";
                sb.AppendLine($"    \"{vars[i]}\": \"\"{comma}");
            }
        }

        sb.AppendLine("  }")
            .Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 7.3 — render the per-spawn
    /// <c>E2eBase.cs</c> step-def base class + <c>SetupSkippedException</c>
    /// sibling. Both types live in the namespace
    /// <c>Forge.Tests.Stories.{storyIdUnderscored}</c> so step-def files
    /// generated under
    /// <c>tests/Steps/{storyId}/{phaseSlug}/{stage}Steps.cs</c> can
    /// inherit from <c>E2eBase</c> without an extra <c>using</c>.
    ///
    /// <para>The <c>SetupSkippedException</c> message intentionally
    /// includes the missing var name (and the exception exposes it via
    /// <c>VarName</c>) so the local skip-renderer / CI failure handler
    /// can show the operator exactly which secret to populate.</para>
    /// </summary>
    public static string RenderE2eBaseClass(string storyId)
    {
        var nsSegment = StoryPaths.Safe(storyId).Replace('-', '_');
        var ns = $"Forge.Tests.Stories.{nsSegment}";

        return $$"""
            // -----------------------------------------------------------------------
            // <copyright file="E2eBase.cs" company="Microsoft Corp.">
            //     Copyright (c) Microsoft Corp. All rights reserved.
            // </copyright>
            // -----------------------------------------------------------------------
            //
            // docs/forge/e2e.md Stage 7.3 — auto-generated step-def base class
            // for story '{{storyId}}'. Emitted ONCE per spawn (by the first
            // qa-e2e workstream to fire); subsequent qa-e2e stages reuse this
            // type. Edit by hand only when adding helpers that apply to every
            // stage of the story; the generator overwrites this file on the
            // next full Forge spawn.

            namespace {{ns}};

            using System;

            /// <summary>
            /// Step-definition base class — derive every Reqnroll
            /// <c>[Binding]</c> class for this story from <c>E2eBase</c>
            /// and call <see cref="RequireEnv"/> in the constructor /
            /// <c>BeforeScenario</c> hook to assert the env-vars
            /// <c>appsettings.e2e.json</c> declares are populated.
            /// </summary>
            public abstract class E2eBase
            {
                /// <summary>
                /// Read an env-var; throw <see cref="SetupSkippedException"/>
                /// when it's null or empty. The exception carries the var
                /// name so the test runner can surface it; locally the
                /// CI-fail-on-skipped flag is off so it bubbles up as a
                /// yellow skip, on CI the override config flips it red.
                /// </summary>
                protected static string RequireEnv(string name)
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (string.IsNullOrEmpty(value))
                    {
                        throw new SetupSkippedException(name);
                    }
                    return value;
                }
            }

            /// <summary>
            /// Thrown by <see cref="E2eBase.RequireEnv(string)"/> when a
            /// required env-var is not populated. Carries the missing var
            /// name as a property so the surrounding test runner /
            /// reporter can render an actionable skip / failure message.
            /// </summary>
            public sealed class SetupSkippedException : Exception
            {
                /// <summary>The env-var name that was unset.</summary>
                public string VarName { get; }

                public SetupSkippedException(string varName)
                    : base($"E2E setup skipped: required env var '{varName}' is not set. Populate it via appsettings.e2e.json overrides or the CI secret store.")
                {
                    this.VarName = varName;
                }
            }

            """;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 7.3 — render both Stage 7.3 files for a
    /// qa-e2e workstream. The caller decides whether to include the
    /// per-spawn base class (only the first qa-e2e per spawn should set
    /// <paramref name="includeBaseClass"/>=true; the strategy tracks
    /// "first-per-spawn" via its own cache and passes the flag).
    /// </summary>
    public static IReadOnlyDictionary<string, string> RenderAll(
        string storyId, string phaseSlug, E2eSetupType setupType, bool includeBaseClass)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppSettingsPathFor(phaseSlug)] = RenderAppSettings(setupType, phaseSlug),
        };
        if (includeBaseClass)
        {
            dict[E2eBasePathFor(storyId)] = RenderE2eBaseClass(storyId);
        }
        return dict;
    }
}
