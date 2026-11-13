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

## Parallel per-tab run

Second run of the same texts with the five tab requests sent at the same time (`parallel/`). Times in seconds; each tab time is measured from the start of the run, so the slowest tab equals the total.

| Text | Single | Parallel: total | first tab | Spelling | Grammar | Punctuation | Wording | Worth checking |
|---|---|---|---|---|---|---|---|---|
| de-email | 7.7 | 7.7 | 4.9 | 7.7 | 5.5 | 7.2 | 4.9 | 4.9 |
| de-dass-das | 5.9 | 6.0 | 3.6 | 5.3 | 6.0 | 5.4 | 5.1 | 3.6 |
| en-report | 7.7 | 13.7 | 3.9 | 5.0 | 13.7 | 7.4 | 4.8 | 3.9 |
| en-email | 7.4 | 8.5 | 4.5 | 5.2 | 7.8 | 8.5 | 4.5 | 4.6 |
| de-technik | 16.7 | 11.7 | 4.5 | 6.1 | 4.5 | 6.5 | 6.1 | 11.7 |
| en-claims | 10.6 | 9.5 | 3.6 | 3.9 | 3.6 | 6.7 | 4.4 | 9.5 |
| de-lang | 8.9 | 9.1 | 6.6 | 8.5 | 9.1 | 8.7 | 9.1 | 6.6 |
| en-clean | 3.6 | 4.8 | 3.3 | 3.3 | 4.8 | 4.2 | 3.9 | 3.7 |
| de-chat | 5.5 | 11.4 | 3.6 | 7.2 | 11.4 | 6.5 | 7.1 | 3.6 |
| en-mixed | 6.4 | 12.4 | 4.2 | 12.4 | 6.2 | 4.2 | 6.5 | 4.5 |
| **Average** | **8.0** | **9.5** | **4.3** | | | | | |

All 50 requests succeeded; no rate limiting. Fixed: single 30/30, parallel 29/30 (en-email used "tomorrow. I just" instead of the expected semicolon, also a valid fix). Findings 4 / 4.

Result: sequential per-tab checks (31.6 s average) are not worth the wait; in parallel the total is close to a single request and the first tab is ready after about 4 s. The app sends per-tab requests in parallel since this run.
