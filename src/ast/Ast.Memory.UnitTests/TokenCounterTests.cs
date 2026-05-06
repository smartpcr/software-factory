// -----------------------------------------------------------------------
// <copyright file="TokenCounterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Memory.Context;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class TokenCounterTests
{
    private TiktokenCounter counter = null!;

    [TestInitialize]
    public void Setup()
    {
        this.counter = new TiktokenCounter();
    }

    [TestMethod]
    public void CountTokens_EmptyString_ReturnsZero()
    {
        this.counter.CountTokens(string.Empty).Should().Be(0);
    }

    [TestMethod]
    public void CountTokens_NullString_ReturnsZero()
    {
        this.counter.CountTokens(null!).Should().Be(0);
    }

    [TestMethod]
    public void CountTokens_SimpleText_ReturnsPositive()
    {
        var tokens = this.counter.CountTokens("Hello, world!");
        tokens.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void CountTokens_LongerTextHasMoreTokens()
    {
        var short_ = this.counter.CountTokens("Hello");
        var long_ = this.counter.CountTokens("Hello, this is a much longer sentence with many more words in it.");
        long_.Should().BeGreaterThan(short_);
    }

    [TestMethod]
    public void CountTokens_CodeSnippet_ReturnsReasonableCount()
    {
        var code = @"public class PaymentService
{
    private readonly ILogger<PaymentService> logger;

    public PaymentService(ILogger<PaymentService> logger)
    {
        this.logger = logger;
    }

    public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
    {
        this.logger.LogInformation(""Processing payment for {Amount}"", request.Amount);
        return new PaymentResult { Success = true };
    }
}";
        var tokens = this.counter.CountTokens(code);

        // cl100k_base tokenizes code at roughly 3-4 chars/token
        tokens.Should().BeGreaterThan(20);
        tokens.Should().BeLessThan(500);
    }
}
