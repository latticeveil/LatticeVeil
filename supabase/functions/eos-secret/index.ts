const corsHeaders: HeadersInit = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-gate-ticket",
  "Access-Control-Allow-Methods": "GET, OPTIONS",
};

function getTrimmedEnv(key: string): string {
  return (Deno.env.get(key) ?? "").trim();
}

function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: {
      ...corsHeaders,
      "Content-Type": "application/json; charset=utf-8",
    },
  });
}

// Validate gate ticket (check database for actual ticket existence)
async function validateGateTicket(ticket: string): Promise<{ valid: boolean; userId?: string }> {
  try {
    const supabaseUrl = Deno.env.get("SUPABASE_URL");
    const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
    
    if (!supabaseUrl || !supabaseServiceKey) {
      console.error("Missing Supabase credentials");
      return { valid: false };
    }

    const { createClient } = await import("https://esm.sh/@supabase/supabase-js@2");
    const supabase = createClient(supabaseUrl, supabaseServiceKey);

    // First check if ticket exists and is valid
    const { data: ticketData, error: ticketError } = await supabase
      .from("online_tickets")
      .select("user_id, expires_at, target")
      .eq("ticket_jwt", ticket)
      .single();

    if (ticketError) {
      console.error("Ticket lookup error:", ticketError);
      return { valid: false };
    }

    if (!ticketData) {
      console.log("Ticket not found in database");
      return { valid: false };
    }

    // Check if ticket has expired
    const expiresAt = new Date(ticketData.expires_at);
    const now = new Date();
    
    if (now > expiresAt) {
      console.log("Ticket expired");
      return { valid: false };
    }

    console.log("Ticket validated successfully for user:", ticketData.user_id);
    return { valid: true, userId: ticketData.user_id };
  } catch (error) {
    console.error("Ticket validation exception:", error);
    return { valid: false };
  }
}

Deno.serve(async (req: Request) => {
  console.log("=== EOS-SECRET FUNCTION CALLED ===");
  console.log("Method:", req.method);
  console.log("Headers:", Object.fromEntries(req.headers.entries()));

  if (req.method === "OPTIONS") {
    return new Response(null, {
      status: 204,
      headers: corsHeaders,
    });
  }

  if (req.method !== "GET") {
    return jsonResponse({ error: "Method not allowed" }, 405);
  }

  try {
    // Extract gate ticket from header
    const gateTicket = req.headers.get("x-gate-ticket");
    if (!gateTicket) {
      return jsonResponse({ ok: false, error: "missing_gate_ticket" }, 400);
    }

    console.log("Validating gate ticket for EOS secret request");

    // Validate the gate ticket
    const ticketValidation = await validateGateTicket(gateTicket);
    if (!ticketValidation.valid) {
      return jsonResponse({ ok: false, error: "invalid_gate_ticket" }, 401);
    }

    console.log("Gate ticket valid, returning EOS secret");

    // Return EOS client secret
    const clientSecret = getTrimmedEnv("EOS_CLIENT_SECRET");
    if (!clientSecret) {
      return jsonResponse({ ok: false, error: "missing_eos_secret" }, 500);
    }

    return jsonResponse({
      ok: true,
      clientSecret: clientSecret,
    });

  } catch (error: any) {
    console.error("EOS secret function error:", error);
    return jsonResponse({ 
      ok: false, 
      error: "internal_error", 
      message: error.message 
    }, 500);
  }
});
