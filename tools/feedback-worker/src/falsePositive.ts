import { defang, fenced, GitHub, noMentions } from "./github";
import { log, sha256Hex } from "./http";
import { redactText } from "./redact";
import { FP_REASONS, type FalsePositive, type FpReason } from "./validate";

/** Most rules summarised per cron run, to stay well inside the per-invocation subrequest limit (3 calls per rule). */
export const MAX_RULES_PER_RUN = 15;
const MAX_SAMPLES = 8;

export const REASON_LABELS: Record<FpReason, string> = {
	text_correct: "The text is correct as written",
	change_wrong: "The suggested change is wrong",
	rule_doesnt_fit: "This rule doesn't fit our docs",
};

export async function storeFalsePositive(fp: FalsePositive, env: Env, now = Date.now()): Promise<{ ok: true; reference: string }> {
	const id = crypto.randomUUID();
	const clientHash = (await sha256Hex(fp.client_id)).slice(0, 16);
	await env.DB.prepare(
		`INSERT INTO false_positives (id, created_at, client_hash, plugin_version, rule_id, rule_name, reason, flagged_text, context, note)
		 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)`,
	)
		.bind(
			id,
			Math.floor(now / 1000),
			clientHash,
			fp.plugin_version,
			fp.rule_id,
			fp.rule_name,
			fp.reason,
			redactText(fp.flagged_text),
			fp.context ? redactText(fp.context) : null,
			fp.note ? redactText(fp.note) : null,
		)
		.run();
	log("false_positive_stored", { rule: fp.rule_id, reason: fp.reason });
	return { ok: true, reference: `fp-${id.slice(0, 8)}` };
}

interface FpRow {
	id: string;
	created_at: number;
	client_hash: string;
	plugin_version: string;
	rule_id: string;
	rule_name: string;
	reason: FpReason;
	flagged_text: string;
	context: string | null;
	note: string | null;
}

function oneLine(text: string, max = 200): string {
	const flat = text.replace(/\s+/g, " ").trim();
	return flat.length <= max ? flat : `${flat.slice(0, max - 1)}…`;
}

function code(text: string): string {
	// Inline code that the text can't break out of.
	const flat = oneLine(text);
	const longest = Math.max(0, ...[...flat.matchAll(/`+/g)].map((m) => m[0].length));
	const tick = "`".repeat(longest + 1);
	return `${tick} ${flat} ${tick}`;
}

function countTable(counts: Map<FpReason, number>): string {
	const rows = FP_REASONS.map((r) => `| ${REASON_LABELS[r]} | ${counts.get(r) ?? 0} |`);
	const total = [...counts.values()].reduce((a, b) => a + b, 0);
	return ["| Reason | Reports |", "|---|---:|", ...rows, `| **Total** | **${total}** |`].join("\n");
}

function count(rows: { reason: FpReason; n: number }[]): Map<FpReason, number> {
	return new Map(rows.map((r) => [r.reason, r.n]));
}

function weekSection(rows: FpRow[], fromS: number, toS: number): string {
	const day = (s: number) => new Date(s * 1000).toISOString().slice(0, 10);
	const counts = new Map<FpReason, number>();
	for (const r of rows) counts.set(r.reason, (counts.get(r.reason) ?? 0) + 1);
	const installs = new Set(rows.map((r) => r.client_hash)).size;
	const versions = [...new Set(rows.map((r) => r.plugin_version))].sort().join(", ");

	const samples = [...new Map(rows.map((r) => [r.flagged_text, r])).values()].slice(0, MAX_SAMPLES);
	const notes = rows.filter((r) => r.note).slice(0, MAX_SAMPLES);
	const contexts = rows.filter((r) => r.context).slice(0, 3);

	const out = [
		`### ${day(fromS)} to ${day(toS)}: ${rows.length} report${rows.length === 1 ? "" : "s"} from ${installs} install${installs === 1 ? "" : "s"}`,
		"",
		countTable(counts),
		"",
		`Versions: ${versions}`,
		"",
		"**Flagged text**",
		"",
		...samples.map((r) => `- ${code(r.flagged_text)} (${REASON_LABELS[r.reason].toLowerCase()})`),
	];
	if (notes.length > 0) out.push("", "**Notes**", "", ...notes.map((r) => `- ${defang(noMentions(oneLine(r.note ?? "", 500)))}`));
	if (contexts.length > 0) out.push("", "**Context (shared by the writer)**", "", ...contexts.map((r) => fenced(r.context ?? "", "text")));
	return out.join("\n");
}

function issueBody(ruleId: string, ruleName: string, totals: Map<FpReason, number>, sinceS: number): string {
	return [
		`False-positive reports for rule \`${ruleId.replace(/`/g, "")}\` (${defang(noMentions(oneLine(ruleName)))}), counted since ${new Date(sinceS * 1000).toISOString().slice(0, 10)}.`,
		"",
		countTable(totals),
		"",
		"Each weekly comment below lists that week's flagged text and notes. This issue is updated by the feedback Worker; close it when the rule is fixed and a new issue starts if reports keep coming.",
	].join("\n");
}

/**
 * Weekly job: one issue per rule with unsummarised false positives. A rule's rows are marked summarised only after
 * its GitHub calls succeed, so a failure is retried next week.
 */
export async function summariseFalsePositives(env: Env, now = Date.now()): Promise<{ rules: number; failed: number }> {
	const db = env.DB;
	const nowS = Math.floor(now / 1000);
	const gh = new GitHub(env.GITHUB_TOKEN, env.GITHUB_REPO);

	const { results: rules } = await db
		.prepare(
			`SELECT rule_id, MIN(created_at) AS first FROM false_positives
			 WHERE summarised_at IS NULL AND created_at <= ?1
			 GROUP BY rule_id ORDER BY COUNT(*) DESC LIMIT ?2`,
		)
		.bind(nowS, MAX_RULES_PER_RUN)
		.all<{ rule_id: string; first: number }>();

	let failed = 0;
	for (const { rule_id, first } of rules) {
		try {
			const { results: rows } = await db
				.prepare("SELECT * FROM false_positives WHERE rule_id = ?1 AND summarised_at IS NULL AND created_at <= ?2 ORDER BY created_at")
				.bind(rule_id, nowS)
				.all<FpRow>();
			if (rows.length === 0) continue;
			const ruleName = rows[rows.length - 1].rule_name;
			const week = weekSection(rows, first, nowS);

			const mapped = await db.prepare("SELECT issue_number, since FROM fp_rule_issues WHERE rule_id = ?1").bind(rule_id).first<{ issue_number: number; since: number }>();

			let issueNumber: number | undefined;
			if (mapped) {
				const { results: totals } = await db
					.prepare("SELECT reason, COUNT(*) AS n FROM false_positives WHERE rule_id = ?1 AND created_at >= ?2 AND created_at <= ?3 GROUP BY reason")
					.bind(rule_id, mapped.since, nowS)
					.all<{ reason: FpReason; n: number }>();
				const updated = await gh.updateIssue(mapped.issue_number, { body: issueBody(rule_id, ruleName, count(totals), mapped.since) });
				if (updated && updated.state === "open") {
					await gh.addComment(updated.number, week);
					issueNumber = updated.number;
				}
			}

			if (issueNumber === undefined) {
				// No issue yet, or the last one was closed or removed: start a new one counting from this batch.
				const since = first;
				const totals = new Map<FpReason, number>();
				for (const r of rows) totals.set(r.reason, (totals.get(r.reason) ?? 0) + 1);
				const title = `False positives: ${rule_id} ${oneLine(ruleName, 120)}`;
				const issue = await gh.createIssue(noMentions(title), issueBody(rule_id, ruleName, totals, since), ["false-positive"]);
				await gh.addComment(issue.number, week);
				await db
					.prepare(
						"INSERT INTO fp_rule_issues (rule_id, issue_number, since) VALUES (?1, ?2, ?3) ON CONFLICT (rule_id) DO UPDATE SET issue_number = ?2, since = ?3",
					)
					.bind(rule_id, issue.number, since)
					.run();
				issueNumber = issue.number;
			}

			await db
				.prepare("UPDATE false_positives SET summarised_at = ?1 WHERE rule_id = ?2 AND summarised_at IS NULL AND created_at <= ?1")
				.bind(nowS, rule_id)
				.run();
			log("fp_summarised", { rule: rule_id, reports: rows.length, issue: issueNumber });
		} catch (e) {
			failed++;
			log("fp_summary_failed", { rule: rule_id, error: e instanceof Error ? e.message : "unknown" });
		}
	}
	return { rules: rules.length, failed };
}
