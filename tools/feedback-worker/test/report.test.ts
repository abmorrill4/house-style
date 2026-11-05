import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { shortFrame, topOwnFrames } from "../src/report";
import { CLIENT_A, CLIENT_B, call, clearDb, errorReport, FakeGitHub, post, problemReport, STACK } from "./helpers";

let gh: FakeGitHub;
beforeEach(async () => {
	await clearDb();
	gh = new FakeGitHub();
	gh.install();
});
afterEach(() => vi.restoreAllMocks());

describe("routing", () => {
	it("answers health checks", async () => {
		const res = await call("/v1/health", "GET");
		expect(res.status).toBe(200);
		expect(await res.json()).toEqual({ ok: true, service: "house-style-feedback" });
	});

	it("requires the app header", async () => {
		const res = await post("/v1/report", errorReport(), { headers: { "x-house-style": "" } });
		expect(res.status).toBe(403);
		expect(await res.json()).toMatchObject({ ok: false, error: expect.stringContaining("House Style") });
	});

	it("rejects unknown paths and wrong methods", async () => {
		expect((await call("/v1/nope", "POST", {})).status).toBe(404);
		expect((await call("/v1/report", "GET")).status).toBe(405);
	});
});

describe("error reports", () => {
	it("creates an issue with a clean title, labels and body", async () => {
		const res = await post("/v1/report", errorReport({ automatic: true }));
		expect(res.status).toBe(200);
		const json = await res.json<Record<string, unknown>>();
		expect(json).toEqual({ ok: true, reference: "#1", duplicate: false });

		const issue = gh.issues.get(1)!;
		expect(issue.title).toBe("[error] NullReferenceException in FindingsPanel.Rebuild (0.2.0)");
		expect(issue.labels).toEqual(["error", "v0.2.0", "automatic"]);
		expect(issue.body).toContain("| House Style | 0.2.0 |");
		expect(issue.body).toContain("| Flare | 22.1.8936 |");
		expect(issue.body).toContain("> I clicked Check Topic.");
		expect(issue.body).toContain("FindingsPanel.cs:line 212");
		expect(issue.body).toContain("<details>\n<summary>diag.log");
		expect(issue.body).toContain('"Model": "claude-sonnet-4-5"');
		expect(issue.body).not.toContain(CLIENT_A);

		const req = gh.requests[0];
		expect(req.headers.get("authorization")).toBe("Bearer test-token-not-real");
		expect(req.headers.get("user-agent")).toMatch(/house-style-feedback-worker/);
		expect(JSON.stringify(json)).not.toContain("test-token");
	});

	it("comments on the open issue instead of filing a duplicate", async () => {
		await post("/v1/report", errorReport());
		const second = await post("/v1/report", errorReport({ client_id: CLIENT_B, user_note: "Happens on long topics.", automatic: true }));
		expect(await second.json()).toEqual({ ok: true, reference: "#1", duplicate: true });
		const third = await post("/v1/report", errorReport({ user_note: null }));
		expect(await third.json()).toMatchObject({ reference: "#1", duplicate: true });

		expect(gh.issues.size).toBe(1);
		const comments = gh.issues.get(1)!.comments;
		expect(comments[0]).toContain("+1 from another install");
		expect(comments[0]).toContain("Reports so far: 2");
		expect(comments[0]).toContain("> Happens on long topics.");
		expect(comments[1]).toContain("+1 from an install that reported this before");

		const row = await env.DB.prepare("SELECT report_count FROM error_signatures").first<{ report_count: number }>();
		expect(row?.report_count).toBe(3);
	});

	it("ignores line numbers and non-House Style frames in the signature, but not the version", async () => {
		const moved = STACK.replace("line 212", "line 999").replace("ExceptionWrapper.InternalRealCall", "Other.Frame");
		await post("/v1/report", errorReport());
		await post("/v1/report", errorReport({ exception: { type: "System.NullReferenceException", message: "", stack: moved } }));
		expect(gh.issues.size).toBe(1);
		await post("/v1/report", errorReport({ plugin_version: "0.2.1" }));
		expect(gh.issues.size).toBe(2);
	});

	it("files a new issue when the tracked one was closed", async () => {
		await post("/v1/report", errorReport());
		gh.issues.get(1)!.state = "closed";
		const res = await post("/v1/report", errorReport({ client_id: CLIENT_B }));
		expect(await res.json()).toEqual({ ok: true, reference: "#2", duplicate: false });
		await post("/v1/report", errorReport());
		expect(gh.issues.get(2)!.comments).toHaveLength(1);
	});

	it("keeps the body under GitHub's limit and the log in a details block", async () => {
		const log = Array.from({ length: 2000 }, (_, i) => `2026-10-22 10:00:${String(i).padStart(4, "0")} event ${"x".repeat(20)}`).join("\n");
		const bigLog = log.slice(log.length - 65_536);
		const settings = Object.fromEntries(Array.from({ length: 100 }, (_, i) => [`Setting${i}`, "v".repeat(500)]));
		const bigStack = (STACK + "\r\n").repeat(60).slice(0, 32_000);
		const res = await post(
			"/v1/report",
			errorReport({
				diag_log_tail: bigLog,
				settings,
				user_note: "n".repeat(4000),
				exception: { type: "System.InvalidOperationException", message: "m".repeat(2000), stack: bigStack },
			}),
		);
		expect(res.status).toBe(200);
		const body = gh.issues.get(1)!.body;
		expect(body.length).toBeLessThanOrEqual(65_536);
		expect(body).toContain("earlier characters omitted");
		expect(body).toContain("(settings truncated)");
		expect(body.trimEnd().endsWith("</details>")).toBe(true);
		// The tail (most recent lines) is what's kept.
		expect(body).toContain(bigLog.slice(-200));
	});
});

describe("problem reports", () => {
	it("uses the first line of what_happened as the title", async () => {
		const res = await post("/v1/report", problemReport());
		expect(await res.json()).toEqual({ ok: true, reference: "#1", duplicate: false });
		const issue = gh.issues.get(1)!;
		expect(issue.title).toBe("[problem] The Fix button did nothing (0.2.0)");
		expect(issue.labels).toEqual(["problem", "v0.2.0"]);
		expect(issue.body).toContain("### What they expected\n\n> The sentence to change.");
	});

	it("is never deduplicated", async () => {
		await post("/v1/report", problemReport());
		await post("/v1/report", problemReport());
		expect(gh.issues.size).toBe(2);
	});

	it("neutralises @mentions", async () => {
		await post("/v1/report", problemReport({ what_happened: "@octocat please look" }));
		const issue = gh.issues.get(1)!;
		expect(issue.title).not.toMatch(/@octocat/);
		expect(issue.body).not.toMatch(/@octocat/);
	});

	it("neutralises @mentions, links and table breaks in os and flare_version", async () => {
		await post("/v1/report", problemReport({ os: "Windows @octocat | `x` [a](http://evil.example)", flare_version: "22 @org/team <b>" }));
		const body = gh.issues.get(1)!.body;
		expect(body).not.toMatch(/@octocat|@org/);
		expect(body).not.toMatch(/(^|[^\\])\[a\]\(/);
		expect(body).toContain("\\|");
		expect(body).toContain("\\`x\\`");
		expect(body).toContain("\\<b>");
	});

	it("does not render links or images from free text", async () => {
		await post("/v1/report", problemReport({ what_happened: "![x](http://evil.example/a.png) [click](http://evil.example) <http://evil.example>" }));
		const body = gh.issues.get(1)!.body;
		expect(body).not.toMatch(/(^|[^\\])\[/m);
		expect(body).not.toMatch(/(^|[^\\])<http/m);
	});
});

describe("redaction", () => {
	it("removes secrets from settings, log, stack and notes", async () => {
		const res = await post(
			"/v1/report",
			errorReport({
				user_note: "my key is sk-ant-api03-AAAABBBBCCCCDDDD and that's it",
				settings: {
					ApiKey: "whatever",
					AnthropicToken: "x",
					ProxyPassword: "hunter2",
					Endpoint: "https://api.anthropic.com",
					Notes: "Bearer abcdefghijklmnop",
					Copy: "uses ghp_ABCDEFGHIJKLMNOPQRSTUVWX1234",
				},
				diag_log_tail: "request headers: x-api-key: sk-ant-secretsecret123\nAuthorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghij\n",
				exception: { type: "System.Exception", message: "password=hunter2;", stack: STACK },
			}),
		);
		expect(res.status).toBe(200);
		const body = gh.issues.get(1)!.body;
		for (const secret of ["sk-ant-api03", "sk-ant-secret", "hunter2", "abcdefghijklmnop", "ghp_", "eyJhbGci", "whatever", "ApiKey", "AnthropicToken", "ProxyPassword"])
			expect(body).not.toContain(secret);
		expect(body).toContain('"Endpoint": "https://api.anthropic.com"');
		expect(body).toContain("5 settings removed by the server");
		expect(body).toContain("[redacted]");
	});
});

describe("stack helpers", () => {
	it("picks House Style frames and shortens them", () => {
		expect(topOwnFrames(STACK)).toEqual([
			"FlareAI.Flare.UI.FindingsPanel.Rebuild",
			"FlareAI.Flare.UI.FindingsPanel.<OnLintDone>b__14_0",
			"FlareAI.Flare.Plugin.RunLint",
		]);
		expect(shortFrame("FlareAI.Flare.UI.FindingsPanel.<OnLintDone>b__14_0")).toBe("FindingsPanel.OnLintDone");
		expect(shortFrame("FlareAI.Flare.UI.FindingsPanel.<>c.<Rebuild>b__5_0")).toBe("FindingsPanel.Rebuild");
	});
});

describe("GitHub failures", () => {
	it("returns 502 with retry_after when GitHub rate limits", async () => {
		gh.failures.push(new Response("slow down", { status: 429, headers: { "retry-after": "120" } }));
		const res = await post("/v1/report", problemReport());
		expect(res.status).toBe(502);
		expect(res.headers.get("retry-after")).toBe("120");
		const json = await res.json<Record<string, unknown>>();
		expect(json).toMatchObject({ ok: false, retry_after: 120 });
		expect(json.error).toMatch(/Save the report/);
	});

	it("retries once on a short Retry-After", async () => {
		gh.failures.push(new Response("", { status: 403, headers: { "retry-after": "1", "x-ratelimit-remaining": "0" } }));
		const res = await post("/v1/report", problemReport());
		expect(res.status).toBe(200);
		expect(gh.issues.size).toBe(1);
	});

	it("returns a plain 502 on a bad token, without echoing it", async () => {
		gh.failures.push(new Response('{"message":"Bad credentials"}', { status: 401 }));
		const res = await post("/v1/report", problemReport());
		expect(res.status).toBe(502);
		const text = await res.text();
		expect(text).not.toContain("test-token");
		expect(text).not.toContain("Bad credentials");
	});
});
