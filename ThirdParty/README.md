# Third-Party Dependencies

This directory contains external dependencies and libraries used by the LatticeVeil project.

## 📦 **Current Dependencies**

### **EOS SDK**
- **Path**: `EOS/EOSSDK-Win64-Shipping.dll`
- **Purpose**: Epic Online Services integration for multiplayer
- **Version**: Latest stable release
- **Usage**: Multiplayer matchmaking, friends, authentication

## 📋 **Adding New Dependencies**

1. Create appropriate subdirectory for the dependency
2. Add documentation here in this README
3. Update project references as needed
4. Include license information if required
5. Test integration thoroughly

## ⚠️ **Important Notes**

- All dependencies must be compatible with the project license
- Keep dependencies to a minimum to reduce bloat
- Document the purpose and version of each dependency
- Ensure dependencies are properly referenced in the build system

## 🔧 **Build Integration**

Dependencies are automatically included in the build process through:
- Project references in `.csproj` files
- Copy-to-output directives in build scripts
- Runtime loading for native libraries

## 📄 **Licenses**

Ensure all third-party dependencies comply with the project's licensing requirements. Document any specific licensing requirements or restrictions here.
