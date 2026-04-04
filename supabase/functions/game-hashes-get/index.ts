import { createClient } from "jsr:@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "https://latticeveil.github.io",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

function json(status: number, body: any) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...corsHeaders, "Content-Type": "application/json" },
  });
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }

  try {
    const url = new URL(req.url);
    const target = url.searchParams.get("target")?.toLowerCase();
    const version = url.searchParams.get("version");

    // If no target specified, return both dev and release (for website)
    if (!target) {
      const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
      const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
      const supabase = createClient(supabaseUrl, supabaseServiceKey);

      const { data: hashes, error } = await supabase
        .from("game_hashes")
        .select("hash,target,is_active,updated_at")
        .eq("is_active", true)
        .in("target", ["dev", "release"]);

      if (error) {
        console.error("Hash query error:", error);
        return json(500, { ok: false, error: "database_error" });
      }

      const response: any = { ok: true };
      
      for (const row of hashes || []) {
        const targetKey = row.target as string;
        response[targetKey] = {
          hash: row.hash,
          sha256: row.hash,
          target: row.target,
          is_active: row.is_active,
          updated_at: row.updated_at,
        };
      }

      return json(200, response);
    }

    if (target !== "dev" && target !== "release") {
      return json(400, { ok: false, error: "invalid_target" });
    }

    const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
    const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const supabase = createClient(supabaseUrl, supabaseServiceKey);

    // Get both dev and release hashes
    const { data: hashes, error } = await supabase
      .from("game_hashes")
      .select("hash,target,is_active,updated_at")
      .eq("is_active", true)
      .in("target", ["dev", "release"]);

    if (error) {
      console.error("Hash query error:", error);
      return json(500, { ok: false, error: "database_error" });
    }

    // Format response with both dev and release
    const response: any = { ok: true };
    
    for (const row of hashes || []) {
      const targetKey = row.target as string;
      response[targetKey] = {
        hash: row.hash,
        sha256: row.hash,
        target: row.target,
        is_active: row.is_active,
        updated_at: row.updated_at,
      };
    }

    return json(200, response);
  } catch (err) {
    console.error("Unexpected error:", err);
    return json(500, { ok: false, error: "internal_error" });
  }
});
