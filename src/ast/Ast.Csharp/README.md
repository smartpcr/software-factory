# Ast.Csharp

C# AST parser and hyperedge extractor using Microsoft Roslyn for semantic analysis.

## Overview

This library parses C# source code into AST nodes and extracts hyperedges that capture code relationships with full context preservation. Unlike traditional graphs where edges connect only two nodes, hyperedges connect multiple nodes with semantic roles, preserving the complete context of code relationships.

**Example**: A method call `Calculate(x, y, z)` is represented as a single hyperedge with:
- Nodes: `Calculate`, `x`, `y`, `z`
- Roles: `callee`, `arg_0`, `arg_1`, `arg_2`

## Key Components

### CsharpAstParser

Parses C# code into `AstNode` objects representing declarations:
- Classes, Interfaces, Structs, Records, Enums
- Methods, Constructors, Properties, Fields
- Namespaces (both traditional and file-scoped)

### CSharpHyperedgeExtractor

Extracts hyperedges from C# code, capturing:
- Definitions and declarations
- Call chains and method invocations
- Type relationships and inheritance
- Control flow and data flow

## Metadata Extracted

### Type Definitions

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `ClassDefinition` | Class with base types | `class`, `base_0`, `base_1`, ... |
| `InterfaceDefinition` | Interface with extended interfaces | `interface`, `extends_0`, ... |
| `StructDefinition` | Struct with implemented interfaces | `struct`, `implements_0`, ... |
| `RecordDefinition` | Record with primary constructor params | `record`, `param_0`, `param_type_0`, ... |

### Member Definitions

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `MethodDefinition` | Method signature with parameters | `method`, `return_type`, `param_0`, `param_type_0`, ... |
| `ConstructorDefinition` | Constructor with parameters | `constructor`, `param_0`, `param_type_0`, `chain` |
| `PropertyDefinition` | Property with type | `property`, `property_type` |
| `EventHandler` | Event with delegate type | `event`, `event_type` |

### Invocations

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `MethodInvocation` | Method call with all arguments | `callee`, `arg_0`, `arg_1`, ... (or `named_X`) |
| `ConstructorInvocation` | Object creation with arguments | `instantiated_type`, `ctor_arg_0`, ... |

### Control Flow

| Hyperedge Type | Description | Metadata |
|----------------|-------------|----------|
| `ControlFlow` | if/for/foreach/while | `control_type` |
| `ExceptionHandling` | try-catch-finally | `try_block`, `handler_0`, `finally` |

### Data Flow

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `Assignment` | Variable assignment | `target`, `value` |
| `DataFlow` | Variable def-use chain | `def_0`, `use_0`, `use_1`, ... |

### Advanced Patterns

| Hyperedge Type | Description | Notes |
|----------------|-------------|-------|
| `DependencyInjection` | DI pattern detection | Constructor injection of interfaces |
| `AsyncAwait` | Async operations | `await`, `awaited_task` |
| `LambdaExpression` | Lambda with params | `lambda`, `param_0`, ... |
| `LinqQuery` | LINQ query expressions | `from`, `source`, `clause_0`, ... |
| `UsingDirective` | Using imports | `imported_namespace`, optional `alias` |

### Architectural Patterns (New)

| Hyperedge Type | Description | Metadata |
|----------------|-------------|----------|
| `ServiceRegistration` | DI container registration | `lifetime`, `service_type`, `implementation_type` |
| `ApiEndpoint` | Controller routes and HTTP methods | `http_method`, `route`, `controller`, `action` |
| `EntryPoint` | Main program / startup | `is_async`, `uses_web_application`, `uses_host_builder` |
| `HttpClientUsage` | External API calls | `http_method`, `url`, `is_async` |
| `DatabaseAccess` | DbContext, SQL connections | `db_type`, `technology`, `connection_string_ref` |
| `FileSystemAccess` | File/Directory operations | `operation`, `method`, `path` |
| `ConfigurationAccess` | IConfiguration usage | `method`, `config_key` |
| `LoggingCall` | ILogger usage patterns | `log_level`, `message_template`, `structured_fields` |
| `MiddlewarePipeline` | ASP.NET middleware | `middleware_name`, `method` |

## Architectural Pattern Extraction

### Dependency Injection Setup

The extractor tracks how services are registered in the DI container:

```csharp
services.AddSingleton<IUserService, UserService>();
services.AddScoped<IRepository, SqlRepository>();
services.AddTransient<IValidator, DataValidator>();
services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
services.AddHttpClient<IApiClient, ApiClient>();
```

Each registration creates a `ServiceRegistration` hyperedge with:
- `registration_method`: AddSingleton, AddScoped, AddTransient, etc.
- `service_type`: The interface being registered
- `implementation_type`: The concrete implementation
- `lifetime`: Singleton, Scoped, or Transient

### API Endpoint Extraction

For ASP.NET controllers, the extractor captures route information:

```csharp
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<ActionResult<User>> GetUser(int id) { ... }

    [HttpPost]
    public async Task<ActionResult<User>> CreateUser([FromBody] CreateUserDto dto) { ... }
}
```

Creates `ApiEndpoint` hyperedges with:
- `http_method`: GET, POST, PUT, DELETE, etc.
- `route`: Full resolved route (e.g., "api/users/{id}")
- `controller`: Controller class name
- `action`: Action method name
- Parameter bindings: `param_route_id`, `param_body_dto`, etc.

### Application Entry Point Detection

Identifies the application startup pattern:

**Traditional Program.cs:**
```csharp
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // ...
    }
}
```

**Minimal Hosting (Top-level statements):**
```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "Hello World");
app.Run();
```

Creates `EntryPoint` hyperedge with:
- `is_async`: Whether Main is async
- `uses_web_application`: Uses WebApplication.CreateBuilder
- `uses_host_builder`: Uses Host.CreateDefaultBuilder
- `uses_minimal_apis`: Uses app.MapGet/MapPost/etc.
- `is_top_level`: True for minimal hosting

### External API Calls (HttpClient)

Tracks outbound HTTP requests:

```csharp
var response = await _httpClient.GetAsync("https://api.example.com/users");
var data = await _httpClient.PostAsJsonAsync("/api/orders", order);
```

Creates `HttpClientUsage` hyperedge with:
- `http_method`: GET, POST, PUT, DELETE
- `url`: Target URL or endpoint
- `is_async`: True for async methods

### Database Access

Tracks database operations:

```csharp
var context = new AppDbContext(options);
using var connection = new SqlConnection(connectionString);
var users = await _dbContext.Users.ToListAsync();
```

Creates `DatabaseAccess` hyperedge with:
- `db_type`: DbContext class name or connection type
- `technology`: EntityFramework, SqlServer, PostgreSQL, etc.
- `connection_string_ref`: Reference to connection string

### Logging Patterns

Tracks ILogger usage for diagnostics:

```csharp
_logger.LogInformation("Processing order {OrderId} for user {UserId}", orderId, userId);
_logger.LogError(ex, "Failed to process order {OrderId}", orderId);
```

Creates `LoggingCall` hyperedge with:
- `log_level`: Trace, Debug, Information, Warning, Error, Critical
- `message_template`: The log message template
- `structured_fields`: Extracted placeholders (OrderId, UserId)
- `has_exception`: Whether logging an exception
- `scope`: Where the log call occurs

### File System Access

Tracks file operations:

```csharp
var content = File.ReadAllText("config.json");
File.WriteAllBytes(outputPath, data);
Directory.CreateDirectory(path);
```

Creates `FileSystemAccess` hyperedge with:
- `operation`: Read, Write, Create, Delete, Copy, Move, List
- `method`: Specific method called
- `path`: File or directory path

### Configuration Access

Tracks IConfiguration usage:

```csharp
var setting = _configuration["AppSettings:ApiKey"];
var section = _configuration.GetSection("Database");
var value = _configuration.GetValue<int>("MaxRetries");
```

Creates `ConfigurationAccess` hyperedge with:
- `method`: GetSection, GetValue, indexer access
- `config_key`: Configuration key accessed

### Middleware Pipeline

Tracks ASP.NET middleware registration:

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseExceptionHandler("/error");
app.UseCors("AllowAll");
```

Creates `MiddlewarePipeline` hyperedge with:
- `middleware_name`: Authentication, Authorization, etc.
- `method`: Use* method called

## Call Chain Tracking

The extractor tracks method invocations to build call graphs:

```csharp
// Source code
public void ProcessData()
{
    var data = repository.GetAll();     // MethodInvocation: repository.GetAll
    var filtered = Filter(data);         // MethodInvocation: Filter(data)
    Save(filtered);                      // MethodInvocation: Save(filtered)
}
```

Each invocation creates a hyperedge connecting:
1. The callee method
2. All argument expressions
3. Named parameters (if used)

## Inheritance Tracking

Class definitions capture the full inheritance hierarchy:

```csharp
public class Dog : Animal, IBarking, IWalking { }
```

Creates hyperedge with:
- `Dog` (role: `class`)
- `Animal` (role: `base_0`)
- `IBarking` (role: `base_1`)
- `IWalking` (role: `base_2`)

Interface definitions track interface inheritance:

```csharp
public interface IRepository<T> : IReadable, IWritable { }
```

Creates hyperedge with:
- `IRepository` (role: `interface`)
- `IReadable` (role: `extends_0`)
- `IWritable` (role: `extends_1`)

## Dependency Injection Detection

The extractor identifies common DI patterns:

**Constructor Injection**:
```csharp
public class UserService
{
    private readonly IUserRepository _repository;

    public UserService(IUserRepository repository) // Detected as DI
    {
        _repository = repository;
    }
}
```

Creates `DependencyInjection` hyperedge with:
- Injected interface type
- Constructor parameter
- Metadata: `injection_type = "constructor"`

**Field Injection**:
```csharp
private readonly ILogger _logger;  // Detected if readonly + interface
```

## Data Flow Analysis

Tracks variable definitions and uses across scopes:

```csharp
public void Process()
{
    var x = 10;          // Definition: x@Process:L3
    var y = x + 5;       // Use: x@Process:L4
    Console.WriteLine(x); // Use: x@Process:L5
}
```

Creates `DataFlow` hyperedge connecting:
- `x@Process:L3` (role: `def_0`)
- `x@Process:L4` (role: `use_0`)
- `x@Process:L5` (role: `use_1`)

## Project and Repository Structure

When used with `RepositoryProcessor` from Ast.Core, the extractor:
1. Processes all `.cs` files in the repository
2. Maintains scope hierarchy: `File > Namespace > Class > Method`
3. Links nodes across files via type references
4. Aggregates metadata at repository level

## Node Properties

Each extracted node contains:

| Property | Description |
|----------|-------------|
| `Id` | Unique hash-based identifier |
| `Name` | Symbol name |
| `NodeType` | Type (class, method, variable, etc.) |
| `Language` | Always "csharp" |
| `FilePath` | Source file path |
| `Scope` | Fully qualified scope |
| `LineStart` | Start line number |
| `LineEnd` | End line number |
| `Modifiers` | Access modifiers (public, private, async, etc.) |

## Usage Example

```csharp
var extractor = new CSharpHyperedgeExtractor("src/Services/UserService.cs");
var (nodes, hyperedges) = extractor.Extract(sourceCode);

// Or get a complete hypergraph
var hypergraph = extractor.ExtractToHypergraph(sourceCode);

// Access relationships
foreach (var edge in hypergraph.Hyperedges)
{
    Console.WriteLine($"{edge.EdgeType}: {string.Join(", ", edge.NodeIds)}");
}
```

## Supported C# Features

- File-scoped namespaces
- Record types (including primary constructors)
- Pattern matching
- LINQ query expressions
- Async/await
- Lambda expressions
- Generic types and methods
- Nested types
- XML documentation extraction
- **Top-level statements** (C# 9+ minimal hosting)
- **ASP.NET Core controllers and endpoints**
- **Dependency injection registrations**
- **HttpClient usage tracking**
- **Entity Framework DbContext access**
- **ILogger structured logging**
- **IConfiguration access patterns**
- **Middleware pipeline tracking**

## Dependencies

- `Microsoft.CodeAnalysis.CSharp` - Roslyn C# compiler APIs
- `Ast.Core` - Shared hypergraph models
