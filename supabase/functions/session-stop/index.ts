import { createClient } from "https://esm.sh/@supabase/supabase-js@2.49.1";

const corsHeaders: HeadersInit = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: {
      ...corsHeaders,
      "Content-Type": "application/json; charset=utf-8",
    },
  });
}

function requiredEnv(name: string): string {
  return (Deno.env.get(name) ?? "").trim();
}

function readBearer(req: Request): string | null {
  const header = req.headers.get("authorization") ?? "";
  const match = header.match(/^Bearer\s+(.+)$/i);
  return match?.[1]?.trim() || null;
}

Deno.serve(async (req: Request) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: corsHeaders });
  }

  if (req.method !== "POST") {
    return jsonResponse({ ok: false, error: "method_not_allowed" }, 405);
  }

  const supabaseUrl = requiredEnv("SUPABASE_URL");
  const serviceRoleKey = requiredEnv("SUPABASE_SERVICE_ROLE_KEY");
  if (!supabaseUrl || !serviceRoleKey) {
    const missing = [
      !supabaseUrl ? "SUPABASE_URL" : "",
      !serviceRoleKey ? "SUPABASE_SERVICE_ROLE_KEY" : "",
    ].filter(Boolean);
    console.error("[session-stop] misconfigured_env", { missing });
    return jsonResponse({ ok: false, error: "misconfigured_env", missing }, 500);
  }

  const accessToken = readBearer(req);
  if (!accessToken) {
    console.warn("[session-stop] missing_auth");
    return jsonResponse({ ok: false, error: "missing_auth" }, 401);
  }

  const supabase = createClient(supabaseUrl, serviceRoleKey, {
    auth: { persistSession: false, autoRefreshToken: false },
  });

  const { data: authData, error: authError } = await supabase.auth.getUser(accessToken);
  const user = authData?.user;
  if (authError || !user) {
    console.warn("[session-stop] invalid_auth", { message: authError?.message ?? "no_user" });
    return jsonResponse({ ok: false, error: "invalid_auth_token" }, 401);
  }

  const hostUserId = user.id;
  const nowIso = new Date().toISOString();

  const { data, error } = await supabase
    .from("online_sessions")
    .update({
      is_hosting: false,
      is_in_world: false,
      status: "Offline",
      updated_at: nowIso,
      expires_at: nowIso,
    })
    .eq("host_user_id", hostUserId)
    .select("id")
    .maybeSingle();

  if (error) {
    console.error("[session-stop] db_error", { code: error.code, message: error.message });
    return jsonResponse({ ok: false, error: "db_error", message: error.message }, 500);
  }

  console.log("[session-stop] ok", { hostUserId, hadRow: !!data?.id });
  return jsonResponse({ ok: true, stopped: !!data?.id });
});
