// -----------------------------------------------------------------------
// <copyright file="SecurityAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class SecurityAnalyzerTests
{
    private SecurityAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new SecurityAnalyzer();
    }

    #region Authorization Tests

    [TestMethod]
    public void AnalyzeAuthorization_AuthorizeAttribute_DetectsAuthorization()
    {
        // Arrange
        var code = @"
[Authorize]
public class AdminController : Controller
{
    public IActionResult Index() => View();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "AdminController.cs");

        // Assert
        authorizations.Should().ContainSingle();
        authorizations[0].AuthorizationType.Should().Be(AuthorizationType.Authorize);
        authorizations[0].Scope.Should().Be(AuthorizationScope.Class);
        authorizations[0].ClassName.Should().Be("AdminController");
    }

    [TestMethod]
    public void AnalyzeAuthorization_AuthorizeWithRoles_ExtractsRoles()
    {
        // Arrange
        var code = @"
public class UserController : Controller
{
    [Authorize(Roles = ""Admin,Manager"")]
    public IActionResult Delete(int id) => View();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "UserController.cs");

        // Assert
        authorizations.Should().ContainSingle();
        authorizations[0].Roles.Should().Contain("Admin");
        authorizations[0].Roles.Should().Contain("Manager");
        authorizations[0].Scope.Should().Be(AuthorizationScope.Method);
    }

    [TestMethod]
    public void AnalyzeAuthorization_AuthorizeWithPolicy_ExtractsPolicy()
    {
        // Arrange
        var code = @"
public class ReportController : Controller
{
    [Authorize(Policy = ""RequireAdminRole"")]
    public IActionResult GenerateReport() => View();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "ReportController.cs");

        // Assert
        authorizations.Should().ContainSingle();
        authorizations[0].Policy.Should().Be("RequireAdminRole");
    }

    [TestMethod]
    public void AnalyzeAuthorization_AllowAnonymous_DetectsAnonymous()
    {
        // Arrange
        var code = @"
[Authorize]
public class AccountController : Controller
{
    [AllowAnonymous]
    public IActionResult Login() => View();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "AccountController.cs");

        // Assert
        authorizations.Should().HaveCount(2);
        authorizations.Should().Contain(a => a.AuthorizationType == AuthorizationType.Authorize);
        authorizations.Should().Contain(a => a.AuthorizationType == AuthorizationType.AllowAnonymous);
    }

    [TestMethod]
    public void AnalyzeAuthorization_AuthenticationSchemes_ExtractsSchemes()
    {
        // Arrange
        var code = @"
public class ApiController : Controller
{
    [Authorize(AuthenticationSchemes = ""Bearer"")]
    public IActionResult GetData() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "ApiController.cs");

        // Assert
        authorizations.Should().ContainSingle();
        authorizations[0].AuthenticationSchemes.Should().Be("Bearer");
    }

    [TestMethod]
    public void AnalyzeAuthorization_MinimalApiRequireAuthorization_Detects()
    {
        // Arrange
        var code = @"
public class Program
{
    public static void Main()
    {
        app.MapGet(""/admin"", () => ""Admin"").RequireAuthorization();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "Program.cs");

        // Assert
        authorizations.Should().ContainSingle();
        authorizations[0].Scope.Should().Be(AuthorizationScope.Endpoint);
    }

    [TestMethod]
    public void AnalyzeAuthorization_MinimalApiRequireAuthorizationWithPolicy_ExtractsPolicy()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure()
    {
        app.MapPost(""/sensitive"", Handler).RequireAuthorization(""AdminOnly"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "Startup.cs");

        // Assert
        authorizations.Should().ContainSingle();
        authorizations[0].Policy.Should().Be("AdminOnly");
    }

    [TestMethod]
    public void AnalyzeAuthorization_MultipleMethodsWithDifferentAuth_DetectsAll()
    {
        // Arrange
        var code = @"
public class ProductController : Controller
{
    [Authorize(Roles = ""Admin"")]
    public IActionResult Create() => View();

    [Authorize(Roles = ""User"")]
    public IActionResult View() => View();

    [AllowAnonymous]
    public IActionResult List() => View();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var authorizations = this.analyzer.AnalyzeAuthorization(tree, "ProductController.cs");

        // Assert
        authorizations.Should().HaveCount(3);
    }

    #endregion

    #region Secret Access Tests

    [TestMethod]
    public void AnalyzeSecrets_KeyVaultGetSecret_DetectsAccess()
    {
        // Arrange
        var code = @"
public class SecretService
{
    private readonly SecretClient _secretClient;

    public async Task<string> GetApiKeyAsync()
    {
        var secret = await _secretClient.GetSecretAsync(""api-key"");
        return secret.Value.Value;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var secrets = this.analyzer.AnalyzeSecrets(tree, "SecretService.cs");

        // Assert
        secrets.Should().ContainSingle();
        secrets[0].SecretType.Should().Be(SecretType.AzureKeyVault);
        secrets[0].AccessType.Should().Be(SecretAccessType.Read);
        secrets[0].SecretName.Should().Be("api-key");
    }

    [TestMethod]
    public void AnalyzeSecrets_EnvironmentVariable_DetectsAccess()
    {
        // Arrange
        var code = @"
public class ConfigService
{
    public string GetConnectionString()
    {
        return Environment.GetEnvironmentVariable(""DATABASE_URL"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var secrets = this.analyzer.AnalyzeSecrets(tree, "ConfigService.cs");

        // Assert
        secrets.Should().ContainSingle();
        secrets[0].SecretType.Should().Be(SecretType.EnvironmentVariable);
        secrets[0].SecretName.Should().Be("DATABASE_URL");
    }

    [TestMethod]
    public void AnalyzeSecrets_ConfigurationConnectionString_DetectsAccess()
    {
        // Arrange
        var code = @"
public class DbService
{
    private readonly IConfiguration _configuration;

    public string GetDbConnection()
    {
        return _configuration.GetConnectionString(""DefaultConnection"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var secrets = this.analyzer.AnalyzeSecrets(tree, "DbService.cs");

        // Assert
        secrets.Should().ContainSingle();
        secrets[0].SecretType.Should().Be(SecretType.ConnectionString);
        secrets[0].SecretName.Should().Be("DefaultConnection");
    }

    [TestMethod]
    public void AnalyzeSecrets_HardcodedConnectionString_DetectsAntiPattern()
    {
        // Arrange
        var code = @"
public class BadService
{
    private readonly string _connectionString = ""Server=localhost;Database=mydb;User=admin;Password=secret123!"";
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var secrets = this.analyzer.AnalyzeSecrets(tree, "BadService.cs");

        // Assert
        secrets.Should().ContainSingle();
        secrets[0].SecretType.Should().Be(SecretType.HardcodedSecret);
        secrets[0].IsAntiPattern.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeSecrets_HardcodedApiKey_DetectsAntiPattern()
    {
        // Arrange
        var code = @"
public class ApiClient
{
    private const string ApiKey = ""sk-1234567890abcdefghijklmnopqrstuvwxyz"";
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var secrets = this.analyzer.AnalyzeSecrets(tree, "ApiClient.cs");

        // Assert
        secrets.Should().ContainSingle();
        secrets[0].SecretType.Should().Be(SecretType.HardcodedSecret);
        secrets[0].IsAntiPattern.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeSecrets_KeyVaultSetSecret_DetectsWrite()
    {
        // Arrange
        var code = @"
public class SecretManager
{
    private readonly SecretClient _client;

    public async Task StoreSecretAsync(string name, string value)
    {
        await _client.SetSecretAsync(name, value);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var secrets = this.analyzer.AnalyzeSecrets(tree, "SecretManager.cs");

        // Assert
        secrets.Should().ContainSingle();
        secrets[0].AccessType.Should().Be(SecretAccessType.Write);
    }

    #endregion

    #region Certificate Tests

    [TestMethod]
    public void AnalyzeCertificates_X509Certificate2FromFile_DetectsLoad()
    {
        // Arrange
        var code = @"
public class CertService
{
    public X509Certificate2 LoadCertificate()
    {
        return new X509Certificate2(""certificate.pfx"", ""password"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var certs = this.analyzer.AnalyzeCertificates(tree, "CertService.cs");

        // Assert
        certs.Should().ContainSingle();
        certs[0].CertificateType.Should().Be("X509Certificate2");
        certs[0].Source.Should().Be(CertificateSource.File);
        certs[0].Operation.Should().Be(CertificateOperation.Load);
    }

    [TestMethod]
    public void AnalyzeCertificates_X509StoreOpen_DetectsStoreAccess()
    {
        // Arrange
        var code = @"
public class CertificateManager
{
    public void OpenStore()
    {
        var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var certs = this.analyzer.AnalyzeCertificates(tree, "CertificateManager.cs");

        // Assert
        certs.Should().Contain(c => c.Operation == CertificateOperation.OpenStore);
    }

    [TestMethod]
    public void AnalyzeCertificates_FindByThumbprint_DetectsFind()
    {
        // Arrange
        var code = @"
public class CertFinder
{
    public X509Certificate2 FindCertificate(X509Store store, string thumbprint)
    {
        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        return certs[0];
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var certs = this.analyzer.AnalyzeCertificates(tree, "CertFinder.cs");

        // Assert
        certs.Should().Contain(c => c.Operation == CertificateOperation.Find);
    }

    [TestMethod]
    public void AnalyzeCertificates_CertificateFromBytes_DetectsSource()
    {
        // Arrange
        var code = @"
public class CertLoader
{
    public X509Certificate2 LoadFromBytes(byte[] rawData)
    {
        return new X509Certificate2(rawData);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var certs = this.analyzer.AnalyzeCertificates(tree, "CertLoader.cs");

        // Assert
        certs.Should().ContainSingle();
        certs[0].Source.Should().Be(CertificateSource.Bytes);
    }

    #endregion

    #region Cryptography Tests

    [TestMethod]
    public void AnalyzeCryptography_SHA256Create_DetectsHashing()
    {
        // Arrange
        var code = @"
public class HashService
{
    public byte[] ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "HashService.cs");

        // Assert
        crypto.Should().Contain(c => c.OperationType == CryptoOperationType.Hashing && c.Algorithm == "SHA256");
        crypto.Should().Contain(c => c.OperationType == CryptoOperationType.Hashing && !c.IsInsecure);
    }

    [TestMethod]
    public void AnalyzeCryptography_MD5Create_DetectsInsecure()
    {
        // Arrange
        var code = @"
public class LegacyHasher
{
    public byte[] Hash(string input)
    {
        using var md5 = MD5.Create();
        return md5.ComputeHash(Encoding.UTF8.GetBytes(input));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "LegacyHasher.cs");

        // Assert
        crypto.Should().Contain(c => c.Algorithm == "MD5" && c.IsInsecure);
    }

    [TestMethod]
    public void AnalyzeCryptography_AesCreate_DetectsEncryption()
    {
        // Arrange
        var code = @"
public class EncryptionService
{
    public byte[] Encrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.Encrypt(data);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "EncryptionService.cs");

        // Assert
        crypto.Should().Contain(c => c.OperationType == CryptoOperationType.Encryption);
    }

    [TestMethod]
    public void AnalyzeCryptography_RSASignData_DetectsSigning()
    {
        // Arrange
        var code = @"
public class SignatureService
{
    private readonly RSA _rsa;

    public byte[] Sign(byte[] data)
    {
        return _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "SignatureService.cs");

        // Assert
        crypto.Should().ContainSingle();
        crypto[0].OperationType.Should().Be(CryptoOperationType.Signing);
    }

    [TestMethod]
    public void AnalyzeCryptography_HMACSHA256_DetectsHashing()
    {
        // Arrange
        var code = @"
public class TokenService
{
    public byte[] CreateSignature(string data, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "TokenService.cs");

        // Assert
        crypto.Should().Contain(c => c.Algorithm == "HMACSHA256");
    }

    [TestMethod]
    public void AnalyzeCryptography_DataProtector_DetectsEncryption()
    {
        // Arrange
        var code = @"
public class ProtectionService
{
    private readonly IDataProtector _protector;

    public string Protect(string input)
    {
        return _protector.Protect(input);
    }

    public string Unprotect(string protectedData)
    {
        return _protector.Unprotect(protectedData);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "ProtectionService.cs");

        // Assert
        crypto.Should().HaveCount(2);
        crypto.Should().Contain(c => c.OperationType == CryptoOperationType.Encryption);
        crypto.Should().Contain(c => c.OperationType == CryptoOperationType.Decryption);
    }

    [TestMethod]
    public void AnalyzeCryptography_VerifyData_DetectsVerification()
    {
        // Arrange
        var code = @"
public class VerificationService
{
    private readonly RSA _rsa;

    public bool Verify(byte[] data, byte[] signature)
    {
        return _rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "VerificationService.cs");

        // Assert
        crypto.Should().ContainSingle();
        crypto[0].OperationType.Should().Be(CryptoOperationType.Verification);
    }

    [TestMethod]
    public void AnalyzeCryptography_TripleDES_DetectsInsecure()
    {
        // Arrange
        var code = @"
public class LegacyEncryption
{
    public byte[] Encrypt(byte[] data)
    {
        using var des = TripleDES.Create();
        return des.Encrypt(data);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var crypto = this.analyzer.AnalyzeCryptography(tree, "LegacyEncryption.cs");

        // Assert
        crypto.Should().Contain(c => c.Algorithm == "TripleDES" && c.IsInsecure);
    }

    #endregion
}
