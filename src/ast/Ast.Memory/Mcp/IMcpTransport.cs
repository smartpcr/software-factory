// -----------------------------------------------------------------------
// <copyright file="IMcpTransport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Mcp;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Abstraction for MCP JSON-RPC transport (stdio, SSE, or in-process).
/// External agents invoke memory tools through this transport.
/// </summary>
public interface IMcpTransport
{
    /// <summary>
    /// Reads the next JSON-RPC request from the transport.
    /// Returns null when the transport is closed.
    /// </summary>
    Task<string?> ReadRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes a JSON-RPC response to the transport.
    /// </summary>
    Task WriteResponseAsync(string response, CancellationToken ct = default);
}
