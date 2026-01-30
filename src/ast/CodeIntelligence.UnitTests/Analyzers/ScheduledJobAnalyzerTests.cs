// -----------------------------------------------------------------------
// <copyright file="ScheduledJobAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ScheduledJobAnalyzerTests
{
    private ScheduledJobAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new ScheduledJobAnalyzer();
    }

    [TestMethod]
    public void Analyze_ThreadingTimer_DetectsTimerCreation()
    {
        // Arrange
        var code = @"
using System.Threading;

public class MyService
{
    private Timer _timer;

    public void Start()
    {
        _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private void OnTick(object state) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "MyService.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.ThreadingTimer);
        results[0].CallbackMethod.Should().Be("OnTick");
    }

    [TestMethod]
    public void Analyze_PeriodicTimer_DetectsPeriodicTimer()
    {
        // Arrange
        var code = @"
using System.Threading;

public class MyWorker
{
    private PeriodicTimer _timer;

    public async Task StartAsync()
    {
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await _timer.WaitForNextTickAsync())
        {
            await DoWorkAsync();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "MyWorker.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.PeriodicTimer);
        results[0].Interval.Should().Contain("TimeSpan.FromMinutes");
    }

    [TestMethod]
    public void Analyze_HangfireEnqueue_DetectsFireAndForgetJob()
    {
        // Arrange
        var code = @"
public class JobScheduler
{
    public void ScheduleJob()
    {
        BackgroundJob.Enqueue(() => SendEmail(""test@example.com""));
    }

    public void SendEmail(string email) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "JobScheduler.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.HangfireEnqueued);
        results[0].JobMethod.Should().Be("SendEmail");
    }

    [TestMethod]
    public void Analyze_HangfireSchedule_DetectsDelayedJob()
    {
        // Arrange
        var code = @"
public class JobScheduler
{
    public void ScheduleLater()
    {
        BackgroundJob.Schedule(() => ProcessOrder(123), TimeSpan.FromHours(1));
    }

    public void ProcessOrder(int orderId) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "JobScheduler.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.HangfireScheduled);
    }

    [TestMethod]
    public void Analyze_HangfireRecurring_DetectsRecurringJob()
    {
        // Arrange
        var code = @"
public class JobRegistration
{
    public void RegisterJobs()
    {
        RecurringJob.AddOrUpdate(""daily-cleanup"", () => CleanupOldData(), ""0 0 * * *"");
    }

    public void CleanupOldData() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "JobRegistration.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.HangfireRecurring);
        results[0].JobId.Should().Be("daily-cleanup");
        results[0].CronExpression.Should().Be("0 0 * * *");
    }

    [TestMethod]
    public void Analyze_HangfireWithCronConstant_DetectsCronExpression()
    {
        // Arrange
        var code = @"
public class JobRegistration
{
    public void RegisterJobs()
    {
        RecurringJob.AddOrUpdate(""hourly-sync"", () => SyncData(), Cron.Hourly);
    }

    public void SyncData() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "JobRegistration.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.HangfireRecurring);
        results[0].CronExpression.Should().Be("Cron.Hourly");
    }

    [TestMethod]
    public void Analyze_QuartzIJob_DetectsQuartzJob()
    {
        // Arrange
        var code = @"
using Quartz;

public class EmailJob : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        await SendEmailsAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "EmailJob.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.QuartzJob);
        results[0].ClassName.Should().Be("EmailJob");
        results[0].HasExecuteMethod.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_QuartzWithDisallowConcurrent_DetectsAttribute()
    {
        // Arrange
        var code = @"
using Quartz;

[DisallowConcurrentExecution]
public class SyncJob : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        await SyncAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "SyncJob.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].JobType.Should().Be(ScheduledJobType.QuartzJob);
        results[0].DisallowConcurrentExecution.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_NoScheduledJobs_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class MyService
{
    public void DoWork() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "MyService.cs");

        // Assert
        results.Should().BeEmpty();
    }

    [TestMethod]
    public void Analyze_MultipleJobs_DetectsAll()
    {
        // Arrange
        var code = @"
using System.Threading;
using Quartz;

public class JobService
{
    private Timer _timer;

    public void Configure()
    {
        _timer = new Timer(OnTick, null, 0, 1000);
        RecurringJob.AddOrUpdate(""cleanup"", () => Cleanup(), ""0 0 * * *"");
    }

    private void OnTick(object state) { }
    public void Cleanup() { }
}

public class BackupJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "JobService.cs");

        // Assert
        results.Should().HaveCount(3);
        results.Should().Contain(r => r.JobType == ScheduledJobType.ThreadingTimer);
        results.Should().Contain(r => r.JobType == ScheduledJobType.HangfireRecurring);
        results.Should().Contain(r => r.JobType == ScheduledJobType.QuartzJob);
    }
}
