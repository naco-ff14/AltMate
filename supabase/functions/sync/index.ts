import { createClient } from "npm:@supabase/supabase-js@2.118.0";
import { handleSync } from "../_shared/sync.ts";

// These are Supabase-provided server environment values. Never copy them to Vite or the agent.
const url = Deno.env.get("SUPABASE_URL");
const key = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
if (!url || !key) throw new Error("Sync server configuration is missing");
const admin = createClient(url, key, { auth: { persistSession: false, autoRefreshToken: false } });
Deno.serve(request => handleSync(request, (name, args) => admin.rpc(name, args)));
