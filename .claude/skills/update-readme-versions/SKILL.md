---
name: update-readme-versions
description: Bump build script version examples in README.md + README.ru.md after a release. Keeps "powershell build.ps1 -Version 'X.Y.Z-rN'" + "./build-mac.sh X.Y.Z-rN" snippets in sync with current candidate.
when: After shipping any -rN candidate or stable. Always sync both README files (en + ru).
---

# Update README version examples

Двa README — английский (`README.md`) и русский (`README.ru.md`). У обоих
секция "Build from source" / "Сборка из исходников" с примерами:

```powershell
# Windows (PowerShell)
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "X.Y.Z-rN"
```
```bash
# macOS DMG
./build-mac.sh X.Y.Z-rN
```

После каждого release эти строки бампятся к текущей версии (любой -rN или
stable — берём то что **только что зашипили**).

## Pattern

```bash
# В README.md и README.ru.md одинаковая структура. Edit обоих:

# 1. Найти текущую версию:
grep "build.ps1 -Version" README.md
grep "build-mac.sh"       README.md

# 2. Заменить через Edit tool:
# OLD: powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.28.2"
# NEW: powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.28.3-r1"
```

## Why both files

Russian README в России основной (большинство пользователей RU-locale).
English для мирового discoverability. Должны быть в синхре.

## When НЕ обновлять README

- При экспериментальных tag'ах не для пользовательской рассылки.
- При hotfix'ах которые не требуют пользовательской пересборки (только server-side fixes).
- Если examples только что были обновлены (избегаем noise commits).

## Commit pattern

```bash
git commit -m "docs(readme): bump build script examples to X.Y.Z-rN

Release-candidate shipped (see release notes on GitHub).
README 'how to build locally' examples now reference the current candidate.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

Маленький commit, без content changes — README chapters про features меняются
**только** на minor version bump (например в v2.28.0 stable добавляется bullet
"v2.28 new feature"), не на каждый -rN.

## NOT to do

- Не редактировать featue list в README на каждый -rN — bloat.
- Не забывать `README.ru.md` — это как написать FreeConfigsPage без подписей.
- Не cap'ать версию более старой чем уже released.
