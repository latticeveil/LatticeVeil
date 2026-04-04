# Release Checklist - LatticeVeil v8.0.0 "Worldforge Convergence"

## Required Environment Variables
- `VEILNET_JWT_SECRET` - For launcher token validation
- `SUPABASE_SERVICE_ROLE_KEY` - For database operations  
- `SUPABASE_ANON_KEY` - For Supabase client initialization
- `LV_GATE_TICKET_URL` - Override gate ticket endpoint (optional)
- `LV_VEILNET_ACCESS_TOKEN` - Veilnet access token (optional)

## Pre-Release Sanity Checks
- [ ] Online gate endpoints resolve correctly:
  - `https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1/online-ticket`
  - `https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1/online-ticket-validate`
- [ ] No hardcoded secrets in client builds
- [ ] EOS DLL copied to release output
- [ ] Version stamping matches v8.0.0
- [ ] Asset packaging excludes legacy folders (Assets/menu, Assets/blocks)
- [ ] Installer creates Documents/LatticeVeil/Assets correctly

## Build Configuration
- [x] DEBUG builds include: `LV_DEVTOOLS`, `LV_VERBOSE_LOGS`
- [x] RELEASE builds include: `LV_RELEASE`
- [x] PublishSingleFile + SelfContained enabled
- [x] DebugSymbols disabled in RELEASE
- [x] TreatWarningsAsErrors OFF (unless blocking shipping)

## Online Gate System Status
- [x] Functions deployed: `online-ticket`, `online-ticket-validate`
- [x] JWT verification disabled in config.toml
- [x] Hash verification supports both `hash` and `sha256` columns
- [x] Uses service role client for DB operations
- [x] 15-minute ticket expiry
- [x] Proper error responses (403 hash_mismatch, 401 unauthorized)

## Security Verification
- [x] No service role keys embedded in client
- [x] No JWT secrets embedded in client
- [x] Server secrets stored in Supabase/Render env vars only

## Testing Requirements
- [ ] Offline launch works
- [ ] LAN multiplayer works  
- [ ] Online gate ticket acquisition works (with valid secrets)
- [ ] EOS optional behavior works (EOS DLL missing should not crash)

## Release Artifacts
- [ ] Single executable file
- [ ] Includes all required DLLs
- [ ] Version info in executable properties
- [ ] Assets properly packaged
- [ ] Installer script functional

## Post-Release
- [ ] Tag release with version number
- [ ] Create GitHub release
- [ ] Update documentation
- [ ] Archive build artifacts
