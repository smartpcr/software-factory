import { useEffect, useMemo, useCallback } from 'react';
import { useForm, type UseFormReturn } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import type { NodeDto } from '../../engine-contract';
import { useGraphStore } from '../state/graphStore';
import { getSchemaForRuntimeType, getFieldDescriptorsForRuntimeType } from './schemaRegistry';
import type { FieldDescriptor } from './fieldDescriptors';

type FormValues = Record<string, unknown>;

export interface UseNodeFormResult {
  form: UseFormReturn<FormValues>;
  onSubmit: (values: FormValues) => void;
  fieldDescriptors: FieldDescriptor[];
}

export function useNodeForm(node: NodeDto | null): UseNodeFormResult {
  const nodeRuntimeType = node?.runtimeType;
  const nodeId = node?.nodeId;

  const schema = useMemo(
    () => (nodeRuntimeType ? getSchemaForRuntimeType(nodeRuntimeType) : undefined),
    [nodeRuntimeType],
  );

  const fieldDescriptors = useMemo(
    () => (nodeRuntimeType ? getFieldDescriptorsForRuntimeType(nodeRuntimeType) : []),
    [nodeRuntimeType],
  );

  const form = useForm<FormValues>({
    resolver: schema
      ? (zodResolver(schema as Parameters<typeof zodResolver>[0]) as unknown as undefined)
      : undefined,
    defaultValues: (node ?? {}) as FormValues,
    mode: 'onBlur',
  });

  // Reset form when selected node changes
  useEffect(() => {
    if (node) {
      form.reset(node as unknown as FormValues);
    }
  }, [nodeId, nodeRuntimeType, form, node]);

  const onSubmit = useCallback(
    (values: FormValues) => {
      if (!nodeId) return;
      useGraphStore.getState().updateNode(nodeId, values as Partial<NodeDto>);
    },
    [nodeId],
  );

  return { form, onSubmit, fieldDescriptors };
}
