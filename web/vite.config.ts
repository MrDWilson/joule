import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { readFileSync } from "node:fs";
import { fileURLToPath, URL } from "node:url";

const pkg = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf8")) as { version: string };

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  define: { __JOULE_VERSION__: JSON.stringify(pkg.version) },
  build: {
    rolldownOptions: {
      output: {
        // Radix (dialogs, switches) in its own chunk and the rest of node_modules in "vendor", so an app-only change
        // leaves the browser's cached copies of the libraries valid.
        codeSplitting: {
          groups: [
            { name: "ui", test: /[\\/]node_modules[\\/]@radix-ui[\\/]/, priority: 2 },
            { name: "vendor", test: /[\\/]node_modules[\\/]/, priority: 1 },
          ],
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
