# How the assistant identifies a VPN application

VPNRouter first tries to identify the application on the device. When one
target is available, it advertises two action IDs bound to that target. DeepSeek
selects the routing action by ID. Application-specific commands omit the
whole-tunnel and general settings controls. With the older text action, the
proposal must agree with the original user request and local evidence. The
verified process name is what goes into the routing rule.

## Request examples

| User request | How VPNRouter identifies it |
| --- | --- |
| Only Microsoft Edge through the VPN, with other apps direct | The localized Edge aliases map to `msedge.exe`. Windows confirms its actual executable. |
| Только Google Chrome через VPN | Chrome aliases map to `chrome.exe`. |
| Telegram напрямую, остальные правила оставь как есть | Telegram aliases map to `Telegram.exe`. The bypass action has its own route policy check. |
| Только браузер по умолчанию через VPN | Windows must identify the same supported executable for both HTTP and HTTPS. A missing or conflicting association requires a name. The gateway can use a default-browser action ID without learning its executable. |
| Use VPN only for Acme Editor | For an unfamiliar name, open the app. VPNRouter can match the displayed product name from the running executable or you can enter its exact basename, such as `AcmeWin.exe`. |
| Только Chrome или Firefox через VPN | This mentions two distinct targets. Name one browser in a new request. |
| Route only Chrome Canary through VPN | Process-name routing cannot distinguish editions that use the same executable basename. The assistant asks for a supported target. |

Use the product name or exact process basename. Keep the route direction
explicit, such as *only this app through VPN* or *this app direct*.

## Local evidence

- Windows recognizes a bounded bilingual list of familiar names. For example,
  Microsoft Edge binds to `msedge.exe`, while its WebView helper is a separate
  process. Familiar names still require evidence that the executable exists.
- For a specific Windows executable, a running image or a registered App
  Paths entry provides the exact file basename. The resolver preserves its
  spelling because the routing engine matches process names by case.
- For an unfamiliar Windows display name, VPNRouter reads product and file
  descriptions from running desktop applications in the current session.
  Exactly one executable can match. An application installer or updater
  requires a specific request and process name of its own.
- The generic Windows browser request uses the user HTTP and HTTPS
  registrations. Both must point to the same supported local browser. The
  registered open command is read only to extract its executable path.
- On macOS and Linux, specify the locally running process name in its exact
  case. The Windows alias and browser registration logic is Windows-specific.
- The gateway receives the existing request and available actions. Process
  paths, the list of other running programs, and registry entries stay local.

Several labels or browser profiles can share an executable. A process rule
applies to all processes with that name. Use a separate executable when a
route needs to distinguish editions.

## Routing and evidence

After binding a target, VPNRouter still checks the proposed route and VPN
connection before committing a verified assistant action. An unavailable
server can stop a correctly identified request at the connection check. An
app-name match does not establish that the user asked for a mutation. Questions,
quotes, negation, and conditional instructions need independent intent
evaluation before any policy change.

The target test corpus supplies original text, the model's proposed name,
and a simulated local process inventory. It verifies the binder independently
of provider output. It includes examples where the target is clear even
though the request is a question or a quotation. These examples must not be
read as permission to change the VPN.

## What clarification means in the current UI

The user submits one request. VPNRouter can run several internal action
steps within it. The gateway's `clarify` reply ends that request and the
desktop shows a local status message. There is no model question field and a
new submission does not carry conversation history from the previous one.

A verified Windows default already answers which browser is intended.
When a request names several applications or the local evidence is missing,
a focused application chooser would be enough. It should retain the pending
request and show locally verified app labels. Selecting one option should
recheck settings and application identity before executing the original
direction. A chooser would keep the resolution on the device. It would not
require exposing a general chat or inventing a target from model text.

For example, with a verified default browser the request can offer:

```json
[
  {"id":1,"name":"RouteOnlyApplicationForDefaultBrowser","kind":"press"},
  {"id":2,"name":"RouteApplicationDirectForDefaultBrowser","kind":"press"}
]
```

The response `{"operation":"act","id":1}` selects VPN for the one resolved
browser. The client checks the original request, the current settings
revision and the browser association again. The app knows that the rule
means `msedge.exe`, for example. DeepSeek need only choose the direction.

For the proposed chooser, retain the original request and a local token for
its settings revision. If a user says “Chrome or Edge”, show verified
options for those two applications and a cancel action. If the executable
cannot be confirmed, give the user a way to open the application and
recheck. A pick resolves the target locally. The ordinary route verification
still runs before saving. A request to route several applications together
requires a separate multi-app action and policy check.
