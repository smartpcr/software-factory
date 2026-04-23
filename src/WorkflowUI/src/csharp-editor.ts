// -----------------------------------------------------------------------
// Monaco-based C# script editor with ExecutionState IntelliSense
// -----------------------------------------------------------------------

import * as monaco from 'monaco-editor';
import { State } from './state';

let registered = false;

/** ExecutionState API members for IntelliSense */
const executionStateMembers = [
  { label: 'Input', kind: monaco.languages.CompletionItemKind.Property, detail: 'Dictionary<string, object>', documentation: 'Input data from the previous node.' },
  { label: 'Output', kind: monaco.languages.CompletionItemKind.Property, detail: 'Dictionary<string, object>', documentation: 'Output data for downstream nodes.' },
  { label: 'GlobalVariables', kind: monaco.languages.CompletionItemKind.Property, detail: 'ConcurrentDictionary<string, object>', documentation: 'Workflow-level global variables.' },
  { label: 'Local', kind: monaco.languages.CompletionItemKind.Property, detail: 'ConcurrentDictionary<string, object>', documentation: 'Local variables for this node execution.' },
  { label: 'GetInput', kind: monaco.languages.CompletionItemKind.Method, detail: 'Func<string, object?>', documentation: 'Get an input value by key. Returns null if not found.', insertText: 'GetInput("${1:key}")', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet },
  { label: 'SetOutput', kind: monaco.languages.CompletionItemKind.Method, detail: 'Action<string, object>', documentation: 'Set an output value for downstream nodes.', insertText: 'SetOutput("${1:key}", ${2:value})', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet },
  { label: 'GetGlobal', kind: monaco.languages.CompletionItemKind.Method, detail: 'Func<string, object?>', documentation: 'Get a global variable value. Returns null if not found.', insertText: 'GetGlobal("${1:key}")', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet },
  { label: 'SetGlobal', kind: monaco.languages.CompletionItemKind.Method, detail: 'Action<string, object>', documentation: 'Set a global variable value.', insertText: 'SetGlobal("${1:key}", ${2:value})', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet },
  { label: 'WorkflowContext', kind: monaco.languages.CompletionItemKind.Property, detail: 'WorkflowExecutionContext', documentation: 'The workflow execution context.' },
  { label: 'NodeContext', kind: monaco.languages.CompletionItemKind.Property, detail: 'NodeExecutionContext', documentation: 'The node execution context.' },
];

/** Dictionary member completions */
const dictMembers = [
  { label: 'ContainsKey', kind: monaco.languages.CompletionItemKind.Method, detail: 'bool ContainsKey(string key)', insertText: 'ContainsKey("${1:key}")', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet },
  { label: 'TryGetValue', kind: monaco.languages.CompletionItemKind.Method, detail: 'bool TryGetValue(string key, out object value)', insertText: 'TryGetValue("${1:key}", out var ${2:value})', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet },
  { label: 'Keys', kind: monaco.languages.CompletionItemKind.Property, detail: 'ICollection<string>' },
  { label: 'Values', kind: monaco.languages.CompletionItemKind.Property, detail: 'ICollection<object>' },
  { label: 'Count', kind: monaco.languages.CompletionItemKind.Property, detail: 'int' },
  { label: 'Clear', kind: monaco.languages.CompletionItemKind.Method, detail: 'void Clear()' },
  { label: 'Remove', kind: monaco.languages.CompletionItemKind.Method, detail: 'bool Remove(string key)', insertText: 'Remove("${1:key}")', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet },
];

function registerCSharpCompletions() {
  if (registered) return;
  registered = true;

  monaco.languages.registerCompletionItemProvider('csharp', {
    triggerCharacters: ['.'],
    provideCompletionItems(model, position) {
      const textUntilPosition = model.getValueInRange({
        startLineNumber: position.lineNumber,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      });

      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endColumn: word.endColumn,
      };

      // After a dot: check what precedes it
      const dotMatch = textUntilPosition.match(/(\w+)\.\s*$/);
      if (dotMatch) {
        const obj = dotMatch[1];
        // ExecutionState properties accessed as globals (no prefix needed in Roslyn scripts)
        if (['Input', 'Output', 'GlobalVariables', 'Local'].includes(obj)) {
          return {
            suggestions: dictMembers.map(m => ({ ...m, range })) as monaco.languages.CompletionItem[],
          };
        }
      }

      // Top-level: show ExecutionState members + workflow variables
      const suggestions: monaco.languages.CompletionItem[] = executionStateMembers.map(m => ({
        ...m,
        range,
      })) as monaco.languages.CompletionItem[];

      // Add workflow default variables as completions
      const wf = State.workflows[State.currentWfId || ''];
      if (wf?.defaultVariables) {
        for (const [key, val] of Object.entries(wf.defaultVariables)) {
          suggestions.push({
            label: `GetGlobal("${key}")`,
            kind: monaco.languages.CompletionItemKind.Variable,
            detail: `Workflow variable: ${typeof val}`,
            documentation: `Current value: ${JSON.stringify(val)}`,
            insertText: `GetGlobal("${key}")`,
            range,
          } as monaco.languages.CompletionItem);
        }
      }

      // Common C# snippets for scripts
      suggestions.push(
        { label: 'var', kind: monaco.languages.CompletionItemKind.Keyword, insertText: 'var ${1:name} = ${2:value};', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range, detail: 'Variable declaration' } as monaco.languages.CompletionItem,
        { label: 'if', kind: monaco.languages.CompletionItemKind.Snippet, insertText: 'if (${1:condition})\n{\n    ${2}\n}', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range, detail: 'if statement' } as monaco.languages.CompletionItem,
        { label: 'foreach', kind: monaco.languages.CompletionItemKind.Snippet, insertText: 'foreach (var ${1:item} in ${2:collection})\n{\n    ${3}\n}', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range, detail: 'foreach loop' } as monaco.languages.CompletionItem,
        { label: 'Console.WriteLine', kind: monaco.languages.CompletionItemKind.Method, insertText: 'Console.WriteLine(${1});', insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range, detail: 'void Console.WriteLine(object)' } as monaco.languages.CompletionItem,
      );

      return { suggestions };
    },
  });

  // Placeholder completion: triggered by { for template expressions like {incidentFolder}
  monaco.languages.registerCompletionItemProvider('csharp', {
    triggerCharacters: ['{'],
    provideCompletionItems(model, position) {
      const textUntilPosition = model.getValueInRange({
        startLineNumber: position.lineNumber,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      });

      // Only trigger after an opening brace
      if (!textUntilPosition.endsWith('{')) return { suggestions: [] };

      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endColumn: word.endColumn,
      };

      const suggestions: monaco.languages.CompletionItem[] = [];

      // Add fields from the placeholder fields store
      for (const f of _placeholderFields) {
        suggestions.push({
          label: `{${f.name}}`,
          kind: monaco.languages.CompletionItemKind.Variable,
          detail: `${f.source} · ${f.type}`,
          documentation: `From: ${f.sourceNode}\nPath: ${f.path}`,
          insertText: `${f.name}}`,
          sortText: `0_${f.name}`,
          range,
        } as monaco.languages.CompletionItem);
      }

      // Add workflow default variables
      const wf = State.workflows[State.currentWfId || ''];
      if (wf?.defaultVariables) {
        for (const [key, val] of Object.entries(wf.defaultVariables)) {
          if (suggestions.some(s => s.label === `{${key}}`)) continue;
          suggestions.push({
            label: `{${key}}`,
            kind: monaco.languages.CompletionItemKind.Variable,
            detail: `global · ${typeof val}`,
            documentation: `Default: ${JSON.stringify(val)}`,
            insertText: `${key}}`,
            sortText: `1_${key}`,
            range,
          } as monaco.languages.CompletionItem);
        }
      }

      return { suggestions };
    },
  });
}

/** Placeholder fields available for {placeholder} autocomplete in expression editors */
let _placeholderFields: { name: string; path: string; type: string; source: string; sourceNode: string }[] = [];

/** Set available placeholder fields for expression editor autocomplete */
export function setPlaceholderFields(fields: typeof _placeholderFields) {
  _placeholderFields = fields;
}

/** Active editor instances keyed by container element ID */
const activeEditors = new Map<string, monaco.editor.IStandaloneCodeEditor>();

/**
 * Create or update a Monaco C# editor in a container element.
 * @param containerId The DOM element ID to host the editor.
 * @param value Initial script content.
 * @param onChange Callback when content changes.
 * @returns Cleanup function to dispose the editor.
 */
export function mountCSharpEditor(
  containerId: string,
  value: string,
  onChange: (val: string) => void,
): () => void {
  registerCSharpCompletions();

  // Dispose existing editor in this container
  const existing = activeEditors.get(containerId);
  if (existing) {
    existing.dispose();
    activeEditors.delete(containerId);
  }

  const container = document.getElementById(containerId);
  if (!container) return () => {};

  // Determine theme from document
  const isDark = document.documentElement.getAttribute('data-theme') !== 'light';

  const editor = monaco.editor.create(container, {
    value,
    language: 'csharp',
    theme: isDark ? 'vs-dark' : 'vs',
    minimap: { enabled: false },
    fontSize: 12,
    fontFamily: "'JetBrains Mono', 'Fira Code', Consolas, monospace",
    lineNumbers: 'on',
    scrollBeyondLastLine: false,
    automaticLayout: true,
    tabSize: 4,
    wordWrap: 'on',
    renderLineHighlight: 'line',
    padding: { top: 8, bottom: 8 },
    scrollbar: { verticalScrollbarSize: 8, horizontalScrollbarSize: 8 },
    suggest: { showKeywords: true, showSnippets: true },
    quickSuggestions: { other: true, comments: false, strings: true },
  });

  editor.onDidChangeModelContent(() => {
    onChange(editor.getValue());
  });

  activeEditors.set(containerId, editor);

  return () => {
    editor.dispose();
    activeEditors.delete(containerId);
  };
}

/**
 * Create or update a Monaco JSON editor in a container element.
 * Monaco provides built-in JSON validation (syntax errors, trailing commas, etc.).
 * @param containerId The DOM element ID to host the editor.
 * @param value Initial JSON content.
 * @param onChange Callback when content changes.
 * @returns Cleanup function to dispose the editor.
 */
export function mountJsonEditor(
  containerId: string,
  value: string,
  onChange: (val: string) => void,
): () => void {
  // Dispose existing editor in this container
  const existing = activeEditors.get(containerId);
  if (existing) {
    existing.dispose();
    activeEditors.delete(containerId);
  }

  const container = document.getElementById(containerId);
  if (!container) return () => {};

  const isDark = document.documentElement.getAttribute('data-theme') !== 'light';

  // Configure JSON language defaults for validation
  const jsonDefaults = (monaco.languages as unknown as Record<string, unknown>).json as unknown as { setDiagnosticsOptions: (opts: Record<string, unknown>) => void };
  if (jsonDefaults?.setDiagnosticsOptions) {
    jsonDefaults.setDiagnosticsOptions({
      validate: true,
      allowComments: false,
      trailingCommas: 'error',
      schemaValidation: 'error',
    });
  }

  const editor = monaco.editor.create(container, {
    value,
    language: 'json',
    theme: isDark ? 'vs-dark' : 'vs',
    minimap: { enabled: false },
    fontSize: 12,
    fontFamily: "'JetBrains Mono', 'Fira Code', Consolas, monospace",
    lineNumbers: 'on',
    scrollBeyondLastLine: false,
    automaticLayout: true,
    tabSize: 2,
    wordWrap: 'on',
    renderLineHighlight: 'line',
    padding: { top: 8, bottom: 8 },
    scrollbar: { verticalScrollbarSize: 8, horizontalScrollbarSize: 8 },
    formatOnPaste: true,
    formatOnType: true,
  });

  editor.onDidChangeModelContent(() => {
    onChange(editor.getValue());
  });

  activeEditors.set(containerId, editor);

  return () => {
    editor.dispose();
    activeEditors.delete(containerId);
  };
}

/** Update theme for all active editors */
export function updateEditorTheme(isDark: boolean) {
  monaco.editor.setTheme(isDark ? 'vs-dark' : 'vs');
}

/** Dispose all active editors (call on workflow switch) */
export function disposeAllEditors() {
  for (const [, editor] of activeEditors) {
    editor.dispose();
  }
  activeEditors.clear();
}
