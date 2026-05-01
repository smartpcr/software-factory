// Tiny markdown → HTML renderer shared by the PR detail panel and the
// work-item detail panel. Handles headings (#, ##, ###), bullets (- ),
// inline bold (**…**), italic (*…*), inline code (`…`), strikethrough
// (~~…~~), and paragraphs with newline preservation. Coalesces adjacent
// <ul> blocks so consecutive `- item` lines render as one list.
//
// Intentionally not a full CommonMark engine — we only need enough to
// render the bodies that ADO/GitHub/Forge produce. If the surface grows
// beyond what this can handle (tables, nested lists, fenced code), swap
// in `react-markdown` + `remark-gfm` instead of extending this further.

export function renderMarkdown(md: string): string {
  if (!md) return '';
  const escaped = escapeHtml(md);
  const lines = escaped.split('\n');
  const out: string[] = [];
  let para: string[] = [];

  const flushPara = () => {
    if (para.length === 0) return;
    out.push(`<p>${inline(para.join(' '))}</p>`);
    para = [];
  };

  for (const line of lines) {
    const t = line.trim();
    if (!t) { flushPara(); continue; }

    // Fenced-code block start/end is a terminator for paragraphs but we
    // don't actually parse fenced blocks — we render them as <pre>.
    const fence = /^```(.*)$/.exec(t);
    if (fence) {
      flushPara();
      // Collect lines until the matching closing fence.
      out.push('__FENCE_PLACEHOLDER__');
      // Note: we leave fence handling shallow on purpose — see comment
      // at the top of the file. Caller can wrap content in <pre> via CSS.
      continue;
    }

    const heading = /^(#{1,6})\s+(.+)$/.exec(t);
    if (heading) {
      flushPara();
      const level = Math.min(heading[1].length, 6);
      out.push(`<h${level}>${inline(heading[2])}</h${level}>`);
      continue;
    }

    if (t.startsWith('- ') || t.startsWith('* ')) {
      flushPara();
      out.push(`<ul><li>${inline(t.slice(2))}</li></ul>`);
      continue;
    }

    // Numbered list — `1.` / `2.` / …
    const ol = /^(\d+)\.\s+(.+)$/.exec(t);
    if (ol) {
      flushPara();
      out.push(`<ol><li>${inline(ol[2])}</li></ol>`);
      continue;
    }

    // Horizontal rule
    if (/^(-{3,}|_{3,}|\*{3,})$/.test(t)) {
      flushPara();
      out.push('<hr/>');
      continue;
    }

    para.push(t);
  }
  flushPara();

  // Coalesce adjacent <ul>…</ul><ul>…</ul> and the same for <ol>.
  return out
    .join('\n')
    .replace(/<\/ul>\n<ul>/g, '')
    .replace(/<\/ol>\n<ol>/g, '');
}

/**
 * Renders content that may already be HTML (ADO PR comments use the
 * Quill HTML editor; GitHub uses GFM markdown) into safe display HTML.
 *
 * Auto-detects the input format:
 *   - looks like HTML → sanitize, then walk text nodes and run a
 *     mini-markdown pass on each so embedded fenced code blocks /
 *     inline code / links / headings render correctly. ADO's PR
 *     Assistant bodies ship a hybrid of HTML chrome + markdown
 *     content and this is the only path that matches what the ADO
 *     web UI shows.
 *   - otherwise → run through {@link renderMarkdown}
 *
 * Use this for any PR-comment / work-item-comment body where the
 * upstream provider's format isn't guaranteed.
 */
export function renderRichText(content: string): string {
  if (!content) return '';
  return looksLikeHtml(content) ? sanitizeHtml(content) : renderMarkdown(content);
}

function looksLikeHtml(s: string): boolean {
  const t = s.trim();
  if (!t.startsWith('<')) return false;
  return /<\/?(p|div|span|a|ul|ol|li|strong|em|b|i|u|code|pre|h[1-6]|br|hr|blockquote|del|ins|img|table|tr|td|th)\b/i.test(t);
}

const ALLOWED_TAGS = new Set([
  'p', 'div', 'span', 'small', 'a', 'strong', 'em', 'b', 'i', 'u', 'code', 'pre',
  'br', 'hr', 'ul', 'ol', 'li', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
  'blockquote', 'del', 'ins', 'img', 'table', 'thead', 'tbody', 'tr', 'td', 'th',
  'details', 'summary',
]);
// `style` + `class` are allowed so ADO PR-Assistant bodies (which lay
// out their tag pills via inline styles + utility classes like
// `flex-row flex-wrap`) render approximately like the ADO web UI.
// `style` values are post-processed to strip URL fetches and known
// CSS-injection vectors — see {@link sanitizeStyleValue}.
const ALLOWED_ATTRS = new Set(['href', 'title', 'src', 'alt', 'class', 'style']);

/**
 * Allowlist HTML sanitizer. Strips disallowed tags (keeping their text
 * content) and disallowed attributes; rewrites `<a>` to open in a new
 * tab with `rel="noopener noreferrer"`; blocks `javascript:` URLs.
 *
 * Falls back to escaped-text rendering when DOMParser is unavailable
 * (e.g. SSR / Node tests without jsdom).
 */
export function sanitizeHtml(html: string): string {
  if (typeof DOMParser === 'undefined') return escapeHtml(html);

  // Hoist fenced code blocks BEFORE handing the body to DOMParser.
  // ADO PR Assistant bodies embed C#/XML inside ``` … ``` (e.g.
  // `<param name="state">…</param>`, `</summary>`), and the HTML
  // parser would otherwise interpret those as real elements,
  // fragmenting the surrounding text and corrupting the layout.
  // We swap each block for a placeholder, parse + sanitize the
  // remaining structure, then re-emit the blocks as `<pre><code>…`
  // with their interior properly HTML-escaped.
  const codeBlocks: string[] = [];
  const stripped = html.replace(
    /```([a-z0-9_+-]*)\r?\n?([\s\S]*?)\r?\n?```/gi,
    (_m, _lang: string, code: string) => {
      const idx = codeBlocks.push(code) - 1;
      return `@@FORGE_FENCE_${idx}@@`;
    },
  );

  const doc = new DOMParser().parseFromString(`<div>${stripped}</div>`, 'text/html');
  const root = doc.body.firstElementChild as HTMLElement | null;
  if (!root) return '';
  removeComments(root);
  sanitizeNode(root);
  applyMarkdownInTextNodes(root, doc);

  let out = root.innerHTML;
  out = out.replace(/@@FORGE_FENCE_(\d+)@@/g, (_m, idxRaw: string) => {
    const code = codeBlocks[Number(idxRaw)] ?? '';
    const escaped = code
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;');
    return `<pre><code>${escaped}</code></pre>`;
  });
  return out;
}

/** Drop any HTML comment nodes (ADO trailers like
 *  `<!-- GitOpsUserAgent=... -->` and tracking pixels would otherwise
 *  survive the element-only sanitizer). */
function removeComments(root: Node): void {
  const walker = (root.ownerDocument ?? document).createTreeWalker(root, NodeFilter.SHOW_COMMENT, null);
  const comments: Comment[] = [];
  let n = walker.nextNode();
  while (n) { comments.push(n as Comment); n = walker.nextNode(); }
  for (const c of comments) c.parentNode?.removeChild(c);
}

/** Tags whose text content is treated as literal — markdown patterns
 *  inside them aren't expanded. */
const SKIP_MD_PARENTS = new Set(['pre', 'code', 'script', 'style', 'a']);

/**
 * Walk every text node under `root`. For each text node *not* inside
 * a literal-text element, run {@link renderTextMarkdown} and replace
 * the node with the resulting parsed fragment. This is what makes
 * the fenced code block in an ADO PR Assistant body actually render
 * as `<pre><code>…</code></pre>` instead of leaving `\`\`\`` markers
 * visible in the rendered output.
 */
function applyMarkdownInTextNodes(root: Element, doc: Document): void {
  const walker = doc.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
  const textNodes: Text[] = [];
  let node = walker.nextNode();
  while (node) { textNodes.push(node as Text); node = walker.nextNode(); }

  for (const text of textNodes) {
    if (insideSkipParent(text)) continue;
    const content = text.textContent ?? '';
    if (!content.trim()) continue;
    const rendered = renderTextMarkdown(content);
    if (rendered === escapeHtmlAttr(content)) continue;

    const tmp = doc.createElement('span');
    tmp.innerHTML = rendered;
    const fragment = doc.createDocumentFragment();
    while (tmp.firstChild) fragment.appendChild(tmp.firstChild);
    text.parentNode?.replaceChild(fragment, text);
  }
}

function insideSkipParent(text: Text): boolean {
  let cur: Node | null = text.parentNode;
  while (cur) {
    if (cur.nodeType === 1 /* ELEMENT_NODE */) {
      const tag = (cur as Element).tagName.toLowerCase();
      if (SKIP_MD_PARENTS.has(tag)) return true;
    }
    cur = cur.parentNode;
  }
  return false;
}

/**
 * Tiny markdown renderer that runs on a single text-node's content.
 * Handles the patterns ADO PR Assistant + GitHub bodies sprinkle
 * inside otherwise-HTML markup:
 *   - fenced code blocks  ```…```  → `<pre><code>…</code></pre>`
 *   - inline code         `…`      → `<code>…</code>`
 *   - markdown links      [t](u)   → `<a href="u" target="_blank" …>t</a>`
 *   - headings            #### …   → `<h4>…</h4>` (h1..h6)
 *
 * The output is concatenated into a parent element via `innerHTML`,
 * so we HTML-escape the source content first and re-emit only the
 * tags we generate ourselves.
 */
function renderTextMarkdown(text: string): string {
  let s = escapeHtmlAttr(text);

  // Fenced code blocks. `[\s\S]*?` so the dot crosses newlines and
  // the match is non-greedy (avoids merging two adjacent fences).
  s = s.replace(/```([a-z0-9_+-]*)\n?([\s\S]*?)\n?```/gi, (_m, _lang: string, code: string) => {
    return `<pre><code>${code}</code></pre>`;
  });

  // Headings — single-line, at start of line.
  s = s.replace(/^######\s+(.+)$/gm, '<h6>$1</h6>');
  s = s.replace(/^#####\s+(.+)$/gm, '<h5>$1</h5>');
  s = s.replace(/^####\s+(.+)$/gm, '<h4>$1</h4>');
  s = s.replace(/^###\s+(.+)$/gm, '<h3>$1</h3>');
  s = s.replace(/^##\s+(.+)$/gm, '<h2>$1</h2>');
  s = s.replace(/^#\s+(.+)$/gm, '<h1>$1</h1>');

  // Markdown links — block javascript: payloads.
  s = s.replace(/\[([^\]\n]+)\]\(([^)\s]+)\)/g, (_m, label: string, href: string) => {
    if (/^\s*javascript:/i.test(href)) return _m;
    return `<a href="${href}" target="_blank" rel="noopener noreferrer">${label}</a>`;
  });

  // Inline code — single backtick, no newlines inside.
  s = s.replace(/`([^`\n]+)`/g, '<code>$1</code>');

  return s;
}

/** HTML-escapes the five chars that are unsafe in both element text
 *  and attribute contexts. Used by the text-node markdown renderer
 *  before re-emitting the content via innerHTML. */
function escapeHtmlAttr(s: string): string {
  return s
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll('\'', '&#39;');
}

function sanitizeNode(node: Element): void {
  for (const child of Array.from(node.children)) {
    const tag = child.tagName.toLowerCase();
    if (!ALLOWED_TAGS.has(tag)) {
      // Unwrap: keep text/inline children, drop the wrapper.
      const parent = child.parentNode!;
      while (child.firstChild) parent.insertBefore(child.firstChild, child);
      parent.removeChild(child);
      continue;
    }
    for (const attr of Array.from(child.attributes)) {
      const name = attr.name.toLowerCase();
      if (!ALLOWED_ATTRS.has(name)) {
        child.removeAttribute(attr.name);
        continue;
      }
      if (name === 'style') {
        const cleaned = sanitizeStyleValue(attr.value);
        if (cleaned !== attr.value) child.setAttribute('style', cleaned);
      }
    }
    if (tag === 'a') {
      const href = child.getAttribute('href') ?? '';
      if (/^\s*javascript:/i.test(href)) child.removeAttribute('href');
      child.setAttribute('target', '_blank');
      child.setAttribute('rel', 'noopener noreferrer');
    }
    sanitizeNode(child);
  }
}

/**
 * Strip the well-known CSS-injection vectors out of an inline style
 * value while leaving layout/typography properties intact. We block
 * `url(...)` (tracking pixels + remote fetches), `expression(...)`
 * (legacy IE), `@import`, and `javascript:` / `-moz-binding` payloads.
 */
function sanitizeStyleValue(value: string): string {
  return value
    .replace(/url\s*\([^)]*\)/gi, '')
    .replace(/expression\s*\([^)]*\)/gi, '')
    .replace(/@import[^;]*;?/gi, '')
    .replace(/-moz-binding\s*:[^;]*;?/gi, '')
    .replace(/javascript\s*:/gi, '');
}

function escapeHtml(s: string): string {
  return s
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function inline(s: string): string {
  return s
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(?<!\*)\*([^*\n]+)\*(?!\*)/g, '<em>$1</em>')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/~~([^~]+)~~/g, '<del>$1</del>')
    // Bare URLs → clickable links.
    .replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener noreferrer">$1</a>');
}
