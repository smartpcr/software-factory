import { useEffect, useMemo, useCallback } from 'react';
import { useForm, type UseFormReturn } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import type { NodeDto } from '../../engine-contract';
import { useGraphStore } from '../state/graphStore';
import { getSchemaForRuntimeType, getFieldDescriptorsForRuntimeType, getVisibilityRulesForRuntimeType } from './schemaRegistry';
import { evaluateVisibilityRules } from './rules/visibilityRules';
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

  const allDescriptors = useMemo(
    () => (nodeRuntimeType ? getFieldDescriptorsForRuntimeType(nodeRuntimeType) : []),
    [nodeRuntimeType],
  );

  const visibilityRules = useMemo(
    () => (nodeRuntimeType ? getVisibilityRulesForRuntimeType(nodeRuntimeType) : []),
    [nodeRuntimeType],
  );

  const form = useForm<FormValues>({
    resolver: schema
      ? (zodResolver(schema as Parameters<typeof zodResolver>[0]) as unknown as undefined)
      : undefined,
    defaultValues: (node ?? {}) as FormValues,
    mode: 'onBlur',
  });

  // Watch form values to drive visibility rules
  const watchedValues = form.watch();

  // Apply visibility rules to filter descriptors
  const fieldDescriptors = useMemo(() => {
    if (visibilityRules.length === 0) return allDescriptors;

    const ruleFieldNames = new Set(visibilityRules.map((r) => r.field));
    const visibleRuleFields = evaluateVisibilityRules(visibilityRules, watchedValues as Record<string, unknown>);

    return allDescriptors.filter((desc) => {
      if (!ruleFieldNames.has(desc.name)) return true;
      return visibleRuleFields.has(desc.name);
    });
  }, [allDescriptors, visibilityRules, watchedValues]);

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
