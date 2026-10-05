import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { jwtVerify } from "https://deno.land/x/jose@v4.14.4/index.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET, OPTIONS"
};

function json(data: Record<string, unknown>, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { ...corsHeaders, "Content-Type": "application/json" }
  });
}

function fail(status: number, error: string, extra: Record<string, unknown> = {}) {
  return json({ ok: false, error, ...extra }, status);
}

function getRequiredEnv(name: string) {
  const value = Deno.env.get(name)?.trim();
  if (!value) throw new Error(`Missing required environment variable: ${name}`);
  return value;
}

function parseBearerToken(authHeader: string | null) {
  if (!authHeader) return null;
  const value = authHeader.trim();
  if (!value.toLowerCase().startsWith("bearer ")) return null;
  const token = value.slice(7).trim();
  return token || null;
}

async function resolveUserId(supabaseUrl: string, anonKey: string, token: string) {
  const authClient = createClient(supabaseUrl, anonKey, { auth: { persistSession: false } });
  const { data: userData } = await authClient.auth.getUser(token);
  const nativeUserId = (userData?.user?.id ?? "").trim();
  if (nativeUserId) return nativeUserId;

  const secret = Deno.env.get("VEILNET_JWT_SECRET")?.trim();
  if (!secret) return "";
  try {
    const key = new TextEncoder().encode(secret);
    const { payload } = await jwtVerify(token, key, { algorithms: ["HS256"] });
    if (payload.typ !== "launcher" || typeof payload.sub !== "string") return "";
    return payload.sub.trim();
  } catch {
    return "";
  }
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: corsHeaders });
  if (req.method !== "GET") return fail(405, "method_not_allowed");

  try {
    const supabaseUrl = getRequiredEnv("SUPABASE_URL");
    const anonKey = getRequiredEnv("SUPABASE_ANON_KEY");
    const serviceRoleKey = getRequiredEnv("SUPABASE_SERVICE_ROLE_KEY");
    const token = parseBearerToken(req.headers.get("Authorization") || req.headers.get("authorization"));
    if (!token) return fail(401, "missing_auth_token");

    const userId = await resolveUserId(supabaseUrl, anonKey, token);
    if (!userId) return fail(401, "invalid_auth_token");

    const admin = createClient(supabaseUrl, serviceRoleKey, { auth: { persistSession: false } });
    const { data, error } = await admin
      .from("player_skins")
      .select("slot, skin_hash, skin_png_base64, display_name, has_layers, byte_length, updated_at")
      .eq("user_id", userId)
      .order("slot", { ascending: true });

    if (error) return fail(500, "skin_lookup_failed", { detail: error.message });
    const rows = data ?? [];
    if (rows.length === 0) return json({ ok: true, hasSkin: false });

    // Backward-compatible single-skin view: the primary skin (slot 0), or the
    // lowest slot present for users whose rows predate slots.
    const primary = rows.find((r) => r.slot === 0) ?? rows[0];

    return json({
      ok: true,
      hasSkin: true,
      skin: {
        hash: primary.skin_hash,
        pngBase64: primary.skin_png_base64,
        displayName: primary.display_name,
        hasLayers: primary.has_layers,
        byteLength: primary.byte_length,
        updatedAt: primary.updated_at
      },
      skins: rows.map((r) => ({
        slot: r.slot ?? 0,
        hash: r.skin_hash,
        pngBase64: r.skin_png_base64,
        displayName: r.display_name,
        hasLayers: r.has_layers,
        byteLength: r.byte_length,
        updatedAt: r.updated_at
      }))
    });
  } catch (err) {
    console.error("[player-skin-get] fatal", err);
    return fail(500, "internal_error");
  }
});
