import "@supabase/functions-js/edge-runtime.d.ts";

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { jwtVerify, SignJWT } from "https://esm.sh/jose@5";

const corsHeaders: HeadersInit = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-client-info",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

function json(status: number, body: Record<string, unknown>): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...corsHeaders, "Content-Type": "application/json; charset=utf-8" },
  });
}

function getBearer(req: Request): string | null {
  const raw = req.headers.get("authorization") ?? req.headers.get("Authorization");
  if (!raw) return null;
  const m = raw.match(/^Bearer\s+(.+)$/i);
  return m ? m[1].trim() : null;
}

function isHex64(v: string): boolean {
  return /^[0-9a-f]{64}$/i.test(v);
}

function getSecretKeyBytes() {
  const secret = (Deno.env.get("VEILNET_JWT_SECRET") ?? "").trim();
  if (!secret) throw new Error("VEILNET_JWT_SECRET missing");
  return new TextEncoder().encode(secret);
}

async function verifyVeilnetLauncherToken(token: string): Promise<{ userId: string }> {
  const key = getSecretKeyBytes();
  const { payload } = await jwtVerify(token, key, { algorithms: ["HS256"] });

  // If typ exists, enforce it (older tokens may omit typ).
  const typ = (payload as any).typ;
  if (typ && typ !== "launcher") throw new Error("invalid token type");

  const sub = (payload.sub ?? "").toString().trim();
  if (!sub) throw new Error("missing sub");
  return { userId: sub };
}

function getServiceClient() {
  const url = (Deno.env.get("SUPABASE_URL") ?? "").trim();
  const serviceKey =
    (Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? Deno.env.get("SERVICE_ROLE_KEY") ?? "").trim();
  if (!url) throw new Error("SUPABASE_URL missing");
  if (!serviceKey) throw new Error("SUPABASE_SERVICE_ROLE_KEY missing");
  return createClient(url, serviceKey, { auth: { persistSession: false } });
}

// IMPORTANT FIX:
// Your DB table `game_hashes` does NOT have a `sha256` column (Postgres 42703).
// `game-hashes-get` may *return* a sha256 field, but it is synthesized from `hash`.
// So for online-ticket, we only select `hash`.
async function getAllowlistedHash(sb: any, target: string) {
  const { data, error } = await sb
    .from("game_hashes")
    .select("id,hash,target,is_active")
    .eq("target", target)
    .eq("is_active", true)
    .order("id", { ascending: false })
    .limit(1)
    .maybeSingle();

  return { data, error };
}

async function mintGateTicket(userId: string, target: string, buildHash: string) {
  const key = getSecretKeyBytes();
  const now = Math.floor(Date.now() / 1000);
  const exp = now + 15 * 60;

  return await new SignJWT({ typ: "gate_ticket", target, build_hash: buildHash })
    .setProtectedHeader({ alg: "HS256" })
    .setSubject(userId)
    .setIssuedAt(now)
    .setExpirationTime(exp)
    .sign(key);
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: corsHeaders });
  if (req.method !== "POST") return json(405, { ok: false, error: "method_not_allowed" });

  const bearer = getBearer(req);
  if (!bearer) return json(401, { ok: false, error: "unauthorized" });

  let userId: string;
  try {
    userId = (await verifyVeilnetLauncherToken(bearer)).userId;
  } catch {
    return json(401, { ok: false, error: "unauthorized" });
  }

  let body: any;
  try {
    body = await req.json();
  } catch {
    return json(400, { ok: false, error: "bad_json" });
  }

  const target = (body?.target ?? "").toString().trim().toLowerCase();
  const exeHash = (body?.exe_hash_sha256 ?? body?.exeHashSha256 ?? "").toString().trim().toLowerCase();

  if (target !== "dev" && target !== "release") return json(400, { ok: false, error: "bad_target" });
  if (!isHex64(exeHash)) return json(400, { ok: false, error: "bad_hash" });

  let sb;
  try {
    sb = getServiceClient();
  } catch {
    return json(500, { ok: false, error: "server_misconfigured" });
  }

  const { data: allow, error: allowErr } = await getAllowlistedHash(sb, target);
  if (allowErr) {
    return json(503, {
      ok: false,
      error: "service_error",
      message: "Database query failed",
      details: { code: allowErr.code, message: allowErr.message },
    });
  }
  if (!allow) return json(503, { ok: false, error: "no_active_hash" });

  const expected = (allow.hash ?? "").toString().trim().toLowerCase();
  if (!expected || exeHash !== expected) {
    return json(403, { ok: false, error: "hash_mismatch", lanAllowed: true });
  }

  // Generate and store the ticket
  const ticket = await mintGateTicket(userId, target, exeHash);
  
  // Store the ticket in the database for validation by eos-secret
  // Cache refresh: 2026-02-21-19:30
  try {
    const expiresAt = new Date(Date.now() + 15 * 60 * 1000).toISOString();
    // Use direct SQL to bypass PostgREST cache issues
    const { error: insertError } = await sb.rpc('insert_online_ticket', {
      p_user_id: userId,
      p_target: target,
      p_build_hash: exeHash,
      p_expires_at: expiresAt,
      p_ticket_jwt: ticket
    });

    if (insertError) {
      console.error("Failed to store ticket:", insertError);
      return json(500, {
        ok: false,
        error: "database_error",
        message: "Failed to store ticket",
        details: {
          code: (insertError as any)?.code,
          message: (insertError as any)?.message,
          details: (insertError as any)?.details,
          hint: (insertError as any)?.hint,
        },
      });
    }
    
    console.log("Ticket stored successfully for user:", userId);
  } catch (error) {
    console.error("Database insert error:", error);
    return json(500, { ok: false, error: "database_error", message: "Failed to store ticket" });
  }

  return json(200, {
    ok: true,
    ticket,
    expiresUtc: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
    eos: null,
  });
});
