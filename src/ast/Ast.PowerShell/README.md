# Ast.PowerShell

PowerShell AST parser and hyperedge extractor using the PowerShell SDK for semantic analysis.

## Overview

This library parses PowerShell scripts into AST nodes and extracts hyperedges that capture code relationships with full context preservation. It handles PowerShell-specific constructs like cmdlet calls, pipelines, param blocks, and DSC configurations.

**Example**: A pipeline `Get-Service | Where-Object {$_.Status -eq 'Running'} | Stop-Service` is represented as a single hyperedge with:
- Nodes: `Get-Service`, `Where-Object`, `Stop-Service`
- Roles: `stage_0`, `stage_1`, `stage_2`

## Key Components

### PowerShellAstParser

Parses PowerShell code into `PsAstNode` objects representing:
- Functions with param blocks
- Classes and enums
- Cmdlet calls
- Variables and parameters

### PowerShellHyperedgeExtractor

Extracts hyperedges from PowerShell code, capturing:
- Function and class definitions
- Cmdlet calls with all parameters
- Pipeline chains
- Data flow and control flow

## Metadata Extracted

### Function Definitions

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `FunctionDefinition` | Function with param block | `function`, `param_0`, `param_type_0`, `param_0_attr`, `cmdlet_binding`, `output_type` |

**Metadata captured**:
- `is_filter` - Whether function is a filter
- `is_workflow` - Whether function is a workflow

### Class Definitions

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `ClassDefinition` | Class with base types | `class`, `base_0`, `base_1`, ... |
| `MethodDefinition` | Class method | `method`, `return_type`, `param_0`, ... |
| `PropertyDefinition` | Class property | `property`, `property_type` |

### Cmdlet Calls

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `CmdletCall` | Cmdlet with all parameters | `cmdlet`, `param_X`, `value_X`, `positional_0`, ... |

**Example**:
```powershell
Get-ChildItem -Path "C:\Logs" -Filter "*.log" -Recurse
```

Creates hyperedge with:
- `Get-ChildItem` (role: `cmdlet`)
- `Path` (role: `param_Path`)
- `"C:\Logs"` (role: `value_Path`)
- `Filter` (role: `param_Filter`)
- `"*.log"` (role: `value_Filter`)
- `Recurse` (role: `param_Recurse`)

### Pipelines

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `Pipeline` | Multi-stage pipeline chain | `stage_0`, `stage_1`, `stage_2`, ... |

**Metadata**: `pipeline_length` - Number of stages

**Example**:
```powershell
Get-Process | Where-Object {$_.CPU -gt 100} | Sort-Object CPU -Descending
```

Creates single hyperedge connecting all three stages.

### Control Flow

| Hyperedge Type | Description | Metadata |
|----------------|-------------|----------|
| `ControlFlow` | foreach/for/while loops | `loop_type` |
| `ExceptionHandling` | try-catch-finally | `try_block`, `catch_0`, `finally` |

### Data Flow

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `Assignment` | Variable assignment | `target`, `value` |
| `DataFlow` | Variable def-use chain | `def_0`, `use_0`, `use_1`, ... |

**Metadata**: `operator` - Assignment operator (=, +=, etc.)

### DSC (Desired State Configuration)

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `DscConfiguration` | DSC configuration block | `configuration` |
| `DscResource` | DSC resource declaration | `resource_type`, `instance_name` |

### Module Imports and Cross-Module Calls

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `ModuleImport` | Import-Module and using statements | `cmdlet`, `imported_module`, `command_prefix` |
| `ModuleFunctionCall` | Module-qualified function calls | `source_module`, `called_function`, params... |

**ModuleImport Metadata**:
- `module_name` - Name of imported module
- `alias` - Module alias if specified
- `version` - Required or minimum version
- `prefix` - Command prefix (e.g., `-Prefix AD` makes `Get-ADUser`)
- `is_global` - Whether imported globally
- `is_force` - Whether force-imported

**Example - Import-Module**:
```powershell
Import-Module ActiveDirectory -Prefix AD -RequiredVersion 1.0.0
```

Creates hyperedge with:
- `Import-Module` (role: `cmdlet`)
- `ActiveDirectory` (role: `imported_module`)
- `AD` (role: `command_prefix`)
- Metadata: `version = "1.0.0"`, `prefix = "AD"`

**Example - Module-qualified call**:
```powershell
Microsoft.PowerShell.Management\Get-ChildItem -Path C:\
```

Creates `ModuleFunctionCall` hyperedge with:
- `Microsoft.PowerShell.Management` (role: `source_module`)
- `Get-ChildItem` (role: `called_function`)
- `Path` (role: `param_Path`)
- `C:\` (role: `value_Path`)

### Remote Execution

| Hyperedge Type | Description | Roles |
|----------------|-------------|-------|
| `RemoteExecution` | Remote command execution | `remote_cmdlet`, `target_computer`, `session`, `credential`, `script_block`, `script_file` |

**Tracked Cmdlets**:
- `Invoke-Command` - Execute commands remotely
- `Enter-PSSession` - Interactive remote session
- `New-PSSession` / `Remove-PSSession` - Session management
- `Connect-PSSession` / `Disconnect-PSSession` - Session connection
- `Export-PSSession` / `Import-PSSession` - Session command import/export
- `Get-PSSession` / `Receive-PSSession` - Session information

**RemoteExecution Metadata**:
- `cmdlet` - The remote execution cmdlet used
- `computer_name` - Target computer(s)
- `session` - PSSession variable reference
- `credential` - Credential used for authentication
- `use_ssl` - Whether SSL is enabled
- `port` - Custom port number
- `authentication` - Authentication method (Kerberos, Negotiate, etc.)
- `configuration_name` - Session configuration
- `as_job` - Whether running as background job
- `has_script_block` - Whether inline script block is used
- `has_script_file` - Whether external script file is used

**Example - Invoke-Command**:
```powershell
Invoke-Command -ComputerName Server01, Server02 -Credential $cred -ScriptBlock {
    Get-Service | Where-Object Status -eq 'Running'
}
```

Creates `RemoteExecution` hyperedge with:
- `Invoke-Command` (role: `remote_cmdlet`)
- `Server01` (role: `target_computer_0`)
- `Server02` (role: `target_computer_1`)
- `$cred` (role: `credential`)
- `{ Get-Service | ... }` (role: `script_block`)

**Example - PSSession workflow**:
```powershell
$session = New-PSSession -ComputerName DC01 -Credential (Get-Credential) -UseSSL
Invoke-Command -Session $session -FilePath .\Deploy.ps1
Remove-PSSession -Session $session
```

Each command creates its own `RemoteExecution` hyperedge, enabling tracking of:
- Session creation with security context
- Commands executed within sessions
- Session lifecycle management

## Module Dependency Tracking

The extractor tracks module dependencies to enable cross-module call resolution:

### Module Loading Tracking

```powershell
# These are tracked and stored for cross-reference
Import-Module ActiveDirectory
Import-Module Az.Storage -RequiredVersion 5.0.0
using module PSReadLine
```

The extractor maintains:
- List of loaded modules
- Module aliases (for `Import-Module -Alias`)
- Module prefixes (for `Import-Module -Prefix`)

### Cross-Module Function Calls

When functions are called with module qualification, the extractor links them to the source module:

```powershell
# Module-qualified call - always unambiguous
Microsoft.PowerShell.Management\Get-Process
ActiveDirectory\Get-ADUser -Filter *

# After Import-Module with prefix
Import-Module ActiveDirectory -Prefix Corp
Get-CorpADUser -Filter *  # Tracked as ActiveDirectory function
```

This enables:
- **Dependency analysis**: Which modules does this script require?
- **Function origin**: Where does this function come from?
- **Conflict resolution**: When multiple modules export same function name

## Call Chain Tracking

The extractor tracks cmdlet calls to build execution graphs:

```powershell
function Process-Data {
    $data = Get-Content -Path $InputFile    # CmdletCall: Get-Content
    $filtered = $data | Where-Object {...}  # Pipeline: data | Where-Object
    $filtered | Out-File -FilePath $Output  # Pipeline: filtered | Out-File
}
```

Each cmdlet call creates a hyperedge containing:
1. The cmdlet name
2. All named parameters
3. All positional arguments
4. Parameter values

## Inheritance Tracking

PowerShell 5+ class definitions capture inheritance:

```powershell
class Dog : Animal, IBarking {
    [void] Bark() { }
}
```

Creates hyperedge with:
- `Dog` (role: `class`)
- `Animal` (role: `base_0`)
- `IBarking` (role: `base_1`)

## Function Parameter Analysis

Advanced functions with param blocks are fully analyzed:

```powershell
function Get-UserData {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Username,

        [int]$MaxResults = 100
    )
}
```

Creates `FunctionDefinition` hyperedge with:
- `Get-UserData` (role: `function`)
- `Username` (role: `param_0`)
- `string` (role: `param_type_0`)
- `Parameter` (role: `param_0_attr`)
- `MaxResults` (role: `param_1`)
- `int` (role: `param_type_1`)
- `CmdletBinding` (role: `cmdlet_binding`)
- `PSCustomObject` (role: `output_type`)

## Data Flow Analysis

Tracks variable definitions and uses:

```powershell
$config = Get-Content config.json    # Definition: config@:L1
$settings = $config | ConvertFrom-Json  # Use: config@:L2
Write-Host $config                   # Use: config@:L3
```

Creates `DataFlow` hyperedge:
- `$config@:L1` (role: `def_0`)
- `$config@:L2` (role: `use_0`)
- `$config@:L3` (role: `use_1`)

## Project and Repository Structure

When used with `RepositoryProcessor` from Ast.Core, the extractor:
1. Processes all `.ps1`, `.psm1`, `.psd1` files
2. Maintains scope hierarchy: `File > Function > Script Block`
3. Links nodes across files via module imports
4. Detects cross-language references (PowerShell calling .NET types)

## Node Properties

Each extracted node contains:

| Property | Description |
|----------|-------------|
| `Id` | Unique hash-based identifier |
| `Name` | Symbol name |
| `NodeType` | Type (function, cmdlet, variable, etc.) |
| `Language` | Always "powershell" |
| `FilePath` | Source file path |
| `Scope` | Fully qualified scope |
| `LineStart` | Start line number |
| `LineEnd` | End line number |

## Node Types

| Type | Description |
|------|-------------|
| `function` | Function definition |
| `parameter` | Function/cmdlet parameter |
| `type` | Type reference |
| `attribute` | Parameter/function attribute |
| `class` | Class definition |
| `enum` | Enum definition |
| `method` | Class method |
| `property` | Class property |
| `cmdlet` | Cmdlet call |
| `param_name` | Named parameter |
| `param_value` | Parameter value |
| `pipeline_stage` | Pipeline stage |
| `variable` | Variable reference |
| `loop_variable` | Loop iterator variable |
| `condition` | Loop/if condition |
| `collection` | Iterated collection |
| `try_block` | Try block |
| `exception_handler` | Catch handler |
| `finally_block` | Finally block |
| `dsc_configuration` | DSC configuration |
| `dsc_resource_type` | DSC resource type |
| `dsc_resource_instance` | DSC resource instance |
| `module` | Imported module |
| `prefix` | Command prefix |
| `remote_cmdlet` | Remote execution cmdlet |
| `remote_computer` | Target computer for remote execution |
| `ps_session` | PowerShell session reference |
| `credential` | Credential reference |
| `remote_script` | Script block for remote execution |
| `script_file` | Script file path for remote execution |
| `function_ref` | Reference to a function (in module-qualified call) |

## Usage Example

```powershell
# PowerShell script to analyze
$script = @'
function Get-ProcessInfo {
    [CmdletBinding()]
    param([string]$Name)

    Get-Process -Name $Name | Select-Object Name, CPU, Memory
}
'@
```

```csharp
var extractor = new PowerShellHyperedgeExtractor("Scripts/Get-ProcessInfo.ps1");
var (nodes, hyperedges) = extractor.Extract(script);

// Or get a complete hypergraph
var hypergraph = extractor.ExtractToHypergraph(script);

// Find all cmdlet calls
var cmdletCalls = hypergraph.Hyperedges
    .Where(e => e.EdgeType == HyperedgeType.CmdletCall);

// Find all pipelines
var pipelines = hypergraph.Hyperedges
    .Where(e => e.EdgeType == HyperedgeType.Pipeline);
```

## Supported PowerShell Features

- Functions with param blocks
- Advanced functions (CmdletBinding, OutputType)
- Parameter attributes (Mandatory, ValidateSet, etc.)
- Pipeline expressions
- Classes and enums (PowerShell 5+)
- DSC configurations and resources
- Using statements (modules, namespaces)
- Control flow (if, foreach, for, while, try-catch)
- Script blocks (Begin, Process, End)
- Filters and workflows
- **Module dependencies** (Import-Module tracking)
- **Module-qualified calls** (ModuleName\FunctionName)
- **Remote execution** (Invoke-Command, PSSession management)
- **Credential tracking** for security analysis

## Dependencies

- `Microsoft.PowerShell.SDK` - PowerShell SDK for AST parsing
- `Ast.Core` - Shared hypergraph models
