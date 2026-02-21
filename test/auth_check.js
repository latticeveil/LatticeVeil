const { createClient } = require('@supabase/supabase-js');

// Load environment variables
require('dotenv').config({ path: '../.env.development' });

const SUPABASE_URL = process.env.VITE_SUPABASE_URL || 'https://lqghurvonrvrxfwjgkuu.supabase.co';
const SUPABASE_ANON_KEY = process.env.VITE_SUPABASE_ANON_KEY;

console.log('Testing Supabase configuration...');
console.log('URL:', SUPABASE_URL);
console.log('Anon Key:', SUPABASE_ANON_KEY ? 'SET' : 'MISSING');

if (!SUPABASE_ANON_KEY) {
  console.error('❌ SUPABASE_ANON_KEY not found in environment');
  process.exit(1);
}

// Initialize Supabase client
const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

async function testAuthEndpoints() {
  console.log('\n=== Testing Auth Endpoints ===');
  
  try {
    // Test 1: Get current user (should fail with 400/403 when no session)
    console.log('\n1. Testing GET /auth/v1/user without session...');
    const { data, error } = await supabase.auth.getUser();
    if (error) {
      console.log('✅ Expected error (no session):', error.message);
      console.log('   Error code:', error.status);
    } else {
      console.log('❌ Unexpected success:', data);
    }
    
    // Test 2: Try to get session (should return null)
    console.log('\n2. Testing session retrieval...');
    const { data: sessionData } = await supabase.auth.getSession();
    console.log('Session:', sessionData ? 'EXISTS' : 'NULL (expected)');
    
    // Test 3: Try to sign in with invalid token (should fail)
    console.log('\n3. Testing invalid token...');
    try {
      const result = await supabase.auth.setSession({
        access_token: 'invalid.jwt.token.here',
        refresh_token: 'invalid'
      });
      console.log('❌ Unexpected success with invalid token');
    } catch (err) {
      console.log('✅ Expected error with invalid token:', err.message);
    }
    
  } catch (error) {
    console.error('❌ Unexpected test error:', error);
  }
}

async function testAPIEndpoint() {
  console.log('\n=== Testing Direct API Call ===');
  
  // Test the exact endpoint that was failing
  const response = await fetch(`${SUPABASE_URL}/auth/v1/user`, {
    method: 'GET',
    headers: {
      'Authorization': `Bearer ${SUPABASE_ANON_KEY}`,
      'apikey': SUPABASE_ANON_KEY
    }
  });
  
  console.log('Direct API Response Status:', response.status);
  console.log('Direct API Response:', await response.text());
}

async function main() {
  await testAuthEndpoints();
  await testAPIEndpoint();
  console.log('\n=== Test Complete ===');
}

main().catch(console.error);
