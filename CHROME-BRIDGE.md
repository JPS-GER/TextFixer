# Chrome editor bridge - implementation contract

Purpose: retain the tray application and ChatGPT connection, with a small plain-JavaScript MV3 companion extension for direct, targeted edits. No frontend compilation. Context-menu selection capture is the first entry point. The current Alt+Q clipboard path remains available for other apps; it must not silently promise rich formatting preservation.

## Ownership for the next increment

- S46 owns `chrome-extension/`, installer scripts, `ReviewWindow.cs`, `TrayApplication.cs`, `Program.cs`, contracts and UI.
- S45 is requested to own only `src/TextFixer/Bridge/` for the C# native messaging and named-pipe transport. Do not edit integration files without agreement.
- Build lock remains `.build.lock`.

## Transport

Host name: `com.textfixer.desktop`. Chrome launches the same TextFixer.exe with its extension origin as first argument. Program detects this before the app mutex and invokes a static native-host entry point supplied by the Bridge layer. Native stdio uses length-prefixed UTF-8 JSON (4-byte little-endian length), 1 MB maximum both directions. No stdout logging.

The native-host process validates the exact extension origin against the locally registered host manifest `allowed_origins`, then forwards framed messages over a current-user-only Windows named pipe to the running tray process. If no app is running, launch TextFixer.exe without origin arguments once and retry connection with a bounded timeout. Keep tokens and service access in the tray process. No localhost server or unauthenticated port.

Proposed C# integration API (S45 may suggest refinements):

- `NativeMessagingHost.IsNativeInvocation(string[] args)` and `RunAsync(string[] args, CancellationToken)`.
- `ChromeBridgeServer` exposes Start, Stop/Dispose, `MessageReceived` with sessionId and JsonElement; `SendAsync(sessionId, object message, CancellationToken)`; disconnect event.
- Events may arrive on background threads; S46 marshals to the WinForms UI thread.
- Server validates framing and JSON size; S46 validates message schema before acting.

## Messages

Extension -> app:

`{kind:"capture", captureId:"UUID", text:"exact selected text", hasFormatting:true}`

`{kind:"applyResult", captureId:"UUID", success:true, message:"..."}`

App -> extension:

`{kind:"apply", captureId:"UUID", original:"exact selected text", edits:[{start:0,end:5,before:"tesst",after:"test"}]}`

Offsets are locally calculated UTF-16 code-unit offsets relative to the captured selection. Never model offsets. Validate complete original text before any edits, validate nonoverlapping ranges and every before string, then apply edits from end to start. Reject changes involving line breaks. Do not send HTML supplied by the model into an editor.

## Editor behavior

Keep captures in an isolated-world content script, bound to the original document, editor and live range. Navigation or source mutation invalidates capture. Preserve untouched DOM formatting and paragraph nodes. Use normal editing commands for targeted text insertions, not wholesale innerHTML/textContent rewrites. Rich frameworks need compatibility checks; if an edit crosses formatting boundaries or cannot be verified, refuse and explain. Canvas editors are unsupported initially. Direct editing reduces clipboard/format losses; it cannot guarantee universal compatibility or eliminate all Unicode problems.

Implementation now exists in `chrome-extension/`, `src/TextFixer/Bridge/`, `EditorEdits.cs`, and the desktop integration files. The stable extension ID is `hpmehbmdgfpgoggpemlefhfmbjeedpop`. The canonical install path is `artifacts/TextFixer`. The app's explicit registration action and `install-chrome-bridge.ps1` store the native-host manifest outside the install folder, under `%LOCALAPPDATA%\TextFixer\Chrome`, and create the current-user registration. No installation or registry registration has been performed automatically. Verify the real ID displayed in Chrome during setup.
