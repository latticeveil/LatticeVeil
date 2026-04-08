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

function sanitizeUsername(input: any) {
  const raw = String(input ?? "").replace(/\r\n/g, "\n").replace(/[\u0000-\u001F\u007F]/g, "").trim();
  if (!raw || raw.length > 64) return "";
  return raw;
}

function isNoRows(error: any) {
  const code = String(error?.code ?? "");
  return code === "PGRST116";
}

async function resolveProfileByUsername(admin: any, username: string) {
  const exact = await admin.from("profiles").select("id, username").eq("username", username).maybeSingle();
  if (!exact.error && exact.data) return exact.data;
  if (exact.error && !isNoRows(exact.error)) throw exact.error;
  const insensitive = await admin.from("profiles").select("id, username").ilike("username", username).limit(1).maybeSingle();
  if (!insensitive.error && insensitive.data) return insensitive.data;
  if (insensitive.error && !isNoRows(insensitive.error)) throw insensitive.error;
  return null;
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
      console.warn("[friend-request] invalid_auth_token", tokenValidation.error);
      return fail(401, "invalid_auth_token", { message: tokenValidation.error });
    }

    const requesterId = tokenValidation.userId!;
    console.log("[friend-request] auth ok", { userId: requesterId });

    const supabaseUrl = Deno.env.get('SUPABASE_URL')!;
    const serviceRoleKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
    const admin = createClient(supabaseUrl, serviceRoleKey, { auth: { persistSession: false } });

    const body = await req.json().catch(()=>null);
    if (!body || typeof body !== "object") return fail(400, "invalid_payload");

    const targetIdInput = String(body.targetId ?? "").trim();
    const usernameInput = sanitizeUsername(body.username ?? "");

    let targetId = "";
    let targetUsername = "";

    if (isUuid(targetIdInput)) {
      const { data: profile, error: profileError } = await admin.from("profiles").select("id, username").eq("id", targetIdInput).maybeSingle();
      if (profileError && !isNoRows(profileError)) return fail(500, "db_error", { detail: profileError.message });
      if (!profile?.id) return fail(404, "target_not_found");
      targetId = profile.id;
      targetUsername = String(profile.username ?? "").trim();
    } else if (usernameInput) {
      const profile = await resolveProfileByUsername(admin, usernameInput);
      if (!profile?.id) return fail(404, "target_not_found");
      targetId = profile.id;
      targetUsername = String(profile.username ?? "").trim();
    } else {
      return fail(400, "missing_target");
    }

    if (targetId === requesterId) return fail(409, "self_friend");

    const reverse = await getFriendRow(admin, targetId, requesterId);
    if (reverse?.status === "blocked") return fail(403, "blocked_by_target");

    const current = await getFriendRow(admin, requesterId, targetId);
    if (current?.status === "accepted") {
      return json({ ok: true, status: "accepted", message: "Already friends.", target: { id: targetId, username: targetUsername } });
    }
    if (current?.status === "pending") {
      return json({ ok: true, status: "pending", message: "Request already pending.", target: { id: targetId, username: targetUsername } });
    }
    if (current?.status === "blocked") return fail(409, "blocked_by_you");

    if (reverse?.status === "pending" || reverse?.status === "accepted") {
      const acceptedStatus = "accepted";
      const reverseUpdate = await admin.from("friends").update({ status: acceptedStatus }).eq("user_id", targetId).eq("friend_id", requesterId);
      if (reverseUpdate.error) return fail(500, "db_error", { detail: reverseUpdate.error.message });
      const directAccepted = await admin.from("friends").upsert({ user_id: requesterId, friend_id: targetId, status: acceptedStatus }, { onConflict: "user_id,friend_id" });
      if (directAccepted.error) return fail(500, "db_error", { detail: directAccepted.error.message });
      return json({ ok: true, status: acceptedStatus, message: "Friend request accepted.", target: { id: targetId, username: targetUsername } });
    }

    const pendingInsert = await admin.from("friends").upsert({ user_id: requesterId, friend_id: targetId, status: "pending" }, { onConflict: "user_id,friend_id" });
    if (pendingInsert.error) return fail(500, "db_error", { detail: pendingInsert.error.message });

    return json({ ok: true, status: "pending", message: "Request sent.", target: { id: targetId, username: targetUsername } });
  } catch (err: any) {
    console.error("[friend-request] fatal", err);
    return fail(500, "internal_error", { detail: err?.message || "unknown" });
  }
});
