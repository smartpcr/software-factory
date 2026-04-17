/**
 * Expected field counts per panel section.
 * Used by tests to verify the property grid layout.
 */
export const PANEL_COUNTS = {
  /** nodeName, description, nodeId(readonly) */
  general: 3,
  /** joinType, priority, maxConcurrentExecutions, retryPolicy, circuitBreakerPolicy */
  policies: 5,
  /** compensationNodeId, fallbackNodeId, configuration */
  references: 3,
  /** strategy, maxAttempts, initialDelay, maxDelay, multiplier, retryOn, doNotRetryOn */
  retryNested: 7,
  /** failureThreshold, halfOpenSuccesses, minimumThroughput, openDuration */
  circuitBreakerNested: 4,
};
