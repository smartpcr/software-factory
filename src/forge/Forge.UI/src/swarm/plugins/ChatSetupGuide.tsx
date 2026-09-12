import { useState } from 'react';
import slackManifest from '../../../../../../docs/chat/slack-app-manifest.yaml?raw';
import slackManifestUrl from '../../../../../../docs/chat/slack-app-manifest.yaml?url';
import slackBasicInformation from '../../assets/chat-setup/slack-basic-information.png';
import slackBotToken from '../../assets/chat-setup/slack-bot-token.png';
import telegramTokenMap from '../../assets/chat-setup/telegram-token-map.svg';

const slackDocs = 'https://docs.slack.dev/tools/bolt-python/creating-an-app/';
const external = { target: '_blank', rel: 'noopener noreferrer' };

function SetupScreenshot({ src, alt, caption }: { src: string; alt: string; caption: string }) {
  return <figure className="chat-setup-figure">
    <a href={src} {...external} aria-label={`Enlarge screenshot: ${caption}`}><img src={src} alt={alt} loading="lazy" width="2048" height={src === slackBotToken ? 1030 : 1170} /></a>
    <figcaption>{caption} · <a href={slackDocs} {...external}>Slack documentation example</a> · Select image to enlarge.</figcaption>
  </figure>;
}

export function ChatSetupGuide({ providerId }: { providerId: string }) {
  const [copyStatus, setCopyStatus] = useState('');
  const slack = providerId === 'slack';
  async function copyManifest() {
    try { await navigator.clipboard.writeText(slackManifest); setCopyStatus('Manifest copied. Paste it into Slack’s YAML editor.'); }
    catch { setCopyStatus('Clipboard is unavailable. Download the manifest or select the YAML below.'); }
  }
  return <details className="chat-setup-guide">
    <summary><span>Step-by-step {slack ? 'Slack' : 'Telegram'} setup</span><small>{slack ? '5 steps · Screenshots included' : '3 steps · Illustrated field guide'}</small></summary>
    <div className="chat-guide-content">
      <p>Create the {slack ? 'Slack app' : 'Telegram bot'} first, then enter its details below. Open a step as you work; your Forge draft stays here when you follow a link.</p>
      {slack ? <>
        <details className="chat-guide-step" open><summary>1. Create the Slack app</summary>
          <ol>
            <li>Open <a href="https://api.slack.com/apps" {...external}>Slack Apps</a> and choose <strong>Create New App → From a manifest</strong>.</li>
            <li>Choose the workspace that contains your channel. Select YAML, paste the Forge manifest, review it, and create the app. Your workspace may require an administrator to approve the installation.</li>
          </ol>
          <div className="chat-guide-links"><button type="button" onClick={() => void copyManifest()}>Copy Slack manifest</button><a href={slackManifestUrl} download="forge-slack-app-manifest.yaml">Download manifest</a></div>
          {copyStatus && <p role="status">{copyStatus}</p>}
          <details><summary>View manifest YAML</summary><pre tabIndex={0}>{slackManifest}</pre></details>
          <p>The manifest enables Socket Mode, interactive buttons, public-channel message events, and the permissions Forge needs. <a href="https://docs.slack.dev/app-manifests/configuring-apps-with-app-manifests/" {...external}>Slack manifest help</a></p>
        </details>
        <details className="chat-guide-step"><summary>2. Find your workspace and app IDs</summary>
          <ol>
            <li>Open your Slack workspace in a browser. Copy the part beginning with <code>T</code> from its address into <strong>Workspace ID</strong>.</li>
            <li>In the app settings, open <strong>Basic Information</strong>, find <strong>App Credentials → App ID</strong>, and copy the ID beginning with <code>A</code> into <strong>App ID</strong>.</li>
          </ol>
          <figure className="chat-id-example"><code>app.slack.com/client/<mark>T012ABCDEF</mark>/C012CHANNEL</code><figcaption>The highlighted example is the workspace ID. Use the ID from your own workspace.</figcaption></figure>
          <SetupScreenshot src={slackBasicInformation} alt="Slack app settings with Basic Information selected and the App Credentials section below it" caption="Basic Information → App Credentials" />
          <p><a href="https://slack.com/help/articles/221769328-Locate-your-Slack-URL-or-ID" {...external}>Find a Slack workspace ID</a></p>
        </details>
        <details className="chat-guide-step"><summary>3. Install the app and copy its bot token</summary>
          <ol>
            <li>In the app settings, open <strong>OAuth &amp; Permissions</strong> and select <strong>Install to Workspace</strong>. Approve the requested permissions.</li>
            <li>Copy the <strong>Bot User OAuth Token</strong> beginning with <code>xoxb-</code> into Forge’s <strong>Bot token</strong> field.</li>
          </ol>
          <SetupScreenshot src={slackBotToken} alt="Slack OAuth and Permissions screen with the Bot User OAuth Access Token and Copy button highlighted; example token is blurred" caption="Copy the highlighted bot token into Bot token" />
        </details>
        <details className="chat-guide-step"><summary>4. Generate the app token and enable Socket Mode</summary>
          <ol>
            <li>Open <strong>Basic Information → App-Level Tokens → Generate Token and Scopes</strong>.</li>
            <li>Name the token, add <code>connections:write</code>, and generate it. Paste the resulting <code>xapp-</code> value into Forge’s <strong>App token</strong> field.</li>
            <li>Open <strong>Socket Mode</strong> and verify it is enabled. In <strong>Interactivity &amp; Shortcuts</strong>, verify interactivity is enabled.</li>
          </ol>
          <p>Forge uses the bot token to post and the app token to receive replies and button clicks. <a href="https://docs.slack.dev/apis/events-api/using-socket-mode/" {...external}>Socket Mode setup</a></p>
        </details>
        <details className="chat-guide-step"><summary>5. Give the app access to your channel</summary>
          <ol>
            <li>Open your chosen channel in Slack, open its details, and add the Forge app under <strong>Integrations → Add apps</strong>.</li>
            <li>For a public channel, keep <strong>Include private channels</strong> unchecked.</li>
            <li>For a private channel, add the manifest’s <code>groups:read</code> and <code>groups:history</code> permissions and <code>message.groups</code> event, reinstall the app, invite it to that channel, and check <strong>Include private channels</strong> in Forge.</li>
          </ol>
          <p>The bot must be a member of the channel. If the picker is empty, confirm membership and that the channel is not archived.</p>
        </details>
      </> : <>
        <details className="chat-guide-step" open><summary>1. Create a bot with BotFather</summary>
          <ol>
            <li>Open the official <a href="https://t.me/BotFather" {...external}>@BotFather</a> in Telegram and send <code>/newbot</code>.</li>
            <li>Follow the prompts for a display name and a unique username ending in <code>bot</code>.</li>
            <li>Copy the full token BotFather returns into Forge’s <strong>Bot token</strong> field. Treat it as a password.</li>
          </ol>
          <p><a href="https://core.telegram.org/bots/tutorial#obtain-your-bot-token" {...external}>Telegram’s BotFather tutorial</a></p>
        </details>
        <details className="chat-guide-step"><summary>2. Copy the bot ID into Forge</summary>
          <p>The numeric part before the colon in the token is the <strong>Bot ID</strong>. The token field needs the whole value, including the colon and the secret after it.</p>
          <figure className="chat-setup-figure"><img src={telegramTokenMap} alt="Illustration: an example token 123456789 followed by a colon and a hidden secret maps to Bot ID 123456789 and the full Bot token" width="720" height="220" loading="lazy" /><figcaption>Illustration with example values. Enter your own bot’s ID and token.</figcaption></figure>
          <p>Use a recognizable connection name, such as “Forge operations bot”.</p>
        </details>
        <details className="chat-guide-step"><summary>3. Prepare a Telegram group</summary>
          <ol>
            <li>Add the bot to the group you want to use for Forge.</li>
            <li>For one conversation per story, enable <strong>Topics</strong> in the group settings and make the bot an administrator with <strong>Manage Topics</strong> permission.</li>
            <li>After the connection is active, send a message in the group so Forge can discover it. You can also add the group by its numeric chat ID.</li>
          </ol>
          <p><a href="https://core.telegram.org/bots/faq#what-messages-will-my-bot-get" {...external}>Telegram group access help</a></p>
        </details>
      </>}
      <details className="chat-guide-step"><summary>Finish in Forge: save, choose a channel, and verify</summary>
        <ol>
          <li>Fill in <strong>Connection details</strong> and <strong>Credentials</strong> below. Select the products that may use this connection.</li>
          <li>Check <strong>Enable connection</strong> and select <strong>Save connection</strong>. To finish later, save with Enable connection unchecked.</li>
          <li>Select <strong>Discover or add channel</strong>, choose the channel or enter its ID, and select <strong>Save channel</strong>.</li>
          <li>On the story row, choose the channel and select <strong>Save and enable</strong>. This saves Chat settings for that story and its children.</li>
          <li>In Chat settings, use <strong>Send test message</strong>, then reply <code>check</code> to that message within ten minutes to verify reception.</li>
          <li>For remote actions, use <strong>Link or re-link my account</strong>, confirm your own chat user ID, and complete the displayed challenge from that account. Question text also requires the product’s redacted-content permission.</li>
        </ol>
        <p>If Forge shows a legacy-engine notice, configuration can be saved, but a host administrator must activate the plugin engine before delivery starts.</p>
      </details>
    </div>
  </details>;
}