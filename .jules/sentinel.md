# Sentinel Security Journal

## 2026-08-16 - AWG Endpoint Subscription-Scope Leak Validation Gap
**Vulnerability:** `LeakProtection.ValidateOutboundServersScopeAware` only iterated over `config.Outbounds`, leaving AmneziaWG (AWG) egress peer addresses in `config.Endpoints[].Peers[]` unvalidated against active subscription / server scope.
**Learning:** sing-box-lx represents AWG proxy outbounds as top-level `Endpoints` rather than standard `Outbounds`. Scope validation functions must inspect both `Outbounds` and `Endpoints` to prevent out-of-scope/legacy server IP leaks.
**Prevention:** Always audit protocol-specific config schema structures (`Outbounds` vs `Endpoints` vs `Inbounds`) when enforcing scope-level security invariants.
