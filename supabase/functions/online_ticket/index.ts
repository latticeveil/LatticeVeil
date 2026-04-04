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

    const header = JSON.parse(new TextDecoder().decode(Deno.base64.decode(parts[0])))
    const payload = JSON.parse(new TextDecoder().decode(Deno.base64.decode(parts[1])))
    const signature = parts[2]

    // Verify signature
    const data = `${parts[0]}.${parts[1]}`
    const signatureData = new Uint8Array(
    [...new TextDecoder().decode(Deno.base64.decode(signature))].map(c => c.charCodeAt(0))
  )
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
  console.log('=== FUNCTION CALLED ===')
  console.log('Method:', req.method)
  console.log('Headers:', Object.fromEntries(req.headers.entries()))
  
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
    // Validate Veilnet launcher token
    const authHeader = req.headers.get('authorization')
    const tokenValidation = await validateVeilnetToken(authHeader || '')
    
    if (!tokenValidation.valid) {
      return new Response(
        JSON.stringify({ ok: false, error: 'unauthorized', message: tokenValidation.error }),
        { status: 401, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    // Parse request body with robust error handling
    let body: any
    try {
      body = await req.json()
    } catch (parseError: any) {
      console.error('JSON parse error:', parseError)
      return new Response(
        JSON.stringify({ ok: false, error: 'bad_request', message: 'Invalid JSON', details: parseError?.message || 'Unknown error' }),
        { status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    const { target, exe_hash_sha256 } = body

    // Validate inputs
    if (!target || !exe_hash_sha256) {
      return new Response(
        JSON.stringify({ ok: false, error: 'bad_request', message: 'Missing target or exe_hash_sha256' }),
        { status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    const targetLower = target.trim().toLowerCase()
    const hashLower = exe_hash_sha256.trim().toLowerCase()

    if (!['dev', 'release'].includes(targetLower)) {
      return new Response(
        JSON.stringify({ ok: false, error: 'bad_request', message: 'Target must be dev or release' }),
        { status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    if (!/^[0-9a-f]{64}$/.test(hashLower)) {
      return new Response(
        JSON.stringify({ ok: false, error: 'bad_request', message: 'Invalid SHA256 format' }),
        { status: 400, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    console.log('online-ticket user', tokenValidation.userId)
    console.log('target', targetLower)
    console.log('got hash', hashLower.slice(0, 8))

    // Use service role client to bypass RLS for hash lookup
    console.log('Creating Supabase client...')
    const supabaseUrl = Deno.env.get('SUPABASE_URL')
    const supabaseServiceKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')
    console.log('URL exists:', !!supabaseUrl)
    console.log('Key exists:', !!supabaseServiceKey)
    
    const serviceClient = createClient(
      supabaseUrl!,
      supabaseServiceKey!
    )

    console.log('Client created, querying hashes...')

    // Query game_hashes for target
    const { data: hashData, error: hashError } = await serviceClient
      .from('game_hashes')
      .select('hash,sha256,target,is_active,updated_at')
      .eq('target', targetLower)
      .eq('is_active', true)
      .order('updated_at', { ascending: false })
      .limit(1)
      .maybeSingle()

    if (hashError) {
      console.log('Hash query error:', hashError)
      console.log('Error details:', JSON.stringify(hashError, null, 2))
      return new Response(
        JSON.stringify({ ok: false, error: 'service_error', message: 'Database query failed', details: hashError }),
        { status: 503, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    console.log('Hash query result:', hashData)

    if (!hashData) {
      console.log('expected', 'none')
      return new Response(
        JSON.stringify({ ok: false, error: 'no_active_hash', message: `No active hash configured for target: ${targetLower}` }),
        { status: 503, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    const expectedA = (hashData.sha256 || '').trim().toLowerCase()
    const expectedB = (hashData.hash || '').trim().toLowerCase()
    console.log('expectedA', expectedA.slice(0, 8))
    console.log('expectedB', expectedB.slice(0, 8))

    // Compare hashes - accept either column
    if (hashLower !== expectedA && hashLower !== expectedB) {
      return new Response(
        JSON.stringify({ 
          ok: false, 
          error: 'hash_mismatch', 
          lanAllowed: true
        }),
        { status: 403, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    // Hash matches - create ticket using service role client to bypass RLS
    const expiresAt = new Date(Date.now() + 15 * 60 * 1000).toISOString()
    const { data: ticketData, error: ticketError } = await serviceClient
      .from('online_tickets')
      .insert({
        user_id: tokenValidation.userId,
        target: targetLower,
        build_hash: hashLower,
        expires_at: expiresAt
      })
      .select()
      .single()

    if (ticketError) {
      console.log('Ticket creation error:', ticketError)
      console.log('Error details:', JSON.stringify(ticketError, null, 2))
      return new Response(
        JSON.stringify({ ok: false, error: 'service_error', message: 'Failed to create ticket', details: ticketError }),
        { status: 503, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
      )
    }

    console.log('Ticket created successfully:', ticketData)

    return new Response(
      JSON.stringify({
        ok: true,
        ticket: ticketData.id,
        expiresAt: expiresAt,
        target: targetLower
      }),
      { headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
    )

  } catch (error: any) {
    console.error('Unexpected error:', error)
    console.error('Error stack:', error.stack)
    return new Response(
      JSON.stringify({ ok: false, error: 'internal_error', message: 'Internal server error', details: error.message }),
      { status: 500, headers: { ...corsHeaders, 'Content-Type': 'application/json' } }
    )
  }
})
