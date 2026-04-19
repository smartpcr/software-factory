import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';

// We test the NodeChrome directly since WorkflowNode requires React Flow context
import { NodeChrome } from '../../nodes/NodeChrome';

describe('NodeChrome', () => {
  it('renders node name and icon label', () => {
    render(
      <NodeChrome runtimeType="Noop" nodeId="n1" nodeName="My Noop">
        <span data-testid="child">child</span>
      </NodeChrome>,
    );
    // Type label badge should contain the kind label
    const chrome = screen.getByTestId('node-chrome-n1');
    expect(chrome.querySelector('.node-type-label')?.textContent).toBe('Noop');
    expect(screen.getByText('My Noop')).toBeDefined();
    expect(screen.getByTestId('child')).toBeDefined();
  });

  it('uses nodeId when nodeName is empty', () => {
    render(<NodeChrome runtimeType="CSharp" nodeId="cs1" nodeName="" />);
    expect(screen.getByText('cs1')).toBeDefined();
  });

  it('shows selected state via CSS class', () => {
    render(<NodeChrome runtimeType="Timer" nodeId="t1" nodeName="Timer" selected />);
    const chrome = screen.getByTestId('node-chrome-t1');
    expect(chrome.classList.contains('selected')).toBe(true);
  });

  it('renders all 13 runtime types', () => {
    const kinds = [
      'Noop', 'CSharp', 'CSharpScript', 'CSharpTask',
      'PowerShell', 'PowerShellTask', 'IfElse', 'ForEach',
      'While', 'Switch', 'Subflow', 'Timer', 'Container',
    ] as const;

    for (const kind of kinds) {
      const { unmount } = render(
        <NodeChrome runtimeType={kind} nodeId={`test-${kind}`} nodeName={`${kind} Node`} />,
      );
      expect(screen.getByTestId(`node-chrome-test-${kind}`)).toBeDefined();
      unmount();
    }
  });

  it('renders status badge when status is provided', () => {
    render(<NodeChrome runtimeType="Noop" nodeId="n1" nodeName="Test" status="Running" />);
    const chrome = screen.getByTestId('node-chrome-n1');
    expect(chrome.getAttribute('data-status')).toBe('Running');
    expect(screen.getByTestId('node-status-badge')).toBeDefined();
  });

  it('renders body preview', () => {
    render(<NodeChrome runtimeType="IfElse" nodeId="n1" nodeName="Branch" />);
    expect(screen.getByTestId('node-body-preview')).toBeDefined();
  });
});
