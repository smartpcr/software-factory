var __spreadArray = (this && this.__spreadArray) || function (to, from, pack) {
    if (pack || arguments.length === 2) for (var i = 0, l = from.length, ar; i < l; i++) {
        if (ar || !(i in from)) {
            if (!ar) ar = Array.prototype.slice.call(from, 0, i);
            ar[i] = from[i];
        }
    }
    return to.concat(ar || Array.prototype.slice.call(from));
};
var _a, _b;
/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';
import { hostname } from 'node:os';
export default defineConfig({
    plugins: [react()],
    resolve: {
        alias: {
            '@': fileURLToPath(new URL('./src', import.meta.url)),
        },
    },
    server: {
        host: '0.0.0.0',
        port: 5181,
        strictPort: true,
        allowedHosts: __spreadArray(['localhost', '127.0.0.1', '[::1]', hostname().toLowerCase()], ((_b = (_a = process.env.FORGE_UI_HOSTS) === null || _a === void 0 ? void 0 : _a.split(',').filter(Boolean)) !== null && _b !== void 0 ? _b : []), true),
        proxy: {
            '/chat/authorize': {
                target: 'http://localhost:5180',
                changeOrigin: true,
                headers: { 'X-Forge-Proxy': 'vite' },
            },
            '/api': {
                target: 'http://localhost:5180',
                changeOrigin: true,
                headers: { 'X-Forge-Proxy': 'vite' },
            },
            '/hubs': {
                target: 'http://localhost:5180',
                changeOrigin: true,
                headers: { 'X-Forge-Proxy': 'vite' },
                ws: true,
            },
        },
    },
    test: {
        // Phase 18.6 / 23.8 — vitest covers `src/**/*.spec.ts` (pure-logic UI
        // tests, no DOM). Playwright e2e specs in `e2e/` use the Playwright
        // test runner and must NOT be discovered here.
        exclude: ['**/node_modules/**', '**/e2e/**', '**/dist/**'],
    },
});
