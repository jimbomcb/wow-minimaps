import eslint from '@eslint/js';
import tseslint from 'typescript-eslint';
import prettier from 'eslint-config-prettier';

export default tseslint.config(
    eslint.configs.recommended,
    ...tseslint.configs.recommended,
    prettier,
    {
        files: ['wwwroot/js/src/**/*.ts'],
        languageOptions: {
            parserOptions: {
                project: './tsconfig.json'
            }
        },
        rules: {
            '@typescript-eslint/no-unused-vars': 'off',

            'linebreak-style': ['error', 'unix'],
            'eqeqeq': ['error', 'always'],
            'prefer-const': 'error',
            'no-constant-binary-expression': 'error',
            '@typescript-eslint/consistent-type-imports': ['error', { prefer: 'type-imports' }],
            '@typescript-eslint/no-unnecessary-condition': 'warn',
        }
    },
    {
        ignores: ['wwwroot/js/dist/**', 'node_modules/**']
    }
);
