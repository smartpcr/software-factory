import { useId, useState } from 'react';

export function ChatTelegramSetup({ busy, onContinue }: { busy: boolean; onContinue: (token: string) => void }) {
  const [token, setToken] = useState('');
  const helpId = useId();
  return <section className="chat-telegram-setup" aria-label="Enable Telegram connections">
    <h4>Enable Telegram connections</h4>
    <p>Telegram requires one manager bot before it can show connection approval prompts. Register it once for this Forge host; future connections only need your approval in Telegram.</p>
    <ol className="chat-bootstrap-steps">
      <li><strong>Create the manager in Telegram</strong>
        <p>Open the official BotFather, choose <strong>Create a New Bot</strong> (or send <code>/newbot</code>), and give it a name and a unique username ending in <code>bot</code>.</p>
        <a className="chat-bootstrap-link" href="https://t.me/BotFather" target="_blank" rel="noopener noreferrer">Open BotFather ↗</a>
      </li>
      <li><strong>Enable Bot Management Mode</strong>
        <p>Open BotFather’s app to find this switch. In Telegram Desktop, click the blue <strong>Open App</strong> button in BotFather’s profile on the right, or <strong>Open</strong> beside the message box.</p>
        <a className="chat-bootstrap-link" href="https://t.me/Botfather?startapp=" target="_blank" rel="noopener noreferrer">Open the BotFather app ↗</a>
        <p>Select the new manager bot from step 1. Scroll below its token’s <strong>Copy / Revoke</strong> buttons to <strong>Settings</strong>, open <strong>Bot Settings</strong>, and turn on <strong>Bot Management Mode</strong>. Use a separate bot reserved for this Forge host.</p>
        <p className="chat-help"><code>/setprivacy</code> controls which group messages a bot receives; it does not enable bot management. If the app cannot open, update Telegram and try the app link again.</p>
        <a href="https://core.telegram.org/bots/features#managed-bots" target="_blank" rel="noopener noreferrer">Telegram’s manager-bot guide ↗</a>
      </li>
      <li><strong>Give Forge the manager token once</strong>
        <p>Copy the bot’s API token from BotFather and paste it below. Telegram does not send this first credential back to Forge automatically.</p>
        <form onSubmit={event => { event.preventDefault(); if (busy || !token.trim()) return; const value = token.trim(); setToken(''); onContinue(value); }}>
          <label>Manager bot token<input type="password" autoComplete="off" spellCheck={false} value={token} maxLength={256}
            disabled={busy} aria-describedby={helpId} onChange={event => setToken(event.target.value)} placeholder="Paste the token from BotFather" /></label>
          <p className="chat-help" id={helpId}>Forge checks the bot, saves the token encrypted on this host, then opens Telegram’s approval prompt. No configuration files or bot IDs to enter.</p>
          <button type="submit" className="primary chat-authorization-connect" disabled={busy || !token.trim()}>{busy ? 'Verifying Telegram setup…' : 'Verify and continue'}</button>
        </form>
      </li>
    </ol>
  </section>;
}