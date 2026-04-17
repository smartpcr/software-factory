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
    // Stage 1.2.3 will activate this rule to restrict direct engine-contract imports
    'no-restricted-imports': 'off',
    // TODO(Stage 1.2.3): activate this rule to enforce facade-only access to engine contracts
    'import/no-restricted-paths': [
      'off',
      {
        zones: [
          {
            target: './src',
            from: './src/engine-contract.generated.ts',
            message: 'Import engine types through the facade, not the generated contract directly.',
          },
        ],
      },
    ],
    // Allow inference on exported functions (opt-in for strict later)
    '@typescript-eslint/explicit-function-return-type': 'off',
    '@typescript-eslint/explicit-module-boundary-types': 'off',
  },
};
