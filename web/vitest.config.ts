import { defineConfig } from "vitest/config";
import { fileURLToPath, URL } from "node:url";

// Unit tests for the pure libraries (formatters, time, routing). Browser behaviour is covered by Playwright in e2e/.
export default defineConfig({
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  test: {
    include: ["src/**/*.test.ts"],
    environment: "node",
  },
});
