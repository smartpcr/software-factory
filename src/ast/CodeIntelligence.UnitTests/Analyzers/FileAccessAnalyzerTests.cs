// -----------------------------------------------------------------------
// <copyright file="FileAccessAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class FileAccessAnalyzerTests
{
    private FileAccessAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new FileAccessAnalyzer();
    }

    #region File Read Tests

    [TestMethod]
    public void Analyze_FileReadAllText_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class ConfigService
{
    public string LoadConfig()
    {
        return File.ReadAllText(""config.json"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ConfigService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Read);
        accesses[0].OperationType.Should().Be(FileOperationType.FileStatic);
        accesses[0].MethodName.Should().Be("ReadAllText");
        accesses[0].FilePath.Should().Be("config.json");
    }

    [TestMethod]
    public void Analyze_FileReadAllTextAsync_DetectsAsyncRead()
    {
        // Arrange
        var code = @"
public class FileService
{
    public async Task<string> LoadFileAsync(string path)
    {
        return await File.ReadAllTextAsync(path);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "FileService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].IsAsync.Should().BeTrue();
        accesses[0].FilePath.Should().Be("path");
    }

    [TestMethod]
    public void Analyze_FileReadAllBytes_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class ImageService
{
    public byte[] LoadImage(string imagePath)
    {
        return File.ReadAllBytes(imagePath);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ImageService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Read);
    }

    [TestMethod]
    public void Analyze_StreamReaderReadToEnd_DetectsRead()
    {
        // Arrange
        var code = @"
public class TextReader
{
    public string ReadFile(StreamReader reader)
    {
        return reader.ReadToEnd();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "TextReader.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Read);
        accesses[0].MethodName.Should().Be("ReadToEnd");
    }

    #endregion

    #region File Write Tests

    [TestMethod]
    public void Analyze_FileWriteAllText_DetectsWriteOperation()
    {
        // Arrange
        var code = @"
public class ConfigService
{
    public void SaveConfig(string config)
    {
        File.WriteAllText(""config.json"", config);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ConfigService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Write);
        accesses[0].MethodName.Should().Be("WriteAllText");
    }

    [TestMethod]
    public void Analyze_FileWriteAllBytesAsync_DetectsAsyncWrite()
    {
        // Arrange
        var code = @"
public class ImageService
{
    public async Task SaveImageAsync(string path, byte[] data)
    {
        await File.WriteAllBytesAsync(path, data);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ImageService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Write);
        accesses[0].IsAsync.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_StreamWriterWriteLine_DetectsWrite()
    {
        // Arrange
        var code = @"
public class LogWriter
{
    public void WriteLog(StreamWriter writer, string message)
    {
        writer.WriteLine(message);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "LogWriter.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Write);
    }

    [TestMethod]
    public void Analyze_FileAppendAllText_DetectsWrite()
    {
        // Arrange
        var code = @"
public class LogService
{
    public void AppendLog(string message)
    {
        File.AppendAllText(""log.txt"", message);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "LogService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Write);
    }

    #endregion

    #region Stream Creation Tests

    [TestMethod]
    public void Analyze_NewStreamReader_DetectsRead()
    {
        // Arrange
        var code = @"
public class FileParser
{
    public void ParseFile(string path)
    {
        using var reader = new StreamReader(path);
        // Process file
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "FileParser.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.Read);
        accesses[0].OperationType.Should().Be(FileOperationType.StreamReader);
        accesses[0].FilePath.Should().Be("path");
    }

    [TestMethod]
    public void Analyze_NewStreamWriter_DetectsWrite()
    {
        // Arrange
        var code = @"
public class ReportGenerator
{
    public void GenerateReport(string outputPath)
    {
        using var writer = new StreamWriter(outputPath);
        writer.WriteLine(""Report"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ReportGenerator.cs");

        // Assert
        accesses.Should().Contain(a => a.OperationType == FileOperationType.StreamWriter && 
                                        a.AccessType == FileAccessType.Write);
    }

    [TestMethod]
    public void Analyze_NewFileStream_DetectsAccess()
    {
        // Arrange
        var code = @"
public class BinaryProcessor
{
    public void ProcessFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open);
        // Process
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "BinaryProcessor.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].OperationType.Should().Be(FileOperationType.FileStream);
    }

    #endregion

    #region Directory Tests

    [TestMethod]
    public void Analyze_DirectoryGetFiles_DetectsDirectoryRead()
    {
        // Arrange
        var code = @"
public class FileScanner
{
    public string[] GetAllFiles(string directory)
    {
        return Directory.GetFiles(directory, ""*.txt"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "FileScanner.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.DirectoryRead);
        accesses[0].OperationType.Should().Be(FileOperationType.DirectoryStatic);
    }

    [TestMethod]
    public void Analyze_DirectoryCreateDirectory_DetectsDirectoryWrite()
    {
        // Arrange
        var code = @"
public class FolderService
{
    public void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "FolderService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].AccessType.Should().Be(FileAccessType.DirectoryWrite);
    }

    #endregion

    #region Blob Storage Tests

    [TestMethod]
    public void AnalyzeBlobStorage_DownloadAsync_DetectsRead()
    {
        // Arrange
        var code = @"
public class BlobService
{
    private readonly BlobClient _blobClient;

    public async Task<BlobDownloadInfo> DownloadAsync()
    {
        return await _blobClient.DownloadAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.AnalyzeBlobStorage(tree, "BlobService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].OperationType.Should().Be(BlobOperationType.Read);
        accesses[0].MethodName.Should().Be("DownloadAsync");
    }

    [TestMethod]
    public void AnalyzeBlobStorage_UploadAsync_DetectsWrite()
    {
        // Arrange
        var code = @"
public class BlobUploader
{
    private readonly BlobClient _blobClient;

    public async Task UploadFileAsync(Stream content)
    {
        await _blobClient.UploadAsync(content);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.AnalyzeBlobStorage(tree, "BlobUploader.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].OperationType.Should().Be(BlobOperationType.Write);
    }

    [TestMethod]
    public void AnalyzeBlobStorage_DeleteAsync_DetectsDelete()
    {
        // Arrange
        var code = @"
public class BlobManager
{
    private readonly BlobClient _blobClient;

    public async Task DeleteBlobAsync()
    {
        await _blobClient.DeleteAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.AnalyzeBlobStorage(tree, "BlobManager.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].OperationType.Should().Be(BlobOperationType.Delete);
    }

    [TestMethod]
    public void AnalyzeBlobStorage_GetBlobClient_ExtractsBlobName()
    {
        // Arrange
        var code = @"
public class ContainerService
{
    private readonly BlobContainerClient _containerClient;

    public BlobClient GetBlob(string name)
    {
        return _containerClient.GetBlobClient(""images/photo.jpg"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.AnalyzeBlobStorage(tree, "ContainerService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].BlobName.Should().Be("images/photo.jpg");
    }

    [TestMethod]
    public void AnalyzeBlobStorage_ExistsAsync_DetectsRead()
    {
        // Arrange
        var code = @"
public class BlobChecker
{
    private readonly BlobClient _blob;

    public async Task<bool> BlobExistsAsync()
    {
        var response = await _blob.ExistsAsync();
        return response.Value;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.AnalyzeBlobStorage(tree, "BlobChecker.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].OperationType.Should().Be(BlobOperationType.Read);
    }

    #endregion

    #region Multiple Operations Tests

    [TestMethod]
    public void Analyze_MultipleFileOperations_DetectsAll()
    {
        // Arrange
        var code = @"
public class FileCopier
{
    public void CopyFile(string source, string destination)
    {
        var content = File.ReadAllBytes(source);
        File.WriteAllBytes(destination, content);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "FileCopier.cs");

        // Assert
        accesses.Should().HaveCount(2);
        accesses.Should().Contain(a => a.AccessType == FileAccessType.Read);
        accesses.Should().Contain(a => a.AccessType == FileAccessType.Write);
    }

    #endregion
}
