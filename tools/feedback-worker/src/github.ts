import { HttpError, log } from "./http";

// Minimal GitHub REST client for issues. The token never leaves this module: it is not logged, not echoed in errors
// and not returned to the plugin.

const API = "https://api.github.com";
const USER_AGENT = "house-style-feedback-worker (+https://github.com/abmorrill4/house-style)";
/** Longest Retry-After we wait for inside a request before giving up and telling the plugin to fall back. */
const MAX_INLINE_WAIT_S = 3;
const TIMEOUT_MS = 10_000;

/** GitHub's hard limit on issue and comment bodies. */
export const GITHUB_BODY_LIMIT = 65_536;

export interface Issue {
	number: number;
	state: "open" | "closed";
	html_url: string;
}

export class GitHub {
	constructor(
		private readonly token: string,
		private readonly repo: string,
	) {
		if (!token) throw new HttpError(503, "Reporting isn't set up on the server yet.");
		if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)) throw new HttpError(503, "Reporting isn't set up on the server yet.");
	}

	createIssue(title: string, body: string, labels: string[]): Promise<Issue> {
		return this.call<Issue>("POST", `/repos/${this.repo}/issues`, { title, body, labels });
	}

	/** Returns null when the issue no longer exists (deleted or transferred). */
	async getIssue(number: number): Promise<Issue | null> {
		try {
			return await this.call<Issue>("GET", `/repos/${this.repo}/issues/${number}`);
		} catch (e) {
			if (e instanceof GitHubNotFound) return null;
			throw e;
		}
	}

	/** Returns null when the issue no longer exists. */
	async updateIssue(number: number, fields: { title?: string; body?: string }): Promise<Issue | null> {
		try {
			return await this.call<Issue>("PATCH", `/repos/${this.repo}/issues/${number}`, fields);
		} catch (e) {
			if (e instanceof GitHubNotFound) return null;
			throw e;
		}
	}

	async addComment(number: number, body: string): Promise<void> {
		await this.call("POST", `/repos/${this.repo}/issues/${number}/comments`, { body });
	}

	private async call<T>(method: string, path: string, body?: unknown, attempt = 0): Promise<T> {
		let res: Response;
		try {
			res = await fetch(API + path, {
				method,
				headers: {
					authorization: `Bearer ${this.token}`,
					accept: "application/vnd.github+json",
					"x-github-api-version": "2022-11-28",
					"user-agent": USER_AGENT,
					...(body === undefined ? {} : { "content-type": "application/json" }),
				},
				body: body === undefined ? undefined : JSON.stringify(body),
				signal: AbortSignal.timeout(TIMEOUT_MS),
			});
		} catch {
			log("github_unreachable", { method });
			throw new HttpError(502, "The report server couldn't reach GitHub. Save the report and try again later.");
		}

		if (res.ok) return (await res.json()) as T;
		await res.body?.cancel();

		if (res.status === 404 && method !== "POST") throw new GitHubNotFound();

		const limited = res.status === 429 || (res.status === 403 && (res.headers.has("retry-after") || res.headers.get("x-ratelimit-remaining") === "0"));
		if (limited) {
			const wait = retryAfterSeconds(res.headers);
			log("github_rate_limited", { status: res.status, wait });
			if (attempt === 0 && wait <= MAX_INLINE_WAIT_S) {
				await new Promise((r) => setTimeout(r, wait * 1000));
				return this.call<T>(method, path, body, attempt + 1);
			}
			throw new HttpError(502, "GitHub is busy right now, so the report wasn't filed. Save the report and try again later.", Math.max(wait, 60));
		}

		// 401/403 (bad or expired token), 404 on create (wrong repo), 422, 5xx. Log the status only.
		log("github_error", { status: res.status, method });
		throw new HttpError(502, "The report couldn't be filed on the server. Save the report and try again later.");
	}
}

class GitHubNotFound extends Error {}

function retryAfterSeconds(h: Headers): number {
	const ra = Number(h.get("retry-after"));
	if (Number.isFinite(ra) && ra > 0) return Math.ceil(ra);
	const reset = Number(h.get("x-ratelimit-reset"));
	if (Number.isFinite(reset) && reset > 0) return Math.max(1, Math.ceil(reset - Date.now() / 1000));
	return 60;
}

/** Stops `@user` and `@org/team` text from pinging people. */
export function noMentions(text: string): string {
	return text.replace(/@(?=[A-Za-z0-9])/g, "@​");
}

/**
 * Stops free text from rendering links, images or HTML in the issue. `[` and `<` start every Markdown link,
 * image, reference definition and autolink, so escaping them (and the backslash that could undo the escape) is enough.
 */
export function defang(text: string): string {
	return text.replace(/[\\[<]/g, (c) => `\\${c}`);
}

/** A Markdown code fence that the content can't close early. */
export function fenced(content: string, lang = ""): string {
	const longest = Math.max(0, ...[...content.matchAll(/`+/g)].map((m) => m[0].length));
	const fence = "`".repeat(Math.max(3, longest + 1));
	return `${fence}${lang}\n${content}\n${fence}`;
}
