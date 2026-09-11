// -----------------------------------------------------------------------
// <copyright file="IChatOperationGate.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

/// <summary>Optional host ownership boundary. A tick retains its lease through dispatch and cursor checkpoint.</summary>
public interface IChatOperationGate
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IDisposable?> EnterAsync(string engine, CancellationToken ct = default);
    IDisposable ContinueLease();
}