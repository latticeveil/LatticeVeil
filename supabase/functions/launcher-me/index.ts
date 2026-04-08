import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET, OPTIONS"
};
function json(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      ...corsHeaders,
      "Content-Type": "application/json"
    }
  });
}
function fail(status, error, extra) {
  return json({
    error,
    ...extra || {}
  }, status);
}
function getRequiredEnv(name) {
  const value = Deno.env.get(name)?.trim();
  if (!value) {
    throw new Error(`Missing required environment variable: ${name}`);
  }
  return value;
}
function toBase64Url(bytes) {
  let s = "";
  for(let i = 0; i < bytes.length; i += 1){
    s += String.fromCharCode(bytes[i]);
  }
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}
function fromBase64Url(value) {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - normalized.length % 4) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for(let i = 0; i < binary.length; i += 1){
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}
async function hmacSha256(input, secret) {
  const enc = new TextEncoder();
  const key = await crypto.subtle.importKey("raw", enc.encode(secret), {
    name: "HMAC",
    hash: "SHA-256"
  }, false, [
    "sign"
  ]);
  const sig = await crypto.subtle.sign("HMAC", key, enc.encode(input));
  return new Uint8Array(sig);
}
function parseBearerToken(authHeader) {
  if (!authHeader) return null;
  const value = authHeader.trim();
  if (!value.toLowerCase().startsWith("bearer ")) return null;
  const token = value.slice(7).trim();
  return token || null;
}
function isUuid(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(String(value || "").trim());
}
async function verifyLauncherToken(token, secret) {
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  const [headerPart, payloadPart, signaturePart] = parts;
  const signingInput = `${headerPart}.${payloadPart}`;
  const expectedSignature = await hmacSha256(signingInput, secret);
  const expectedSignaturePart = toBase64Url(expectedSignature);
  if (signaturePart !== expectedSignaturePart) return null;
  let header;
  let payload;
  try {
    header = JSON.parse(new TextDecoder().decode(fromBase64Url(headerPart)));
    payload = JSON.parse(new TextDecoder().decode(fromBase64Url(payloadPart)));
  } catch  {
    return null;
  }
  if (header.alg !== "HS256" || header.typ !== "JWT") return null;
  if (!payload || payload.typ !== "launcher") return null;
  if (!payload.sub || typeof payload.sub !== "string") return null;
  const nowSec = Math.floor(Date.now() / 1000);
  if (typeof payload.exp !== "number" || payload.exp <= nowSec) return null;
  return payload;
}
Deno.serve(async (req)=>{
  if (req.method === "OPTIONS") {
    return new Response(null, {
      headers: corsHeaders
    });
  }
  if (req.method !== "GET") {
    return fail(405, "method_not_allowed");
  }
  try {
    const supabaseUrl = getRequiredEnv("SUPABASE_URL");
    const supabaseAnonKey = getRequiredEnv("SUPABASE_ANON_KEY");
    const serviceRoleKey = getRequiredEnv("SUPABASE_SERVICE_ROLE_KEY");
    const jwtSecret = getRequiredEnv("VEILNET_JWT_SECRET");
    const authHeader = req.headers.get("Authorization") || req.headers.get("authorization");
    const token = parseBearerToken(authHeader);
    console.log("[launcher-me] auth headers", {
      hasAuthorization: !!token,
      hasGateTicket: !!(req.headers.get("x-gate-ticket") || "").trim()
    });
    if (!token) {
      return fail(401, "missing_auth_token");
    }
    const authClient = createClient(supabaseUrl, supabaseAnonKey, {
      auth: {
        persistSession: false
      }
    });
    const { data: userData, error: userError } = await authClient.auth.getUser(token);
    let profileUserId = (userData?.user?.id ?? "").trim();
    let authMode = "supabase";
    if (!profileUserId) {
      authMode = "launcher";
      const claims = await verifyLauncherToken(token, jwtSecret);
      profileUserId = claims?.sub?.trim() ?? "";
    }
    if (!profileUserId || !isUuid(profileUserId)) {
      console.warn("[launcher-me] invalid_auth_token", {
        userError: userError?.message ?? "none"
      });
      return fail(401, "invalid_auth_token");
    }
    console.log("[launcher-me] auth ok", {
      authMode
    });
    const admin = createClient(supabaseUrl, serviceRoleKey, {
      auth: {
        persistSession: false
      }
    });
    const { data: profile, error: profileError } = await admin.from("profiles").select("username, picture, banner, aboutme, themecolor, theme, updatedat").eq("id", profileUserId).maybeSingle();
    if (profileError) {
      console.error("[launcher-me] profile lookup failed", profileError);
      return fail(500, "profile_lookup_failed");
    }
    const username = String(profile?.username || "").trim();
    if (!username) {
      return fail(409, "username_required");
    }
    return json({
      username,
      picture: profile?.picture ?? null,
      banner: profile?.banner ?? null,
      aboutme: profile?.aboutme ?? null,
      themecolor: profile?.themecolor ?? null,
      theme: profile?.theme ?? null,
      updatedat: profile?.updatedat ?? null,
      user_id: profileUserId,
      userId: profileUserId
    });
  } catch (err) {
    console.error("[launcher-me] fatal", err);
    return fail(500, "internal_error");
  }
});
