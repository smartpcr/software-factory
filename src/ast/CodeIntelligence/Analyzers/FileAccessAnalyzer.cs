// -----------------------------------------------------------------------
// <copyright file="FileAccessAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect file I/O and blob storage access patterns.
/// Uses pure AST-based detection.
/// </summary>
public sealed class FileAccessAnalyzer
{
    /// <summary>
    /// File read methods.
    /// </summary>
    private static readonly HashSet<string> FileReadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // File class
        "ReadAllText",
        "ReadAllTextAsync",
        "ReadAllBytes",
        "ReadAllBytesAsync",
        "ReadAllLines",
        "ReadAllLinesAsync",
        "ReadLines",
        "OpenRead",
        "OpenText",
        // FileStream
        "Read",
        "ReadAsync",
        "ReadByte",
        // StreamReader
        "ReadToEnd",
        "ReadToEndAsync",
        "ReadLine",
        "ReadLineAsync",
        "ReadBlock",
        "ReadBlockAsync",
        // BinaryReader
        "ReadBytes",
        "ReadString",
        "ReadInt32",
        "ReadInt64",
        "ReadDouble",
        "ReadBoolean",
    };

    /// <summary>
    /// File write methods.
    /// </summary>
    private static readonly HashSet<string> FileWriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // File class
        "WriteAllText",
        "WriteAllTextAsync",
        "WriteAllBytes",
        "WriteAllBytesAsync",
        "WriteAllLines",
        "WriteAllLinesAsync",
        "AppendAllText",
        "AppendAllTextAsync",
        "AppendAllLines",
        "AppendAllLinesAsync",
        "OpenWrite",
        "Create",
        "CreateText",
        // FileStream
        "Write",
        "WriteAsync",
        "WriteByte",
        // StreamWriter
        "WriteLine",
        "WriteLineAsync",
        // BinaryWriter
        "Write",
    };

    /// <summary>
    /// Directory operations.
    /// </summary>
    private static readonly HashSet<string> DirectoryMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetFiles",
        "GetDirectories",
        "EnumerateFiles",
        "EnumerateDirectories",
        "CreateDirectory",
        "Delete",
        "Move",
        "Exists",
        "GetCurrentDirectory",
        "SetCurrentDirectory",
    };

    /// <summary>
    /// Blob storage methods.
    /// </summary>
    private static readonly HashSet<string> BlobReadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "DownloadAsync",
        "DownloadContentAsync",
        "DownloadToAsync",
        "DownloadStreamingAsync",
        "OpenReadAsync",
        "GetBlobClient",
        "GetBlobs",
        "GetBlobsAsync",
        "GetProperties",
        "GetPropertiesAsync",
        "Exists",
        "ExistsAsync",
    };

    /// <summary>
    /// Blob write methods.
    /// </summary>
    private static readonly HashSet<string> BlobWriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "UploadAsync",
        "Upload",
        "OpenWriteAsync",
        "SetMetadata",
        "SetMetadataAsync",
        "CreateIfNotExists",
        "CreateIfNotExistsAsync",
    };

    /// <summary>
    /// Blob delete methods.
    /// </summary>
    private static readonly HashSet<string> BlobDeleteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeleteAsync",
        "Delete",
        "DeleteIfExists",
        "DeleteIfExistsAsync",
        "DeleteBlob",
        "DeleteBlobAsync",
    };

    /// <summary>
    /// Analyzes a syntax tree for file I/O operations.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected file access operations.</returns>
    public IReadOnlyList<FileAccessInfo> Analyze(SyntaxTree tree, string filePath)
    {
        var accesses = new List<FileAccessInfo>();
        var root = tree.GetRoot();

        // Analyze invocations
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var accessInfo = this.AnalyzeInvocation(invocation, filePath);
            if (accessInfo != null)
            {
                accesses.Add(accessInfo);
            }
        }

        // Analyze object creations (new FileStream, new StreamReader, etc.)
        var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        foreach (var creation in objectCreations)
        {
            var accessInfo = this.AnalyzeObjectCreation(creation, filePath);
            if (accessInfo != null)
            {
                accesses.Add(accessInfo);
            }
        }

        return accesses;
    }

    /// <summary>
    /// Analyzes a syntax tree for blob storage operations.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected blob storage operations.</returns>
    public IReadOnlyList<BlobStorageAccessInfo> AnalyzeBlobStorage(SyntaxTree tree, string filePath)
    {
        var accesses = new List<BlobStorageAccessInfo>();
        var root = tree.GetRoot();

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var accessInfo = this.AnalyzeBlobInvocation(invocation, filePath);
            if (accessInfo != null)
            {
                accesses.Add(accessInfo);
            }
        }

        return accesses;
    }

    private FileAccessInfo? AnalyzeInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        string? methodName = null;
        string? receiver = null;
        FileAccessType? accessType = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.Text;
            receiver = memberAccess.Expression.ToString();
        }
        else if (invocation.Expression is IdentifierNameSyntax identifier)
        {
            methodName = identifier.Identifier.Text;
        }

        if (methodName == null)
        {
            return null;
        }

        // Determine access type
        if (FileReadMethods.Contains(methodName))
        {
            accessType = FileAccessType.Read;
        }
        else if (FileWriteMethods.Contains(methodName))
        {
            accessType = FileAccessType.Write;
        }
        else if (DirectoryMethods.Contains(methodName))
        {
            accessType = this.IsDirectoryModification(methodName) 
                ? FileAccessType.DirectoryWrite 
                : FileAccessType.DirectoryRead;
        }

        if (accessType == null)
        {
            return null;
        }

        // Verify it's a file operation
        if (!this.IsFileOperation(receiver, methodName))
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new FileAccessInfo
        {
            AccessType = accessType.Value,
            OperationType = this.DetermineFileOperationType(receiver),
            MethodName = methodName,
            ReceiverExpression = receiver,
            FilePath = this.ExtractFilePath(invocation),
            IsAsync = methodName.EndsWith("Async"),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            SourceFilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = this.CalculateConfidence(receiver, methodName),
        };
    }

    private FileAccessInfo? AnalyzeObjectCreation(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();

        FileOperationType? operationType = null;
        FileAccessType? accessType = null;

        if (typeName == "FileStream" || typeName.EndsWith(".FileStream"))
        {
            operationType = FileOperationType.FileStream;
            accessType = this.DetermineFileStreamAccess(creation);
        }
        else if (typeName == "StreamReader" || typeName.EndsWith(".StreamReader"))
        {
            operationType = FileOperationType.StreamReader;
            accessType = FileAccessType.Read;
        }
        else if (typeName == "StreamWriter" || typeName.EndsWith(".StreamWriter"))
        {
            operationType = FileOperationType.StreamWriter;
            accessType = FileAccessType.Write;
        }
        else if (typeName == "BinaryReader" || typeName.EndsWith(".BinaryReader"))
        {
            operationType = FileOperationType.BinaryReader;
            accessType = FileAccessType.Read;
        }
        else if (typeName == "BinaryWriter" || typeName.EndsWith(".BinaryWriter"))
        {
            operationType = FileOperationType.BinaryWriter;
            accessType = FileAccessType.Write;
        }
        else if (typeName == "FileInfo" || typeName.EndsWith(".FileInfo"))
        {
            operationType = FileOperationType.FileInfo;
            accessType = FileAccessType.Read; // FileInfo itself is just reading metadata
        }

        if (operationType == null || accessType == null)
        {
            return null;
        }

        var containingClass = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new FileAccessInfo
        {
            AccessType = accessType.Value,
            OperationType = operationType.Value,
            MethodName = $"new {typeName}",
            ReceiverExpression = null,
            FilePath = this.ExtractFilePathFromCreation(creation),
            IsAsync = false,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            SourceFilePath = filePath,
            LineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private BlobStorageAccessInfo? AnalyzeBlobInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();

        // Determine blob operation type
        BlobOperationType? operationType = null;
        if (BlobReadMethods.Contains(methodName))
        {
            operationType = BlobOperationType.Read;
        }
        else if (BlobWriteMethods.Contains(methodName))
        {
            operationType = BlobOperationType.Write;
        }
        else if (BlobDeleteMethods.Contains(methodName))
        {
            operationType = BlobOperationType.Delete;
        }

        if (operationType == null)
        {
            return null;
        }

        // Verify it's a blob operation
        if (!this.IsBlobOperation(receiver))
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new BlobStorageAccessInfo
        {
            OperationType = operationType.Value,
            ClientType = this.DetermineBlobClientType(receiver),
            MethodName = methodName,
            ReceiverExpression = receiver,
            ContainerName = this.ExtractContainerName(invocation),
            BlobName = this.ExtractBlobName(invocation),
            IsAsync = methodName.EndsWith("Async"),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.85f,
        };
    }

    private bool IsFileOperation(string? receiver, string methodName)
    {
        if (receiver == null)
        {
            return false;
        }

        // Static File class methods
        if (receiver == "File" || receiver.EndsWith(".File"))
        {
            return true;
        }

        // Static Directory class methods
        if (receiver == "Directory" || receiver.EndsWith(".Directory"))
        {
            return true;
        }

        // Instance methods on streams
        if (receiver.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("reader", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("writer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private bool IsBlobOperation(string receiver)
    {
        return receiver.Contains("BlobClient", StringComparison.OrdinalIgnoreCase) ||
               receiver.Contains("BlobContainer", StringComparison.OrdinalIgnoreCase) ||
               receiver.Contains("_blobClient", StringComparison.OrdinalIgnoreCase) ||
               receiver.Contains("_containerClient", StringComparison.OrdinalIgnoreCase) ||
               receiver.Contains("blob", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsDirectoryModification(string methodName)
    {
        return methodName == "CreateDirectory" || methodName == "Delete" || methodName == "Move";
    }

    private FileOperationType DetermineFileOperationType(string? receiver)
    {
        if (receiver == null)
        {
            return FileOperationType.Unknown;
        }

        if (receiver == "File" || receiver.EndsWith(".File"))
        {
            return FileOperationType.FileStatic;
        }

        if (receiver == "Directory" || receiver.EndsWith(".Directory"))
        {
            return FileOperationType.DirectoryStatic;
        }

        if (receiver.Contains("StreamReader", StringComparison.OrdinalIgnoreCase))
        {
            return FileOperationType.StreamReader;
        }

        if (receiver.Contains("StreamWriter", StringComparison.OrdinalIgnoreCase))
        {
            return FileOperationType.StreamWriter;
        }

        if (receiver.Contains("FileStream", StringComparison.OrdinalIgnoreCase))
        {
            return FileOperationType.FileStream;
        }

        return FileOperationType.Unknown;
    }

    private FileAccessType? DetermineFileStreamAccess(ObjectCreationExpressionSyntax creation)
    {
        var args = creation.ArgumentList?.Arguments;
        if (args == null || args.Value.Count < 2)
        {
            return FileAccessType.Read; // Default
        }

        var modeArg = args.Value.Skip(1).FirstOrDefault()?.Expression.ToString();
        if (modeArg == null)
        {
            return FileAccessType.Read;
        }

        if (modeArg.Contains("Create") || modeArg.Contains("Write") || 
            modeArg.Contains("Append") || modeArg.Contains("Truncate"))
        {
            return FileAccessType.Write;
        }

        return FileAccessType.Read;
    }

    private BlobClientType DetermineBlobClientType(string receiver)
    {
        if (receiver.Contains("BlockBlob", StringComparison.OrdinalIgnoreCase))
        {
            return BlobClientType.BlockBlob;
        }

        if (receiver.Contains("AppendBlob", StringComparison.OrdinalIgnoreCase))
        {
            return BlobClientType.AppendBlob;
        }

        if (receiver.Contains("PageBlob", StringComparison.OrdinalIgnoreCase))
        {
            return BlobClientType.PageBlob;
        }

        if (receiver.Contains("Container", StringComparison.OrdinalIgnoreCase))
        {
            return BlobClientType.Container;
        }

        return BlobClientType.BlobClient;
    }

    private string? ExtractFilePath(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return null;
        }

        var firstArg = args[0].Expression;

        if (firstArg is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }

        return firstArg.ToString();
    }

    private string? ExtractFilePathFromCreation(ObjectCreationExpressionSyntax creation)
    {
        var args = creation.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
        {
            return null;
        }

        var firstArg = args.Value[0].Expression;

        if (firstArg is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }

        return firstArg.ToString();
    }

    private string? ExtractContainerName(InvocationExpressionSyntax invocation)
    {
        // Try to find container name in the call chain or arguments
        var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression.ToString();
        if (receiver?.Contains("Container") == true)
        {
            // Container client - name might be in construction
            return null; // Would need more context
        }

        return null;
    }

    private string? ExtractBlobName(InvocationExpressionSyntax invocation)
    {
        var methodName = (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
        
        // For GetBlobClient, the blob name is the first argument
        if (methodName == "GetBlobClient")
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count > 0)
            {
                var firstArg = args[0].Expression;
                if (firstArg is LiteralExpressionSyntax literal)
                {
                    return literal.Token.ValueText;
                }

                return firstArg.ToString();
            }
        }

        return null;
    }

    private float CalculateConfidence(string? receiver, string methodName)
    {
        if (receiver == "File" || receiver == "Directory")
        {
            return 0.95f;
        }

        if (receiver?.Contains("Stream") == true)
        {
            return 0.90f;
        }

        return 0.85f;
    }
}

/// <summary>
/// Information about a detected file access operation.
/// </summary>
public record FileAccessInfo
{
    /// <summary>Gets the access type (Read/Write).</summary>
    public required FileAccessType AccessType { get; init; }

    /// <summary>Gets the operation type.</summary>
    public required FileOperationType OperationType { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the receiver expression.</summary>
    public string? ReceiverExpression { get; init; }

    /// <summary>Gets the file path being accessed.</summary>
    public string? FilePath { get; init; }

    /// <summary>Gets whether the operation is async.</summary>
    public required bool IsAsync { get; init; }

    /// <summary>Gets the containing class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the containing method name.</summary>
    public string? MethodContext { get; init; }

    /// <summary>Gets the source file path.</summary>
    public required string SourceFilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the detection confidence.</summary>
    public required float Confidence { get; init; }
}

/// <summary>
/// Information about a detected blob storage operation.
/// </summary>
public record BlobStorageAccessInfo
{
    /// <summary>Gets the operation type.</summary>
    public required BlobOperationType OperationType { get; init; }

    /// <summary>Gets the blob client type.</summary>
    public required BlobClientType ClientType { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the receiver expression.</summary>
    public required string ReceiverExpression { get; init; }

    /// <summary>Gets the container name if detected.</summary>
    public string? ContainerName { get; init; }

    /// <summary>Gets the blob name if detected.</summary>
    public string? BlobName { get; init; }

    /// <summary>Gets whether the operation is async.</summary>
    public required bool IsAsync { get; init; }

    /// <summary>Gets the containing class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the containing method name.</summary>
    public string? MethodContext { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the detection confidence.</summary>
    public required float Confidence { get; init; }
}

/// <summary>
/// Types of file access.
/// </summary>
public enum FileAccessType
{
    /// <summary>Read operation.</summary>
    Read,

    /// <summary>Write operation.</summary>
    Write,

    /// <summary>Directory read operation.</summary>
    DirectoryRead,

    /// <summary>Directory write operation.</summary>
    DirectoryWrite,
}

/// <summary>
/// Types of file operations.
/// </summary>
public enum FileOperationType
{
    /// <summary>Static File class methods.</summary>
    FileStatic,

    /// <summary>Static Directory class methods.</summary>
    DirectoryStatic,

    /// <summary>FileStream operations.</summary>
    FileStream,

    /// <summary>StreamReader operations.</summary>
    StreamReader,

    /// <summary>StreamWriter operations.</summary>
    StreamWriter,

    /// <summary>BinaryReader operations.</summary>
    BinaryReader,

    /// <summary>BinaryWriter operations.</summary>
    BinaryWriter,

    /// <summary>FileInfo operations.</summary>
    FileInfo,

    /// <summary>Unknown operation type.</summary>
    Unknown,
}

/// <summary>
/// Types of blob operations.
/// </summary>
public enum BlobOperationType
{
    /// <summary>Read/download operation.</summary>
    Read,

    /// <summary>Write/upload operation.</summary>
    Write,

    /// <summary>Delete operation.</summary>
    Delete,

    /// <summary>Metadata operation.</summary>
    Metadata,
}

/// <summary>
/// Types of blob clients.
/// </summary>
public enum BlobClientType
{
    /// <summary>Generic BlobClient.</summary>
    BlobClient,

    /// <summary>Block blob client.</summary>
    BlockBlob,

    /// <summary>Append blob client.</summary>
    AppendBlob,

    /// <summary>Page blob client.</summary>
    PageBlob,

    /// <summary>Container client.</summary>
    Container,
}
