# Official Online Service Terms

This repository is open-source, but access to the official online service
(`https://eos-service.onrender.com`) is private and controlled by the project owner.

## What Is Open

- Game source code
- Build scripts
- Offline/LAN functionality
- Ability for third parties to run their own EOS/gate backend

## What Is Restricted

- Official Render gate admin APIs
- Official gate signing key and admin token
- Official EOS private credentials
- Official allowlist/hash management

## Policy

1. Only builds approved by the official gate allowlist are allowed to use the official online service.
2. Modified/forked builds are expected to use offline/LAN or a separate backend controlled by that fork's operator.
3. Accessing or attempting to access the official admin endpoints without authorization is prohibited.
4. Redistributing this source code does not grant access to official infrastructure secrets.

## Enforcement Notes

- Gate decisions are enforced by server-side secrets (`GATE_JWT_SIGNING_KEY`, `GATE_ADMIN_TOKEN`).
- Runtime hash updates require the admin token.
- Peer joins in EOS sessions require server-validated gate tickets.

If you fork this project, configure your own EOS/gate infrastructure and tokens.
