// -----------------------------------------------------------------------
// <copyright file="AuthPolicies.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

/// <summary>
/// Named authorization policies and role constants.
/// </summary>
public static class AuthPolicies
{
    public const string EditorPolicy = "Editor";
    public const string ViewerPolicy = "Viewer";
    public const string EditorRole = "Editor";
    public const string ViewerRole = "Viewer";
}
