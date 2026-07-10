# Context Pack Guide

This pack is for a ChatGPT Project knowledge base.

## Research Basis

OpenAI describes ChatGPT Projects as a way to group chats, uploaded files, and project instructions so future conversations share the same context. That matches this use case: VPNRouter discussions repeat the same product rules, screenshots, upstream docs, and troubleshooting assumptions.

This is not a formal standard with a required filename layout. The practical pattern is:

- short Project Instructions for behavior
- stable project context as uploaded knowledge
- current investigations as a separate, easy-to-update file
- screenshots with a text map
- links to upstream documentation

## Why Markdown

Markdown is not mandatory. It is used here because it is plain text with headings, lists, links, and code snippets. That makes it easy for ChatGPT to retrieve and quote relevant sections.

Good formats for this use:

- `.md` for structured context and instructions
- `.txt` for plain notes
- `.png` for screenshots
- `.zip` only as a transport format; upload the files themselves when possible

Avoid PDF/DOCX unless layout matters. They add extraction noise for a mostly text-based knowledge base.

## Upload Order

1. Paste `CHATGPT_PROJECT_INSTRUCTIONS.md` into Project Instructions.
2. Upload `VPNROUTER_CONTEXT.md`.
3. Upload `CURRENT_INVESTIGATIONS.md`.
4. Upload `LIBRARY_DOCS.md`.
5. Upload `SCREENSHOTS.md`.
6. Upload the PNG screenshots that are relevant to the discussion.

## Maintenance

Keep stable and temporary knowledge separate:

- stable product truth goes in `VPNROUTER_CONTEXT.md`
- active bugs and hypotheses go in `CURRENT_INVESTIGATIONS.md`
- library/upstream links go in `LIBRARY_DOCS.md`
- screenshot descriptions go in `SCREENSHOTS.md`

When a bug is fixed or proven wrong, update or delete it from `CURRENT_INVESTIGATIONS.md`.

## Privacy

Do not upload credentials, private keys, subscription URLs, full diagnostic archives with personal paths, or handoff files with infrastructure secrets.
