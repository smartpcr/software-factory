/**
 * Service contracts for the four engines that operate on the knowledge graph.
 *
 * These interfaces match the architecture.md §4.1–4.5 contracts exactly.
 * Method names follow TypeScript camelCase convention (the canonical API
 * surface); data-layer field names (nodes, edges, properties) use
 * snake_case to match the JSON-Schema wire format.
 *
 * Downstream workstreams implement these interfaces; this package only
 * publishes the shapes so that all producers and consumers share a single
 * type surface.
 */
export {};
//# sourceMappingURL=interfaces.js.map