# House Style feedback Worker

A Cloudflare Worker that receives reports from the House Style plugin (issue #124) and files them in a private
GitHub repo. The GitHub token lives only here; the plugin has no secrets.

| Endpoint | What it does |
|---|---|
| `POST /v1/report` | `kind: "error"` or `"problem"`. Creates an issue in the private repo. Errors with the same signature (exception type + top 3 House Style frames + version) add a "+1" comment to the open issue instead. |
| `POST /v1/false-positive` | Stores the report in D1. No issue per report. |
| `GET /v1/health` | `{"ok":true}` |
| weekly cron (Mon 08:00 UTC) | One issue per rule with new false positives: counts by reason, sample flagged text, notes. Also prunes old rate-limit rows. |

Every POST needs `Content-Type: application/json` and `X-House-Style: 1`. The header is not a secret (the plugin is
open source); it only filters out stray traffic. Bodies may be gzip-compressed (`Content-Encoding: gzip`) and are
limited to 256 KB after decompression. Responses are JSON: `{"ok":true,"reference":"#12"}` or
`{"ok":false,"error":"<plain message>"}`. A 502 means GitHub couldn't be reached or is rate limiting: the plugin
should save the report as a zip instead. A 429 carries `Retry-After`.

Limits: 20 reports an hour per IP and 5 a minute per install; 60 false positives an hour per IP and 30 an hour per
install.

## One-time setup (owner)

1. Create the private repo for reports:
   ```
   gh repo create abmorrill4/house-style-reports --private
   ```
2. Create a fine-grained token at GitHub > Settings > Developer settings > Fine-grained tokens:
   **Repository access:** only `abmorrill4/house-style-reports`; **Permissions:** Issues: Read and write
   (Metadata: Read is added automatically). Pick an expiry and put a reminder in your calendar to rotate it.
3. From this folder:
   ```
   npm ci
   npx wrangler login
   npx wrangler d1 create house-style-feedback
   ```
   Copy the `database_id` it prints into `wrangler.jsonc` (replace the zeros) and commit that change.
4. Create the tables, store the token, deploy:
   ```
   npx wrangler d1 migrations apply house-style-feedback --remote
   npx wrangler secret put GITHUB_TOKEN
   npx wrangler secret put HASH_SECRET
   npx wrangler deploy
   ```
   `HASH_SECRET` is any long random string. With it set, IP addresses and install ids are stored as HMACs instead of
   plain SHA-256 hashes. Without it the Worker still runs, with the weaker hash. Changing it later starts new hashes,
   so installs count as new once. The weekly job deletes reports and summarised false positives after 90 days.
   To stop taking reports quickly, set `FEEDBACK_DISABLED` to `"1"` (Worker settings or `wrangler.jsonc`).
   If you used a different repo name, change `GITHUB_REPO` in `wrangler.jsonc` first.

`wrangler deploy` prints the URL, normally `https://house-style-feedback.<your-subdomain>.workers.dev`. Configure
that base URL in the plugin (the plugin appends `/v1/report` and `/v1/false-positive`).

## Try it

```
URL=https://house-style-feedback.<your-subdomain>.workers.dev

curl $URL/v1/health

curl -X POST $URL/v1/report -H "Content-Type: application/json" -H "X-House-Style: 1" -d '{
  "kind": "problem",
  "client_id": "0f8fad5b-d9cb-469f-a165-70867728950e",
  "plugin_version": "0.2.0",
  "what_happened": "Test report from curl, please close."
}'

curl -X POST $URL/v1/false-positive -H "Content-Type: application/json" -H "X-House-Style: 1" -d '{
  "client_id": "0f8fad5b-d9cb-469f-a165-70867728950e",
  "plugin_version": "0.2.0",
  "rule_id": "google:quotation-marks/2",
  "rule_name": "Put periods inside quotation marks",
  "reason": "text_correct",
  "flagged_text": "Care and feeding."
}'
```

To run the weekly summary now, use the dashboard (Workers > house-style-feedback > Settings > Triggers), or locally
with `npx wrangler dev --test-scheduled` and `curl "http://localhost:8787/__scheduled?cron=0+8+*+*+1"`.

## Development

```
npm ci
npm test            # vitest in the Workers runtime; GitHub is mocked, D1 is local
npm run typecheck   # regenerates worker-configuration.d.ts from wrangler.jsonc, then tsc
npx wrangler d1 migrations apply house-style-feedback --local && npx wrangler dev   # local server (needs a .dev.vars with GITHUB_TOKEN to file issues)
```

Schema changes go in a new file under `migrations/` (never edit an applied one), then
`npx wrangler d1 migrations apply house-style-feedback --remote`.

## What is stored

- **GitHub issues (private repo):** report content after server-side redaction (anything that looks like an API key,
  bearer token, GitHub/Slack/AWS key or JWT, `password=`-style pairs, and every setting whose name contains key,
  token, secret, password, auth, credential, cookie or session). The install is shown as the first 8 characters of a
  hash of its client id.
- **D1:** hashed client ids, report references (no report text), error signatures, false-positive reports (the
  flagged words, the optional note and the opt-in context), and hourly rate-limit counters keyed by hashed IP or client
  id, which are deleted once they expire. Raw IPs are never stored.
- **Logs:** event names, status codes, signatures and issue numbers only. No IPs, client ids, report text or tokens.
  That is what this Worker's own code logs. Cloudflare keeps its own request and invocation logs for the Worker, and
  those can include the client IP address. Turn them off or shorten their retention in the Cloudflare dashboard if you
  don't want that.
