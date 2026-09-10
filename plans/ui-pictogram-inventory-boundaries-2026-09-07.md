# UI pictogram inventory boundaries

Base: b7ce0e4f. Product unchanged at brief commit 29a3f1a6.

## Evidence from independent read-only inventories
Desktop has four actual XAML Paths: DpiBypassPage shield and play; TelegramPage plane and play. Stop icons are rounded Borders, not Paths. Other explicit pictograms are Unicode/ASCII text, localized label prefixes, Ellipse status indicators and custom radio primitives. Android has no owned Path/Geometry/PathIcon/DrawingImage definitions; it uses text, Ellipses/radios and Android framework notification resources.

Important provenance distinctions:
- Localization wrapper exports alone do not prove a current UI consumer. Trace Core string through App/Android wrapper and ViewModel to a render site.
- Comments in NetworkPage mention old icons that current label values no longer contain. Do not promote comments into inventory entries.
- Wide/narrow layouts are separate consumer locations, not distinct icon designs.
- Different actions sharing a cross (dismiss, delete, cancel task) must not be collapsed semantically.
- Country flags are a generated regional-indicator family. Preserve country identity rather than replacing every flag with one globe. The globe can only replace the unknown-country fallback.
- Third-party application icons are identity images, not replaceable UI pictograms.
- Android tools/DPI builders and profile overlays include dormant/unreachable items: label source-only, not live visible UI.

## Excluded by owner scope
All logos/mascots; application/window/taskbar/tray/shortcut icons; PNG/ICO/ICNS and launcher mipmaps; third-party application identity images. No product image is regenerated.

## Excluded as non-pictograms
Decorative glow Ellipses, layout Borders, prose bullets and separators, truncation dots, unknown-value em dashes, instructional/breadcrumb arrows without an icon role. Plain-text navigation receives no invented icons.

## External and dynamic limits
Fluent/native checkbox, ComboBox, Expander and window-chrome templates are dependency-owned, not repository vectors. Android notification lock/close drawables and third-party QR scanner drawing have no portable repository path data; show explicit resource descriptions, not counterfeit before-images. Arbitrary user/subscription/server names can contain arbitrary symbols, so no finite literal inventory can enumerate those. CLI Spectre spinner frames are dependency-owned; terminal status symbols are documented separately from visual-app proposals.

## Audit methods
Scanned App and Android source, resources, localized strings and owning Core localization; searched Path/Geometry/PathIcon/DrawingImage/SVG, Unicode arrow/symbol/emoji ranges, numeric Unicode escapes, Text/Content assignments, glyph factories, Ellipse/Border primitives, resource/bitmap/image consumers and ASCII action symbols. Followed matches into consumers and recorded dormant/source-only cases. Source audit does not claim runtime screenshot coverage.
