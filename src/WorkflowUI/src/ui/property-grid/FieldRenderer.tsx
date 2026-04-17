import type { UseFormReturn } from 'react-hook-form';
import type { FieldDescriptor } from './fieldDescriptors';
import { FieldErrorBadge } from './validation/FieldErrorBadge';

export interface FieldRendererProps {
  descriptor: FieldDescriptor;
  form: UseFormReturn<Record<string, unknown>>;
}

export function FieldRenderer({ descriptor, form }: FieldRendererProps) {
  const { register, formState: { errors } } = form;
  const error = errors[descriptor.name];
  const errorMessage = typeof error?.message === 'string' ? error.message : undefined;

  const commonProps = {
    ...register(descriptor.name),
    id: descriptor.name,
    disabled: descriptor.readOnly,
    'data-field-path': descriptor.name,
    'data-testid': `field-${descriptor.name}`,
    'aria-invalid': errorMessage ? true : undefined,
  };

  const renderInput = () => {
    switch (descriptor.kind) {
      case 'textarea':
        return (
          <textarea
            {...commonProps}
            placeholder={descriptor.placeholder}
            rows={3}
          />
        );
      case 'select':
        return (
          <select {...commonProps}>
            {descriptor.options?.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        );
      case 'number':
        return (
          <input
            {...commonProps}
            type="number"
            placeholder={descriptor.placeholder}
          />
        );
      case 'switch':
        return (
          <input
            {...commonProps}
            type="checkbox"
          />
        );
      case 'text':
      case 'keyvalue':
      case 'stringlist':
      default:
        return (
          <input
            {...commonProps}
            type="text"
            placeholder={descriptor.placeholder}
          />
        );
    }
  };

  return (
    <div data-testid={`field-wrapper-${descriptor.name}`}>
      <label htmlFor={descriptor.name}>{descriptor.label}</label>
      {renderInput()}
      <FieldErrorBadge error={errorMessage} />
    </div>
  );
}
