// -----------------------------------------------------------------------
// Monaco-based template editor with {placeholder} IntelliSense.
// Provides autocomplete for inbound fields when user types "{".
// -----------------------------------------------------------------------

import * as monaco from 'monaco-editor';
import type { InboundField } from './inboundSchema';

const LANG_ID = 'workflow-template';
let langRegistered = false;

/** Active editor instances keyed by container element ID */
const activeEditors = new Map<string, monaco.editor.IStandaloneCodeEditor>();

/** Per-editor inbound fields (keyed by model URI) */
const fieldsByUri = new Map<string, InboundField[]>();

function registerTemplateLanguage() {
  if (langRegistered) return;
  langRegistered = true;

  // Register a minimal language for message templates
  monaco.languages.register({ id: LANG_ID });

  // Set word pattern to support dotted paths like "ai.inputTokens"
  monaco.languages.setLanguageConfiguration(LANG_ID, {
    wordPattern: /[\w.]+/,
    brackets: [['{', '}']],
    autoClosingPairs: [],  // don't auto-close — we insert "}" via completion
  });

  // Tokenizer: highlight {placeholders} distinctly
  monaco.languages.setMonarchTokensProvider(LANG_ID, {
    tokenizer: {
      root: [
        [/\{[\w.]+\}/, 'placeholder'],
        [/[^{]+/, 'string'],
        [/\{/, 'delimiter.bracket'],
      ],
    },
  });

  // Theme rules for placeholder highlighting
  monaco.editor.defineTheme('template-dark', {
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: 'placeholder', foreground: '4fc1ff', fontStyle: 'bold' },
      { token: 'string', foreground: 'cccccc' },
    ],
    colors: {},
  });
  monaco.editor.defineTheme('template-light', {
    base: 'vs',
    inherit: true,
    rules: [
      { token: 'placeholder', foreground: '0070c1', fontStyle: 'bold' },
      { token: 'string', foreground: '333333' },
    ],
    colors: {},
  });

  // Completion provider triggered by "{" and "."
  monaco.languages.registerCompletionItemProvider(LANG_ID, {
    triggerCharacters: ['{', '.'],
    provideCompletionItems(model, position) {
      const uri = model.uri.toString();
      const fields = fieldsByUri.get(uri) || [];
      if (fields.length === 0) return { suggestions: [] };

      const textUntilPosition = model.getValueInRange({
        startLineNumber: position.lineNumber,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      });

      // Check if we're inside a {…} placeholder
      const braceMatch = textUntilPosition.match(/\{([\w.]*)$/);
      if (!braceMatch) return { suggestions: [] };

      const fragment = braceMatch[1]; // text after "{"
      const braceCol = position.column - fragment.length;

      // Replace range covers from after "{" to current cursor
      const range: monaco.IRange = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: braceCol,
        endColumn: position.column,
      };

      const sourceIcons: Record<string, monaco.languages.CompletionItemKind> = {
        error: monaco.languages.CompletionItemKind.Event,
        output: monaco.languages.CompletionItemKind.Field,
        context: monaco.languages.CompletionItemKind.Variable,
      };

      const suggestions: monaco.languages.CompletionItem[] = fields.map(f => ({
        label: f.path,
        kind: sourceIcons[f.source] || monaco.languages.CompletionItemKind.Field,
        detail: `${f.type} · ${f.sourceNode}`,
        documentation: `Source: ${f.sourceNode} (${f.sourcePort || 'out'})\nPath: ${f.path}`,
        insertText: `${f.path}}`,  // insert path + closing brace
        range,
        sortText: f.source === 'output' ? `0_${f.path}` : f.source === 'error' ? `1_${f.path}` : `2_${f.path}`,
      }));

      return { suggestions };
    },
  });
}

/**
 * Create or update a Monaco template editor in a container element.
 * @param containerId DOM element ID to host the editor.
 * @param value Initial template content.
 * @param fields Available inbound fields for autocomplete.
 * @param onChange Callback when content changes.
 * @returns Cleanup function to dispose the editor.
 */
export function mountTemplateEditor(
  containerId: string,
  value: string,
  fields: InboundField[],
  onChange: (val: string) => void,
): () => void {
  registerTemplateLanguage();

  // Dispose existing editor
  const existing = activeEditors.get(containerId);
  if (existing) {
    fieldsByUri.delete(existing.getModel()?.uri.toString() || '');
    existing.dispose();
    activeEditors.delete(containerId);
  }

  const container = document.getElementById(containerId);
  if (!container) return () => {};

  const isDark = document.documentElement.getAttribute('data-theme') !== 'light';

  const editor = monaco.editor.create(container, {
    value,
    language: LANG_ID,
    theme: isDark ? 'template-dark' : 'template-light',
    minimap: { enabled: false },
    fontSize: 12,
    fontFamily: "'JetBrains Mono', 'Fira Code', Consolas, monospace",
    lineNumbers: 'off',
    scrollBeyondLastLine: false,
    automaticLayout: true,
    tabSize: 2,
    wordWrap: 'on',
    renderLineHighlight: 'none',
    padding: { top: 6, bottom: 6 },
    scrollbar: { verticalScrollbarSize: 6, horizontalScrollbarSize: 6 },
    suggest: { showKeywords: false, showSnippets: false },
    quickSuggestions: { other: true, comments: false, strings: true },
    autoClosingBrackets: 'never',
    overviewRulerLanes: 0,
    folding: false,
    glyphMargin: false,
    lineDecorationsWidth: 4,
    lineNumbersMinChars: 0,
  });

  // Store fields for completion provider lookup
  const uri = editor.getModel()?.uri.toString() || '';
  fieldsByUri.set(uri, fields);

  editor.onDidChangeModelContent(() => {
    onChange(editor.getValue());
  });

  activeEditors.set(containerId, editor);

  return () => {
    fieldsByUri.delete(uri);
    editor.dispose();
    activeEditors.delete(containerId);
  };
}
