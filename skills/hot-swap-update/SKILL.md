---
name: hot-swap-update
description: Update a Windows desktop app (.exe) in its install folder while it is running, without closing it, by renaming the running files with a timestamp, moving the new build in, and deleting old versions when free or at the next reboot/sign-in. Use this whenever the user wants to publish, deploy, install, ship or update a Windows exe (.NET WinForms/WPF/WebView2, Tauri, Electron, C++) and the app may be open, when a publish fails because the exe is "in use" or locked, when they say "replace the running exe", "update without closing the app", "the user shouldn't be disturbed", "new version on next start", or when setting up a publish/release script for a new Windows desktop project — even if they don't mention renaming or locks.
---

# Hot-swap updates for Windows apps

Windows locks a running exe against overwriting and deleting, **but not against renaming on the same drive**. The running process keeps its renamed file; the original path becomes free, the new build moves in, and the next start runs the new version. The user is never interrupted.

## The system

1. **Build** into a staging folder next to the install folder (same drive, so moves are cheap renames and never cross volumes).
2. **Retire** every current item in the install folder: rename it to `<name>.old-<yyyyMMdd-HHmmss>` (files keep their extension: `App.old-20261107-142301.exe`). Nothing is overwritten. Timestamps make repeated updates safe even while several old versions are still running.
3. **Move in** the new files.
4. **Clean up** each `*.old-*` item:
   - delete it now if nothing uses it;
   - else mark it for deletion at the next reboot with `MoveFileEx(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)`. This needs an elevated shell, and a folder must be emptied first, so mark its files, then subfolders deepest first, then the folder;
   - else (no admin rights) add a `HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce` entry that deletes it at the next sign-in. After a reboot this amounts to the same.

The ready-made script is `scripts/publish-swap.ps1` in this skill's folder. **Copy it into the project's `scripts/` folder** (so the repo is self-contained) and call it from there:

```
# .NET: publishes a self-contained single-file exe and swaps it in
powershell -ExecutionPolicy Bypass -File scripts\publish-swap.ps1 -Project src\App\App.csproj -Target publish\App

# Anything already built (Tauri, Electron, C++): swaps in that folder's contents
powershell -ExecutionPolicy Bypass -File scripts\publish-swap.ps1 -From <build-output-folder> -Target <install-folder>
```

A thin project wrapper (e.g. `scripts/publish.ps1` that calls it with the right `-Project`/`-Target`) keeps the command short.

## Make the app ready for it

The swap only works cleanly when the install folder holds **nothing but code that is loaded once at start**. Check these before wiring up the script:

- **Embed loose assets in the exe.** HTML/CSS/JS for a WebView, images, config templates: files next to the exe aren't locked, so the swap replaces them under the running old exe. A page reload (or WebView crash recovery) would then mix old host code with new page code. Embedding also turns the app into one file.
  - .NET + WebView2: `references/webview2-embedded-assets.md` (EmbeddedResource + `WebResourceRequested`).
  - Tauri and Electron bundle their frontend into the binary/asar already; nothing to do.
- **Keep all state outside the install folder**, in `%LOCALAPPDATA%\<App>` (settings, secrets, logs, WebView2/browser profiles, caches). Then sign-ins, keys and settings survive every update. Never write next to the exe.
- **Single-file .NET apps** that unpack native libraries (`IncludeNativeLibrariesForSelfExtract`) unpack into a temp folder named after a hash of the build, so old and new versions don't clash. Nothing to do.
- **Single instance:** if the app uses a single-instance mutex, starting it while the old one runs brings the old window forward; the new version runs after the old one closes. That's fine; mention it to the user once.

## When applying this to a project

1. Look at how the project publishes today and where it installs (often `publish\<App>` in the repo, or a fixed tool folder).
2. Move loose assets into the exe and state out of the install folder if needed (see above). Verify the app still starts and loads its UI from the published exe — for WebView2 apps, fetch a page file and an unknown path from the running app (expect 200 and 404).
3. Copy `scripts/publish-swap.ps1` into the project, add the wrapper, and **test it for real**: start the currently installed exe, run the script while it runs, and confirm that the old process is still alive, the install folder has the new exe plus `*.old-<stamp>.exe`, and the cleanup was registered (`Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'`). Then close the old app and start the new one.
4. Add `publish/` to `.gitignore` if the install folder is inside the repo, and document the command in the README (build section) and the architecture doc.

## Gotchas

- The rename must stay on the same drive; the script stages next to the target for that reason.
- `MoveFileEx` delay-delete fails without admin rights (`ERROR_ACCESS_DENIED`); the script falls back to RunOnce automatically. Don't promise "deleted at reboot" unless the shell was elevated.
- Don't delete `*.old-*` leftovers by force or kill the user's running app to make a publish succeed — that is exactly what this system avoids.
- Write the PowerShell file in plain ASCII (Windows PowerShell 5.1 reads BOM-less files as ANSI) and run it with `powershell -ExecutionPolicy Bypass -File …`.
