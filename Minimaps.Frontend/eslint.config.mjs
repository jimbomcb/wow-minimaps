import eslint from '@eslint/js';
import tseslint from 'typescript-eslint';

export default tseslint.config(
    eslint.configs.recommended,
    ...tseslint.configs.recommended,
    {
        files: ['wwwroot/js/src/**/*.ts'],
        languageOptions: {
            parserOptions: {
                project: './tsconfig.json'
            }
        },
        rules: {
            '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
        }
    },
    {
        ignores: ['wwwroot/js/dist/**', 'node_modules/**']
    }
);
