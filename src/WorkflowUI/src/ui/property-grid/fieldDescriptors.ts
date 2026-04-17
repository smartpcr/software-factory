import type { RuntimeType } from '../../engine-contract';

export type FieldKind =
  | 'text'
  | 'number'
  | 'select'
  | 'switch'
  | 'textarea'
  | 'keyvalue'
  | 'stringlist';

export interface FieldDescriptor {
  name: string;
  label: string;
  kind: FieldKind;
  readOnly?: boolean;
  required?: boolean;
  options?: { value: string; label: string }[];
  group?: 'general' | 'kind-specific' | 'policies' | 'references';
  placeholder?: string;
}

export function baseFieldDescriptors(): FieldDescriptor[] {
  return [
    {
      name: 'nodeId',
      label: 'Node ID',
      kind: 'text',
      readOnly: true,
      group: 'general',
    },
    {
      name: 'nodeName',
      label: 'Name',
      kind: 'text',
      required: true,
      group: 'general',
      placeholder: 'Enter node name',
    },
    {
      name: 'description',
      label: 'Description',
      kind: 'textarea',
      group: 'general',
      placeholder: 'Enter description',
    },
    {
      name: 'joinType',
      label: 'Join Type',
      kind: 'select',
      options: [
        { value: 'Any', label: 'Any' },
        { value: 'All', label: 'All' },
      ],
      group: 'policies',
    },
    {
      name: 'priority',
      label: 'Priority',
      kind: 'select',
      options: [
        { value: 'Low', label: 'Low' },
        { value: 'Normal', label: 'Normal' },
        { value: 'High', label: 'High' },
      ],
      group: 'policies',
    },
    {
      name: 'maxConcurrentExecutions',
      label: 'Max Concurrency',
      kind: 'number',
      group: 'policies',
      placeholder: '0',
    },
  ];
}

export function kindFieldDescriptors(runtimeType: RuntimeType): FieldDescriptor[] {
  switch (runtimeType) {
    case 'Noop':
      return [];
    case 'CSharp':
      return [
        { name: 'assemblyPath', label: 'Assembly Path', kind: 'text', group: 'kind-specific' },
        { name: 'typeName', label: 'Type Name', kind: 'text', group: 'kind-specific' },
      ];
    case 'CSharpScript':
      return [
        { name: 'scriptPath', label: 'Script Path', kind: 'text', group: 'kind-specific' },
        { name: 'scriptContent', label: 'Script Content', kind: 'textarea', group: 'kind-specific' },
      ];
    case 'CSharpTask':
      return [
        { name: 'scriptContent', label: 'Script Content', kind: 'textarea', group: 'kind-specific' },
        { name: 'executorTypeName', label: 'Executor Type', kind: 'text', group: 'kind-specific' },
        { name: 'executorAssemblyPath', label: 'Executor Assembly', kind: 'text', group: 'kind-specific' },
      ];
    case 'PowerShell':
      return [
        { name: 'scriptPath', label: 'Script Path', kind: 'text', required: true, group: 'kind-specific' },
        { name: 'requiredModules', label: 'Required Modules', kind: 'stringlist', group: 'kind-specific' },
        { name: 'modulePaths', label: 'Module Paths', kind: 'keyvalue', group: 'kind-specific' },
      ];
    case 'PowerShellTask':
      return [
        { name: 'scriptPath', label: 'Script Path', kind: 'text', group: 'kind-specific' },
        { name: 'scriptContent', label: 'Script Content', kind: 'textarea', group: 'kind-specific' },
        { name: 'requiredModules', label: 'Required Modules', kind: 'stringlist', group: 'kind-specific' },
        { name: 'modulePaths', label: 'Module Paths', kind: 'stringlist', group: 'kind-specific' },
      ];
    case 'IfElse':
      return [
        { name: 'condition', label: 'Condition', kind: 'text', required: true, group: 'kind-specific' },
      ];
    case 'ForEach':
      return [
        { name: 'collectionExpression', label: 'Collection Expression', kind: 'text', required: true, group: 'kind-specific' },
        { name: 'itemVariableName', label: 'Item Variable', kind: 'text', required: true, group: 'kind-specific' },
      ];
    case 'While':
      return [
        { name: 'conditionExpression', label: 'Condition Expression', kind: 'text', required: true, group: 'kind-specific' },
        { name: 'maxIterations', label: 'Max Iterations', kind: 'number', group: 'kind-specific' },
      ];
    case 'Switch':
      return [
        { name: 'expression', label: 'Expression', kind: 'text', required: true, group: 'kind-specific' },
        { name: 'cases', label: 'Cases', kind: 'keyvalue', group: 'kind-specific' },
      ];
    case 'Subflow':
      return [
        { name: 'workflowFilePath', label: 'Workflow File Path', kind: 'text', group: 'kind-specific' },
        { name: 'workflowDefinition', label: 'Inline Workflow', kind: 'textarea', group: 'kind-specific' },
        { name: 'inputMappings', label: 'Input Mappings', kind: 'keyvalue', group: 'kind-specific' },
        { name: 'outputMappings', label: 'Output Mappings', kind: 'keyvalue', group: 'kind-specific' },
        { name: 'timeout', label: 'Timeout', kind: 'text', group: 'kind-specific' },
        { name: 'skipValidation', label: 'Skip Validation', kind: 'switch', group: 'kind-specific' },
      ];
    case 'Timer':
      return [
        { name: 'schedule', label: 'Schedule', kind: 'text', required: true, group: 'kind-specific', placeholder: 'CRON or duration' },
      ];
    case 'Container':
      return [
        {
          name: 'executionMode',
          label: 'Execution Mode',
          kind: 'select',
          options: [
            { value: 'Sequential', label: 'Sequential' },
            { value: 'Parallel', label: 'Parallel' },
          ],
          group: 'kind-specific',
        },
      ];
    default:
      return [];
  }
}
