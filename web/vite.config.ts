import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import { configError } from "./src/config";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, ".", "VITE_");
  const url = env.VITE_SUPABASE_URL?.trim() ?? "";
  const key = env.VITE_SUPABASE_ANON_KEY?.trim() ?? "";
  // Fail before bundling if a secret was mistakenly supplied. No values are logged.
  // Missing configuration builds a helpful setup screen, rather than a blank page.
  const error = configError(url, key);
  if ((url || key) && error) throw new Error(error);
  return { plugins: [react()] };
});
