import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS"
};
const LAUNCHER_TOKEN_DAYS = 30;
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
    ok: false,
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
function bytesToHex(bytes) {
  let out = "";
  for(let i = 0; i < bytes.length; i += 1){
    out += bytes[i].toString(16).padStart(2, "0");
  }
  return out;
}
function toBase64Url(bytes) {
  let s = "";
  for(let i = 0; i < bytes.length; i += 1){
    s += String.fromCharCode(bytes[i]);
  }
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}
function encodeJsonBase64Url(data) {
  const bytes = new TextEncoder().encode(JSON.stringify(data));
  return toBase64Url(bytes);
}
function normalizeCode(input) {
  return String(input || "").toUpperCase().replace(/[^A-Z0-9]/g, "").trim();
}
function formatCode(raw) {
  const code = normalizeCode(raw);
  if (code.length <= 4) return code;
  return `${code.slice(0, 4)}-${code.slice(4)}`;
}
function buildCodeCandidates(raw) {
  const normalized = normalizeCode(raw);
  const values = new Set();
  if (normalized) {
    values.add(normalized);
    values.add(formatCode(normalized));
  }
  return Array.from(values);
}
async function hashCode(code, secret) {
  const payload = new TextEncoder().encode(`${normalizeCode(code)}:${secret}`);
  const digest = await crypto.subtle.digest("SHA-256", payload);
  return bytesToHex(new Uint8Array(digest));
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
async function mintLauncherToken(secret, payload) {
  const headerPart = encodeJsonBase64Url({
    alg: "HS256",
    typ: "JWT"
  });
  const payloadPart = encodeJsonBase64Url(payload);
  const signingInput = `${headerPart}.${payloadPart}`;
  const signature = await hmacSha256(signingInput, secret);
  const signaturePart = toBase64Url(signature);
  return `${signingInput}.${signaturePart}`;
}
function parseCode(body) {
  if (!body || typeof body !== "object") return null;
  const raw = body.code;
  if (typeof raw !== "string") return null;
  const normalized = normalizeCode(raw);
  if (!normalized) return null;
  if (normalized.length > 24) return null;
  return normalized;
}
function asDbError(error) {
  const err = error;
  return {
    code: String(err?.code || "unknown"),
    message: String(err?.message || "unknown_db_error")
  };
}
Deno.serve(async (req)=>{
  if (req.method === "OPTIONS") {
    return new Response(null, {
      headers: corsHeaders
    });
  }
  if (req.method !== "POST") {
    return fail(405, "method_not_allowed");
  }
  try {
    const supabaseUrl = getRequiredEnv("SUPABASE_URL");
    const serviceRoleKey = getRequiredEnv("SUPABASE_SERVICE_ROLE_KEY");
    const jwtSecret = getRequiredEnv("VEILNET_JWT_SECRET");
    const rawBody = await req.json().catch(()=>null);
    const code = parseCode(rawBody);
    if (!code) {
      return fail(400, "invalid_or_expired");
    }
    const admin = createClient(supabaseUrl, serviceRoleKey, {
      auth: {
        persistSession: false
      }
    });
    const nowIso = new Date().toISOString();
    const codeCandidates = buildCodeCandidates(code);
    let userId = "";

    const plainMatch = await admin.from("launcher_link_codes").select("id, user_id, expires_at").in("code", codeCandidates).is("consumed_at", null).gt("expires_at", nowIso).order("expires_at", { ascending: false }).limit(1).maybeSingle();

    if (!plainMatch.error && plainMatch.data?.id) {
      // CLEAR AFTER USE: Delete instead of update
      const { data: deleteData, error: deleteError } = await admin.from("launcher_link_codes").delete().eq("id", plainMatch.data.id).select("user_id").maybeSingle();

      if (deleteError) {
        const dbErr = asDbError(deleteError);
        return fail(500, "db_error", { stage: "delete_plain", db_code: dbErr.code, detail: dbErr.message });
      }
      if (deleteData?.user_id) {
        userId = String(deleteData.user_id);
      }
    } else if (plainMatch.error && plainMatch.error.code !== "42703") {
      const dbErr = asDbError(plainMatch.error);
      return fail(500, "db_error", { stage: "lookup_plain", db_code: dbErr.code, detail: dbErr.message });
    }

    if (!userId) {
      const codeHash = await hashCode(code, jwtSecret);
      const legacyMatch = await admin.from("launcher_link_codes").select("id, user_id, expires_at").eq("code_hash", codeHash).is("used_at", null).gt("expires_at", nowIso).maybeSingle();

      if (legacyMatch.error) {
        if (legacyMatch.error.code === "42703" || legacyMatch.error.code === "PGRST116") {
          return fail(400, "invalid_or_expired");
        }
        const dbErr = asDbError(legacyMatch.error);
        return fail(500, "db_error", { stage: "lookup_legacy", db_code: dbErr.code, detail: dbErr.message });
      }

      if (!legacyMatch.data?.id) {
        return fail(400, "invalid_or_expired");
      }

      // CLEAR AFTER USE: Delete instead of update
      const { data: deleteLegacy, error: deleteLegacyErr } = await admin.from("launcher_link_codes").delete().eq("id", legacyMatch.data.id).select("user_id").maybeSingle();

      if (deleteLegacyErr) {
        const dbErr = asDbError(deleteLegacyErr);
        return fail(500, "db_error", { stage: "delete_legacy", db_code: dbErr.code, detail: dbErr.message });
      }
      if (!deleteLegacy?.user_id) {
        return fail(400, "invalid_or_expired");
      }
      userId = String(deleteLegacy.user_id);
    }
    if (!userId) {
      return fail(400, "invalid_or_expired");
    }
    const { data: profile, error: profileError } = await admin.from("profiles").select("username").eq("id", userId).maybeSingle();
    if (profileError) {
      const dbErr = asDbError(profileError);
      return fail(500, "db_error", {
        stage: "profile_lookup",
        db_code: dbErr.code,
        detail: dbErr.message
      });
    }
    const username = String(profile?.username || "").trim();
    if (!username) {
      return fail(409, "username_required");
    }
    const nowSec = Math.floor(Date.now() / 1000);
    const expSec = nowSec + LAUNCHER_TOKEN_DAYS * 24 * 60 * 60;
    const token = await mintLauncherToken(jwtSecret, {
      sub: userId,
      username,
      typ: "launcher",
      iat: nowSec,
      exp: expSec
    });
    return json({
      ok: true,
      token,
      username,
      user_id: userId,
      userId
    });
  } catch (err) {
    const detail = String(err?.message || err || "unknown_error");
    console.error("[launcher-exchange] fatal", err);
    return fail(500, "internal_error", {
      detail
    });
  }
});
