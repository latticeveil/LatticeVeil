import { createClient } from "https://esm.sh/@supabase/supabase-js@2.49.1";

const corsHeaders: HeadersInit = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

type SessionUpsertPayload = {
  product_user_id?: string | null;
  display_name?: string | null;
  world_name?: string | null;
  game_mode?: string | null;
  join_target?: string | null;
  status?: string | null;
  cheats?: boolean;
  player_count?: number;
  max_players?: number;
  is_in_world?: boolean;
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

function normalizeText(value: unknown, fallback = ""): string {
  if (typeof value !== "string") return fallback;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : fallback;
}

function normalizeBool(value: unknown, fallback: boolean): boolean {
  return typeof value === "boolean" ? value : fallback;
}

function normalizeInt(value: unknown, fallback: number, min = 0): number {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.max(min, Math.floor(parsed));
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
    console.error("[session-host-start] misconfigured_env", { missing });
    return jsonResponse({ ok: false, error: "misconfigured_env", missing }, 500);
  }

  const accessToken = readBearer(req);
  if (!accessToken) {
    console.warn("[session-host-start] missing_auth");
    return jsonResponse({ ok: false, error: "missing_auth" }, 401);
  }

  const supabase = createClient(supabaseUrl, serviceRoleKey, {
    auth: { persistSession: false, autoRefreshToken: false },
  });

  const { data: authData, error: authError } = await supabase.auth.getUser(accessToken);
  const user = authData?.user;
  if (authError || !user) {
    console.warn("[session-host-start] invalid_auth", { message: authError?.message ?? "no_user" });
    return jsonResponse({ ok: false, error: "invalid_auth_token" }, 401);
  }

  let body: SessionUpsertPayload = {};
  try {
    body = (await req.json()) as SessionUpsertPayload;
  } catch {
    body = {};
  }

  const hostUserId = user.id;
  const productUserId = normalizeText(body.product_user_id);
  const joinTarget = normalizeText(body.join_target, productUserId);
  const hostUsername = normalizeText(body.display_name, user.email?.split("@")[0] ?? "Player");
  const worldName = normalizeText(body.world_name, "WORLD");
  const gameMode = normalizeText(body.game_mode) || null;
  const status = normalizeText(body.status, `Hosting ${worldName}`);
  const cheats = normalizeBool(body.cheats, false);
  const playerCount = normalizeInt(body.player_count, 1, 1);
  const maxPlayers = Math.max(playerCount, normalizeInt(body.max_players, 8, 1));
  const isInWorld = normalizeBool(body.is_in_world, false);

  const now = Date.now();
  const expiresAt = new Date(now + 30_000).toISOString();

  const upsertPayload = {
    host_user_id: hostUserId,
    host_product_user_id: productUserId || null,
    host_username: hostUsername,
    world_name: worldName,
    mode: gameMode,
    cheats,
    player_count: playerCount,
    max_players: maxPlayers,
    join_target: joinTarget || null,
    status,
    is_hosting: true,
    is_in_world: isInWorld,
    updated_at: new Date(now).toISOString(),
    expires_at: expiresAt,
  };

  const { data, error } = await supabase
    .from("online_sessions")
    .upsert(upsertPayload, { onConflict: "host_user_id" })
    .select("id, host_user_id, host_product_user_id, host_username, world_name, mode, cheats, player_count, max_players, join_target, status, is_hosting, is_in_world, updated_at, expires_at")
    .maybeSingle();

  if (error) {
    console.error("[session-host-start] db_error", { code: error.code, message: error.message });
    return jsonResponse({ ok: false, error: "db_error", message: error.message }, 500);
  }

  console.log("[session-host-start] ok", {
    hostUserId,
    worldName,
    gameMode,
    playerCount,
    isInWorld,
  });

  return jsonResponse({ ok: true, session: data, expires_at: data?.expires_at ?? expiresAt });
});
