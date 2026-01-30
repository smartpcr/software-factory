// -----------------------------------------------------------------------
// <copyright file="SecurityAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect security patterns including authorization, secrets, and cryptography.
/// Uses pure AST-based detection.
/// </summary>
public sealed class SecurityAnalyzer
{
    /// <summary>
    /// Authorization attributes.
    /// </summary>
    private static readonly HashSet<string> AuthorizationAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorize",
        "AuthorizeAttribute",
        "AllowAnonymous",
        "AllowAnonymousAttribute",
    };

    /// <summary>
    /// Secret access methods.
    /// </summary>
    private static readonly HashSet<string> SecretAccessMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Azure KeyVault
        "GetSecret",
        "GetSecretAsync",
        "SetSecret",
        "SetSecretAsync",
        // Environment
        "GetEnvironmentVariable",
        // Configuration
        "GetConnectionString",
        "GetValue",
        "GetSection",
    };

    /// <summary>
    /// Hashing algorithms.
    /// </summary>
    private static readonly HashSet<string> HashingTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHA256",
        "SHA384",
        "SHA512",
        "SHA1",
        "MD5",
        "HMACSHA256",
        "HMACSHA384",
        "HMACSHA512",
        "HMACMD5",
        "BCrypt",
        "Argon2",
        "PBKDF2",
        "Rfc2898DeriveBytes",
    };

    /// <summary>
    /// Encryption types.
    /// </summary>
    private static readonly HashSet<string> EncryptionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aes",
        "AesCng",
        "AesGcm",
        "AesCcm",
        "RSA",
        "RSACng",
        "RSACryptoServiceProvider",
        "ECDsa",
        "ECDsaCng",
        "ECDiffieHellman",
        "TripleDES",
        "DES",
        "IDataProtector",
        "IDataProtectionProvider",
    };

    /// <summary>
    /// Certificate types.
    /// </summary>
    private static readonly HashSet<string> CertificateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "X509Certificate",
        "X509Certificate2",
        "X509Certificate2Collection",
        "X509Store",
        "X509Chain",
    };

    /// <summary>
    /// Analyzes a syntax tree for authorization requirements.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected authorization requirements.</returns>
    public IReadOnlyList<AuthorizationInfo> AnalyzeAuthorization(SyntaxTree tree, string filePath)
    {
        var authorizations = new List<AuthorizationInfo>();
        var root = tree.GetRoot();

        // Analyze class-level attributes
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        foreach (var classDecl in classes)
        {
            var classAuth = this.AnalyzeAuthorizationAttributes(classDecl.AttributeLists, classDecl.Identifier.Text, null, filePath, classDecl);
            authorizations.AddRange(classAuth);

            // Analyze method-level attributes
            var methods = classDecl.Members.OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                var methodAuth = this.AnalyzeAuthorizationAttributes(method.AttributeLists, classDecl.Identifier.Text, method.Identifier.Text, filePath, method);
                authorizations.AddRange(methodAuth);
            }
        }

        // Analyze Minimal API authorization
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var minimalApiAuth = this.AnalyzeMinimalApiAuthorization(invocation, filePath);
            if (minimalApiAuth != null)
            {
                authorizations.Add(minimalApiAuth);
            }
        }

        return authorizations;
    }

    /// <summary>
    /// Analyzes a syntax tree for secret access patterns.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected secret accesses.</returns>
    public IReadOnlyList<SecretAccessInfo> AnalyzeSecrets(SyntaxTree tree, string filePath)
    {
        var secrets = new List<SecretAccessInfo>();
        var root = tree.GetRoot();

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var secretInfo = this.AnalyzeSecretInvocation(invocation, filePath);
            if (secretInfo != null)
            {
                secrets.Add(secretInfo);
            }
        }

        // Detect hardcoded secrets (anti-pattern)
        var literals = root.DescendantNodes().OfType<LiteralExpressionSyntax>();
        foreach (var literal in literals)
        {
            var hardcodedSecret = this.DetectHardcodedSecret(literal, filePath);
            if (hardcodedSecret != null)
            {
                secrets.Add(hardcodedSecret);
            }
        }

        return secrets;
    }

    /// <summary>
    /// Analyzes a syntax tree for certificate usage.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected certificate usages.</returns>
    public IReadOnlyList<CertificateUsageInfo> AnalyzeCertificates(SyntaxTree tree, string filePath)
    {
        var certificates = new List<CertificateUsageInfo>();
        var root = tree.GetRoot();

        // Analyze object creations
        var creations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        foreach (var creation in creations)
        {
            var certInfo = this.AnalyzeCertificateCreation(creation, filePath);
            if (certInfo != null)
            {
                certificates.Add(certInfo);
            }
        }

        // Analyze method invocations on certificate types
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var certInfo = this.AnalyzeCertificateInvocation(invocation, filePath);
            if (certInfo != null)
            {
                certificates.Add(certInfo);
            }
        }

        return certificates;
    }

    /// <summary>
    /// Analyzes a syntax tree for encryption and hashing operations.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected cryptographic operations.</returns>
    public IReadOnlyList<CryptoOperationInfo> AnalyzeCryptography(SyntaxTree tree, string filePath)
    {
        var cryptoOps = new List<CryptoOperationInfo>();
        var root = tree.GetRoot();

        // Analyze object creations (new SHA256(), new Aes(), etc.)
        var creations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        foreach (var creation in creations)
        {
            var cryptoInfo = this.AnalyzeCryptoCreation(creation, filePath);
            if (cryptoInfo != null)
            {
                cryptoOps.Add(cryptoInfo);
            }
        }

        // Analyze static Create methods (SHA256.Create(), Aes.Create())
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var cryptoInfo = this.AnalyzeCryptoInvocation(invocation, filePath);
            if (cryptoInfo != null)
            {
                cryptoOps.Add(cryptoInfo);
            }
        }

        return cryptoOps;
    }

    private IEnumerable<AuthorizationInfo> AnalyzeAuthorizationAttributes(
        SyntaxList<AttributeListSyntax> attributeLists,
        string className,
        string? methodName,
        string filePath,
        SyntaxNode node)
    {
        foreach (var attrList in attributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString().Replace("Attribute", "");

                if (!AuthorizationAttributes.Contains(attrName) &&
                    !AuthorizationAttributes.Contains(attrName + "Attribute"))
                {
                    continue;
                }

                var authType = attrName.Contains("AllowAnonymous")
                    ? AuthorizationType.AllowAnonymous
                    : AuthorizationType.Authorize;

                string? roles = null;
                string? policy = null;
                string? authSchemes = null;

                // Extract attribute arguments
                if (attr.ArgumentList != null)
                {
                    foreach (var arg in attr.ArgumentList.Arguments)
                    {
                        var argName = arg.NameEquals?.Name.Identifier.Text ?? arg.NameColon?.Name.Identifier.Text;
                        var argValue = arg.Expression.ToString().Trim('"');

                        if (argName == "Roles")
                        {
                            roles = argValue;
                        }
                        else if (argName == "Policy")
                        {
                            policy = argValue;
                        }
                        else if (argName == "AuthenticationSchemes")
                        {
                            authSchemes = argValue;
                        }
                        else if (argName == null && arg.Expression is LiteralExpressionSyntax literal)
                        {
                            // Positional argument - could be policy name
                            policy = literal.Token.ValueText;
                        }
                    }
                }

                yield return new AuthorizationInfo
                {
                    AuthorizationType = authType,
                    Scope = methodName != null ? AuthorizationScope.Method : AuthorizationScope.Class,
                    ClassName = className,
                    MethodName = methodName,
                    Roles = roles?.Split(',').Select(r => r.Trim()).ToList(),
                    Policy = policy,
                    AuthenticationSchemes = authSchemes,
                    FilePath = filePath,
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Confidence = 0.95f,
                };
            }
        }
    }

    private AuthorizationInfo? AnalyzeMinimalApiAuthorization(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;

        if (methodName != "RequireAuthorization" && methodName != "AllowAnonymous")
        {
            return null;
        }

        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        string? policy = null;
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                policy = literal.Token.ValueText;
            }
        }

        return new AuthorizationInfo
        {
            AuthorizationType = methodName == "AllowAnonymous"
                ? AuthorizationType.AllowAnonymous
                : AuthorizationType.Authorize,
            Scope = AuthorizationScope.Endpoint,
            ClassName = null,
            MethodName = containingMethod?.Identifier.Text,
            Policy = policy,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private SecretAccessInfo? AnalyzeSecretInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        string? methodName = null;
        string? receiver = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.Text;
            receiver = memberAccess.Expression.ToString();
        }

        if (methodName == null || !SecretAccessMethods.Contains(methodName))
        {
            return null;
        }

        SecretType? secretType = this.DetermineSecretType(receiver, methodName);
        if (secretType == null)
        {
            return null;
        }

        string? secretName = this.ExtractSecretName(invocation);

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new SecretAccessInfo
        {
            SecretType = secretType.Value,
            AccessType = methodName.Contains("Set") ? SecretAccessType.Write : SecretAccessType.Read,
            MethodName = methodName,
            SecretName = secretName,
            IsAsync = methodName.EndsWith("Async"),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private SecretAccessInfo? DetectHardcodedSecret(LiteralExpressionSyntax literal, string filePath)
    {
        var value = literal.Token.ValueText;

        // Skip short strings and common non-secret patterns
        if (string.IsNullOrEmpty(value) || value.Length < 16)
        {
            return null;
        }

        // Check for potential secret patterns
        var isSecret = false;
        var secretPattern = "";

        // Check for base64-like patterns (potential keys)
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9+/=]{32,}$"))
        {
            isSecret = true;
            secretPattern = "Base64-like key";
        }
        // Check for connection string patterns
        else if (value.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("pwd=", StringComparison.OrdinalIgnoreCase))
        {
            isSecret = true;
            secretPattern = "Connection string with password";
        }
        // Check for API key patterns
        else if (value.StartsWith("sk-") || value.StartsWith("pk_") ||
                 value.StartsWith("api_key_") || value.StartsWith("apikey_"))
        {
            isSecret = true;
            secretPattern = "API key pattern";
        }

        if (!isSecret)
        {
            return null;
        }

        var containingClass = literal.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        return new SecretAccessInfo
        {
            SecretType = SecretType.HardcodedSecret,
            AccessType = SecretAccessType.Hardcoded,
            MethodName = "Literal",
            SecretName = secretPattern,
            IsAsync = false,
            IsAntiPattern = true,
            ClassName = containingClass?.Identifier.Text,
            FilePath = filePath,
            LineNumber = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.85f,
        };
    }

    private CertificateUsageInfo? AnalyzeCertificateCreation(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();
        var simpleTypeName = typeName.Split('.').Last();

        if (!CertificateTypes.Contains(simpleTypeName))
        {
            return null;
        }

        var source = this.DetermineCertificateSource(creation);

        var containingClass = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new CertificateUsageInfo
        {
            CertificateType = simpleTypeName,
            Source = source,
            Operation = CertificateOperation.Load,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private CertificateUsageInfo? AnalyzeCertificateInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();

        // Certificate store method names that are specific enough to identify cert operations
        var certStoreMethodNames = new HashSet<string> { "Open", "Find", "FindByThumbprint", "Verify", "Build", "GetRawCertData" };

        // Check for certificate store operations
        var isCertRelated = receiver.Contains("X509", StringComparison.OrdinalIgnoreCase) ||
                            receiver.Contains("Certificate", StringComparison.OrdinalIgnoreCase) ||
                            receiver.Contains("store", StringComparison.OrdinalIgnoreCase) ||
                            receiver.Contains("cert", StringComparison.OrdinalIgnoreCase);

        if (!isCertRelated && !certStoreMethodNames.Contains(methodName))
        {
            return null;
        }

        CertificateOperation? operation = null;
        if (methodName == "Open")
        {
            operation = CertificateOperation.OpenStore;
        }
        else if (methodName == "Find" || methodName == "FindByThumbprint")
        {
            operation = CertificateOperation.Find;
        }
        else if (methodName == "Verify" || methodName == "Build")
        {
            operation = CertificateOperation.Validate;
        }
        else if (methodName == "Export" || methodName == "GetRawCertData")
        {
            operation = CertificateOperation.Export;
        }
        else if (methodName == "Import")
        {
            operation = CertificateOperation.Import;
        }

        if (operation == null)
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new CertificateUsageInfo
        {
            CertificateType = "X509Certificate",
            Source = CertificateSource.Store,
            Operation = operation.Value,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.85f,
        };
    }

    private CryptoOperationInfo? AnalyzeCryptoCreation(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();
        var simpleTypeName = typeName.Split('.').Last();

        CryptoOperationType? opType = null;
        string? algorithm = null;

        if (HashingTypes.Contains(simpleTypeName))
        {
            opType = CryptoOperationType.Hashing;
            algorithm = simpleTypeName;
        }
        else if (EncryptionTypes.Contains(simpleTypeName))
        {
            opType = CryptoOperationType.Encryption;
            algorithm = simpleTypeName;
        }

        if (opType == null)
        {
            return null;
        }

        var containingClass = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        // Check for insecure algorithms
        var isInsecure = this.IsInsecureAlgorithm(algorithm);

        return new CryptoOperationInfo
        {
            OperationType = opType.Value,
            Algorithm = algorithm,
            IsInsecure = isInsecure,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private CryptoOperationInfo? AnalyzeCryptoInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();

        CryptoOperationType? opType = null;
        string? algorithm = null;

        // Check for static Create methods
        if (methodName == "Create")
        {
            if (HashingTypes.Contains(receiver))
            {
                opType = CryptoOperationType.Hashing;
                algorithm = receiver;
            }
            else if (EncryptionTypes.Contains(receiver))
            {
                opType = CryptoOperationType.Encryption;
                algorithm = receiver;
            }
        }
        // Check for ComputeHash, Encrypt, Decrypt, Protect methods
        else if (methodName == "ComputeHash" || methodName == "ComputeHashAsync")
        {
            opType = CryptoOperationType.Hashing;
            algorithm = receiver.Split('.').Last();
        }
        else if (methodName == "Encrypt" || methodName == "EncryptAsync" ||
                 methodName == "Protect")
        {
            opType = CryptoOperationType.Encryption;
            algorithm = receiver.Split('.').Last();
        }
        else if (methodName == "Decrypt" || methodName == "DecryptAsync" ||
                 methodName == "Unprotect")
        {
            opType = CryptoOperationType.Decryption;
            algorithm = receiver.Split('.').Last();
        }
        else if (methodName == "SignData" || methodName == "SignHash")
        {
            opType = CryptoOperationType.Signing;
            algorithm = receiver.Split('.').Last();
        }
        else if (methodName == "VerifyData" || methodName == "VerifyHash")
        {
            opType = CryptoOperationType.Verification;
            algorithm = receiver.Split('.').Last();
        }

        if (opType == null)
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new CryptoOperationInfo
        {
            OperationType = opType.Value,
            Algorithm = algorithm,
            IsInsecure = this.IsInsecureAlgorithm(algorithm),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.85f,
        };
    }

    private SecretType? DetermineSecretType(string? receiver, string methodName)
    {
        if (receiver == null)
        {
            return null;
        }

        // Check for Azure Key Vault patterns
        if (receiver.Contains("SecretClient", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("KeyVault", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("_secretClient", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("secretClient", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("_keyVault", StringComparison.OrdinalIgnoreCase))
        {
            return SecretType.AzureKeyVault;
        }

        // Check for secret access by method name for generic receivers like "_client"
        if ((methodName == "GetSecretAsync" || methodName == "GetSecret" ||
             methodName == "SetSecretAsync" || methodName == "SetSecret") &&
            (receiver.Contains("_client", StringComparison.OrdinalIgnoreCase) ||
             receiver.Contains("client", StringComparison.OrdinalIgnoreCase)))
        {
            return SecretType.AzureKeyVault;
        }

        if (receiver.Equals("Environment", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("Environment", StringComparison.OrdinalIgnoreCase))
        {
            return SecretType.EnvironmentVariable;
        }

        if (receiver.Contains("Configuration", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("_config", StringComparison.OrdinalIgnoreCase))
        {
            if (methodName == "GetConnectionString")
            {
                return SecretType.ConnectionString;
            }

            return SecretType.Configuration;
        }

        return null;
    }

    private string? ExtractSecretName(InvocationExpressionSyntax invocation)
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

    private CertificateSource DetermineCertificateSource(ObjectCreationExpressionSyntax creation)
    {
        var args = creation.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
        {
            return CertificateSource.Unknown;
        }

        var firstArg = args.Value[0].Expression.ToString();

        if (firstArg.Contains(".pfx") || firstArg.Contains(".cer") ||
            firstArg.Contains(".pem") || firstArg.Contains("path", StringComparison.OrdinalIgnoreCase))
        {
            return CertificateSource.File;
        }

        if (firstArg.Contains("byte") || firstArg.Contains("rawData"))
        {
            return CertificateSource.Bytes;
        }

        return CertificateSource.Unknown;
    }

    private bool IsInsecureAlgorithm(string? algorithm)
    {
        if (algorithm == null)
        {
            return false;
        }

        var insecureAlgorithms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MD5",
            "SHA1",
            "DES",
            "TripleDES",
            "RC2",
            "RC4",
        };

        return insecureAlgorithms.Contains(algorithm);
    }
}

/// <summary>
/// Information about authorization requirements.
/// </summary>
public record AuthorizationInfo
{
    /// <summary>Gets the authorization type.</summary>
    public required AuthorizationType AuthorizationType { get; init; }

    /// <summary>Gets the scope (class/method/endpoint).</summary>
    public required AuthorizationScope Scope { get; init; }

    /// <summary>Gets the class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the method name.</summary>
    public string? MethodName { get; init; }

    /// <summary>Gets the required roles.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>Gets the required policy.</summary>
    public string? Policy { get; init; }

    /// <summary>Gets the authentication schemes.</summary>
    public string? AuthenticationSchemes { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the detection confidence.</summary>
    public required float Confidence { get; init; }
}

/// <summary>
/// Information about secret access.
/// </summary>
public record SecretAccessInfo
{
    /// <summary>Gets the secret type.</summary>
    public required SecretType SecretType { get; init; }

    /// <summary>Gets the access type.</summary>
    public required SecretAccessType AccessType { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the secret name or key.</summary>
    public string? SecretName { get; init; }

    /// <summary>Gets whether the access is async.</summary>
    public required bool IsAsync { get; init; }

    /// <summary>Gets whether this is an anti-pattern.</summary>
    public bool IsAntiPattern { get; init; }

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
/// Information about certificate usage.
/// </summary>
public record CertificateUsageInfo
{
    /// <summary>Gets the certificate type.</summary>
    public required string CertificateType { get; init; }

    /// <summary>Gets the certificate source.</summary>
    public required CertificateSource Source { get; init; }

    /// <summary>Gets the operation type.</summary>
    public required CertificateOperation Operation { get; init; }

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
/// Information about cryptographic operations.
/// </summary>
public record CryptoOperationInfo
{
    /// <summary>Gets the operation type.</summary>
    public required CryptoOperationType OperationType { get; init; }

    /// <summary>Gets the algorithm name.</summary>
    public string? Algorithm { get; init; }

    /// <summary>Gets whether the algorithm is considered insecure.</summary>
    public required bool IsInsecure { get; init; }

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
/// Types of authorization.
/// </summary>
public enum AuthorizationType
{
    /// <summary>Requires authorization.</summary>
    Authorize,

    /// <summary>Allows anonymous access.</summary>
    AllowAnonymous,
}

/// <summary>
/// Scope of authorization.
/// </summary>
public enum AuthorizationScope
{
    /// <summary>Class-level authorization.</summary>
    Class,

    /// <summary>Method-level authorization.</summary>
    Method,

    /// <summary>Endpoint-level (minimal API).</summary>
    Endpoint,
}

/// <summary>
/// Types of secrets.
/// </summary>
public enum SecretType
{
    /// <summary>Azure KeyVault secret.</summary>
    AzureKeyVault,

    /// <summary>Environment variable.</summary>
    EnvironmentVariable,

    /// <summary>Connection string.</summary>
    ConnectionString,

    /// <summary>Configuration value.</summary>
    Configuration,

    /// <summary>Hardcoded secret (anti-pattern).</summary>
    HardcodedSecret,
}

/// <summary>
/// Types of secret access.
/// </summary>
public enum SecretAccessType
{
    /// <summary>Reading a secret.</summary>
    Read,

    /// <summary>Writing a secret.</summary>
    Write,

    /// <summary>Hardcoded in source.</summary>
    Hardcoded,
}

/// <summary>
/// Certificate sources.
/// </summary>
public enum CertificateSource
{
    /// <summary>Certificate from file.</summary>
    File,

    /// <summary>Certificate from store.</summary>
    Store,

    /// <summary>Certificate from bytes.</summary>
    Bytes,

    /// <summary>Unknown source.</summary>
    Unknown,
}

/// <summary>
/// Certificate operations.
/// </summary>
public enum CertificateOperation
{
    /// <summary>Loading a certificate.</summary>
    Load,

    /// <summary>Opening certificate store.</summary>
    OpenStore,

    /// <summary>Finding certificate.</summary>
    Find,

    /// <summary>Validating certificate.</summary>
    Validate,

    /// <summary>Exporting certificate.</summary>
    Export,

    /// <summary>Importing certificate.</summary>
    Import,
}

/// <summary>
/// Types of cryptographic operations.
/// </summary>
public enum CryptoOperationType
{
    /// <summary>Hashing operation.</summary>
    Hashing,

    /// <summary>Encryption operation.</summary>
    Encryption,

    /// <summary>Decryption operation.</summary>
    Decryption,

    /// <summary>Signing operation.</summary>
    Signing,

    /// <summary>Signature verification.</summary>
    Verification,
}
