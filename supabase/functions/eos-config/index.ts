const corsHeaders: HeadersInit = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET, OPTIONS",
};

const REQUIRED_ENV_KEYS = [
  "EOS_PRODUCT_ID",
  "EOS_SANDBOX_ID",
  "EOS_DEPLOYMENT_ID",
  "EOS_CLIENT_ID",
  "EOS_PRODUCT_NAME",
  "EOS_PRODUCT_VERSION",
] as const;

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

Deno.serve((req: Request) => {
  if (req.method === "OPTIONS") {
    return new Response(null, {
      status: 204,
      headers: corsHeaders,
    });
  }

  if (req.method !== "GET") {
    return jsonResponse({ error: "Method not allowed" }, 405);
  }

  const missing = REQUIRED_ENV_KEYS.filter((key) => getTrimmedEnv(key).length === 0);
  if (missing.length > 0) {
    return jsonResponse(
      {
        error: "Missing required EOS public config environment variables.",
        missing,
      },
      500,
    );
  }

  return jsonResponse({
    ProductId: getTrimmedEnv("EOS_PRODUCT_ID"),
    SandboxId: getTrimmedEnv("EOS_SANDBOX_ID"),
    DeploymentId: getTrimmedEnv("EOS_DEPLOYMENT_ID"),
    ClientId: getTrimmedEnv("EOS_CLIENT_ID"),
    ProductName: getTrimmedEnv("EOS_PRODUCT_NAME"),
    ProductVersion: getTrimmedEnv("EOS_PRODUCT_VERSION"),
  });
});
