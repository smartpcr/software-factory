/**
 * Exhaustive discriminator check helper.
 * Use in switch/if-else blocks to ensure all RuntimeType variants are handled.
 * Throws at runtime if called with a value (should be unreachable at compile time).
 */
export function assertNever(x: never): never {
  throw new Error('Unhandled runtime type: ' + JSON.stringify(x));
}
