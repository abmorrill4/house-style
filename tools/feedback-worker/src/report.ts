import { defang, fenced, GITHUB_BODY_LIMIT, GitHub, noMentions } from "./github";
import { identityHash, log, sha256Hex } from "./http";
import { redactSettings, redactText } from "./redact";
import type { ExceptionInfo, Report } from "./validate";

/** Namespaces that are House Style code (the rest of a stack is Flare, WPF or the BCL). */
const OWN_FRAME = /^(FlareAI|HouseStyle)\./;
const TITLE_MAX = 200;
const SETTINGS_MAX = 8000;
/** Room kept free for the truncation notes and the details wrapper. */
const BODY_SLACK = 1000;

export interface ReportResult {
	ok: true;
	reference: string;
	duplicate: boolean;
}

/** Frames of a .NET stack trace, without file paths and line numbers ("Ns.Class.Method(Args)"). */
export function stackFrames(stack: string): string[] {
	const frames: string[] = [];
	for (const line of stack.split(/\r?\n/)) {
		const m = /^\s*at\s+(.+?)(?:\s+in\s+.+:line\s+\d+)?\s*$/.exec(line);
		if (m) frames.push(m[1]);
	}
	return frames;
}

/** The first three House Style frames (or the first three frames, if none are ours), without argument lists. */
export function topOwnFrames(stack: string): string[] {
	const all = stackFrames(stack).map((f) => f.replace(/\(.*$/, ""));
	const own = all.filter((f) => OWN_FRAME.test(f));
	return (own.length > 0 ? own : all).slice(0, 3);
}

/** Dedupe key: exception type + top 3 House Style frames + plugin version. */
export async function errorSignature(ex: ExceptionInfo, version: string): Promise<string> {
	const key = [ex.type, ...topOwnFrames(ex.stack), version].join("|");
	return (await sha256Hex(key)).slice(0, 16);
}

/** "FlareAI.Flare.UI.FindingsPanel.Rebuild" -> "FindingsPanel.Rebuild"; lambdas resolve to their method. */
export function shortFrame(frame: string): string {
	const parts = frame.split(".");
	const lambda = /<([A-Za-z0-9_]+)>/.exec(frame);
	const method = lambda ? lambda[1] : parts[parts.length - 1];
	const typeIdx = parts.findIndex((p) => p.startsWith("<"));
	const type = typeIdx > 0 ? parts[typeIdx - 1] : parts[parts.length - 2];
	return type ? `${type}.${method}` : method;
}

function clip(text: string, max: number): string {
	return text.length <= max ? text : `${text.slice(0, max - 1)}…`;
}

export function reportTitle(r: Report): string {
	if (r.kind === "error" && r.exception) {
		const type = r.exception.type.split(".").pop() ?? r.exception.type;
		const frame = topOwnFrames(r.exception.stack)[0];
		const where = frame ? ` in ${shortFrame(frame)}` : "";
		return noMentions(clip(`[error] ${type}${where} (${r.plugin_version})`, TITLE_MAX));
	}
	const first = (r.what_happened ?? "").split(/\r?\n/).find((l) => l.trim() !== "")?.trim() ?? "Problem report";
	return noMentions(`[problem] ${clip(redactText(first), 80)} (${r.plugin_version})`);
}

export function reportLabels(r: Report): string[] {
	// Only a plain x.y.z becomes a label, so junk version strings can't create labels in the reports repo.
	const version = /^\d{1,3}\.\d{1,3}\.\d{1,5}/.exec(r.plugin_version);
	const labels = version ? [r.kind, `v${version[0]}`] : [r.kind];
	if (r.automatic) labels.push("automatic");
	return labels;
}

/** One value for a Markdown table cell. Untrusted text can't ping anyone, link, open HTML or break the table. */
export function tableCell(value: string): string {
	return defang(noMentions(value))
		.replace(/[`|]/g, (c) => `\\${c}`)
		.replace(/\s+/g, " ")
		.trim();
}

function quote(text: string): string {
	return defang(noMentions(redactText(text)))
		.split(/\r?\n/)
		.map((l) => `> ${l}`)
		.join("\n");
}

/** Renders the issue body, keeping it under GitHub's 65,536-character limit by trimming the log from the top. */
export function renderReportBody(r: Report, signature: string | undefined, clientHash: string, reference?: string): string {
	const parts: string[] = [];
	const row = (k: string, v: string | undefined, trusted = false) => (v ? `| ${k} | ${trusted ? v : tableCell(v)} |` : "");
	parts.push(
		[
			"| | |",
			"|---|---|",
			row("Kind", r.kind + (r.automatic ? " (sent automatically)" : "")),
			row("House Style", r.plugin_version),
			row("Flare", r.flare_version),
			row("OS", r.os),
			row("Reference", reference ? `\`${reference}\`` : undefined, true),
			row("Install", clientHash.slice(0, 8)),
			row("Signature", signature ? `\`${signature}\`` : undefined, true),
		]
			.filter(Boolean)
			.join("\n"),
	);

	if (r.what_happened) parts.push(`### What happened\n\n${quote(r.what_happened)}`);
	if (r.expected) parts.push(`### What they expected\n\n${quote(r.expected)}`);
	if (r.user_note) parts.push(`### Note from the writer\n\n${quote(r.user_note)}`);

	if (r.exception) {
		const ex = r.exception;
		let section = `### Exception\n\n\`${ex.type.replace(/`/g, "")}\``;
		if (ex.message) section += `\n\n${fenced(redactText(ex.message), "text")}`;
		if (ex.stack) section += `\n\n${fenced(redactText(ex.stack), "text")}`;
		parts.push(section);
	}

	if (r.settings) {
		const { kept, dropped } = redactSettings(r.settings);
		let text = JSON.stringify(kept, null, 2);
		if (text.length > SETTINGS_MAX) text = `${text.slice(0, SETTINGS_MAX)}\n… (settings truncated)`;
		let section = `### Settings\n\n${fenced(text, "json")}`;
		if (dropped > 0) section += `\n\n_${dropped} setting${dropped === 1 ? "" : "s"} removed by the server because ${dropped === 1 ? "it looks" : "they look"} like a secret._`;
		parts.push(section);
	}

	let body = parts.join("\n\n");

	if (r.diag_log_tail) {
		let logText = redactText(r.diag_log_tail);
		const budget = GITHUB_BODY_LIMIT - BODY_SLACK - body.length;
		let note = "";
		if (logText.length > budget) {
			const cut = Math.max(0, budget);
			let kept = cut > 0 ? logText.slice(logText.length - cut) : "";
			const nl = kept.indexOf("\n");
			if (nl >= 0 && nl < 200) kept = kept.slice(nl + 1); // start on a whole line
			note = `_${(logText.length - kept.length).toLocaleString("en-US")} earlier characters omitted to fit GitHub's limit._\n\n`;
			logText = kept;
		}
		const sizeKb = Math.max(1, Math.round(logText.length / 1024));
		body += `\n\n<details>\n<summary>diag.log (last ${sizeKb} KB)</summary>\n\n${note}${fenced(logText, "text")}\n\n</details>`;
	}

	if (body.length > GITHUB_BODY_LIMIT) body = `${body.slice(0, GITHUB_BODY_LIMIT - 100)}\n\n… (report truncated)`;
	return body;
}

function commentBody(r: Report, clientHash: string, newInstall: boolean, count: number, reference: string): string {
	const lines = [
		`**+1 from ${newInstall ? "another install" : "an install that reported this before"}** (House Style ${r.plugin_version}` +
			`${r.flare_version ? `, Flare ${tableCell(r.flare_version)}` : ""}, install ${clientHash.slice(0, 8)}` +
			`${r.automatic ? ", sent automatically" : ""}). Reference \`${reference}\`. Reports so far: ${count}.`,
	];
	if (r.user_note) lines.push(`\n${quote(r.user_note)}`);
	if (r.exception?.message) lines.push(`\n${fenced(redactText(r.exception.message), "text")}`);
	return lines.join("\n");
}

export async function handleReport(r: Report, env: Env, now = Date.now()): Promise<ReportResult> {
	const gh = new GitHub(env.GITHUB_TOKEN, env.GITHUB_REPO);
	const db = env.DB;
	const clientHash = (await identityHash(env, r.client_id)).slice(0, 16);
	const nowS = Math.floor(now / 1000);
	const id = crypto.randomUUID();
	// What the writer sees and quotes. Not the issue number, which would tell any sender how many reports exist.
	const reference = `r-${id.slice(0, 8)}`;

	let signature: string | undefined;
	if (r.kind === "error" && r.exception) {
		signature = await errorSignature(r.exception, r.plugin_version);
		const existing = await db
			.prepare("SELECT issue_number, report_count FROM error_signatures WHERE signature = ?1")
			.bind(signature)
			.first<{ issue_number: number; report_count: number }>();

		if (existing) {
			const issue = await gh.getIssue(existing.issue_number);
			if (issue && issue.state === "open") {
				const seen = await db
					.prepare("SELECT 1 AS hit FROM signature_clients WHERE signature = ?1 AND client_hash = ?2")
					.bind(signature, clientHash)
					.first();
				const count = existing.report_count + 1;
				await gh.addComment(issue.number, commentBody(r, clientHash, seen === null, count, reference));
				await db.batch([
					db
						.prepare("INSERT OR IGNORE INTO signature_clients (signature, client_hash) VALUES (?1, ?2)")
						.bind(signature, clientHash),
					db
						.prepare("UPDATE error_signatures SET report_count = report_count + 1, last_seen = ?2 WHERE signature = ?1")
						.bind(signature, nowS),
					db
						.prepare(
							"INSERT INTO reports (id, kind, client_hash, plugin_version, signature, issue_number, duplicate, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 1, ?7)",
						)
						.bind(id, r.kind, clientHash, r.plugin_version, signature, issue.number, nowS),
				]);
				log("report_duplicate", { kind: r.kind, signature, issue: issue.number });
				return { ok: true, reference, duplicate: true };
			}
			// The tracking issue was closed or removed: this is a regression or a new occurrence, so file it again.
		}
	}

	const title = reportTitle(r);
	const issue = await gh.createIssue(title, renderReportBody(r, signature, clientHash, reference), reportLabels(r));
	const statements = [
		db
			.prepare(
				"INSERT INTO reports (id, kind, client_hash, plugin_version, signature, issue_number, duplicate, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 0, ?7)",
			)
			.bind(id, r.kind, clientHash, r.plugin_version, signature ?? null, issue.number, nowS),
	];
	if (signature) {
		statements.push(
			db
				.prepare(
					`INSERT INTO error_signatures (signature, issue_number, title, report_count, first_seen, last_seen)
					 VALUES (?1, ?2, ?3, 1, ?4, ?4)
					 ON CONFLICT (signature) DO UPDATE SET issue_number = ?2, title = ?3, report_count = 1, first_seen = ?4, last_seen = ?4`,
				)
				.bind(signature, issue.number, title, nowS),
			db.prepare("DELETE FROM signature_clients WHERE signature = ?1").bind(signature),
			db.prepare("INSERT INTO signature_clients (signature, client_hash) VALUES (?1, ?2)").bind(signature, clientHash),
		);
	}
	await db.batch(statements);
	log("report_created", { kind: r.kind, signature, issue: issue.number });
	return { ok: true, reference, duplicate: false };
}
