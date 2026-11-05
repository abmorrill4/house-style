# tools/feedback-worker: public Cloudflare Worker (in-app reports become issues)

- **It's a public endpoint.** Anyone on the internet can POST to it. The `X-House-Style` header is not a secret.
  Treat every field as attacker-controlled.
  - Keep D1 queries as `prepare().bind()`.
  - Keep the body caps, strict validation and redaction.
  - Run text that goes into GitHub issues through `noMentions` and the fencing helpers.
- **Open hardening:**
  - #148: false-positive summary job
- **Test:** `npm ci; npm run typecheck; npm test` (vitest with the Workers pool). CI runs this as the
  `feedback-worker` check.
- **Deploying is manual and done by the owner** (`wrangler deploy`, see README). Never deploy, or call the live
  Worker, from a session.
- **Secrets are owner-set Worker secrets:** `GITHUB_TOKEN`, scoped to issues on the private `house-style-reports`
  repo, and `.dev.vars` for local work, which is git-ignored. Never commit or print either.
- **The client side** is `src/FlareAI.Core/Feedback` (`ReportBuilder`, `FeedbackClient`). Redaction happens on both
  sides.
