import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// относительные пути: панель работает под любым префиксом, который отдаёт nginx
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: '../src/VlessCheck.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:8788',
      '/sub': 'http://127.0.0.1:8788',
    },
  },
})
