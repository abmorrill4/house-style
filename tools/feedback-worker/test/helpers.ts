import { createExecutionContext, waitOnExecutionContext } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { vi } from "vitest";
import worker from "../src/index";

export interface FakeIssue {
	number: number;
	title: string;
	body: string;
	labels: string[];
	state: "open" | "closed";
	comments: string[];
}

export interface Seen {
	method: string;
	url: string;
	headers: Headers;
}

/** In-memory GitHub issues API, installed as the global fetch. */
export class FakeGitHub {
	issues = new Map<number, FakeIssue>();
	requests: Seen[] = [];
	/** Queue of canned failures returned (in order) before normal handling resumes. */
	failures: Response[] = [];
	private next = 1;

	install(): void {
		vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
			const req = new Request(input, init);
			this.requests.push({ method: req.method, url: req.url, headers: req.headers });
			const fail = this.failures.shift();
			if (fail) return fail;
			return this.handle(req);
		});
	}

	private async handle(req: Request): Promise<Response> {
		const url = new URL(req.url);
		if (url.origin !== "https://api.github.com") return new Response("unexpected host", { status: 500 });
		const m = /^\/repos\/abmorrill4\/house-style-reports\/issues(?:\/(\d+))?(\/comments)?$/.exec(url.pathname);
		if (!m) return new Response("not found", { status: 404 });
		const num = m[1] ? Number(m[1]) : undefined;

		if (req.method === "POST" && num === undefined) {
			const b = (await req.json()) as { title: string; body: string; labels: string[] };
			const issue: FakeIssue = { number: this.next++, title: b.title, body: b.body, labels: b.labels, state: "open", comments: [] };
			this.issues.set(issue.number, issue);
			return Response.json(this.view(issue), { status: 201 });
		}
		const issue = num !== undefined ? this.issues.get(num) : undefined;
		if (!issue) return new Response("not found", { status: 404 });
		if (req.method === "GET") return Response.json(this.view(issue));
		if (req.method === "PATCH") {
			const b = (await req.json()) as { title?: string; body?: string };
			if (b.title) issue.title = b.title;
			if (b.body) issue.body = b.body;
			return Response.json(this.view(issue));
		}
		if (req.method === "POST" && m[2]) {
			const b = (await req.json()) as { body: string };
			issue.comments.push(b.body);
			return Response.json({ id: issue.comments.length }, { status: 201 });
		}
		return new Response("bad", { status: 400 });
	}

	private view(issue: FakeIssue) {
		return { number: issue.number, state: issue.state, html_url: `https://github.com/x/y/issues/${issue.number}` };
	}
}

export const CLIENT_A = "0f8fad5b-d9cb-469f-a165-70867728950e";
export const CLIENT_B = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

let ipCounter = 0;
/** A fresh IP per call unless one is given, so per-IP limits don't leak between tests. */
export function freshIp(): string {
	ipCounter++;
	return `198.51.${Math.floor(ipCounter / 250)}.${ipCounter % 250}`;
}

export interface PostOptions {
	ip?: string;
	headers?: Record<string, string>;
	raw?: BodyInit;
}

export async function call(path: string, method: string, body?: unknown, opts: PostOptions = {}): Promise<Response> {
	const headers: Record<string, string> = {
		"content-type": "application/json",
		"x-house-style": "1",
		"cf-connecting-ip": opts.ip ?? freshIp(),
		...opts.headers,
	};
	const init: RequestInit<IncomingRequestCfProperties> = { method, headers };
	if (opts.raw !== undefined) init.body = opts.raw;
	else if (body !== undefined) init.body = JSON.stringify(body);
	const ctx = createExecutionContext();
	const res = await worker.fetch(new Request<unknown, IncomingRequestCfProperties>(`https://feedback.example.workers.dev${path}`, init), env, ctx);
	await waitOnExecutionContext(ctx);
	return res;
}

export const post = (path: string, body?: unknown, opts?: PostOptions) => call(path, "POST", body, opts);

export async function clearDb(): Promise<void> {
	await env.DB.batch(
		["rate_limits", "error_signatures", "signature_clients", "reports", "false_positives", "fp_rule_issues"].map((t) =>
			env.DB.prepare(`DELETE FROM ${t}`),
		),
	);
}

export const STACK = [
	"   at FlareAI.Flare.UI.FindingsPanel.Rebuild(IList`1 findings) in C:\\src\\FlareAI.Flare\\UI\\FindingsPanel.cs:line 212",
	"   at FlareAI.Flare.UI.FindingsPanel.<OnLintDone>b__14_0() in C:\\src\\FlareAI.Flare\\UI\\FindingsPanel.cs:line 180",
	"   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(Delegate callback, Object args, Int32 numArgs)",
	"   at FlareAI.Flare.Plugin.RunLint(Document doc) in C:\\src\\FlareAI.Flare\\Plugin.cs:line 90",
	"   at FlareAI.Core.Style.StyleEngine.Lint(String[] blocks) in C:\\src\\FlareAI.Core\\Style\\StyleEngine.cs:line 40",
].join("\r\n");

export function errorReport(over: Record<string, unknown> = {}): Record<string, unknown> {
	return {
		kind: "error",
		client_id: CLIENT_A,
		plugin_version: "0.2.0",
		flare_version: "22.1.8936",
		os: "Microsoft Windows NT 10.0.26200.0",
		automatic: false,
		what_happened: null,
		expected: null,
		user_note: "I clicked Check Topic.",
		exception: { type: "System.NullReferenceException", message: "Object reference not set to an instance of an object.", stack: STACK },
		diag_log_tail: "2026-10-22 10:00:00 lint start\n2026-10-22 10:00:01 lint failed\n",
		settings: { Model: "claude-sonnet-4-5", GuideProject: "C:\\Guides\\Company.flprj", AutoCheck: true, MaxFindings: 200 },
		...over,
	};
}

export function problemReport(over: Record<string, unknown> = {}): Record<string, unknown> {
	return {
		kind: "problem",
		client_id: CLIENT_A,
		plugin_version: "0.2.0",
		flare_version: "22.1.8936",
		os: "Microsoft Windows NT 10.0.26200.0",
		what_happened: "The Fix button did nothing\nI tried twice.",
		expected: "The sentence to change.",
		diag_log_tail: "log line\n",
		...over,
	};
}

/** Waits until at least `margin` seconds are left in the current `windowSeconds` window, so a test's requests can't straddle the rollover. */
export async function stayInWindow(windowSeconds: number, margin = 10): Promise<void> {
	const left = windowSeconds - (Math.floor(Date.now() / 1000) % windowSeconds);
	if (left < margin) await new Promise((r) => setTimeout(r, (left + 1) * 1000));
}
