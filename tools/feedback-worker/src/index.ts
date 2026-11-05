import { storeFalsePositive, summariseFalsePositives } from "./falsePositive";
import { errorResponse, HttpError, json, log, readJsonBody } from "./http";
import { hit, LIMITS, networkKey, pruneExpired } from "./ratelimit";
import { handleReport } from "./report";
import { parseFalsePositive, parseReport } from "./validate";

/**
 * Header every plugin request carries. It is NOT a secret (the plugin is open source, so anyone can read it); it
 * only filters out scanners and stray traffic before any work is done.
 */
const APP_HEADER = "x-house-style";

async function route(request: Request, env: Env): Promise<Response> {
	const url = new URL(request.url);
	const path = url.pathname.replace(/\/+$/, "");

	if (path === "/v1/health") {
		if (request.method !== "GET" && request.method !== "HEAD") throw new HttpError(405, "Use GET.");
		return json({ ok: true, service: "house-style-feedback" });
	}

	const isReport = path === "/v1/report";
	const isFp = path === "/v1/false-positive";
	if (!isReport && !isFp) throw new HttpError(404, "Not found.");
	if (request.method !== "POST") throw new HttpError(405, "Use POST.");
	if (request.headers.get(APP_HEADER) !== "1") throw new HttpError(403, "This endpoint only accepts reports from the House Style plugin.");

	// Kill switch: set FEEDBACK_DISABLED to "1" to stop taking reports without redeploying code.
	if (String(env.FEEDBACK_DISABLED) === "1") throw new HttpError(503, "Reporting is switched off right now.", 3600);

	// Per-address limit (IPv6 by /64) and the global cap, both before the body is read, so floods are cheap to turn away.
	const ip = networkKey(request.headers.get("cf-connecting-ip") ?? "unknown");
	await hit(env.DB, isReport ? LIMITS.reportPerIp : LIMITS.fpPerIp, ip);
	await hit(env.DB, isReport ? LIMITS.reportGlobal : LIMITS.fpGlobal, "all", Date.now(), 503);

	const body = await readJsonBody(request);
	if (isReport) {
		const report = parseReport(body);
		await hit(env.DB, LIMITS.reportPerClient, report.client_id);
		return json(await handleReport(report, env));
	}
	const fp = parseFalsePositive(body);
	await hit(env.DB, LIMITS.fpPerClient, fp.client_id);
	return json(await storeFalsePositive(fp, env));
}

export default {
	async fetch(request, env, _ctx): Promise<Response> {
		try {
			return await route(request, env);
		} catch (e) {
			if (e instanceof HttpError) {
				if (e.status >= 500 || e.status === 429) log("request_rejected", { status: e.status, path: new URL(request.url).pathname });
				return errorResponse(e);
			}
			// Never echo internals: the message could contain report content.
			log("unhandled_error", { name: e instanceof Error ? e.name : "unknown" });
			return errorResponse(new HttpError(500, "The report server hit an error. Save the report and try again later."));
		}
	},

	async scheduled(controller, env, ctx): Promise<void> {
		const now = controller.scheduledTime;
		ctx.waitUntil(pruneExpired(env.DB, now));
		const result = await summariseFalsePositives(env, now);
		log("fp_summary_run", { rules: result.rules, failed: result.failed });
	},
} satisfies ExportedHandler<Env>;
