import { fileURLToPath, URL } from 'node:url';

import { defineConfig } from 'vite';
import plugin from '@vitejs/plugin-react';
import fs from 'fs';
import path from 'path';
import child_process from 'child_process';
import { env } from 'process';

// The dev server's HTTPS certificate and API proxy. Built lazily and only for `vite serve`:
// it shells out to `dotnet dev-certs`, which does not exist in a production build environment
// (the Docker image builds the SPA in a Node container with no .NET SDK), and a build has no
// dev server to configure anyway.
function devServerConfig() {
   const baseFolder =
      env.APPDATA !== undefined && env.APPDATA !== ''
         ? `${env.APPDATA}/ASP.NET/https`
         : `${env.HOME}/.aspnet/https`;

   const certificateName = "nodeseteditor.client";
   const certFilePath = path.join(baseFolder, `${certificateName}.pem`);
   const keyFilePath = path.join(baseFolder, `${certificateName}.key`);

   if (!fs.existsSync(baseFolder)) {
      fs.mkdirSync(baseFolder, { recursive: true });
   }

   if (!fs.existsSync(certFilePath) || !fs.existsSync(keyFilePath)) {
      if (0 !== child_process.spawnSync('dotnet', [
         'dev-certs',
         'https',
         '--export-path',
         certFilePath,
         '--format',
         'Pem',
         '--no-password',
      ], { stdio: 'inherit', }).status) {
         throw new Error("Could not create certificate.");
      }
   }

   const target = env.ASPNETCORE_HTTPS_PORT ? `https://localhost:${env.ASPNETCORE_HTTPS_PORT}` :
      env.ASPNETCORE_URLS ? env.ASPNETCORE_URLS.split(';')[0] : 'https://localhost:7098';

   return {
      proxy: {
         '^/weatherforecast': {
            target,
            secure: false
         },
         '^/api': {
            target,
            secure: false,
            ws: true
         },
         '^/openapi': {
            target,
            secure: false
         },
         '^/swagger': {
            target,
            secure: false
         },
         '^/opcua': {
            target,
            secure: false
         }
      },
      port: parseInt(env.DEV_SERVER_PORT || '5173'),
      https: {
         key: fs.readFileSync(keyFilePath),
         cert: fs.readFileSync(certFilePath),
      }
   };
}

// https://vitejs.dev/config/
export default defineConfig(({ command }) => ({
   plugins: [plugin()],
   resolve: {
      alias: {
         '@': fileURLToPath(new URL('./src', import.meta.url))
      },
      // Force a single copy of React across the whole module graph. Without
      // this, transitively imported packages (or stale entries in Vite's
      // pre-bundled deps cache) can resolve their own React, leaving
      // ReactSharedInternals.H null for the second copy and producing
      // "Cannot read properties of null (reading 'useState')" at the first
      // hook call. See https://react.dev/link/invalid-hook-call.
      //
      // Emotion is deduped for the same reason: MUI loads @emotion/react, and
      // a second module instance triggers "You are loading @emotion/react when
      // it is already loaded" plus a broken style cache.
      dedupe: ['react', 'react-dom', '@emotion/react', '@emotion/styled'],
   },
   optimizeDeps: {
      // Pre-bundle these together so dev-mode HMR can't end up with one
      // pre-bundled copy and one freshly resolved copy.
      include: [
         'react', 'react-dom', 'react-dom/client',
         'react/jsx-runtime', 'react/jsx-dev-runtime',
         '@emotion/react', '@emotion/styled', '@emotion/cache',
      ],
   },
   ...(command === 'serve' ? { server: devServerConfig() } : {}),
}))
