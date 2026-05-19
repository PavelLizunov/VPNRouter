# .github/workflows/

CI pipelines. 4 workflow'а — каждый делает что-то специфичное и триггерится
независимо.

## Workflows

| File | Триггер | Что делает |
|---|---|---|
| `build-mac.yml` | `push` tag `v*` + `workflow_dispatch` | Собирает macOS DMG + ZIP на mac-runner. Уплоадит на release. Дисптачит Homebrew Cask update (только stable, prerelease skip). |
| `build-linux.yml` | `push` tag `v*` + `workflow_dispatch` | Linux .deb (postinst setcap для passwordless TUN) + AppImage + .tar.gz + 4 sha256. Уплоадит на release. |
| `build-android.yml` | `push` tag `v*` + `workflow_dispatch` | Android APK (arm64, signed). Phase 6 Wave 26 (2026-05-18) бампнул на .NET 10 + Android API 36 + Avalonia 12 + provision libbox.aar из `LIBBOX_AAR_BASE64` secret. Graceful skip если keystore/libbox secrets не выставлены. |
| `build-free-pool.yml` | cron каждые 6ч + `workflow_dispatch` | Server-side aggregator: фетчит 14 free-config sources, validates TCP+TLS, GeoIP enrich → `pool.json` artifact для in-app Free Configs tab. |
| `publish-apt.yml` | `release` event + `workflow_dispatch` | Index'ит .deb из последнего stable release в reprepro APT repo на gh-pages. Также копирует `install.sh`, `install.ps1`, `uninstall.ps1`, `index.html` → gh-pages. CNAME `vpn.ninitux.com` deploy via GitHub Pages. |

## Secrets

| Secret | Кто использует |
|---|---|
| `GITHUB_TOKEN` | автоматический, для `gh release upload --clobber` etc. |
| `HOMEBREW_TAP_DISPATCH_TOKEN` | `build-mac.yml` Trigger Homebrew Cask step (cross-repo dispatch к `PavelLizunov/homebrew-vpnrouter`) |
| `ANDROID_KEYSTORE_BASE64` | `build-android.yml` Decode signing keystore step — base64-encoded JKS keystore для подписи APK |
| `ANDROID_KEYSTORE_PASSWORD` | `build-android.yml` dotnet publish step — пароль keystore + key |
| `LIBBOX_AAR_BASE64` | `build-android.yml` Provision libbox.aar step — base64-encoded ~11.7 MB sing-box gomobile binding (gitignored в репо). См. `.github/SECRETS.md` за provisioning-командой. |

Phase 6 Wave 26 (2026-05-18) добавила `LIBBOX_AAR_BASE64` чтобы CI мог собрать Android APK после Wave 23 (commit c33e372) бампа на net10.0-android36.0 + Avalonia 12. libbox.aar остаётся gitignored (private gomobile build, не комитим). Подробности + rotation policy — `.github/SECRETS.md`.

`GH_TOKEN` обязателен в env для каждого `gh release ...` step (иначе anonymously fails). См. `plans/session-handoff-2026-04-24.md` — урок от пропущенного `GH_TOKEN` в Trigger Homebrew Cask step.

## Триггер цепочки на стандартный релиз

1. Локально: `git push --tags` → push тэга `v2.X.Y-rN`.
2. **build-mac** + **build-linux** автостартуют параллельно.
3. Локально: `build.ps1 -Version "2.X.Y-rN" -Upload` создаёт release + кладёт Windows ZIP'ы.
4. Mac/Linux уплоадят свои артефакты в существующий release (`gh release upload --clobber`).
5. **publish-apt** триггерится `release` event'ом — индексирует .deb если stable.

## Race condition на тэг

Если **Mac CI завершилась ДО того как build.ps1 создал release** — Mac upload step выйдет с warning "Release does not exist yet — skipping upload". Решение: re-trigger Mac workflow вручную через `workflow_dispatch` после того как release создан. См. v2.28.1-r1 patch session.

## Force-update tag

Иногда после force-push tag (когда нужно поправить commit) надо отменить старые runs:
```bash
gh tag -f vX.Y.Z-rN <new-commit>
git push -f github vX.Y.Z-rN
gh run cancel <old-run-id> --repo PavelLizunov/VPNRouter
```

## Workflow_dispatch params

Все 4 поддерживают `workflow_dispatch` без обязательных inputs (или с optional `version`). Удобно для retry без bumping тэга.

## Не редактировать без причины

`pages-build-deployment` — это **GitHub-managed** workflow для Pages, не наш файл. Не пишется в `.github/workflows/`.
