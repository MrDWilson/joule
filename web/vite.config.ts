import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { readFileSync } from "node:fs";
import { fileURLToPath, URL } from "node:url";

const pkg = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf8")) as { version: string };

// Recharts and the libraries it pulls in (Redux Toolkit, Immer, Reselect, es-toolkit, EventEmitter3, D3, decimal.js)
// load only with the first chart, so the first paint downloads the app shell and Today.
const chartLibraries =
  /[\\/]node_modules[\\/](recharts|d3-[^\\/]+|victory-vendor|react-smooth|decimal\.js-light|decimal\.js|@reduxjs|immer|reselect|es-toolkit|eventemitter3|react-redux|redux|redux-thunk|internmap)[\\/]/;

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  define: { __JOULE_VERSION__: JSON.stringify(pkg.version) },
  build: {
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes("node_modules")) {
            if (chartLibraries.test(id)) return "charts";
            if (id.includes("@radix-ui")) return "ui";
            return "vendor";
          }
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: process.env.JOULE_API_URL || process.env.PREDBAT_API_URL || "http://127.0.0.1:5080",
        // Preserve the browser's authority for the API's same-origin check.
        changeOrigin: false,
      },
    },
  },
});
