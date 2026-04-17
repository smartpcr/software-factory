module.exports = {
  root: true,
  env: { browser: true, es2020: true },
  extends: [
    'eslint:recommended',
    'plugin:@typescript-eslint/strict',
    'plugin:react/recommended',
    'plugin:react/jsx-runtime',
    'plugin:react-hooks/recommended',
    'plugin:import/recommended',
    'plugin:import/typescript',
    'prettier',
  ],
  ignorePatterns: ['dist', '.eslintrc.cjs'],
  parser: '@typescript-eslint/parser',
  parserOptions: {
    ecmaVersion: 'latest',
    sourceType: 'module',
  },
  plugins: ['react', '@typescript-eslint', 'import'],
  settings: {
    react: { version: '18' },
    'import/resolver': {
      typescript: true,
      node: true,
    },
  },
  rules: {
    // Forbid direct imports of the generated contract file — use the facade instead
    'no-restricted-imports': ['error', {
      patterns: [{
        group: ['**/engine-contract.generated', '**/engine-contract.generated.ts'],
        message: 'Import from "./engine-contract" (the facade) instead.',
      }],
    }],
    'import/no-restricted-paths': [
      'off',
    ],
    // Allow inference on exported functions (opt-in for strict later)
    '@typescript-eslint/explicit-function-return-type': 'off',
    '@typescript-eslint/explicit-module-boundary-types': 'off',
  },
  overrides: [
    {
      // The facade file itself is allowed to import from the generated file
      files: ['src/engine-contract.ts'],
      rules: {
        'no-restricted-imports': 'off',
      },
    },
  ],
};
