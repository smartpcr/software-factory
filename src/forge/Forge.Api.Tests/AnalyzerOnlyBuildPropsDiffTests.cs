// -----------------------------------------------------------------------
// <copyright file="AnalyzerOnlyBuildPropsDiffTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// 2026-05-26 — contract for <see cref="ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff"/>,
/// which decides whether a Directory.Build.props/.targets change is build-time-only
/// (analyzer wiring → safe to NOT force the whole test suite). The classifier must be
/// CONSERVATIVE: only a purely-additive, analyzer/diagnostics-only diff is neutral;
/// anything else stays whole-suite. Getting this wrong skips tests that should run, so
/// the negative cases matter most.
/// </summary>
public class AnalyzerOnlyBuildPropsDiffTests
{
    // Mirrors the real common-infra stage-1.3 banned-api-analyzer wiring diff:
    // an additive Choose/When block + comment + analyzer PackageReference +
    // WarningsAsErrors + AdditionalFiles. Should be classified analyzer-only.
    private const string AnalyzerWiringDiff = @"diff --git a/Directory.Build.props b/Directory.Build.props
index 7cf201af5..1c627bc73 100644
--- a/Directory.Build.props
+++ b/Directory.Build.props
@@ -143,4 +143,40 @@
 	</Target>

+  <!--
+  Banned-API analyzer wiring (story local:HTTP-MEMORY, Stage 1.3).
+  Attaches Microsoft.CodeAnalysis.BannedApiAnalyzers to src\Http and src\WebLayer.
+  -->
+  <Choose>
+    <When Condition=""$(MSBuildProjectExtension) == '.csproj'"">
+      <ItemGroup>
+        <PackageReference Include=""Microsoft.CodeAnalysis.BannedApiAnalyzers"" />
+        <AdditionalFiles Include=""$(SRCROOT)\Http\BannedSymbols.txt"" />
+      </ItemGroup>
+      <PropertyGroup>
+        <WarningsAsErrors>$(WarningsAsErrors);RS0030</WarningsAsErrors>
+      </PropertyGroup>
+    </When>
+  </Choose>
";

    private const string AnalyzerWiringDiffWithAnalyzerPackageMetadata = @"diff --git a/Directory.Build.props b/Directory.Build.props
index 7cf201af5..f99436cd5 100644
--- a/Directory.Build.props
+++ b/Directory.Build.props
@@ -114,6 +114,42 @@
     <AdditionalFiles Include=""$(SRCROOT)\stylecop.json"" />
   </ItemGroup>

+  <!--
+  Banned-API analyzer wiring (story local:HTTP-MEMORY, Phase 1 / Stage 1.3)
+  -->
+  <Choose>
+    <When Condition="" '$(MSBuildProjectExtension)' == '.csproj' And ( $(MSBuildProjectDirectory.StartsWith('$(MSBuildThisFileDirectory)src\Http\')) Or $(MSBuildProjectDirectory.StartsWith('$(MSBuildThisFileDirectory)src\WebLayer\')) ) "">
+      <ItemGroup>
+        <PackageReference Include=""Microsoft.CodeAnalysis.BannedApiAnalyzers"">
+          <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
+          <PrivateAssets>all</PrivateAssets>
+        </PackageReference>
+        <AdditionalFiles Include=""$(MSBuildThisFileDirectory)src\Http\BannedSymbols.txt""
+                         Condition="" $(MSBuildProjectDirectory.StartsWith('$(MSBuildThisFileDirectory)src\Http\')) "" />
+        <AdditionalFiles Include=""$(MSBuildThisFileDirectory)src\WebLayer\BannedSymbols.txt""
+                         Condition="" $(MSBuildProjectDirectory.StartsWith('$(MSBuildThisFileDirectory)src\WebLayer\')) "" />
+      </ItemGroup>
+    </When>
+  </Choose>

   <!-- Packaging settings  -->
   <PropertyGroup>";

    [Fact]
    public void RealAnalyzerWiringDiff_IsNeutral()
    {
        Assert.True(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(AnalyzerWiringDiff));
    }

    [Fact]
    public void RealAnalyzerWiringDiffWithPackageMetadata_IsNeutral()
    {
        Assert.True(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(AnalyzerWiringDiffWithAnalyzerPackageMetadata));
    }

    // Verbatim common-infra stage-1.3 `src/packages.props` diff: an additive
    // comment + a single analyzer PackageReference version pin. Central-package-
    // version files (`packages.props` / `Directory.Packages.props`) are now
    // global-impact (a real version bump must run the whole suite), but an
    // analyzer-only additive pin like this is build-time-only and must be relaxed
    // so it doesn't drag the 74-project net462 suite into a 40-min timeout.
    private const string Stage13PackagesPropsAnalyzerPinDiff = @"diff --git a/src/packages.props b/src/packages.props
index 0e30894a8..8e6113b47 100644
--- a/src/packages.props
+++ b/src/packages.props
@@ -101,6 +101,16 @@
         <PrivateAssets>all</PrivateAssets>
     </PackageReference>

+    <!--
+      Banned-API analyzer (story local:HTTP-MEMORY, Phase 1 / Stage 1.3).
+      Version pin only; the actual PackageReference Include lives in the
+      root Directory.Build.props inside a Choose/When block.
+    -->
+    <PackageReference Update=""Microsoft.CodeAnalysis.BannedApiAnalyzers"" Version=""3.3.4"" />
+
     <!-- OpenTelemetry packages -->
     <PackageReference Update=""OpenTelemetry"" Version=""1.15.3"" GeneratePathProperty=""true""/>";

    [Fact]
    public void Stage13PackagesPropsAnalyzerPin_IsNeutral()
    {
        // Regression pin for the common-infra/1.3 root cause: the central
        // package-version file's analyzer pin must be classified analyzer-only
        // so #3 (packages.props is global-impact) doesn't re-trip whole-suite.
        Assert.True(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(Stage13PackagesPropsAnalyzerPinDiff));
    }

    [Fact]
    public void NonAnalyzerPackageVersionBump_IsNotNeutral()
    {
        // A real dependency version bump in a central-package-version file IS
        // compilation-affecting and must NOT be relaxed (keep → whole suite).
        const string diff = @"@@ -101,1 +101,1 @@
+    <PackageReference Update=""OpenTelemetry"" Version=""1.16.0"" GeneratePathProperty=""true""/>";
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(diff));
    }

    [Fact]
    public void EmptyOrNullDiff_IsNotNeutral()
    {
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(null));
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(""));
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff("   "));
    }

    [Fact]
    public void TargetFrameworkChange_IsNotNeutral()
    {
        const string diff = @"@@ -1,1 +1,2 @@
+  <PropertyGroup>
+    <TargetFramework>net9.0</TargetFramework>
+  </PropertyGroup>";
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(diff));
    }

    [Fact]
    public void DefineConstantsChange_IsNotNeutral()
    {
        const string diff = "@@ -1 +1 @@\n+    <DefineConstants>$(DefineConstants);FEATURE_X</DefineConstants>";
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(diff));
    }

    [Fact]
    public void NonAnalyzerPackageReference_IsNotNeutral()
    {
        // Adding a real (non-analyzer) dependency can change runtime behaviour.
        const string diff = "@@ -1 +1 @@\n+    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />";
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(diff));
    }

    [Fact]
    public void RemovedOrModifiedLine_IsNotNeutral()
    {
        // A modification (remove + add) to existing build config — can't prove neutral.
        const string diff = @"@@ -10,2 +10,2 @@
-    <WarningsAsErrors>RS0030</WarningsAsErrors>
+    <WarningsAsErrors>RS0030;CS1591</WarningsAsErrors>";
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(diff));
    }

    [Fact]
    public void AdditiveButNoAnalyzerMarker_IsNotNeutral()
    {
        // Purely structural additions with no analyzer marker → not provably neutral.
        const string diff = "@@ -1 +1 @@\n+  <ItemGroup>\n+  </ItemGroup>";
        Assert.False(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(diff));
    }

    [Fact]
    public void CommentMentioningTargetFramework_StaysNeutral()
    {
        // A compilation keyword INSIDE an XML comment must not flip the verdict.
        const string diff = @"@@ -1 +1 @@
+  <!-- bumps nothing; do not confuse with a <TargetFramework> change -->
+  <ItemGroup>
+    <AdditionalFiles Include=""BannedSymbols.txt"" />
+  </ItemGroup>";
        Assert.True(ImpactedTestResolver.IsAnalyzerOnlyBuildPropsDiff(diff));
    }
}
