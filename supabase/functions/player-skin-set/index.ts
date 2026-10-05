import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { jwtVerify } from "https://deno.land/x/jose@v4.14.4/index.ts";

const MaxPngBytes = 32 * 1024;
const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS"
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

function decodeBase64(value: string) {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function toHex(bytes: Uint8Array) {
  return Array.from(bytes).map((b) => b.toString(16).padStart(2, "0")).join("");
}

function isPng(bytes: Uint8Array) {
  return bytes.length >= 8
    && bytes[0] === 0x89
    && bytes[1] === 0x50
    && bytes[2] === 0x4e
    && bytes[3] === 0x47
    && bytes[4] === 0x0d
    && bytes[5] === 0x0a
    && bytes[6] === 0x1a
    && bytes[7] === 0x0a;
}

function cleanDisplayName(value: unknown) {
  const text = String(value ?? "").trim().replace(/[\r\n\t]/g, " ");
  return text.length > 0 ? text.slice(0, 32) : "Account Skin";
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: corsHeaders });
  if (req.method !== "POST") return fail(405, "method_not_allowed");

  try {
    const supabaseUrl = getRequiredEnv("SUPABASE_URL");
    const anonKey = getRequiredEnv("SUPABASE_ANON_KEY");
    const serviceRoleKey = getRequiredEnv("SUPABASE_SERVICE_ROLE_KEY");
    const token = parseBearerToken(req.headers.get("Authorization") || req.headers.get("authorization"));
    if (!token) return fail(401, "missing_auth_token");

    const userId = await resolveUserId(supabaseUrl, anonKey, token);
    if (!userId) return fail(401, "invalid_auth_token");

    const body = await req.json().catch(() => null) as Record<string, unknown> | null;
    const action = String(body?.action ?? "set").trim().toLowerCase();
    const admin = createClient(supabaseUrl, serviceRoleKey, { auth: { persistSession: false } });

    if (action === "clear") {
      const { error } = await admin.from("player_skins").delete().eq("user_id", userId);
      if (error) return fail(500, "skin_clear_failed", { detail: error.message });
      return json({ ok: true, hasSkin: false });
    }

    const hash = String(body?.hash ?? "").trim().toLowerCase();
    const pngBase64 = String(body?.pngBase64 ?? "").trim();
    if (!/^[0-9a-f]{64}$/.test(hash)) return fail(400, "invalid_skin_hash");
    if (!pngBase64 || pngBase64.length > 48000) return fail(400, "invalid_skin_payload");

    // Optional slot: 0 = primary/account skin (legacy default), 1..4 = extra
    // library slots. Unknown/invalid values fall back to slot 0 so older
    // clients keep the exact same behaviour as before.
    let slot = 0;
    if (body?.slot !== undefined && body?.slot !== null) {
      const parsed = Number(body.slot);
      if (Number.isInteger(parsed) && parsed >= 0 && parsed <= 4) slot = parsed;
    }

    let bytes: Uint8Array;
    try {
      bytes = decodeBase64(pngBase64);
    } catch {
      return fail(400, "invalid_skin_base64");
    }

    if (bytes.length <= 0 || bytes.length > MaxPngBytes) return fail(400, "skin_too_large");
    if (!isPng(bytes)) return fail(400, "skin_not_png");

    const actualHash = toHex(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)));
    if (actualHash !== hash) return fail(400, "skin_hash_mismatch");

    const payload = {
      user_id: userId,
      slot,
      skin_hash: hash,
      skin_png_base64: pngBase64,
      display_name: cleanDisplayName(body?.displayName),
      has_layers: body?.hasLayers === true,
      byte_length: bytes.length,
      updated_at: new Date().toISOString()
    };

    const { error } = await admin.from("player_skins").upsert(payload, { onConflict: "user_id,slot" });
    if (error) return fail(500, "skin_save_failed", { detail: error.message });
    return json({ ok: true, hasSkin: true, hash, slot, byteLength: bytes.length });
  } catch (err) {
    console.error("[player-skin-set] fatal", err);
    return fail(500, "internal_error");
  }
});
