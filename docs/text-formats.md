# Text formats: reading, keeping and exporting

Research and design notes for how TextFixer recognizes the format of captured text, keeps it through AI tasks and exports results in other formats. Collected from the platform help pages listed at the end and from live paste tests (see "Tested profiles").

## Plan

1. **Copy formats and format display** (step 1, built 2026-11-14): one copy button per export format (Formatted text, Plain text, Markdown, BBCode, M! Forum). Formats that match the captured text are marked. A "Received / Source format" line under the captured text shows measured clipboard facts and format candidates with their evidence.
2. **Protection during Translate and Rewrite**: code, link targets, mentions, hashtags and unknown objects are replaced by placeholders before the request and restored afterwards. Link targets come from `href`, because sites may show shortened link text (see M! Forum). Links pass through every task unchanged and are restored as the original link elements, silently; a notice appears only if a link cannot be restored, and then Replace refuses instead of pasting dead text. Quotes are context, not the subject: every task works on the user's own text and reads the quotes only to understand it. Review and Rewrite leave quotes unchanged; Fact check checks only the user's claims and uses the quotes to understand what they answer; Summary and Explain use quotes as context; Translate translates them too; every quote level is kept. Chat formats (YouTube, WhatsApp, Slack) and Unicode bold join the export buttons after paste tests. Manual override of the source format.
3. **Canonical document model**: one internal format for all tasks (see "Canonical format"), with a source map so Replace patches the original instead of rebuilding it. Decided after a benchmark of a small tag set against extended Markdown on real captures.

## Principles

- Recognize a bigger set of elements than any single task produces. Review, Translate and Rewrite keep what the captured text already has; Summary and Explain generate only a small subset.
- Keep the original capture. Replace edits the original (the existing HTML patch path for Word and browsers); Copy converts and may drop elements, with a visible note.
- Compatibility is not evidence. Plain prose fits every format and shows "No distinguishing markup", not a list of all formats.
- HTML on the clipboard means a rich representation arrived, not that the author wrote HTML.
- Ambiguous markers stay literal until the format is known: `*x*` is italic in Markdown and Discord, bold in YouTube, WhatsApp and Slack.
- Convert from a parsed structure, never with global search and replace. Escape while writing.
- Model output is untrusted. The window builds elements with `textContent`, never `innerHTML`.

## Elements

Paragraph, line break, bold, italic, underline, strikethrough, quote, spoiler, mention, hashtag, link, inline code, code block, heading, bullet list, numbered list (with start number), nested list, horizontal rule, table. Preserved untouched until supported: images and attachments, embedded objects (polls), footnotes, task checkboxes, superscript and subscript.

## Export syntax

| Format | Bold / italic / underline / strike | Quote / spoiler | Code / links |
|---|---|---|---|
| Formatted text (HTML, for Word, Outlook, Teams, Google Docs) | `strong` / `em` / `u` / `s` | `blockquote`; no portable spoiler | `code`, `pre`; `a` with checked URL |
| Plain text | text only | `> ` prefix; `Spoiler: text` | text; `label (URL)` |
| Markdown (CommonMark/GFM) | `**B**` `*I*`, no underline, `~~S~~` | `>`; no spoiler | backticks, fences; `[label](URL)` |
| Discord | `**B**` `*I*` `__U__` `~~S~~` | `>`, `>>>`; `\|\|spoiler\|\|` | backticks, fences; `[label](URL)` |
| Reddit Markdown editor | `**B**` `*I*`, no underline, `~~S~~` | `>`; `>!spoiler!<` | backticks; `[label](URL)` |
| BBCode (phpBB baseline) | `[b]` `[i]` `[u]`; strike depends on the board | `[quote]`; spoiler depends on the board | `[code]`; `[url=URL]label[/url]` |
| YouTube comments | `*B*` `_I_`, no underline, `-S-` | none | none; bare URL |
| WhatsApp | `*B*` `_I_`, no underline, `~S~` | `>`; no spoiler | backticks; bare URL |
| Slack composer | `*B*` `_I_`, no underline, `~S~` | `>`; no spoiler | backticks, fences; `[label](URL)` |
| Telegram | native entities; typed syntax needs a client profile | native quote and spoiler | native code and links |
| Unicode social text | styled letters for bold/italic only | text only | text; bare URL |

Lists: HTML `ul`/`ol`, Markdown `- ` and `1. `, BBCode `[list][*]` and `[list=1][*]`, plain text `•` and `1.`. Headings: HTML `h1` to `h6`, Markdown `#`, a separate line elsewhere.

Fallbacks: underline becomes plain text (not bold); strikethrough becomes `Struck: text` where the meaning matters; a spoiler stays visible as `Spoiler: text` with a notice; links keep label and URL; mentions stay `@name` without an invented ID, and a pasted `@name` notifies nobody unless the target turns it into a mention.

Telegram Bot API MarkdownV2 and Slack API mrkdwn are separate from what people type or paste.

## Detection

Order: editor metadata, clipboard structure (HTML, RTF, `SourceURL`), the user's choice or a remembered choice for that site, then conservative syntax candidates. Code is parsed first so examples inside code do not count.

| Evidence | Meaning |
|---|---|
| `SourceURL` of a known site | strongest: that site's profile |
| `data-pm-slice`, `ProseMirror-*` classes | ProseMirror/Tiptap rich editor |
| balanced `[b]`, `[url]`, `[quote]`, `[list]` | BBCode family |
| `>!x!<` | Reddit spoiler |
| `\|\|x\|\|` | Discord or Telegram |
| `<@123>` | Discord mention (could be an example) |
| `~~x~~`, `**x**`, fences, `[label](url)`, `> `, `- `, `1. ` | shared; narrows candidates only |
| `*x*`, `_x_`, `__x__` | conflicting meanings; keep literal |
| `@name`, `#tag` | ordinary text; no dialect |

Display labels: "Strong evidence", "Ambiguous", "No distinguishing markup". No percentages. A manual choice applies to the current capture; "Remember for this site" is explicit and keyed by site, not by browser or window title. Changing the interpretation re-reads the original capture.

Measured facts ("Received"): clipboard formats, CRLF/LF/CR counts, non-breaking spaces, tabs, emoji, combining and zero-width characters, `SourceURL`, a declared HTML charset (labelled as declared).

## Encoding and line breaks

- The Windows text clipboard is always Unicode (UTF-16, CRLF line ends). Markdown and BBCode are content formats, not encodings.
- `HTML Format` (CF_HTML) is UTF-8; its offsets are byte offsets computed after encoding. .NET's `Clipboard.GetData("HTML Format")` decodes it wrongly as ANSI; read the raw bytes as UTF-8.
- RTF needs escaped `\`, `{`, `}` and `\uN` escapes for every non-ASCII UTF-16 unit, including both halves of surrogate pairs.
- Publish all representations as one clipboard item. Plain-only copies publish only plain text.
- Markdown: a single newline is a soft break; two trailing spaces or a backslash make a hard break; a blank line starts a paragraph. BBCode, YouTube, WhatsApp, Discord and Slack keep newlines.
- Never strip non-breaking spaces, joiners, variation selectors or zero-width characters globally. Do not normalize source text.
- Old forums may use Latin-1; do not turn emoji into `&#...;` automatically.
- Length limits count differently per target (Discord 2,000 or 4,000 with Nitro; X weights emoji as 2 and URLs as 23). No verified official limits for YouTube and WhatsApp.

## Canonical format (step 3 proposal)

A small typed tag set ("TFDoc") rather than extended Markdown: `<doc v="1">` with `p`, `br`, `b`, `i`, `u`, `s`, `spoiler`, `mention ref`, `hashtag`, `a ref`, `code ref`, `pre ref lang`, `quote`, `h level`, `ul`, `ol start`, `li`, `hr`, `table`/`tr`/`th`/`td`, `span fmt` (source-only styling) and `opaque ref`. Every element has a stable id. Protected payloads (code, URLs, mention identities, unknown objects) stay in a local registry and are restored from it, never from the model. Validation compares ids, types, nesting and protected payloads, not just tag counts. Each task keeps its own result shape: Review returns mapped edits, Translate and Rewrite return changed blocks, Summary and Explain a new document.

## Tested profiles

### M! Forum (remix.maniac-forum.de)

Tiptap/ProseMirror editor. Tested 2026-11-14 by pasting prepared clipboard HTML into the post editor.

| Element | Pastes as | Copy from the editor gives |
|---|---|---|
| bold, italic, underline, strike | `strong`, `em`, `u`, `s` ✅ | same |
| quote (several paragraphs) | `blockquote` ✅ | same |
| quoting a previous post ("Zitieren") | selected parts of the post become a regular `blockquote`, no author or post link | same |
| nested quotes | `blockquote` inside `blockquote`, formatting inside kept | same; plain text loses every quote level (no `>`, no indentation) |
| spoiler | only `<spoiler>` ✅; `span data-spoiler`, `data-type="spoiler"` and class-only spans arrive as plain text | `<span class="spoiler ..." data-spoiler="true">` |
| mention | `<mention-component data-id data-label data-user>` ✅ becomes a real mention | same; plain text `@name` |
| bullet and nested list, numbered list with start | `ul`, `ol start` ✅ | same |
| code block | `<pre><code>` ✅ (indentation and `<` kept, highlighted) | `pre.code-block` |
| inline code | not supported, arrives as text | none |
| link | always shown as the URL without `https://`; labels are dropped (`node-mlink`) | `a` inside the link widget; long URLs are shortened with "..." in the visible text (`www.youtube.com/watch?v=DOhAx...`), the full URL is only in `href`. Plain text carries just the shortened text, so links must be read from the HTML and protected |
| image link (png on imgur and others) | a normal link, no embedded image in the editor | same as link |
| links in the read view (outside the editor) | — | the forum renders YouTube links as an embedded player and image links as the image with a caption "Quelle: <shortened link>"; a copy from the read view has no `a` elements: YouTube is an `iframe` (`youtube.com/embed/<id>`, video title in `title`), images are `img` with the full `src`, `alt` "Image: <domain>" and a caption "Quelle:" with the shortened link. Plain text drops the video completely and turns each image into the lines "Image: ...", "Quelle:" and the shortened link. A reader for this site builds the text for the model from the HTML: `[Video: <title> - <watch URL>]`, `[Image: <src>]`, captions removed |
| heading, horizontal rule, line break | `h2`, `hr`, `br` ✅ | same |
| umlauts, €, emoji with skin tone | ✅ | same |
| poll | embedded object `div data-poll-id`, no text | lost in plain text |

Export profile: Formatted HTML with `<spoiler>` for spoilers, `mention-component` only with a known ID, links as "Label: URL", inline code as plain text. Two kinds of copied HTML occur: clean editor HTML with `data-pm-slice`, and the browser's copy of the rendered editor with inline styles, widgets and a zero-width space before links. The clipboard carries `SourceURL`, which identifies the site.

## Sources

[CommonMark](https://spec.commonmark.org/0.31.2/), [GFM](https://github.github.io/gfm/), [Discord formatting](https://support.discord.com/hc/en-us/articles/210298617-Markdown-Text-101-Chat-Formatting-Bold-Italic-Underline), [Discord spoilers](https://support.discord.com/hc/en-us/articles/360022320632-Spoiler-Tags), [Discord limits](https://support.discord.com/hc/en-us/articles/360034632292-Sending-Messages), [Reddit formatting](https://support.reddithelp.com/hc/en-us/articles/360043033952-Formatting-Guide), [phpBB BBCode](https://www.phpbb.com/community/help/bbcode), [YouTube](https://support.google.com/youtube/answer/6000964?hl=en), [WhatsApp](https://faq.whatsapp.com/539178204879377/), [Slack composer](https://slack.com/help/articles/360039953113-Format-your-messages-in-Slack-with-markup), [Teams](https://support.microsoft.com/en-us/teams/chat/use-markdown-formatting-in-microsoft-teams), [Telegram entities](https://core.telegram.org/bots/api#formatting-options), [Google Docs Markdown](https://support.google.com/docs/answer/12014036?hl=en), [X counting](https://docs.x.com/fundamentals/counting-characters), [CF_HTML](https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format), [Clipboard formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats).
