// -----------------------------------------------------------------------
// Phase 34 — preset-chip control for per-repo sync cadence overrides.
// Mirrors the DateRangeSelect chip pattern used elsewhere on RepoSyncPanel
// so the operator picks a cadence in one click. Custom reveals a number +
// unit input for power users. Selecting "Default" clears the override.
// -----------------------------------------------------------------------

import { useState } from 'react';

interface Props {
  /** Current value in seconds. 0 / null = inherit global default. */
  valueSeconds: number;
  onChange: (seconds: number) => void;
  /** Global default rendered next to the "Default" chip (e.g. "2 hr"). */
  globalDefaultLabel?: string;
  disabled?: boolean;
  testIdPrefix?: string;
}

interface Preset { label: string; seconds: number; }

// Common cadences. 2 hr is the global default and is therefore not in the
// preset list (operators pick "Default" for that); 24 hr maps to "Daily".
const PRESETS: Preset[] = [
  { label: '30 min', seconds: 1800 },
  { label: '1 hr',   seconds: 3600 },
  { label: '4 hr',   seconds: 14400 },
  { label: '8 hr',   seconds: 28800 },
  { label: 'Daily',  seconds: 86400 },
];

export function SyncFrequencyChips({
  valueSeconds,
  onChange,
  globalDefaultLabel = '2 hr',
  disabled,
  testIdPrefix,
}: Props) {
  // Match preset by exact seconds; everything else (incl. an explicit 7200
  // that happens to equal global) falls into "Custom" so the user can see
  // their override is active.
  const matchedPreset = PRESETS.find((p) => p.seconds === valueSeconds);
  const isDefault = !valueSeconds || valueSeconds <= 0;
  const isCustom = !isDefault && !matchedPreset;

  const [customOpen, setCustomOpen] = useState(isCustom);

  // Local custom-input state so the user can type before committing.
  const [customValue, setCustomValue] = useState<number | ''>(
    isCustom ? Math.round(valueSeconds / 60) : '',
  );
  const [customUnit, setCustomUnit] = useState<'min' | 'hr'>(
    isCustom && valueSeconds % 3600 === 0 ? 'hr' : 'min',
  );

  function pickDefault() {
    setCustomOpen(false);
    onChange(0);
  }

  function pickPreset(p: Preset) {
    setCustomOpen(false);
    onChange(p.seconds);
  }

  function pickCustom() {
    setCustomOpen(true);
    // Pre-fill the input with the current value if there is one.
    if (valueSeconds > 0 && !matchedPreset) {
      // already showing as custom — keep state
    } else if (valueSeconds > 0) {
      setCustomValue(valueSeconds % 3600 === 0 ? valueSeconds / 3600 : Math.round(valueSeconds / 60));
      setCustomUnit(valueSeconds % 3600 === 0 ? 'hr' : 'min');
    }
  }

  function commitCustom(v: number | '', unit: 'min' | 'hr') {
    setCustomValue(v);
    setCustomUnit(unit);
    if (v === '' || v <= 0) {
      onChange(0);
      return;
    }
    onChange(unit === 'hr' ? v * 3600 : v * 60);
  }

  const chipBase: React.CSSProperties = {
    padding: '4px 10px',
    fontSize: 11,
    fontWeight: 600,
    borderRadius: 'var(--radius-sm)',
    cursor: disabled ? 'not-allowed' : 'pointer',
    transition: 'all 100ms',
  };

  function chipStyle(active: boolean): React.CSSProperties {
    return {
      ...chipBase,
      border: `1px solid ${active ? 'var(--accent)' : 'var(--border)'}`,
      background: active ? 'color-mix(in srgb, var(--accent) 12%, transparent)' : 'transparent',
      color: active ? 'var(--accent)' : 'var(--text-tertiary)',
      opacity: disabled ? 0.5 : 1,
    };
  }

  const inputStyle: React.CSSProperties = {
    padding: '6px 10px',
    fontSize: 12,
    fontFamily: 'var(--font-mono, monospace)',
    color: 'var(--text-primary)',
    background: 'var(--bg-surface)',
    border: '1px solid var(--border-strong)',
    borderRadius: 'var(--radius-sm)',
    outline: 'none',
  };

  return (
    <div style={{ display: 'grid', gap: 8 }}>
      <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
        <button
          type="button"
          onClick={pickDefault}
          disabled={disabled}
          style={chipStyle(isDefault)}
          data-testid={testIdPrefix ? `${testIdPrefix}-default` : undefined}
        >
          Default ({globalDefaultLabel})
        </button>
        {PRESETS.map((p) => (
          <button
            key={p.seconds}
            type="button"
            onClick={() => pickPreset(p)}
            disabled={disabled}
            style={chipStyle(matchedPreset?.seconds === p.seconds)}
            data-testid={testIdPrefix ? `${testIdPrefix}-${p.seconds}` : undefined}
          >
            {p.label}
          </button>
        ))}
        <button
          type="button"
          onClick={pickCustom}
          disabled={disabled}
          style={chipStyle(isCustom || customOpen)}
          data-testid={testIdPrefix ? `${testIdPrefix}-custom` : undefined}
        >
          Custom
        </button>
      </div>
      {(isCustom || customOpen) && (
        <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
          <input
            type="number"
            min={1}
            value={customValue}
            onChange={(e) => commitCustom(e.target.value === '' ? '' : Math.max(1, Number(e.target.value)), customUnit)}
            placeholder="value"
            disabled={disabled}
            style={{ ...inputStyle, width: 100 }}
          />
          <select
            value={customUnit}
            onChange={(e) => commitCustom(customValue, e.target.value as 'min' | 'hr')}
            disabled={disabled}
            style={{ ...inputStyle, width: 80, fontFamily: 'inherit' }}
          >
            <option value="min">min</option>
            <option value="hr">hr</option>
          </select>
          <span style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>
            (override slows this repo down)
          </span>
        </div>
      )}
    </div>
  );
}
