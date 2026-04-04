// Decode JWT token without external dependencies
function decodeJWT(token) {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) {
      console.log('Invalid JWT format - expected 3 parts, got:', parts.length);
      return null;
    }
    
    const header = JSON.parse(Buffer.from(parts[0], 'base64').toString());
    const payload = JSON.parse(Buffer.from(parts[1], 'base64').toString());
    
    return { header, payload };
  } catch (error) {
    console.error('Failed to decode JWT:', error.message);
    return null;
  }
}

// Test with a sample token (replace with actual token from logs)
const sampleToken = process.argv[2];
if (!sampleToken) {
  console.log('Usage: node decode_token.js <jwt_token>');
  process.exit(1);
}

console.log('Decoding JWT token...');
const decoded = decodeJWT(sampleToken);

if (decoded) {
  console.log('\n=== JWT Header ===');
  console.log(JSON.stringify(decoded.header, null, 2));
  
  console.log('\n=== JWT Payload ===');
  console.log(JSON.stringify(decoded.payload, null, 2));
  
  console.log('\n=== Key Info ===');
  console.log('Issuer (iss):', decoded.payload.iss);
  console.log('Subject (sub):', decoded.payload.sub);
  console.log('Audience (aud):', decoded.payload.aud);
  console.log('Expires at (exp):', new Date(decoded.payload.exp * 1000));
  console.log('Issued at (iat):', new Date(decoded.payload.iat * 1000));
}
