'use strict';
// extract-icm.js — Extract ICM incident content via Playwright + Turndown

const { chromium } = require('playwright');
const TurndownService = require('turndown');
const fs = require('fs');
const path = require('path');
const os = require('os');

// ---------------------------------------------------------------------------
// Argument parsing  (--key value ...)
// ---------------------------------------------------------------------------
const opts = {};
for (let i = 2; i < process.argv.length; i++) {
    const arg = process.argv[i];
    if (arg.startsWith('--')) {
        const key = arg.slice(2);
        const val = process.argv[i + 1];
        if (val && !val.startsWith('--')) { opts[key] = val; i++; }
        else opts[key] = 'true';
    }
}

const INCIDENT_ID = opts.incidentId;
const OUTPUT_BASE = opts.outputBase || 'E:\\icms';
const HEADLESS    = opts.headless === 'true';

// ---------------------------------------------------------------------------
// HTML → Markdown
// ---------------------------------------------------------------------------
const td = new TurndownService({ headingStyle: 'atx', bulletListMarker: '-', codeBlockStyle: 'fenced' });

td.addRule('removeChrome', {
    filter: ['nav', 'button', 'header', 'footer', 'aside', 'script', 'style'],
    replacement: () => '',
});

td.addRule('preservePre', {
    filter: 'pre',
    replacement: (_content, node) => `\n\n\`\`\`\n${(node.textContent || '').trim()}\n\`\`\`\n\n`,
});

function toMarkdown(html) {
    if (!html) return '';
    try {
        return td.turndown(html).replace(/\n{3,}/g, '\n\n').trim();
    } catch {
        return html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
function sanitizeName(s) {
    if (!s || !s.trim()) return 'Unknown';
    return s.trim()
        // Keep only ASCII printable characters (strip all Unicode incl. PUA, zero-width, etc.)
        .replace(/[^\x20-\x7e]/g, '')
        // Replace filesystem-invalid chars
        .replace(/[<>:"/\\|?*]/g, '_')
        // Collapse whitespace to underscore
        .replace(/\s+/g, '_')
        .replace(/_{2,}/g, '_')
        .replace(/[._]+$/, '')
        .substring(0, 64)
        .trim()
        || 'Unknown';
}

function fileKb(p) {
    try { return Math.round(fs.statSync(p).size / 1024); } catch { return 0; }
}

async function waitReady(page) {
    await page.waitForLoadState('domcontentloaded');
    try { await page.waitForLoadState('networkidle', { timeout: 20000 }); } catch { /* ignore */ }
    await page.waitForTimeout(2500);
}

// ---------------------------------------------------------------------------
// Content extraction from the active tab panel
// ---------------------------------------------------------------------------
async function getTabContent(page) {
    // Strategy 1: try innerText from tabpanel (works if content is in open shadow DOM)
    const innerTextResult = await page.evaluate(() => {
        for (const sel of [
            '[role="tabpanel"]',
            '[id="incidentPivotsContent"]',
            '[role="main"]',
            'main',
        ]) {
            const el = document.querySelector(sel);
            const text = el && el.innerText;
            if (text && text.trim().length > 200) {
                return text.trim();
            }
        }
        return null;
    });

    // Strategy 2: recursive shadow root traversal (works for open shadow DOMs)
    const shadowText = await page.evaluate(() => {
        function extractText(root, depth) {
            if (depth > 20) return '';
            let text = '';
            if (root.nodeType === Node.TEXT_NODE) {
                const t = root.textContent.trim();
                if (t) text += t + '\n';
            }
            // Open shadow root traversal
            if (root.shadowRoot) {
                text += extractText(root.shadowRoot, depth + 1);
            }
            for (const child of (root.childNodes || [])) {
                text += extractText(child, depth + 1);
            }
            return text;
        }
        const panel = document.querySelector('[role="tabpanel"]') || document.body;
        return extractText(panel, 0);
    });

    // Strategy 3: accessibility tree (Playwright API - traverses all shadow DOMs)
    let a11yText = '';
    try {
        const tabPanel = page.locator('[role="tabpanel"]').first();
        a11yText = await tabPanel.innerText({ timeout: 5000 }).catch(() => '');
    } catch { /* ignore */ }

    // Pick the result with the most content
    const candidates = [innerTextResult || '', shadowText || '', a11yText || ''];
    const best = candidates.reduce((a, b) => a.length >= b.length ? a : b, '');

    return best.replace(/\n{3,}/g, '\n\n').trim();
}

// ---------------------------------------------------------------------------
// Split full-page inner text into named ICM sections
// ---------------------------------------------------------------------------
function splitSections(text) {
    // Section headings as they appear in the ICM tabpanel innerText
    const sectionRe = /^(Authored summary|Discussion|Root Cause Analysis|Post-Incident Review|Authored RCA)$/m;
    const toolbarRe = /^(Edit|Fullscreen|Collapse|Expand|Refresh|Add comment)\s*$/m;

    const parts = text.split(sectionRe);
    const result = {};

    // parts: [before, heading1, body1, heading2, body2, ...]
    for (let i = 1; i + 1 < parts.length; i += 2) {
        const key = parts[i].toLowerCase().replace(/[\s-]+/g, '_');
        let body = (parts[i + 1] || '').trim();
        // Strip leading toolbar lines
        body = body.replace(/^(?:(?:Edit|Fullscreen|Collapse|Expand|Refresh|Add comment)\s*\n)+/m, '').trim();
        result[key] = body;
    }

    return result;
}

// ---------------------------------------------------------------------------
// Metadata extraction
// ---------------------------------------------------------------------------

// DOM-only extraction: title, status, severity, service, team, owner
async function extractDomMetadata(page) {
    return page.evaluate(() => {
        // Title: h2[contenteditable="true"] in the incident header
        const titleEl = document.querySelector('h2[contenteditable="true"]') ||
                        document.querySelector('h2[aria-label="editable-input"]');
        const title = titleEl ? titleEl.textContent.trim() : '';

        // Status: span[data-testid="incident-lifecycle"]
        const statusEl = document.querySelector('span[data-testid="incident-lifecycle"]');
        const status = statusEl ? statusEl.textContent.trim() : '';

        // Severity: button whose aria-label starts with "Severity"
        const sevBtn = document.querySelector('button[aria-label^="Severity"]');
        const severity = sevBtn ? sevBtn.getAttribute('aria-label') : '';

        const findByLabel = (labelText) => {
            const lower = labelText.toLowerCase();
            for (const el of Array.from(document.querySelectorAll('*'))) {
                if (el.children.length === 0 && el.textContent.trim().toLowerCase() === lower) {
                    const next = el.nextElementSibling;
                    if (next) return next.textContent.trim();
                    const parentNext = el.parentElement && el.parentElement.nextElementSibling;
                    if (parentNext) return parentNext.textContent.trim();
                }
            }
            return '';
        };

        const service = findByLabel('Owning Service') || findByLabel('Service');
        const team    = findByLabel('Owning Team')    || findByLabel('Team');
        const owner   = findByLabel('Owner');

        return { title, status, severity, service, team, owner };
    });
}

// Text-based extraction: fields embedded in Shadow DOM content — use the already-extracted full text
function extractTextMetadata(fullText, title) {
    const re = (...pats) => {
        for (const p of pats) {
            const m = fullText.match(p);
            if (m) return m[1].trim().replace(/^['"\s]+|['"\s]+$/g, '');
        }
        return '';
    };

    // AEODeviceARMResourceUri — ARM resource URI for HCI stamps
    const aeoUri = re(
        /AEODeviceARMResourceUri\s*:\s*'([^'\n\r|,]{10,})'/i,
        /AEODeviceARMResourceUri\s*:\s*"([^"\n\r|,]{10,})"/i,
        /AEODeviceARMResourceUri\s*:\s*(\/[Ss]ubscriptions\/[^\s\n\r|,]{10,})/i,
    );

    // AEORegion — from incident title [AzureReg:xxx], or from body text
    const aeoRegion = ((title || '').match(/\[AzureReg:([^\]]+)\]/) || [])[1] ||
        re(/AEORegion\s*:\s*'?([a-z][a-z0-9]+)'?/i);

    // ImpactStartTime — from the ICM Q&A JSON block embedded in the page text
    const impactStart = re(
        /"ControlId"\s*:\s*"problem_start_time"[^}]*?"AnswerValue"\s*:\s*"([^"]+)"/,
        /"Answer"\s*:\s*"(\d{4}-\d{2}-\d{2}T[^"]+)"[^}]*?"ControlId"\s*:\s*"problem_start_time"/,
        /ImpactStartTime\s*:\s*(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[^\s|]*)/i,
    );

    // Kusto fields (Hub incidents)
    const kustoCluster = re(
        /Kusto\s+Cluster\s*:\s*([^\s\n,|]{10,})/i,
        /cluster\('([^']+\.kusto\.windows\.net[^']*)'\)/i,
    );
    const kustoDb = re(
        /Kusto\s+Database\s*:\s*([^\s\n,|]{3,})/i,
        /\.database\('([^']+)'\)/i,
    );

    return { aeoUri, aeoRegion, impactStart, kustoCluster, kustoDb };
}

// ---------------------------------------------------------------------------
// Per-incident extraction
// ---------------------------------------------------------------------------
async function processIncident(page, incidentId) {
    const base = `https://portal.microsofticm.com/imp/v5/incidents/details/${incidentId}`;

    console.log(`Opening incident ${incidentId}...`);
    await page.goto(`${base}/summary`, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await waitReady(page);

    // Auth check: we need to be on the /imp/ path, not the /sso2 sign-in page.
    if (!page.url().includes('microsofticm.com/imp')) {
        console.log(`Authentication required (URL: ${page.url()})`);
        // Try to auto-click the Entra ID (AAD) sign-in link if present
        try {
            const signInLink = await page.$('a[href*="identityProvider=EntraID"]');
            if (signInLink) {
                console.log('Clicking "Sign in with Microsoft Entra ID"...');
                await signInLink.click();
                await page.waitForTimeout(2000);
            }
        } catch { /* best-effort */ }
        console.log('Please complete sign-in in the browser window (including any MFA). Waiting up to 5 minutes...');
        try {
            await page.waitForURL(u => u.includes('microsofticm.com/imp'), { timeout: 300000 });
        } catch {
            throw new Error(`Authentication timed out. Final URL: ${page.url()}`);
        }
        await waitReady(page);
    }

    // --- Extract full tabpanel content first (shared for metadata + section splitting) ---
    console.log('Extracting page content...');
    const fullText = await getTabContent(page);
    console.log(`  (extracted ${fullText.length} chars)`);

    // --- Metadata (DOM elements + text-based regex on the already-extracted content) ---
    console.log('Extracting Metadata...');
    const domMeta  = await extractDomMetadata(page);
    const textMeta = extractTextMetadata(fullText, domMeta.title);
    const meta     = { ...domMeta, ...textMeta };

    const service = sanitizeName(meta.service);
    const team    = sanitizeName(meta.team);
    const outDir  = path.join(OUTPUT_BASE, service, team, incidentId.toString());
    fs.mkdirSync(outDir, { recursive: true });

    const metaFile = path.join(outDir, 'metadata.md');
    fs.writeFileSync(metaFile, [
        `# Incident ${incidentId} Metadata`,
        '',
        '| Field | Value |',
        '|-------|-------|',
        `| IncidentId | ${incidentId} |`,
        `| Title | ${meta.title      || ''} |`,
        `| Status | ${meta.status    || ''} |`,
        `| Severity | ${meta.severity || ''} |`,
        `| Service | ${meta.service  || ''} |`,
        `| Team | ${meta.team       || ''} |`,
        `| Owner | ${meta.owner     || ''} |`,
        `| AEODeviceARMResourceUri | ${meta.aeoUri       || ''} |`,
        `| AEORegion | ${meta.aeoRegion     || ''} |`,
        `| KustoCluster | ${meta.kustoCluster  || ''} |`,
        `| KustoDatabase | ${meta.kustoDb      || ''} |`,
        `| ImpactStartTime | ${meta.impactStart  || ''} |`,
        '',
    ].join('\n'), 'utf8');
    console.log('  metadata.md done');

    // --- Split the full text into sections by heading ---
    const sections = splitSections(fullText);

    const sumContent  = sections['authored_summary'] || sections['summary'] || fullText;
    const discContent = sections['discussion'] || '';

    const sumFile = path.join(outDir, 'summary.md');
    fs.writeFileSync(sumFile, `# Incident ${incidentId} Summary\n\n${sumContent}\n`, 'utf8');
    console.log(`  summary.md done (${fileKb(sumFile)} KB)`);

    const discFile = path.join(outDir, 'discussion.md');
    fs.writeFileSync(discFile, `# Incident ${incidentId} Discussion\n\n${discContent}\n`, 'utf8');
    console.log(`  discussion.md done (${fileKb(discFile)} KB)`);

    // --- RCA (try multiple URL suffixes, look for content different from the summary page) ---
    console.log('Extracting RCA...');
    let rcaContent = sections['root_cause_analysis'] || sections['authored_rca'] || sections['post_incident_review'] || '';
    if (!rcaContent) {
        for (const suffix of ['rootcause', 'postincidentreview', 'rca', 'root-cause']) {
            try {
                await page.goto(`${base}/${suffix}`, { waitUntil: 'domcontentloaded', timeout: 30000 });
                await waitReady(page);
                const u = page.url();
                if (!/404|error/i.test(u) && u.includes('microsofticm.com')) {
                    const rcaText = await getTabContent(page);
                    const rcaSecs = splitSections(rcaText);
                    rcaContent = rcaSecs['root_cause_analysis'] || rcaSecs['authored_rca'] ||
                                 rcaSecs['post_incident_review'] || rcaText;
                    break;
                }
            } catch { /* try next suffix */ }
        }
    }
    const rcaFile = path.join(outDir, 'rca.md');
    fs.writeFileSync(rcaFile, `# Incident ${incidentId} Root Cause Analysis\n\n${rcaContent}\n`, 'utf8');
    console.log(`  rca.md done (${fileKb(rcaFile)} KB)`);

    // --- Final summary ---
    console.log(`\nSaved to: ${outDir}`);
    console.log(`  - summary.md (${fileKb(sumFile)} KB)`);
    console.log(`  - discussion.md (${fileKb(discFile)} KB)`);
    console.log(`  - rca.md (${fileKb(rcaFile)} KB)`);
    console.log(`  - metadata.md (${fileKb(metaFile)} KB)`);
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------
async function main() {
    if (!INCIDENT_ID) {
        console.error('Error: --incidentId is required');
        process.exit(1);
    }

    const localAppData      = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    const edgeProfile       = path.join(localAppData, 'Microsoft', 'Edge', 'User Data');
    const playwrightProfile = path.join(localAppData, 'Playwright', 'open-icm');

    let context;

    // 1. Try user's main Edge profile (reuses existing auth session)
    try {
        context = await chromium.launchPersistentContext(edgeProfile, {
            channel: 'msedge',
            headless: HEADLESS,
            args: ['--profile-directory=Default', '--no-first-run', '--no-default-browser-check'],
            timeout: 30000,
        });
    } catch (e1) {
        // 2. Dedicated Playwright profile (persists session across runs, never conflicts with running Edge)
        console.log(`Note: Edge profile busy (${e1.message}). Using dedicated Playwright profile...`);
        fs.mkdirSync(playwrightProfile, { recursive: true });
        try {
            context = await chromium.launchPersistentContext(playwrightProfile, {
                channel: 'msedge',
                headless: HEADLESS,
                args: ['--no-first-run'],
                timeout: 30000,
            });
        } catch (e2) {
            // 3. Plain fresh launch — no session persistence
            console.log('Note: Using fresh Edge instance (you may need to sign in)...');
            const browser = await chromium.launch({ channel: 'msedge', headless: HEADLESS, timeout: 30000 });
            context = await browser.newContext();
        }
    }

    const page = await context.newPage();
    page.setDefaultTimeout(60000);

    try {
        await processIncident(page, INCIDENT_ID);
    } catch (err) {
        console.error('\nError:', err.message);
        if (err.stack) console.error(err.stack);
        process.exit(1);
    } finally {
        await context.close().catch(() => {});
    }
}

main().catch(err => { console.error('Fatal:', err.message); process.exit(1); });
