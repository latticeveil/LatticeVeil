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

function shortId(id: string) {
  const clean = String(id || "").trim();
  if (!clean) return "Player";
  return clean.length <= 8 ? clean : clean.slice(0, 8);
}

function makeUser(id: string, map: Map<string, any>) {
  const profile = map.get(id);
  const username = (profile?.username ?? "").trim();
  const display = username || shortId(id);
  return {
    productUserId: id,
    username: display,
    displayName: display,
    friendCode: shortId(id)
  };
}

serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: CORS_HEADERS });
  if (req.method !== "GET") return fail(405, "method_not_allowed");

  try {
    const tokenValidation = await validateVeilnetToken(req)
    
    if (!tokenValidation.valid) {
      console.warn("[friend-list] invalid_auth_token", tokenValidation.error);
      return fail(401, "invalid_auth_token", { message: tokenValidation.error });
    }

    const requesterId = tokenValidation.userId!;
    console.log("[friend-list] auth ok", { userId: requesterId });

    const supabaseUrl = Deno.env.get('SUPABASE_URL')!;
    const serviceRoleKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
    
    const admin = createClient(supabaseUrl, serviceRoleKey, {
      auth: { persistSession: false }
    });

    const { data: rows, error: rowsErr } = await admin
      .from("friends")
      .select("user_id, friend_id, status, created_at")
      .or(`user_id.eq.${requesterId},friend_id.eq.${requesterId}`);

    if (rowsErr) return fail(500, "db_error", { detail: rowsErr.message });

    const list = Array.isArray(rows) ? rows : [];
    const relatedIds = new Set<string>();
    for (const row of list) {
      if (row.user_id !== requesterId) relatedIds.add(row.user_id);
      if (row.friend_id !== requesterId) relatedIds.add(row.friend_id);
    }

    const idList = Array.from(relatedIds);
    const profileMap = new Map<string, any>();
    if (idList.length > 0) {
      const { data: profiles, error: profilesErr } = await admin
        .from("profiles")
        .select("id, username, picture, banner")
        .in("id", idList);

      if (profilesErr) return fail(500, "db_error", { detail: profilesErr.message });
      for (const p of profiles ?? []) profileMap.set(p.id, p);
    }

    const friends = [];
    const incomingRequests = [];
    const outgoingRequests = [];
    const blockedUsers = [];
    const friendSeen = new Set();
    const blockedSeen = new Set();

    for (const row of list) {
      const status = String(row.status || "").trim().toLowerCase();
      const createdAt = row.created_at ?? new Date().toISOString();
      if (status === "accepted") {
        const otherId = row.user_id === requesterId ? row.friend_id : row.user_id;
        if (!friendSeen.has(otherId)) {
          friendSeen.add(otherId);
          friends.push(makeUser(otherId, profileMap));
        }
      } else if (status === "pending") {
        if (row.friend_id === requesterId) {
          incomingRequests.push({ productUserId: row.user_id, requestedUtc: createdAt, user: makeUser(row.user_id, profileMap) });
        } else if (row.user_id === requesterId) {
          outgoingRequests.push({ productUserId: row.friend_id, requestedUtc: createdAt, user: makeUser(row.friend_id, profileMap) });
        }
      } else if (status === "blocked" && row.user_id === requesterId) {
        if (!blockedSeen.has(row.friend_id)) {
          blockedSeen.add(row.friend_id);
          blockedUsers.push(makeUser(row.friend_id, profileMap));
        }
      }
    }

    return json({
      ok: true,
      message: "ok",
      count: friends.length,
      friends,
      incomingRequests,
      outgoingRequests,
      blockedUsers
    });
  } catch (err: any) {
    console.error("[friend-list] fatal", err);
    return fail(500, "internal_error", { detail: err?.message || "unknown" });
  }
});
