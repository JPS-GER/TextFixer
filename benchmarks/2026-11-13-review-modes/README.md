# Review modes benchmark (2026-11-13)

Model: gpt-6.1-sol. 10 texts (German and English) with 30 planted mistakes. Requests ran one after another, as in the app. Times in seconds.

| Text | Single | Per tab: total | first tab | Spelling | Grammar | Punctuation | Wording | Worth checking | Fixed single | Fixed per tab | Findings single / per tab |
|---|---|---|---|---|---|---|---|---|---|---|---|
| de-email | 9.0 | 26.8 | 7.2 | 7.2 | 5.1 | 6.5 | 4.1 | 4.0 | 5/5 | 5/5 | 0 / 0 |
| de-dass-das | 5.5 | 28.3 | 5.5 | 5.5 | 6.9 | 5.6 | 5.6 | 4.6 | 2/2 | 2/2 | 0 / 0 |
| en-report | 8.5 | 33.7 | 5.3 | 5.3 | 9.0 | 10.5 | 5.6 | 3.3 | 5/5 | 5/5 | 0 / 0 |
| en-email | 9.9 | 30.6 | 5.4 | 5.4 | 6.2 | 9.4 | 5.5 | 4.1 | 5/5 | 5/5 | 0 / 0 |
| de-technik | 13.4 | 42.5 | 6.4 | 6.4 | 4.7 | 6.8 | 8.4 | 16.2 | 4/4 | 4/4 | 1 / 1 |
| en-claims | 11.0 | 31.5 | 4.2 | 4.2 | 3.3 | 8.0 | 4.5 | 11.4 | 0/0 | 0/0 | 4 / 4 |
| de-lang | 9.4 | 42.6 | 7.7 | 7.7 | 10.2 | 9.2 | 8.0 | 7.6 | 3/3 | 3/3 | 0 / 0 |
| en-clean | 4.8 | 19.2 | 4.6 | 4.6 | 3.4 | 3.7 | 3.5 | 4.0 | 0/0 | 0/0 | 0 / 0 |
| de-chat | 8.9 | 29.0 | 4.3 | 4.3 | 7.1 | 6.9 | 5.6 | 5.1 | 2/2 | 2/2 | 0 / 0 |
| en-mixed | 6.7 | 31.9 | 8.7 | 8.7 | 6.6 | 4.5 | 4.5 | 7.6 | 4/4 | 4/4 | 0 / 0 |
| **Total** | **87.2** | **316.0** | **59.3** | | | | | | **30/30** | **30/30** | **5 / 5** |

Per-tab results are merged with the app logic (`focusParts` in `app.js`). The first run exposed a merge bug: the same fix from several tabs ("das" -> "dass") was applied twice ("dasss"); fixed, and the scores above use the fixed merge.

Files: `texts.json` (texts and planted mistakes), `results.json` (raw answers and timings per request), `evaluate.cjs` (scoring: `node evaluate.cjs`, or `node evaluate.cjs <id>` for the merged per-tab text), `Program.cs` and `bench.csproj` (runner; uses the signed-in ChatGPT plan, 6 requests per text).
