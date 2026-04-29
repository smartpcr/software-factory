// -----------------------------------------------------------------------
// <copyright file="SystemNotify.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;

/// <summary>
/// Phase 19 — fire Windows toast notifications for key execution events
/// so operators with multiple terminals get alerted. Uses PowerShell's
/// built-in <c>[Windows.UI.Notifications]</c> API — no extra modules.
/// All methods are fire-and-forget; failures are silently swallowed.
/// </summary>
public static class SystemNotify
{
    public static void Toast(string title, string body)
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Use BurntToast if available, fall back to raw WinRT API.
                var script = $@"
                    try {{
                        Import-Module BurntToast -ErrorAction Stop
                        New-BurntToastNotification -Text '{Escape(title)}', '{Escape(body)}' -AppLogo $null
                    }} catch {{
                        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                        $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
                        $text = $xml.GetElementsByTagName('text')
                        $text[0].AppendChild($xml.CreateTextNode('{Escape(title)}')) | Out-Null
                        $text[1].AppendChild($xml.CreateTextNode('{Escape(body)}')) | Out-Null
                        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
                        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Forge').Show($toast)
                    }}";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch
            {
                // Best effort — never break the main flow.
            }
        });
    }

    public static void AgentCompleted(string workItemId, string? agentId, int score)
    {
        Toast("✓ Agent completed", $"{workItemId} — score {score} by {agentId ?? "agent"}");
    }

    public static void AgentFailed(string workItemId, string? error)
    {
        var msg = string.IsNullOrEmpty(error) ? workItemId : $"{workItemId}: {error?[..Math.Min(error.Length, 80)]}";
        Toast("✗ Agent failed", msg);
    }

    public static void PrCreated(string workItemId, string? prId)
    {
        Toast("⊕ PR created", $"{workItemId} → {prId ?? "PR"}");
    }

    private static string Escape(string s) =>
        s.Replace("'", "''").Replace("\n", " ").Replace("\r", "");
}
