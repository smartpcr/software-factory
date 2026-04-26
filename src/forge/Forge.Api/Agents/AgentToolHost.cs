// -----------------------------------------------------------------------
// <copyright file="AgentToolHost.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Phase 11.6 — default in-proc tool host. Path checks resolve everything to
/// an absolute, fully-qualified form and require it to live under the
/// worktree root. Shell runs use the worktree as the working directory and
/// the allowlist from <see cref="AgentContext.AllowedTools"/>.
/// </summary>
public sealed class AgentToolHost : IAgentToolHost
{
    public IAgentToolSession OpenSession(AgentContext context, TimeSpan wallClock, long? maxTokens = null)
        => new Session(context, wallClock, maxTokens);

    private sealed class Session : IAgentToolSession
    {
        private readonly AgentContext context;
        private readonly string root;
        private readonly Stopwatch wall;
        private readonly TimeSpan wallBudget;
        private readonly long? tokenBudget;
        private long inputUsed;
        private long outputUsed;
        private bool disposed;

        public Session(AgentContext context, TimeSpan wallBudget, long? tokenBudget)
        {
            this.context = context;
            this.root = NormalizeRoot(context.WorktreePath);
            this.wall = Stopwatch.StartNew();
            this.wallBudget = wallBudget;
            this.tokenBudget = tokenBudget;
        }

        public async Task<string> ReadFileAsync(string path, CancellationToken ct)
        {
            this.EnsureLive();
            this.RequireTool("read_file");
            var resolved = this.ResolveInsideRoot(path);
            return await File.ReadAllTextAsync(resolved, ct);
        }

        public async Task WriteFileAsync(string path, string contents, CancellationToken ct)
        {
            this.EnsureLive();
            this.RequireTool("write_file");
            var resolved = this.ResolveInsideRoot(path);
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(resolved, contents, ct);
        }

        public async Task<ToolRunResult> RunCommandAsync(string command, IReadOnlyList<string> args, CancellationToken ct)
        {
            this.EnsureLive();
            this.RequireTool("run_command");

            var psi = new ProcessStartInfo
            {
                FileName = command,
                WorkingDirectory = this.root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!proc.Start())
            {
                throw new InvalidOperationException($"Failed to start '{command}'.");
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw;
            }

            return new ToolRunResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        }

        public void RecordTokens(long input, long output)
        {
            this.EnsureLive();
            this.inputUsed += Math.Max(0, input);
            this.outputUsed += Math.Max(0, output);
            if (this.tokenBudget is long max && this.inputUsed + this.outputUsed > max)
            {
                throw new AgentBudgetExceededException(
                    $"Token budget exceeded: used {this.inputUsed + this.outputUsed} > limit {max}");
            }
        }

        public void Dispose() => this.disposed = true;

        private void EnsureLive()
        {
            if (this.disposed) throw new ObjectDisposedException(nameof(Session));
            if (this.wall.Elapsed > this.wallBudget)
            {
                throw new AgentBudgetExceededException(
                    $"Wall-clock budget exceeded: {this.wall.Elapsed} > {this.wallBudget}");
            }
        }

        private void RequireTool(string toolName)
        {
            if (!this.context.AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                throw new AgentSandboxViolationException(
                    $"Tool '{toolName}' is not in the soul.md allowlist for crew '{this.context.CrewId}'.");
            }
        }

        private string ResolveInsideRoot(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                throw new AgentSandboxViolationException("Path is required.");
            }
            // Reject explicit traversal to fail fast even if the resolved path
            // would have stayed inside (operators read these errors).
            if (requested.Contains("..", StringComparison.Ordinal))
            {
                throw new AgentSandboxViolationException($"Path traversal not permitted: '{requested}'.");
            }
            var combined = Path.IsPathRooted(requested) ? requested : Path.Combine(this.root, requested);
            var full = Path.GetFullPath(combined);
            if (!full.StartsWith(this.root, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentSandboxViolationException(
                    $"Path '{requested}' resolves outside worktree '{this.root}'.");
            }
            // Symlink trip: if the resolved file/dir is a symlink that points
            // out of the worktree, refuse.
            var info = File.Exists(full) ? (FileSystemInfo)new FileInfo(full)
                     : Directory.Exists(full) ? new DirectoryInfo(full)
                     : null;
            if (info?.LinkTarget is string target)
            {
                var targetFull = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(this.root, target));
                if (!targetFull.StartsWith(this.root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AgentSandboxViolationException(
                        $"Symlink '{full}' points outside worktree to '{targetFull}'.");
                }
            }
            return full;
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new AgentSandboxViolationException("WorktreePath is required for tool sessions.");
            }
            var full = Path.GetFullPath(root);
            // Make sure trailing-slash compares are stable.
            if (!full.EndsWith(Path.DirectorySeparatorChar)) full += Path.DirectorySeparatorChar;
            return full;
        }
    }
}
