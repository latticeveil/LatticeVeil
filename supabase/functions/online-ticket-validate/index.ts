import "@supabase/functions-js/edge-runtime.d.ts";

import { jwtVerify } from "https://esm.sh/jose@5";

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

function getSecretKeyBytes() {
  const secret = (Deno.env.get("VEILNET_JWT_SECRET") ?? "").trim();
  if (!secret) throw new Error("VEILNET_JWT_SECRET missing");
  return new TextEncoder().encode(secret);
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: corsHeaders });
  if (req.method !== "POST") return json(405, { ok: false, error: "method_not_allowed" });

  let body: any;
  try {
    body = await req.json();
  } catch {
    return json(400, { ok: false, error: "bad_json" });
  }

  const ticket = (body?.ticket ?? "").toString().trim();
  const requiredChannel = (body?.target ?? body?.requiredChannel ?? "").toString().trim().toLowerCase();

  if (!ticket) return json(400, { ok: false, error: "bad_ticket" });
  if (requiredChannel && requiredChannel !== "dev" && requiredChannel !== "release") {
    return json(400, { ok: false, error: "bad_target" });
  }

  try {
    const key = getSecretKeyBytes();
    const { payload } = await jwtVerify(ticket, key, { algorithms: ["HS256"] });

    const typ = (payload as any).typ;
    if (typ !== "gate_ticket") return json(403, { ok: false, reason: "Invalid gate ticket type." });

    const target = ((payload as any).target ?? "").toString().trim().toLowerCase();
    if (requiredChannel && target && target !== requiredChannel) {
      return json(403, { ok: false, reason: `Gate ticket channel mismatch (${target} != ${requiredChannel}).` });
    }

    return json(200, { ok: true });
  } catch {
    return json(403, { ok: false, reason: "Invalid ticket." });
  }
});
