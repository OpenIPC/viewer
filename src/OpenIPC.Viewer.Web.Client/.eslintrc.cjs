/* eslint-disable no-undef */
module.exports = {
  root: true,
  env: { browser: true, es2022: true },
  extends: [
    'eslint:recommended',
    'plugin:@typescript-eslint/recommended',
    'plugin:react-hooks/recommended',
  ],
  parser: '@typescript-eslint/parser',
  parserOptions: { ecmaVersion: 'latest', sourceType: 'module' },
  plugins: ['react-refresh'],
  ignorePatterns: ['dist', '.eslintrc.cjs', 'vite.config.ts'],
  rules: {
    // We colocate each Context provider with its use* hook (auth.tsx, i18n.tsx) —
    // an intentional, idiomatic pattern. The rule only guards dev fast-refresh
    // granularity, not correctness, so silence it rather than split the files.
    'react-refresh/only-export-components': 'off',
  },
}
