import js from "@eslint/js";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import { defineConfig } from "eslint/config";

/**
 * Numbers and dates go through web/src/lib/format.ts and lib/time.ts so every figure reads the same everywhere.
 * Formatting directly with .toFixed(), toLocale*() or new Intl.* outside lib/ is an error.
 */
const formattingBan = [
  {
    selector: "CallExpression[callee.property.name='toFixed']",
    message: "Use kwh/gbp/pence/percent/number from lib/format.ts instead of .toFixed().",
  },
  {
    selector: "CallExpression[callee.property.name=/^toLocale/]",
    message: "Use lib/format.ts (numbers) or lib/time.ts (dates) instead of toLocale*().",
  },
  {
    selector: "NewExpression[callee.object.name='Intl']",
    message: "Use lib/format.ts or lib/time.ts instead of new Intl.*.",
  },
];

/**
 * Files that still format numbers and dates inline. They warn rather than fail until each is converted;
 * remove a file from this list when it uses lib/format and lib/time.
 */
const notYetConverted = [
  "src/components/EnergyEvidence.tsx",
  "src/components/ExperimentCard.tsx",
  "src/components/InvestigationText.tsx",
];

export default defineConfig(
  { ignores: ["dist", "node_modules", "e2e", "playwright-report", "test-results"] },
  js.configs.recommended,
  tseslint.configs.recommended,
  {
    files: ["src/**/*.{ts,tsx}"],
    plugins: { "react-hooks": reactHooks },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "warn",
      "no-restricted-syntax": ["error", ...formattingBan],
      // Existing code uses these freely; they are style, not correctness.
      "@typescript-eslint/no-explicit-any": "off",
      "@typescript-eslint/no-unused-vars": ["warn", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
      "@typescript-eslint/no-unused-expressions": "off",
      "no-empty": ["error", { allowEmptyCatch: true }],
    },
  },
  { files: notYetConverted, rules: { "no-restricted-syntax": ["warn", ...formattingBan] } },
  // The formatting libraries themselves, and the period/comparison helpers that work on calendar dates.
  { files: ["src/lib/**"], rules: { "no-restricted-syntax": "off" } },
);
