# Iteration A — Client views & ViewModels raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `CL-1` (Avalonia ViewModels) and `CL-2` (Views, accessibility, runtime XAML)
Status: unverified swarm output; no item below is accepted until lead source verification.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| CL-1 | 3/3 | correctness; UI-state/cancellation/lifetime; tests/schema/upstream | 14 | 10 |
| CL-2 | 3/3 | correctness/rendering; accessibility/narrow-layout/contrast; XAML-loading/lifecycle/upstream | 13 | 10 |

## Unverified candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Reporters | Status |
|---|---|---|---|---|---|
| CL-1-1 | P1 | FreeConfigs cross-tab selection state race | `FreeConfigsPageViewModel.cs:187-203,1213,1776`; `FreeConfigsPage.axaml:25,202-203,339-340,446` | UI-state/cancellation/lifetime | pending |
| CL-1-2 | P2 | Un-disposed `CancellationTokenSource` in subscription auto-refresh | `MainWindowViewModel.Subscriptions.cs:271-275,290-292` | UI-state/cancellation/lifetime | pending |
| CL-1-3 | P1 | Unbound background timer refresh execution across disconnect or mode switch | `MainWindowViewModel.Subscriptions.cs:248-268,283-294`; `MainWindowViewModel.Connection.cs:91,108` | correctness; lifetime | pending |
| CL-1-4 | P1 | Auto-failover and server test timer cancellation token race | `MainWindowViewModel.ServerTesting.cs:24-25,195-199,378-382` | UI-state/cancellation/lifetime | pending |
| CL-1-5 | P2 | Disarm countdown token leak on settings window disarm trigger | `MainWindowViewModel.Settings.cs:370-385` | UI-state/cancellation/lifetime | pending |
| CL-1-6 | P1 | FreeConfigs `TrimAndReclaim` concurrent collection modification during enumeration | `FreeConfigsPageViewModel.cs:1266-1292,1914-1923` | correctness; UI-state | pending |
| CL-1-7 | P2 | `DataContextChanged` unsubscription leak across window recreation | `FreeConfigsPage.axaml.cs:26-34`; `AGENTS.md:69-70` | UI-state/cancellation/lifetime | pending |
| CL-1-8 | P2 | FreeConfigs deep verifier cancellation classified as generic error | `FreeConfigsPageViewModel.cs:383,600,685,1349` | correctness; UI-state | pending |
| CL-1-9 | P2 | `NumericUpDown` decimal-to-int two-way binding cast risk | `FreeConfigsPageViewModel.cs:112,126`; `FreeConfigsPage.axaml:112,126`; `AGENTS.md:62-63` | tests/schema/upstream | pending |
| CL-1-10 | P2 | Zapret probe elapsed timer tick race after token cancellation | `MainWindowViewModel.cs:4943-4969,5255,5582,7155` | UI-state/cancellation/lifetime | pending |
| CL-2-11 | P1 | `AboutWindow` synchronous process stdout/stderr pipe reading deadlock | `AboutWindow.axaml.cs:97-100` | correctness/rendering; lifetime | pending |
| CL-2-12 | P2 | `AboutWindow` child process orphan on execution timeout | `AboutWindow.axaml.cs:99-100` | lifetime/upstream | pending |
| CL-2-13 | P2 | Bare-string `CheckBox.Content` layout overflow on narrow windows | `SubscribePage.axaml:274-278`; `DpiBypassPage.axaml:614`; `AGENTS.md:53-60` | accessibility/narrow-layout/contrast | pending |
| CL-2-14 | P2 | Avalonia 12 `TabControl` Carousel height constraint propagation bug | `MainWindow.axaml:779,790`; `FreeConfigsPage.axaml:180`; `AGENTS.md:49-51` | accessibility/narrow-layout/contrast | pending |
| CL-2-15 | P1 | Unhandled `AvaloniaXamlLoader` runtime exception on missing resource or type | `FreeConfigsPage.axaml.cs:18`; `SimplePage.axaml.cs:13`; `App.axaml.cs:26` | XAML-loading/lifecycle/upstream | pending |
| CL-2-16 | P2 | Missing `AutomationProperties` accessibility labels on interactive controls | `ApplicationsPage.axaml:277,336`; `ServersPage.axaml:298,441,447`; `NetworkPage.axaml:1538,1585` | accessibility/narrow-layout/contrast | pending |
| CL-2-17 | P2 | Parent `DataContext` cast failure in `FreeConfigsPage` XAML command binding | `FreeConfigsPage.axaml:379,388`; `FreeConfigsPage.axaml.cs:26` | XAML-loading/lifecycle/upstream | pending |
| CL-2-18 | P2 | Direct color brushes violating semantic design token contract | `NetworkPage.axaml:2317,2323,2329`; `Tokens.axaml`; `AGENTS.md:30-34` | accessibility/narrow-layout/contrast | pending |
| CL-2-19 | P2 | Bare-string `Button.Content` text clipping on localized strings | `DpiBypassPage.axaml:596,607,708,740,807`; `EmergencyChannelPage.axaml:80,191,225`; `NetworkPage.axaml:401,1982,2147` | accessibility/narrow-layout/contrast | pending |
| CL-2-20 | P2 | `AboutWindow` URL launch unhandled exception on non-standard desktops | `AboutWindow.axaml.cs:43-58` | correctness/rendering; upstream | pending |

## Lead status

Pending Iteration B and source verification. Similar-looking candidates remain separate until the lead traces their actual control flow.
