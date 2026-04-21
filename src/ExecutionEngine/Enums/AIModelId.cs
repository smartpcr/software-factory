// -----------------------------------------------------------------------
// <copyright file="AIModelId.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Enums;

using System.Runtime.Serialization;

/// <summary>
/// Enumerates the available AI model identifiers.
/// </summary>
public enum AIModelId
{
    /// <summary>Claude Opus 4.7 — premium tier.</summary>
    [EnumMember(Value = "claude-opus-4.7")]
    ClaudeOpus47 = 0,

    /// <summary>Claude Opus 4.6 — premium tier.</summary>
    [EnumMember(Value = "claude-opus-4.6")]
    ClaudeOpus46,

    /// <summary>Claude Opus 4.6 with 1M context — premium tier.</summary>
    [EnumMember(Value = "claude-opus-4.6-1m")]
    ClaudeOpus46_1M,

    /// <summary>Claude Opus 4.5 — premium tier.</summary>
    [EnumMember(Value = "claude-opus-4.5")]
    ClaudeOpus45,

    /// <summary>Claude Sonnet 4.6 — standard tier.</summary>
    [EnumMember(Value = "claude-sonnet-4.6")]
    ClaudeSonnet46,

    /// <summary>Claude Sonnet 4.5 — standard tier.</summary>
    [EnumMember(Value = "claude-sonnet-4.5")]
    ClaudeSonnet45,

    /// <summary>Claude Sonnet 4 — standard tier.</summary>
    [EnumMember(Value = "claude-sonnet-4")]
    ClaudeSonnet4,

    /// <summary>Claude Haiku 4.5 — fast/cheap tier.</summary>
    [EnumMember(Value = "claude-haiku-4.5")]
    ClaudeHaiku45,

    /// <summary>GPT-5.4 — standard tier.</summary>
    [EnumMember(Value = "gpt-5.4")]
    Gpt54,

    /// <summary>GPT-5.3-Codex — standard tier.</summary>
    [EnumMember(Value = "gpt-5.3-codex")]
    Gpt53Codex,

    /// <summary>GPT-5.2-Codex — standard tier.</summary>
    [EnumMember(Value = "gpt-5.2-codex")]
    Gpt52Codex,

    /// <summary>GPT-5.2 — standard tier.</summary>
    [EnumMember(Value = "gpt-5.2")]
    Gpt52,

    /// <summary>GPT-5.4 mini — fast/cheap tier.</summary>
    [EnumMember(Value = "gpt-5.4-mini")]
    Gpt54Mini,

    /// <summary>GPT-5 mini — fast/cheap tier.</summary>
    [EnumMember(Value = "gpt-5-mini")]
    Gpt5Mini,

    /// <summary>GPT-4.1 — fast/cheap tier.</summary>
    [EnumMember(Value = "gpt-4.1")]
    Gpt41,
}