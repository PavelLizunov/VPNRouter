# Sentinel Security Journal

## 2026-08-15 - Line-by-Line Fallback Redaction for Malformed Config YAML
**Vulnerability:** When a user configuration YAML file (config.yaml) is corrupt or contains syntax errors, structured deserialization fails. The previous redactor omitted the file entirely, destroying diagnostic utility and preventing post-incident analysis for unloadable config backups.
**Learning:** Omission on parse failure left support blind during config corruption incidents. Using a fail-closed line-by-line fallback where unknown key values default to *** and URLs/text pass through scrubber rules preserves syntax/structure for support without leaking credentials.
**Prevention:** Always pair strict allowlist-based AST parsers with a fail-closed regex/key-value line scrubber fallback so parse errors retain diagnostic context without exposing secrets.
