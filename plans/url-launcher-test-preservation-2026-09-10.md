# Safe launcher test preservation proposal

## Result

Subsequent owner choice: minimal launch seam is permitted in principle (launcher-safe-test-boundary). The concrete three-section Micro-Spec was presented in chat and still awaits explicit Утвердить/Согласовано; no implementation authorized by automatic goal rounds. The source-bound alternative below is retained as investigation history, not the selected implementation.

No tests or product files changed. Direct runtime adaptation is not safely available with the existing launcher API. Parent requested a proposal if no safe seam exists; this is that proposal, not runtime test completion.

Read canonical agent contract, Tests zone, private OpenUrl, test project dependencies and both original stale fixtures. Skills: ponytail (reuse/minimality), security-review (bounded shell-execution test safety). No builds, commits, pushes or remote operations.

## Evidence

MainWindowViewModel.cs6370-6388 defines private static void OpenUrl(string). It directly calls Process.Start with UseShellExecute=true after Uri.TryCreate absolute HTTP(S) validation. There is no process delegate parameter, injected launcher or owned override. Tests csproj references xUnit/Avalonia only; existing test search found no Harmony/Cecil/Reflection.Emit interceptor infrastructure. Original PR248 TryOpenUrlSecurityTests and PR245 AppUrlLauncherSecurityTests invoke the method directly with file/cmd/powershell/relative inputs. Porting that approach via reflection would permit OS handler execution if the guard regresses, exactly when the tests should fail. Absence of an expected handler on a particular worker is not a safety boundary. PATH changes do not prevent Windows shell handlers or absolute paths.

## Recommended bounded option without production change

Preserve the union of original negative input rows in a source-bound characterization fixture, explicitly named/documented as source/BCL policy evidence:

1. Extract private OpenUrl source with exact known start/end delimiters and fail closed if unique delimiters or method shape differ. Assert the complete normalized method body equals the reviewed small body, including the URI guard dominating the sole Process.Start, rather than weak independent Contains checks.
2. Execute the exact BCL predicate Uri.TryCreate(input, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) against all original negative rows and assert false. No VM method invocation, no MethodInfo.Invoke, no Process.Start. Preserve original literal differences, not merely categories.
3. Describe this as preservation of test data plus source-bound validation-policy coverage, NOT execution of compiled OpenUrl or proof no shell process is started at runtime. A source change fails the method-body pin before policy claims remain valid; no auto-repinning.
4. Worker focused test and existing Windows characterization CI are still required. Do not change MVM member-surface hash. Parent should approve this weaker but safe coverage classification before considering the original runtime-test gap closed.

A small complete source-body pin is intentionally brittle but reviewable; a regex semantic evaluator, custom IL interpreter, rewriting copied IL, or new runtime patch dependency is disproportionate here and carries worse safety/reward-hacking risks.

## If actual compiled-method behavior is required

Request separate approval for a minimal production-owned launch seam, preserving or deliberately reviewing the characterization contract. An injected process-start action can capture attempted launches without invoking the OS; negative fixtures then assert zero calls and positive inert control asserts one normalized HTTP(S) call. This exceeds the current no-product-modification approval. Alternatively an independently proven OS sandbox denying every process/shell spawn could execute the compiled method, but no such worker guarantee currently exists and provisioning it is not authorized by this task.

No branch deletion justified solely by this proposal. Parent retains the two original branch tips until chosen coverage/archival disposition is implemented and verified.
