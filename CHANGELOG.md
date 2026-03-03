# LatticeVeil Changelog

## v10.0.0 - "Worldshaper Reforged" - 2026-03-03

### 🌍 World Generation vNext
- **Terrain-First Defaults**: New worlds now default to non-flat terrain generation, with flatlands preserved as an opt-in path.
- **Biome Expansion**: Added Forest and Hills to the active biome set with deterministic selection and smoother transition behavior.
- **Biome Locate Reliability**: `/biome` and `/biomes` now resolve from deterministic biome-index data to reduce locate failures.
- **Tree Rules by Biome**: Forest has highest tree density; Grasslands/Hills are moderate; Desert/Ocean are tree-free.

### ⚙️ Stability, Recovery, and Rejoin
- **Freeze-Safe Worldgen Pipeline**: Creation work is moved off the UI thread to keep menu/game responsiveness during generation.
- **Generation Recovery**: Added generation-state tracking with pause/resume semantics for interrupted creation sessions.
- **Spawn Readiness Flow**: Improved spawn prewarm behavior for faster, safer rejoin and first-load world readiness.

### 💾 World Storage vNext
- **Canonical Region Storage**: Active chunk persistence standardized to `regions/*.lvregion`.
- **Artifact Convention Update**: Spawn prewarm uses `Spawn.lvpwarm`; player state uses `playerdata/*.lvplayer`.
- **Legacy Path Isolation**: Runtime paths no longer depend on legacy chunk/mesh `.bin` artifacts for vNext worlds.
- **Growth Guardrails**: Added world-size budget/compaction groundwork for long-term save health.

### 🎨 Rendering and Visual Fidelity
- **Transparency Pipeline Fixes**: Improved cutout/blended routing to reduce xray-like transparency artifacts.
- **Glass + Leaves Pass**: Better glass readability and foliage transparency behavior across world and held-item rendering.
- **Connected Visuals**: Glass seam behavior was refined for cleaner multi-block panels.

### 👁️ VeilSeer and First-Person UX
- **VeilSeer Functionality**: Spectate/no-clip behavior and mode-specific controls were hardened.
- **Control Feedback**: Added clearer fly-speed interaction cues and updated in-game controls/help messaging.
- **HUD Behavior**: VeilSeer HUD presentation was cleaned up while preserving inventory persistence.

### 🎒 Inventory and Command UX
- **Artificer Catalog Upgrade**: Added smooth scroll flow, search, favorites tooling, and clear-inventory controls.
- **Search/Interaction Polish**: Improved focus behavior, text editing ergonomics, and catalog usability.
- **Give Command Improvements**: `/give` token resolution and autocomplete now better support named block lookup.
- **Naming Consistency**: Ore naming and command-facing identifiers were normalized.

### 🌐 Multiplayer and Contract Alignment
- **World Contract Consistency**: World type/generator expectations are enforced more strictly across host/join paths.
- **Session Reliability**: Multiplayer world-state handling remains aligned with vNext storage/generation assumptions.

### ⚠️ Compatibility Notes
- This release intentionally includes breaking-format behavior in worldgen/storage paths.
- Legacy world artifacts are treated as non-vNext and may require regeneration/migration strategy.

---

## v9.0.0 - "Gatekeeper's Fix" - 2026-02-21

### 🚀 Major Online System Overhaul
- **Fixed EOS Secret Request**: Resolved invalid gate ticket errors by implementing proper JWT validation and database storage
- **Enhanced Gate Ticket System**: Created robust ticket minting and validation with proper expiration handling
- **Improved Build Configuration**: Fixed release build detection to use proper build configuration instead of file existence checks
- **Added Supabase Functions**: 
  - `eos-secret`: Secure EOS client secret retrieval with gate ticket validation
  - `game-hashes-get`: Centralized hash management for both dev and release channels
  - `online-ticket`: Enhanced ticket creation with database persistence
- **Fixed Database Schema**: Updated `online_tickets` table with proper UUID generation and NOT NULL constraints
- **Enhanced Launcher Integration**: Improved launcher-to-game communication with proper environment variable handling

### 🔧 Core Improvements
- **Asset System**: Enhanced asset pack installation and validation
- **Build Verification**: Improved official build verification with proper hash checking
- **Error Handling**: Better error reporting and user feedback throughout the application
- **Configuration Management**: Centralized Supabase configuration with proper environment variable handling

### 🎮 Gameplay Improvements
- **Enhanced Command System**: Improved command input handling with better prediction and tab completion
- **Fixed Control Rebinding**: Controls now properly rebind without conflicts or lost bindings
- **Pause Menu Fixes**: Resolved floating/movement issues when game is paused
- **Enhanced Input Handling**: Better key detection and modifier key support
- **Improved Chat System**: Enhanced chat history and command input processing

### 🎨 Asset Updates
- **Added New Texture**: Added `air.png` texture for improved block rendering
- **New Background Images**: Added 5 new multiplayer background images:
  - `InviteFriends_bg.png` for friend invitation screen
  - `JoinByCode_bg.png` for code joining screen
  - `Kicked_background.png` for kick/disconnect screen
  - `MultiplayerHost_bg.png` for multiplayer hosting screen
  - `ShareJoinCode_bg.png` for code sharing screen
- **Asset Structure Cleanup**: Removed duplicate textures and maintained proper folder structure
- **Enhanced Compatibility**: Ensured proper launcher asset loading paths

### 🛡️ Security & Reliability
- **JWT Token Validation**: Proper token verification with expiration checking
- **Database Integrity**: Enhanced data validation and constraint handling
- **Network Resilience**: Improved retry logic and connection handling
- **Build Authentication**: Stronger verification of official builds

### 🚀 Release Build
- **Production Ready**: Release build (`LatticeVeilMonoGame.exe`) included in `bin/Release/net8.0-windows/win-x64/`
- **Stable Hash**: Release build uses proper release hash validation
- **Optimized Performance**: Release build optimized for production deployment
- **Fixed Release Build Detection**: Release builds now correctly use release hash lists instead of dev
- **Resolved Database Errors**: Fixed null constraint violations in ticket storage
- **Corrected CORS Issues**: Proper cross-origin handling for web functions
- **Fixed Asset Loading**: Improved asset pack discovery and installation

### 📋 Technical Details
- **Supabase Integration**: Complete overhaul of authentication and data persistence
- **RPC Functions**: Added direct SQL functions to bypass PostgREST cache issues
- **Environment Configuration**: Proper handling of dev/release build modes
- **Hash Validation**: Enhanced executable hash verification and allowlist checking

---

## Previous Versions
### v1.2.0 - "Foundation Update" - 2026-02-18
- Initial EOS integration framework
- Basic online authentication system
- Launcher protocol implementation

### v1.1.0 - "Alpha Release" - 2026-02-16
- Core multiplayer functionality
- Basic world hosting and joining
- Initial asset system

### v1.0.0 - "Pre-Alpha" - 2026-02-10
- Initial game release
- Basic singleplayer functionality
- Foundation UI systems
