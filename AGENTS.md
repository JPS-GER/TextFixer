# Project instructions

- Do not use em dashes or en dashes. Use a regular hyphen, comma, colon, or rephrase.
- Use straight quotation marks.
- For publishing or updating this Windows app while it may be running, read and apply `skills/hot-swap-update/SKILL.md`.
- Publish to the canonical `artifacts/TextFixer` directory with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1`.
- Do not terminate a running app to publish it. The publisher retires locked code files and leaves the running instance open. The update takes effect on the next normal launch.
- UI assets are embedded in the assembly. Preserve the legacy loose `Web` directory during the transition from old builds.
- All user state belongs under `%LOCALAPPDATA%\TextFixer`, outside the application directory.
- Acquire `.build.lock` for publishing. Keep shared file ownership agreements with S45.
