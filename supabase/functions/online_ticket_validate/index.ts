import { serve } from "https://deno.land/std@0.168.0/http/server.ts"
import { createClient } from 'https://esm.sh/@supabase/supabase-js@2'
import { SignJWT, jwtVerify } from "https://deno.land/x/jose@v4.14.4/index.ts"

const corsHeaders = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type',
}

// JWT validation for Veilnet launcher tokens
async function validateVeilnetToken(authHeader: string): Promise<{ valid: boolean; userId?: string; username?: string; error?: string }> {
  if (!authHeader || !authHeader.startsWith('Bearer ')) {
    return { valid: false, error: 'Missing Authorization header' }
  }

  const token = authHeader.substring(7)
  const secret = Deno.env.get('VEILNET_JWT_SECRET')
  
  if (!secret) {
    console.error('VEILNET_JWT_SECRET not configured')
    return { valid: false, error: 'Server configuration error' }
  }

  try {
    const encoder = new TextEncoder()
    const keyData = encoder.encode(secret)
    const key = await crypto.subtle.importKey(
      'raw',
      keyData,
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['verify']
    )

    const parts = token.split('.')
    if (parts.length !== 3) {
      return { valid: false, error: 'Invalid token format' }
    }

    const header = JSON.parse(atob(parts[0]))
    const payload = JSON.parse(atob(parts[1]))
    const signature = parts[2]

    // Verify signature
    const data = `${parts[0]}.${parts[1]}`
    const signatureData = Uint8Array.from(atob(signature), c => c.charCodeAt(0))
    const dataBuffer = encoder.encode(data)
    
    const isValid = await crypto.subtle.verify(
      'HMAC',
      key,
      signatureData,
      dataBuffer
    )

    if (!isValid) {
      return { valid: false, error: 'Invalid signature' }
    }

    // Validate token type and extract claims
    if (payload.typ !== 'launcher') {
      return { valid: false, error: 'Invalid token type' }
    }

    if (!payload.sub) {
      return { valid: false, error: 'Missing user ID' }
    }

    return { 
      valid: true, 
      userId: payload.sub, 
      username: payload.username 
    }
  } catch (error) {
    console.error('Token validation error:', error)
    return { valid: false, error: 'Token validation failed' }
  }
}

serve(async (req) => {
  // Handle CORS preflight requests
  if (req.method === 'OPTIONS') {
    return new Response('ok', { headers: corsHeaders })
  }

  // Only allow POST
  if (req.method !== 'POST') {
    return new Response(
      JSON.stringify({ ok: false, error: 'method_not_allowed' }),
      { status: 405, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
    )
  }

  try {
    // Validate Veilnet token
    const authHeader = req.headers.get('Authorization')
    const tokenValidation = await validateVeilnetToken(authHeader || '')
    
    if (!tokenValidation.valid) {
      return new Response(
        JSON.stringify({ ok: false, error: 'unauthorized' }),
        { status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    // Parse request body
    const body = await req.json()
    const { ticket, target } = body

    // Validate inputs
    if (!ticket || !target) {
      return new Response(
        JSON.stringify({ ok: false, error: 'bad_request', message: 'Missing ticket or target' }),
        { status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    const targetLower = target.trim().toLowerCase()
    if (!['dev', 'release'].includes(targetLower)) {
      return new Response(
        JSON.stringify({ ok: false, error: 'bad_request', message: 'Target must be dev or release' }),
        { status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    // Use service role client to bypass RLS for ticket lookup
    const serviceClient = createClient(
      Deno.env.get('SUPABASE_URL')!,
      Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!
    )

    // Query ticket for this user
    const { data: ticketData, error: ticketError } = await serviceClient
      .from('online_tickets')
      .select('*')
      .eq('id', ticket)
      .eq('user_id', tokenValidation.userId)
      .eq('target', targetLower)
      .gt('expires_at', new Date().toISOString())
      .maybeSingle()

    if (ticketError) {
      return new Response(
        JSON.stringify({ ok: false, error: 'service_error', message: 'Database query failed' }),
        { status: 503, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    if (!ticketData) {
      return new Response(
        JSON.stringify({ ok: false, error: 'ticket_invalid', message: 'Ticket not found, expired, or wrong target' }),
        { status: 403, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    return new Response(
      JSON.stringify({ ok: true }),
      { headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
    )

  } catch (error) {
    console.error('Unexpected error:', error)
    return new Response(
      JSON.stringify({ ok: false, error: 'internal_error', message: 'Internal server error' }),
      { status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
    )
  }
})
