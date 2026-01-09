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
            '@typescript-eslint/no-unused-vars': 'off',
            'linebreak-style': ['error', 'unix'],
        }
    },
    {
        ignores: ['wwwroot/js/dist/**', 'node_modules/**']
    }
);
