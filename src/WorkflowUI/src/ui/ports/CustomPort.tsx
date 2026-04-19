import { Handle, Position, type HandleProps } from '@xyflow/react';
import './CustomPort.css';

export interface CustomPortProps extends Omit<HandleProps, 'position'> {
  position?: Position;
  variant?: 'in' | 'out' | 'error';
}

export function CustomPort({
  variant = 'out',
  position,
  type,
  ...rest
}: CustomPortProps) {
  const resolvedPosition = position ?? (variant === 'in' ? Position.Top : Position.Bottom);
  const resolvedType = type ?? (variant === 'in' ? 'target' : 'source');
  const className = `custom-port${variant === 'error' ? ' error-port' : ''}`;

  return (
    <Handle
      type={resolvedType}
      position={resolvedPosition}
      className={className}
      {...rest}
    />
  );
}
