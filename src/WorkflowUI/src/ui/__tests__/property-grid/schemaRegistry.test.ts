import { describe, it, expect } from 'vitest';
import { RUNTIME_TYPES } from '../../../engine-contract';
import type { RuntimeType } from '../../../engine-contract';
import {
  getSchemaForRuntimeType,
  getFieldDescriptorsForRuntimeType,
  allRuntimeTypesRegistered,
} from '../../property-grid/schemaRegistry';
import { allKindFixtures } from '../fixtures/property-grid/all-kinds-form-data';
import * as fs from 'fs';
import * as path from 'path';

describe('schemaRegistry', () => {
  it('covers every runtime type', () => {
    expect(allRuntimeTypesRegistered()).toBe(true);
    for (const rt of RUNTIME_TYPES) {
      const schema = getSchemaForRuntimeType(rt);
      expect(schema).toBeDefined();
    }
  });

  it('getFieldDescriptorsForRuntimeType returns descriptors for all types', () => {
    for (const rt of RUNTIME_TYPES) {
      const descriptors = getFieldDescriptorsForRuntimeType(rt);
      expect(descriptors.length).toBeGreaterThan(0);
      // Every type should have at least the base fields
      const names = descriptors.map((d) => d.name);
      expect(names).toContain('nodeId');
      expect(names).toContain('nodeName');
    }
  });

  it('validates fixture data against schemas', () => {
    for (const fixture of allKindFixtures) {
      const schema = getSchemaForRuntimeType(fixture.runtimeType as RuntimeType);
      const result = schema.safeParse(fixture);
      expect(result.success).toBe(true);
    }
  });

  it('has no RJSF dependency in package.json', () => {
    const pkgPath = path.resolve(__dirname, '../../../../package.json');
    const pkgContent = fs.readFileSync(pkgPath, 'utf-8');
    expect(pkgContent).not.toContain('rjsf');
    expect(pkgContent).not.toContain('@rjsf');
  });
});
