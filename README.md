# TextFixer

First test build of a Windows tray writing assistant.

Agreed design: .NET 10 WinForms shell, WebView2 review window, plain HTML/CSS/JavaScript, Alt+Q selection capture, explicit review and replacement, and ChatGPT plan sign-in. No frontend build or Node runtime.

This is an experimental test build. Live authentication and Chrome editing need interactive verification with the user's account.

## Build and run

Requires the .NET 10 SDK to build. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1` (or `build.ps1`), then open `artifacts/TextFixer/TextFixer.exe`. There is no npm, frontend build, or Node runtime. Keep the whole output folder together. Running this framework-dependent test build requires the .NET 10 Desktop Runtime and Microsoft Edge WebView2 Runtime.

Publishing now uses `scripts/publish-swap.ps1`, copied and adapted from the hot-swap-update skill. It builds beside the install folder, renames locked code files with a timestamp, and moves in the new build without terminating the app. UI files are embedded in the assembly. An existing instance keeps running its old version; the next normal start runs the update. The legacy loose `Web` folder is preserved for the first transition so a currently running older build retains its page files. New code does not read that folder.

Locked retired files are retained and retried for cleanup on a later publish. This environment's wrapper skips reboot/sign-in cleanup registration because it cannot write those system/user settings. App state, credentials and Chrome registration manifests are stored under `%LOCALAPPDATA%\TextFixer`.

The skill is available project-locally at `skills/hot-swap-update/SKILL.md`, referenced by `AGENTS.md`. Global installation into `C:\Users\JPS\.codex\skills` was denied by the filesystem sandbox. `install-codex-skill.ps1` is prepared for execution outside that restriction.

The window opens on first launch. TextFixer has no taskbar button; it lives in the tray. Settings > Startup > "Start with Windows" (off by default) adds a current-user Run entry; a start with Windows opens no window and waits in the tray. Closing or minimizing the window hides it to the tray; double-click the tray icon to reopen, or choose Exit from the tray menu to stop the app. Open (default Alt+E) captures the selection and shows a task choice: Review, Translate, Fact check, Rewrite, Summary or Explain; nothing is sent until a task is chosen. Review has the default shortcut Alt+Q and starts right away. The other tasks start without a shortcut; each can get one in Settings > Shortcuts, which then captures and starts that task (Rewrite opens its presets). Startup reports any shortcut another app has registered.

Settings > Shortcuts lists Open, Review, Translate, Fact check, Rewrite, Summary and Explain. Click a shortcut, press the new keys (Esc cancels); Remove clears an optional one, and Open always keeps one. A shortcut is Ctrl and/or Alt, optionally Shift, with A to Z, 0 to 9 or F1 to F24 (`Services/Shortcuts.cs`). Refused: duplicates between the tasks, Ctrl+A/C/V/X/Y/Z (editing, and TextFixer sends Ctrl+C and Ctrl+V itself), Alt+F4, Ctrl+Alt combinations that type a character on the current keyboard layout (AltGr, checked with `ToUnicodeEx`), and combinations another app has registered. `Native/HotkeyManager.cs` registers only the set shortcuts (hotkey ids 46 to 52 in task order). While a shortcut is being recorded it unregisters all of them so the keys reach the window; they return after the change, on Esc, when the window or Settings closes, and after 30 seconds at the latest. A rejected change keeps the previous registrations. Changes apply immediately and are stored as `shortcuts` in `settings.json`: text such as "Ctrl+Alt+K", or null for a task without a shortcut. An invalid entry keeps its default; a saved set with duplicates falls back to all defaults. The tray tooltip, Help and window texts show the current shortcuts.

## Review mode

Settings > Review (stored in `%LOCALAPPDATA%\TextFixer\settings.json` as `reviewMode`):

- **One request for all tabs** (default): one ChatGPT request returns the corrected text, the labelled corrections and the Worth checking findings.
- **One request per tab**: five focused requests (Spelling, Grammar, Punctuation, Wording, Worth checking) are sent at the same time; Formatting is checked locally. A full check takes about as long as the slowest single request. Each request fixes only its own category (`ReviewPrompt.FocusedInstructions`). The app sends one `tabProgress` listing all running tabs, then a `tabResult` as each answer arrives, in any order; the window marks tabs ready, running (spinning ring), failed (`!`) or stopped, and finished tabs can be reviewed while the rest run. Results are merged in `focusParts` (`app.js`): where two tabs change the same characters, the tab further left wins; changes that only touch the same word (for example a capital letter and a following comma) are kept as separate character-level edits. Decisions made while later tabs run are kept. Copy and paste unlock when all requests finish. A failed tab is reported and skipped; sign-in problems and an exhausted usage limit cancel the other requests and stop the run. This mode uses about five times the plan allowance of a single review.

## Clipboard history

TextFixer keeps your last 20 copied texts (Clipboard history button in the header). Each entry has "Copy plain" (text only) and, when it was copied with HTML or RTF, "Copy formatted" (text plus the saved HTML and RTF); these buttons are the only way to copy an entry, and clicking the entry text does nothing. The × in the corner removes an entry. A request without `plain:false` copies plain text. Copying the same text again moves its entry to the top. Images and files are not recorded. Content marked `ExcludeClipboardContentFromMonitorProcessing` or `CanIncludeInClipboardHistory = 0` (password managers, TextFixer's own temporary Alt+Q data) is skipped, as are texts over one million characters. The history is stored in `%LOCALAPPDATA%\TextFixer\clipboard-history.bin`, DPAPI-encrypted for the current Windows user. Settings > Clipboard history turns recording on or off (on by default; turning it off deletes the history) and clears it. The setting lives in `%LOCALAPPDATA%\TextFixer\settings.json`. Offline tests: `dotnet run -c Release --project tests/ClipboardHistoryTests`.

## First test

1. Click "Continue with ChatGPT" and finish authorization in the system browser. The app waits for the callback; Stop ends the wait.
2. In a disposable Chrome text field, type a paragraph, select it, and press Alt+Q. Release both keys.
3. The captured selection is sent for review automatically after capture when ChatGPT is connected.
4. Review suggestions in the Spelling, Grammar, Punctuation, Wording, Worth checking and Formatting tabs. Accept or reject individual changes. Reasoning rewrites require an explicit "Use this wording" action.
5. Choose "Copy with formatting" / "Paste with formatting", or the explicit plain-text option. Replacement returns focus, copies the selection again, and aborts if it no longer matches. Check the editor result. Ctrl+Z in the editor should undo a normal paste.
6. Also test changed selections, a closed target window, multiple Chrome tabs, multiline text, and clipboard restoration before using it on important text.

Only the selected or pasted text is sent, up to 30,000 characters. Text inside password fields is generally not copyable; do not use this app for secrets. The app never submits or sends posts or emails.

## Chrome direct editing (new test build)

The canonical current build is `artifacts/TextFixer/TextFixer.exe`. Earlier test folders named TextFixer-Chrome and TextFixer-update are superseded. Your saved ChatGPT connection is reused.

`Update TextFixer.cmd` now uses the same hot-swap publisher. It does not wait for the running app to close and does not forcibly terminate it. The updated version becomes active on the next normal launch.

AI connection and Chrome setup are in the Settings dialog, opened from the header. They do not occupy the daily review screen. Click "Register Chrome connection" there to register the currently running executable for your Windows user, then load the extension as described below. No registration is performed by merely starting the app.

Translate: German text becomes English, text in other languages becomes German (`ReviewPrompt.BuildTranslationRequest`, one plain-text request, about 2 to 5 seconds). The window shows the translation with "Copy translation" and "Replace selection"; replacement pastes plain text over the captured selection after the same target checks as Alt+Q. Read-only captures get copy only. Leading and trailing whitespace of the selection is kept. A field below the translation takes a change request (for example "more formal", "use du", "shorter"); Revise sends the source, the current translation and the request (`ReviewPrompt.BuildTranslationRevisionRequest`, up to 500 characters) and replaces the translation with the answer.

Fact check checks the factual claims of the selection with web search (`ReviewPrompt.BuildFactCheckRequest`, the Responses `web_search` tool, structured JSON when the model supports it, about 10 to 30 seconds, limit `ChatGptProtocol.FactCheckTimeout` = 300 s). Before the check starts, an optional context field (up to 4,000 characters: topic, time, place, the referred article, links) waits for Check facts (also when started by the Fact check shortcut); the request sends a JSON object with `text`, `context`, `clarifications` and `only`, and the model opens links in the context through web search, TextFixer never loads them. Each claim is shown with its quote, a verdict (Supported by the sources found, Wrong, Disputed, Can't verify, Needs context), a short explanation and its sources; Needs context (`needs_context`, with a `question`) marks a claim that can only be checked with information from the user, needs no source and no completed search, and shows the question with an answer field: the answer goes into `clarifications`, and `only` limits the re-check to that quote, whose card is replaced (its sources join the `openSource` allowlist). "Add context and check again" below the results checks the whole text again with the new context and all answers. Context and answers live in the window per capture and are not saved; a source opens in the default browser. Wrong claims can carry a correction; accepted corrections become exact edits of the quoted range and are pasted through the normal review pipeline, so formatting is kept (Alt+Q HTML patching, Chrome direct editing). A correction whose quote occurs more than once is shown but cannot be applied; overlapping corrections exclude each other. Opinions, plans and questions are not claims; a text without claims gets "No checkable factual claims found".

Provenance rules (`Services/FactCheckParser.cs`): sources come only from `url_citation` annotations the API attaches to the output (stream events `response.output_text.annotation.added`, plus the final response). Each citation belongs to the claim whose JSON object contains its offset (UTF-16 offsets into the model output; `ClaimSpans` maps them). URLs written into the JSON text are not sources. A supported, wrong or disputed verdict without a citation becomes "Can't verify" with a note. Sources must be absolute https URLs without user info, control characters or whitespace; at most 5 per claim and 40 claims. If claims come back but no web search completed, the check fails with "Web search did not run" instead of answering from memory. The window opens a source only through the `openSource` action, and only for a URL cited by the current capture's fact check; the WebView never navigates.

Rewrite, Summary and Explain (`ChatGptReviewService.WriteAsync`, `ReviewPrompt.BuildWritingRequest`, one plain-text request each, review model): Rewrite has the presets More formal, Friendlier, Shorter, Simpler and Clearer structure and a free-text request field (up to 500 characters). Each further request changes the latest rewrite; "Show changes" diffs the captured original against the latest rewrite, and "Replace as plain text" pastes the whole rewrite over the selection (formatting is not kept). The prompt keeps facts, names, numbers, dates, quotations and links and treats the text as data. Summary and Explain answer in the language of the text, add nothing and do not judge truth. Below the answer a dialog takes follow-up questions and objections (`discuss` action, `ChatGptReviewService.DiscussAsync`, `ReviewPrompt.BuildDialogRequest`): each request sends the captured text as data, the first answer and the conversation so far, uses the fact-check model with the `web_search` tool, and asks the model to check objections and say plainly whether they are right instead of agreeing to please. `FactCheckParser.CitedAnswer` removes citation markers, keeps line breaks and takes sources only from `url_citation` annotations (exact https URLs, up to 8); an answer without a completed search is labeled "Answered without web search". Cited URLs join the per-capture allowlist that `openSource` checks. The conversation lives in the window per capture (up to 20 questions, nothing written to disk); a failed or cancelled question returns to the input field. Each answer has its own Copy button; Copy conversation copies the whole dialog with sources as plain text.

Settings > Spelling and address sets the spelling of "E-Mail" (Keep as written, E-Mail, E-mail, Email) for every task and the form of address for translations into German (Automatic, du, Du, Sie), stored as `emailSpelling` and `addressForm` (`Services/WritingPreferences.cs`). "du or Du in Review" (Keep as written, du, Du; `reviewDu`) sets the capitalization of du forms for review and fact-check corrections; Keep as written tells Review to leave the capitalization of du forms as the text writes it, and the rule never switches between du and Sie or changes the formal Sie/Ihnen/Ihr. The address rule goes only into translation and translation revision requests; Automatic adds no rule. Rewrite, Summary and Explain keep the form the text uses. The E-Mail rule excludes URLs, email addresses, file names, code, product names and quoted text.

The task row (Review, Translate, Fact check, Rewrite, Summary, Explain) above the results runs another task on the same captured text without capturing again. Results and accepted choices are kept per task, so switching back sends no new request. Every capture has an id (`captureId`); answers for an older capture are dropped, and a new capture clears all task results. After a paste, no task can replace the selection again.

Settings > Model lists the models the signed-in account offers (`/models`, entries with visibility "list") as radio buttons, separately for reviews (also used by Rewrite, Summary and Explain), translations and fact checks. Review "Automatic" uses the first listed model; translation and fact check "Same as review" follow the review choice. The choices are stored as `model`, `translationModel` and `factCheckModel` in `settings.json`; a saved model the account no longer lists falls back to the default, and the window says so.

Settings > Punctuation style has seven groups, each with its own setting: quotation marks and apostrophes, dashes, ellipsis, special spaces (non-breaking, narrow, thin), the multiplication sign, arrows, and feet and inches (prime marks). Each group is "Keep as typed" (default), "Make plain" or "Allow typographic", stored as `punctuationStyle` in `settings.json`. Keep as typed: answers use the plain form (`"`, `'`, `-`, `...`, space, `x`, `->`), and characters already in the captured text stay. Make plain: the user's own typographic characters are converted as well and appear as Punctuation corrections (in per-tab mode they come from the Punctuation tab only). Allow typographic: no rule. The groups live in `Services/Punctuation.cs` (prompt rules added to review, translation and revision requests) and `reviewStyleMaps` in `review-model.js` (conversion of answers, Worth-checking rewrites and translations); keep both lists in sync.

Alt+Q captures any selection the active app can copy. At capture, Windows UI Automation reports whether the focused element accepts typing. Selected text in a read-only document (for example a web page paragraph) gets "Copy" only; editable fields and content-editable editors keep "Paste". When the answer is unclear, both actions stay available. A failed capture writes the app name and reason (never text) to `%LOCALAPPDATA%\TextFixer\diagnostics.log`.

A status-bar icon identifies a Chrome or Alt+Q capture, with its source in the tooltip. Alt+Q remains a clipboard workflow; direct editing is available after an extension capture. When the clipboard includes valid HTML, Alt+Q retains that original fragment, patches only accepted text corrections into its text tokens, and offers "Paste with formatting" or "Copy with formatting". Original paragraph elements, empty elements, inline formatting, attributes and styles remain intact. CF_HTML offsets are recalculated in UTF-8 bytes. Cross-format edits or HTML/plain-text mismatches are rejected without an automatic plain-text fallback. Plain-text replacement requires the explicit checkbox and is enforced in C#.

The companion extension is plain JavaScript, without a build step. Setup is once per Windows user:

1. Open `chrome://extensions` in Chrome, enable Developer mode, choose Load unpacked, and select the project's `chrome-extension` directory.
2. The bundled manifest key gives it the fixed ID `hpmehbmdgfpgoggpemlefhfmbjeedpop`. Verify this is the ID Chrome displays.
3. Run `powershell -File install-chrome-bridge.ps1` from the project directory. It registers only that extension for the new executable using a current-user native messaging registry entry. This setup script has been supplied, not run automatically.
4. In a disposable text field or supported contenteditable editor, select a passage and right-click "Review selection in TextFixer". Check the result in the desktop app, then choose "Apply in Chrome".

This route does not use the clipboard. It computes accepted correction ranges locally, validates the complete original editor text, preflights rich-text ranges, and applies small native text editing commands from end to start. Untouched paragraph and formatting elements are retained. Corrections that cross text-node/formatting boundaries or change line breaks are refused. Navigation, disconnection or changed text invalidates the capture; identical-text re-renders are allowed. Nonbreaking and regular spaces compare equivalently without changing offsets; NBSP-only suggestions are omitted. Each edit is checked for a stable expected result, polling for up to one second to allow asynchronous editors to update. At most 200 corrections can be applied per batch. There is no automatic clipboard fallback after a direct edit fails.

Accepted corrections are trimmed to their smallest differing character range without splitting Unicode surrogate pairs. Before sending a direct edit, the extension yields for editor selection handlers and verifies the exact selection and text again. It stops between edits at a 60-second batch deadline, reporting how many corrections were applied, before the desktop's 90-second timeout. Reload the extension in `chrome://extensions` after updating its files.

Some rich editors intercept or transform commands. If an editor rejects a correction midway through a batch, earlier corrections may already be applied. Check the editor and use its Undo before retrying. Undo grouping is editor-dependent. Canvas-based editors such as Google Docs are not supported by this first bridge. No universal compatibility or zero Unicode-error guarantee is made. Alt+Q uses clipboard replacement, with original HTML retained when available.

The extension starts a native-host mode of the same executable. It relays framed UTF-8 JSON over a current-user-only named pipe to the tray app. Credentials stay in the tray process. Host registration validates the exact extension origin and executable path. Move the executable only after re-running registration for the new path.

Validation: bridge framing/pipe tests and local range validation pass. `tests/chrome-editor.html` is a standalone local browser regression page covering textarea Unicode/paragraphs, rich-text bold/italic/empty paragraphs, source mutation and cross-format refusal. Automated headless execution failed at browser runtime startup in this restricted environment; actual editor behavior and the installed extension round trip need normal-Chrome verification. Open that page in normal Chrome and inspect its PASS/FAIL results before testing real drafts.

## ChatGPT connection

Uses the official Sign in with ChatGPT plan-usage protocol for eligible Plus/Pro accounts, subject to plan availability and limits. No API key or Node companion is required. See https://developers.openai.com/cookbook/articles/sign-in-with-chatgpt.

Tokens stay in C# and are encrypted with Windows DPAPI for the current user under `%LOCALAPPDATA%\TextFixer\ChatGPT\credentials.bin`. Sign out clears the local session and attempts remote revocation. "Manage usage" opens ChatGPT usage settings. The protocol is a preview and may change.

## Editing limits

Capture and replacement use Ctrl+C and Ctrl+V, not accessibility writes. Common clipboard formats are saved and restored, provided the clipboard has not changed independently. Private/delayed formats may not survive. Temporary text written by TextFixer requests exclusion from clipboard history/cloud sync; the source app's own copy operation may still enter history.

HTML clipboard capture now preserves the original fragment and changes only safely mapped text ranges. Both the original paragraph markup and Unicode text are offered to the destination. RTF-only capture does not yet support rich rewriting; plain-text replacement remains explicit. Destination applications may ignore rich clipboard formats or normalize the pasted HTML. Paste uses delayed rendering: restoration occurs only after a supported-format read from the target process has been observed, with a short grace period. If the read cannot be attributed to that process within five seconds, the revision stays on the clipboard and a notice asks you to check the editor. Read confirmation does not prove that the editor accepted the paste.

Delayed rendering supplies only the format Windows requested; disposal materializes any remaining formats. The HTML SourceURL header is retained so relative links retain their original base. `tests/ClipboardIntegrationTests` exercises real WinForms/raw CF_HTML Unicode round trips in a private, non-visible window station, never the user's interactive clipboard. It skips before any clipboard access if isolation is unavailable. That isolation was denied in the current test environment, so this live integration check remains unverified here.

Restored entries also request history/cloud exclusion. Clipboard-manager automatic expiry may be disrupted by any save/restore operation. Elevated applications are rejected. Identical text in another field of the same window cannot be distinguished by the text comparison. Use a Copy action when uncertain.

## WebView2 diagnostics

A WebView2 crash occurred during a restricted automated launch. Its root cause is unconfirmed; the app has not been visually verified on the normal desktop. Initialization and runtime failure events are logged without text or credentials in `%LOCALAPPDATA%\TextFixer\diagnostics.log`. Runtime crash dumps may be under `%LOCALAPPDATA%\TextFixer\WebView2\EBWebView\Crashpad\reports`. Start the release executable from Explorer for the first real test. Do not change security settings or disable browser sandboxing to make it run.

## Branding

The approved concept 02 TF ligature is implemented as vector geometry. `src/TextFixer/Web/logo.svg` supplies the UI mark. `src/TextFixer/Assets/TextFixer.ico` supplies the executable and window icons; `tray-dark.ico` and `tray-light.ico` supply monochrome tray variants, selected from the Windows system theme and refreshed on preference changes. All are embedded in the application.

The interface now uses AI Center's petrol palette: background `#0a2532`, panel layers `#0d2c3b` / `#163a4c`, pale text, and amber `#f5cc57` actions and focus accents. Correction insertions/removals retain distinct green/red colors. Settings, the title bar, the tray menu, and the logo's dark-surface treatment follow the same palette. App and extension icons use petrol backgrounds with a white mark.

The Chrome extension uses matching PNG assets at 16, 32, 48 and 128 pixels. Reload it in `chrome://extensions` to refresh its toolbar and extension-list icons. The extension ID remains unchanged.

Optional development generator: `dotnet run --project tools/BrandAssets -c Release`. Generated SVG/ICO/PNG files are already included in the source tree; normal application builds require no additional asset-generation step. `design/logo-concepts/textfixer-02-production.png` previews the production mark and tray sizes.

## Validation

`node tests/diff.test.cjs` runs offline reconstruction/accept-reject tests for the local word diff. Node is used only for development tests, never by the app or its build. The service layer was independently checked offline for parsing, quoted evidence, DPAPI, callback handling, and invalid token rejection. Real sign-in, refresh, inference, and Chrome round-trip editing still need an interactive account test.

## Review tabs and finding rewrites

The review opens on the first non-empty tab in the fixed order below. If every tab is empty, Spelling remains selected. Tabs appear in this order: Spelling, Grammar, Punctuation, Wording, Worth checking, Formatting. Each has a count; empty tabs remain visible and greyed. The full text remains visible in "Corrections in context". Only the selected tab's corrections are highlighted and clickable. Other corrections are faded and inert, showing their accepted or rejected text. Switching tabs preserves choices, and the revised text includes accepted corrections from every tab. "Accept all" and "Reject all" affect the selected tab only.

Service-supplied correction categories are matched to locally computed diff parts using original UTF-16 ranges. Missing labels fall back to punctuation for punctuation/quote/whitespace-only changes, spelling for a single changed word, and grammar otherwise. If one diff unit overlaps several classified ranges, the largest overlap wins, with source order breaking ties. Category labels never supply the edit positions: the local diff and minimal Unicode-safe patches remain authoritative.

Worth checking contains all finding kinds, each labelled (ambiguity, term misuse, contradiction, unsupported claim or logic gap). A finding offers "Use this wording" only when its first quote is non-empty and occurs exactly once, including overlapping occurrences. Choosing it creates an accepted replacement in the normal edit pipeline. Overlapping spelling/grammar corrections are superseded without losing their choices. "Undo wording" or rejecting the replacement restores those choices. Overlapping finding rewrites are mutually exclusive. Worth checking bulk actions affect only rewrites the user has activated, not every unchosen finding. Dismissing a finding also undoes its active rewrite. Rewrites that change line breaks are disabled, with the reason displayed beside the button. Existing HTML mapping and direct-editor safety checks apply; a rewrite that crosses formatting can be refused with the usual mapping error.

Validation commands (Node is development-only):

- `node tests/review-model.test.cjs`: UTF-16 category matching, reconstruction, superseding and wording undo.
- `node tests/review-ui.test.cjs`: tab counts, active/inert controls, scoped choices, preview and messaging contracts, fact-check rendering, exact corrections, task row cache and capture ids.
- `dotnet run --project tests/ClipboardHistoryTests -c Release`: clipboard history, settings, and shortcut parsing, rules and loading.
- `dotnet run --project tests/FactCheckTests -c Release`: fact-check stream reading (deltas, citation events, final-only citations, search call status) and provenance rules.
- `node tests/diff.test.cjs` and `node tests/edit-trim.test.cjs`: local diff and minimal-edit reconstruction.
- `dotnet run --project tests/ReviewParserTests -c Release -p:NuGetAudit=false`: categorized response parsing and offset resolution (S66-owned).
- `dotnet run --project tests/HtmlClipboardTests -c Release -p:NuGetAudit=false`: safe rich-text patching and formatting operations.
- `dotnet run --project tests/EditorEditTests -c Release -p:NuGetAudit=false`: edit-batch validation.
- `dotnet run --project tests/BridgeTests -c Release -p:NuGetAudit=false`: framing and native bridge checks (S66-owned).

Acquire `.build.lock` before .NET builds and test runs. The publisher acquires it internally. Visual browser checks are coordinated by S66.

## Ownership

- S67 (formerly S46): project files, application shell, native clipboard/hotkey integration, WebView2 UI, extension, build, integration and docs.
- S66 (formerly S45): coordination, `src/TextFixer/Services/`, `src/TextFixer/Bridge/`, parser/bridge tests and Git commit/push. The shared correction contract update in `Contracts/ReviewContracts.cs` is agreed with S67.
- Shared build output lock: `.build.lock`. Acquire with exclusive file creation before builds; remove after completion or failure. Do not build while another session holds it.

Credentials must remain in the C# process and be protected with Windows DPAPI. Never expose tokens to WebView2 or logs.

### Optional Alt+Q formatting checks

Capture with Alt+Q and check the text as usual. Formatting is checked automatically when copied HTML is available. Any formatting suggestions appear separately in the review and start unchecked. Accept each desired fix before "Paste with formatting" or "Copy with formatting". These checks run locally and apply only to the Alt+Q HTML clipboard path.

The first rule handles a single edge space in simple explicit underline, strikethrough or link tags, where adjacent unformatted text makes the boundary clear. It preserves the text and moves the space outside that tag. Complex/nested formatting, CSS-dependent decoration, paragraph-end spaces, repeated spaces and ambiguous captures are skipped. A text correction touching the same space must be rejected before applying its formatting fix. Destination apps still control HTML paste rendering. Chrome direct editing is unchanged.
