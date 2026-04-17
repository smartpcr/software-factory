export interface VisibilityRule {
  field: string;
  visibleWhen: (values: Record<string, unknown>) => boolean;
}

/**
 * Evaluates visibility rules and returns the set of visible field names.
 * Fields not mentioned in any rule are visible by default.
 */
export function evaluateVisibilityRules(
  rules: VisibilityRule[],
  values: Record<string, unknown>,
): Set<string> {
  const visible = new Set<string>();
  for (const rule of rules) {
    if (rule.visibleWhen(values)) {
      visible.add(rule.field);
    }
  }
  return visible;
}
