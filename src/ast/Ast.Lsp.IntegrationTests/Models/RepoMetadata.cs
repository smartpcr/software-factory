// -----------------------------------------------------------------------
// <copyright file="RepoMetadata.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.IntegrationTests.Models;

/// <summary>
/// Repository-level metadata extracted via LSP analysis.
/// </summary>
public class LspRepoMetadata
{
    /// <summary>
    /// Gets or sets the repository name.
    /// </summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the repository path.
    /// </summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the analysis timestamp.
    /// </summary>
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the entry points found in the repository.
    /// </summary>
    public List<EntryPointInfo> EntryPoints { get; set; } = [];

    /// <summary>
    /// Gets or sets the dependency injection registrations.
    /// </summary>
    public List<DiRegistration> DiRegistrations { get; set; } = [];

    /// <summary>
    /// Gets or sets the API controllers and their endpoints.
    /// </summary>
    public List<ApiControllerInfo> ApiControllers { get; set; } = [];
}

/// <summary>
/// Information about an application entry point.
/// </summary>
public class EntryPointInfo
{
    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the entry point type (Main, TopLevelStatements, WebApplicationBuilder).
    /// </summary>
    public string EntryPointType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the method signature from LSP hover info.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>
    /// Gets or sets the line number.
    /// </summary>
    public int Line { get; set; }

    /// <summary>
    /// Gets or sets the class name if applicable.
    /// </summary>
    public string? ClassName { get; set; }

    /// <summary>
    /// Gets or sets the namespace.
    /// </summary>
    public string? Namespace { get; set; }
}

/// <summary>
/// Information about a dependency injection registration.
/// </summary>
public class DiRegistration
{
    /// <summary>
    /// Gets or sets the file path where the registration occurs.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the line number.
    /// </summary>
    public int Line { get; set; }

    /// <summary>
    /// Gets or sets the service type (interface or abstract class).
    /// </summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the implementation type.
    /// </summary>
    public string ImplementationType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service lifetime (Singleton, Scoped, Transient).
    /// </summary>
    public string Lifetime { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the registration method used.
    /// </summary>
    public string RegistrationMethod { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full signature from LSP hover.
    /// </summary>
    public string? Signature { get; set; }
}

/// <summary>
/// Information about an API controller.
/// </summary>
public class ApiControllerInfo
{
    /// <summary>
    /// Gets or sets the controller name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the namespace.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets the base route from RoutePrefix or Route attribute.
    /// </summary>
    public string? BaseRoute { get; set; }

    /// <summary>
    /// Gets or sets the base class name.
    /// </summary>
    public string? BaseClass { get; set; }

    /// <summary>
    /// Gets or sets the API version if specified.
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Gets or sets whether authorization is required.
    /// </summary>
    public bool RequiresAuthorization { get; set; }

    /// <summary>
    /// Gets or sets the endpoints in this controller.
    /// </summary>
    public List<ApiEndpointInfo> Endpoints { get; set; } = [];

    /// <summary>
    /// Gets or sets the constructor dependencies from LSP analysis.
    /// </summary>
    public List<string> Dependencies { get; set; } = [];
}

/// <summary>
/// Information about an API endpoint.
/// </summary>
public class ApiEndpointInfo
{
    /// <summary>
    /// Gets or sets the method name.
    /// </summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the HTTP method (GET, POST, PUT, DELETE, etc.).
    /// </summary>
    public string HttpMethod { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the route template.
    /// </summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full route (base route + endpoint route).
    /// </summary>
    public string FullRoute { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the return type from LSP hover.
    /// </summary>
    public string? ReturnType { get; set; }

    /// <summary>
    /// Gets or sets the parameters.
    /// </summary>
    public List<ParameterInfo> Parameters { get; set; } = [];

    /// <summary>
    /// Gets or sets the line number.
    /// </summary>
    public int Line { get; set; }
}

/// <summary>
/// Parameter information.
/// </summary>
public class ParameterInfo
{
    /// <summary>
    /// Gets or sets the parameter name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parameter type.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source (FromBody, FromQuery, FromRoute, etc.).
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets whether the parameter is optional.
    /// </summary>
    public bool IsOptional { get; set; }
}