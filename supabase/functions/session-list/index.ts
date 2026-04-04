import { createClient } from "https://esm.sh/@supabase/supabase-js@2.49.1";

const corsHeaders: HeadersInit = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
};

type SessionListRequest = {
  host_user_ids?: string[];
  product_user_ids?: string[];
  friend_ids?: string[];
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

function normalizeStringList(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  const result: string[] = [];
  for (const raw of value) {
    if (typeof raw !== "string") continue;
    const normalized = raw.trim();
    if (normalized.length === 0) continue;
    result.push(normalized);
  }
  return Array.from(new Set(result));
}

Deno.serve(async (req: Request) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: corsHeaders });
  }

  if (req.method !== "GET" && req.method !== "POST") {
    return jsonResponse({ ok: false, error: "method_not_allowed" }, 405);
  }

  const supabaseUrl = requiredEnv("SUPABASE_URL");
  const anonKey = requiredEnv("SUPABASE_ANON_KEY");
  if (!supabaseUrl || !anonKey) {
    const missing = [
      !supabaseUrl ? "SUPABASE_URL" : "",
      !anonKey ? "SUPABASE_ANON_KEY" : "",
    ].filter(Boolean);
    console.error("[session-list] misconfigured_env", { missing });
    return jsonResponse({ ok: false, error: "misconfigured_env", missing }, 500);
  }

  const supabase = createClient(supabaseUrl, anonKey, {
    auth: { persistSession: false, autoRefreshToken: false },
  });

  let hostUserIds: string[] = [];

  if (req.method === "GET") {
    const url = new URL(req.url);
    const idsQuery = url.searchParams.get("host_user_ids") ?? url.searchParams.get("friend_ids") ?? "";
    if (idsQuery.trim().length > 0) {
      hostUserIds = idsQuery
        .split(",")
        .map((value) => value.trim())
        .filter((value) => value.length > 0);
    }
  } else {
    let body: SessionListRequest = {};
    try {
      body = (await req.json()) as SessionListRequest;
    } catch {
      body = {};
    }

    hostUserIds = normalizeStringList(body.host_user_ids);
    if (hostUserIds.length === 0) hostUserIds = normalizeStringList(body.friend_ids);
    if (hostUserIds.length === 0) hostUserIds = normalizeStringList(body.product_user_ids);
  }

  let query = supabase
    .from("online_sessions")
    .select("host_user_id, host_product_user_id, host_username, world_name, mode, cheats, player_count, max_players, join_target, status, is_hosting, is_in_world, updated_at, expires_at")
    .eq("is_hosting", true)
    .eq("is_in_world", true)
    .gt("expires_at", new Date().toISOString())
    .order("updated_at", { ascending: false });

  const { data, error } = await query;
  if (error) {
    console.error("[session-list] query_failed", { code: error.code, message: error.message });
    return jsonResponse({ ok: false, error: "query_failed", message: error.message }, 500);
  }

  const rows = (data ?? []) as Array<Record<string, unknown>>;
  let filtered = rows;
  if (hostUserIds.length > 0) {
    const lookup = new Set(hostUserIds.map((value) => value.toLowerCase()));
    filtered = rows.filter((row) => {
      const hostUserId = String(row["host_user_id"] ?? "").trim().toLowerCase();
      const hostProductUserId = String(row["host_product_user_id"] ?? "").trim().toLowerCase();
      return lookup.has(hostUserId) || lookup.has(hostProductUserId);
    });
  }

  console.log("[session-list] ok", {
    requestedHostUserIds: hostUserIds.length,
    returnedSessions: filtered.length,
  });

  return jsonResponse({ ok: true, sessions: filtered });
});
