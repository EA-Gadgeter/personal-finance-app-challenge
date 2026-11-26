import { readFileSync } from 'node:fs';
import { defineConfig } from 'vite';

export default defineConfig(({ command }) => {
  if (command === 'build') {
    return {};
  }

  return {
    server: {
      host: 'localhost',
      port: 5173,
      strictPort: true,
      https: {
        cert: readFileSync(new URL('./.certs/localhost.pem', import.meta.url)),
        key: readFileSync(new URL('./.certs/localhost.key', import.meta.url)),
      },
      proxy: {
        '/api': {
          target: 'http://127.0.0.1:5075',
          xfwd: true,
        },
      },
    },
  };
});
