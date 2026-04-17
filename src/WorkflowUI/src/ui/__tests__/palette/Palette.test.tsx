import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Palette } from '../../palette/Palette';

describe('Palette', () => {
  it('renders palette with all groups', () => {
    render(<Palette />);
    expect(screen.getByTestId('palette')).toBeDefined();
    expect(screen.getByTestId('palette-group-task')).toBeDefined();
    expect(screen.getByTestId('palette-group-control')).toBeDefined();
    expect(screen.getByTestId('palette-group-composition')).toBeDefined();
  });

  it('renders all 13 palette items', () => {
    render(<Palette />);
    const kinds = [
      'Noop', 'CSharp', 'CSharpScript', 'CSharpTask',
      'PowerShell', 'PowerShellTask', 'IfElse', 'ForEach',
      'While', 'Switch', 'Subflow', 'Timer', 'Container',
    ];
    for (const kind of kinds) {
      expect(screen.getByTestId(`palette-item-${kind}`)).toBeDefined();
    }
  });

  it('has search input', () => {
    render(<Palette />);
    expect(screen.getByTestId('palette-search')).toBeDefined();
  });

  it('items are draggable', () => {
    render(<Palette />);
    const item = screen.getByTestId('palette-item-Noop');
    expect(item.getAttribute('draggable')).toBe('true');
  });
});
