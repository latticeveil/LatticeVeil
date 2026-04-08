import { serve } from "https://deno.land/std@0.168.0/http/server.ts"
import { createClient } from 'https://esm.sh/@supabase/supabase-js@2'
import { jwtVerify } from "https://deno.land/x/jose@v4.14.4/index.ts"

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-veilnet-auth",
  "Access-Control-Allow-Methods": "POST, OPTIONS"
};

function json(body: any, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      ...CORS_HEADERS,
      "Content-Type": "application/json"
    }
  });
}

function fail(status: number, error: string, extra = {}) {
  return json({
    ok: false,
    error,
    ...extra
  }, status);
}

// Robust JWT validation for Veilnet launcher tokens
async function validateVeilnetToken(req: Request): Promise<{ valid: boolean; userId?: string; username?: string; error?: string }> {
  const tokenHeader = req.headers.get('x-veilnet-auth') || req.headers.get('authorization')
  if (!tokenHeader) return { valid: false, error: 'Missing auth token' }

  const token = tokenHeader.startsWith('Bearer ') ? tokenHeader.substring(7) : tokenHeader
  const secret = Deno.env.get('VEILNET_JWT_SECRET')
  if (!secret) return { valid: false, error: 'Server configuration error' }

  try {
    const key = new TextEncoder().encode(secret)
    const { payload } = await jwtVerify(token, key, { algorithms: ['HS256'] })

    if (payload.typ !== 'launcher') return { valid: false, error: 'Invalid token type' }
    if (!payload.sub || typeof payload.sub !== 'string') return { valid: false, error: 'Missing or invalid user ID in token' }

    return { 
      valid: true, 
      userId: payload.sub, 
      username: typeof payload.username === 'string' ? payload.username : undefined
    }
  } catch (error: any) {
    return { valid: false, error: 'Token validation failed: ' + error.code }
  }
}

function isUuid(value: any) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(String(value || "").trim());
}

function isNoRows(error: any) {
  const code = String(error?.code ?? "");
  return code === "PGRST116";
}

async function getFriendRow(admin: any, userId: string, friendId: string) {
  const { data, error } = await admin.from("friends").select("user_id, friend_id, status").eq("user_id", userId).eq("friend_id", friendId).maybeSingle();
  if (error && !isNoRows(error)) throw error;
  return data ?? null;
}

serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: CORS_HEADERS });
  if (req.method !== "POST") return fail(405, "method_not_allowed");

  try {
    const tokenValidation = await validateVeilnetToken(req)
    if (!tokenValidation.valid) {
      console.warn("[friend-respond] invalid_auth_token", tokenValidation.error);
      return fail(401, "invalid_auth_token", { message: tokenValidation.error });
    }

    const requesterId = tokenValidation.userId!;
    console.log("[friend-respond] auth ok", { userId: requesterId });

    const supabaseUrl = Deno.env.get('SUPABASE_URL')!;
    const serviceRoleKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
    const admin = createClient(supabaseUrl, serviceRoleKey, { auth: { persistSession: false } });

    const body = await req.json().catch(()=>null);
    if (!body || typeof body !== "object") return fail(400, "invalid_payload");

    const sourceId = String(body.requesterProductUserId ?? "").trim();
    if (!isUuid(sourceId)) return fail(400, "invalid_requester");
    const accept = body.accept === true;
    const block = body.block === true;

    const incoming = await getFriendRow(admin, sourceId, requesterId);
    if (!incoming || incoming.status !== "pending") return fail(404, "request_not_found");

    if (block) {
      await admin.from("friends").delete().eq("user_id", sourceId).eq("friend_id", requesterId);
      await admin.from("friends").delete().eq("user_id", requesterId).eq("friend_id", sourceId);
      const blockUpsert = await admin.from("friends").upsert({ user_id: requesterId, friend_id: sourceId, status: "blocked" }, { onConflict: "user_id,friend_id" });
      if (blockUpsert.error) return fail(500, "db_error", { detail: blockUpsert.error.message });
      return json({ ok: true, status: "blocked", message: "User blocked." });
    }

    if (!accept) {
      const removePending = await admin.from("friends").delete().eq("user_id", sourceId).eq("friend_id", requesterId).eq("status", "pending");
      if (removePending.error) return fail(500, "db_error", { detail: removePending.error.message });
      return json({ ok: true, status: "declined", message: "Friend request declined." });
    }

    const setIncomingAccepted = await admin.from("friends").update({ status: "accepted" }).eq("user_id", sourceId).eq("friend_id", requesterId).eq("status", "pending");
    if (setIncomingAccepted.error) return fail(500, "db_error", { detail: setIncomingAccepted.error.message });

    const setDirectAccepted = await admin.from("friends").upsert({ user_id: requesterId, friend_id: sourceId, status: "accepted" }, { onConflict: "user_id,friend_id" });
    if (setDirectAccepted.error) return fail(500, "db_error", { detail: setDirectAccepted.error.message });

    return json({ ok: true, status: "accepted", message: "Friend request accepted." });
  } catch (err: any) {
    console.error("[friend-respond] fatal", err);
    return fail(500, "internal_error", { detail: err?.message || "unknown" });
  }
});
