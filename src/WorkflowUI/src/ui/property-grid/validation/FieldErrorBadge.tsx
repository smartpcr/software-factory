export interface FieldErrorBadgeProps {
  error: string | undefined;
}

export function FieldErrorBadge({ error }: FieldErrorBadgeProps) {
  if (!error) return null;

  return (
    <span
      data-testid="field-error-badge"
      role="alert"
      style={{ color: 'red', fontSize: '0.8em' }}
    >
      {error}
    </span>
  );
}
