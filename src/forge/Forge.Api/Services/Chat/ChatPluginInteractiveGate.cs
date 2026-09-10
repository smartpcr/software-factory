namespace Forge.Api.Services.Chat;

/// <summary>
/// The shared identity prerequisite does not enable the future plugin runtime.
/// Stage 1/2 must replace this gate only after connection grants, subscriptions,
/// action receipts and confirmations have passed their own acceptance gates.
/// Configuration alone cannot advertise or activate unfinished plugin actions.
/// </summary>
public static class ChatPluginInteractiveGate
{
    public static bool IsAvailable => false;
    public static string Reason => "Chat plugin interactive mode is unavailable until the plugin runtime and action delivery stages are complete.";
    public static void RequireAvailable()
    {
        if (!IsAvailable) throw new InvalidOperationException(Reason);
    }
}
