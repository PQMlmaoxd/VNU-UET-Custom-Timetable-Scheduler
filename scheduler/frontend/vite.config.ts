import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  // WebView2 serves the bundle at the virtual-host root. Relative asset URLs also
  // keep the static output portable when it is inspected outside the desktop host.
  base: "./",
  plugins: [react()],
  server: {
    port: 5173,
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: "./vitest.setup.ts",
    exclude: ["node_modules", "dist", "tests/e2e/**"],
  },
});
