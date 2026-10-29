import { createClient } from "@supabase/supabase-js";
import { configError } from "./config";
const url = import.meta.env.VITE_SUPABASE_URL?.trim() ?? "";
const key = import.meta.env.VITE_SUPABASE_ANON_KEY?.trim() ?? "";
export const configurationError = configError(url, key);
// Capture the intent before the auth client consumes and removes the URL fragment.
export const recoveryRequested =
  new URLSearchParams(window.location.hash.slice(1)).get("type") ===
    "recovery" ||
  new URLSearchParams(window.location.search).get("reset") === "password";
export const supabase = configurationError
  ? null
  : createClient(url, key, {
      auth: {
        persistSession: true,
        autoRefreshToken: true,
        detectSessionInUrl: true,
      },
    });
