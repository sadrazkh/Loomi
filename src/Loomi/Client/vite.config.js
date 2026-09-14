import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
export default defineConfig({ plugins: [vue()], base: '/dist/', build: { outDir: '../wwwroot/dist', emptyOutDir: true }, server: { proxy: { '/api': 'http://localhost:5080', '/hubs': { target: 'http://localhost:5080', ws: true }, '/desktop': { target: 'http://localhost:5080', ws: true } } } });
