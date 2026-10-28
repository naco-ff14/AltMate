import { createClient } from "@supabase/supabase-js";
import { configError } from "./config";
const url = import.meta.env.VITE_SUPABASE_URL?.trim() ?? "";
const key = import.meta.env.VITE_SUPABASE_ANON_KEY?.trim() ?? "";
export const configurationError = configError(url, key);
export const supabase = configurationError
  ? null
  : createClient(url, key, {
      auth: {
        persistSession: true,
        autoRefreshToken: true,
        detectSessionInUrl: false,
      },
    });
