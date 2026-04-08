import { serve } from "https://deno.land/std@0.168.0/http/server.ts"
import { createClient } from 'https://esm.sh/@supabase/supabase-js@2'
import { jwtVerify } from "https://deno.land/x/jose@v4.14.4/index.ts"

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-veilnet-auth",
  "Access-Control-Allow-Methods": "GET, OPTIONS"
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

function sanitizeUsername(input: any) {
  const raw = String(input ?? "").replace(/\r\n/g, "\n").replace(/[\u0000-\u001F\u007F]/g, "").trim();
  if (!raw || raw.length > 64) return "";
  return raw;
}

function isNoRows(error: any) {
  const code = String(error?.code ?? "");
  return code === "PGRST116";
}

async function lookupProfile(admin: any, username: string) {
  const exact = await admin.from("profiles").select("id, username, picture, banner").eq("username", username).maybeSingle();
  if (!exact.error && exact.data) return exact.data;
  if (exact.error && !isNoRows(exact.error)) throw exact.error;
  const insensitive = await admin.from("profiles").select("id, username, picture, banner").ilike("username", username).limit(1).maybeSingle();
  if (!insensitive.error && insensitive.data) return insensitive.data;
  if (insensitive.error && !isNoRows(insensitive.error)) throw insensitive.error;
  return null;
}

serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: CORS_HEADERS });
  if (req.method !== "GET") return fail(405, "method_not_allowed");

  try {
    const tokenValidation = await validateVeilnetToken(req)
    if (!tokenValidation.valid) {
      console.warn("[friend-lookup] invalid_auth_token", tokenValidation.error);
      return fail(401, "invalid_auth_token", { message: tokenValidation.error });
    }

    const requesterId = tokenValidation.userId!;
    console.log("[friend-lookup] auth ok", { userId: requesterId });

    const supabaseUrl = Deno.env.get('SUPABASE_URL')!;
    const serviceRoleKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
    const admin = createClient(supabaseUrl, serviceRoleKey, { auth: { persistSession: false } });

    const username = sanitizeUsername(new URL(req.url).searchParams.get("username"));
    if (!username) return fail(400, "invalid_username");

    const profile = await lookupProfile(admin, username);
    if (!profile) return json({ ok: true, found: false });

    return json({
      ok: true,
      found: true,
      profile: { id: profile.id, username: profile.username, picture: profile.picture, banner: profile.banner }
    });
  } catch (err: any) {
    console.error("[friend-lookup] fatal", err);
    return fail(500, "internal_error", { detail: err?.message || "unknown" });
  }
});
