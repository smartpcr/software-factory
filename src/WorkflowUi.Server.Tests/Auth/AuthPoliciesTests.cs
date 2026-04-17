// -----------------------------------------------------------------------
// <copyright file="AuthPoliciesTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Auth;

using WorkflowUi.Server.Auth;
using Xunit;

public class AuthPoliciesTests
{
    [Fact]
    public void EditorPolicy_Equals_Editor()
    {
        Assert.Equal("Editor", AuthPolicies.EditorPolicy);
    }

    [Fact]
    public void ViewerPolicy_Equals_Viewer()
    {
        Assert.Equal("Viewer", AuthPolicies.ViewerPolicy);
    }

    [Fact]
    public void EditorRole_Equals_Editor()
    {
        Assert.Equal("Editor", AuthPolicies.EditorRole);
    }

    [Fact]
    public void ViewerRole_Equals_Viewer()
    {
        Assert.Equal("Viewer", AuthPolicies.ViewerRole);
    }
}
