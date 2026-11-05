import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CLIENT_A, clearDb, errorReport, FakeGitHub, freshIp, post, problemReport, stayInWindow } from "./helpers";

let gh: FakeGitHub;
beforeEach(async () => {
	await clearDb();
	gh = new FakeGitHub();
	gh.install();
});
afterEach(() => vi.restoreAllMocks());

async function expect400(body: unknown, message: RegExp) {
	const res = await post("/v1/report", body);
	expect(res.status).toBe(400);
	const json = await res.json<{ ok: boolean; error: string }>();
	expect(json.ok).toBe(false);
	expect(json.error).toMatch(message);
}

async function gzip(text: string): Promise<ArrayBuffer> {
	const stream = new Blob([text]).stream().pipeThrough(new CompressionStream("gzip"));
	return new Response(stream).arrayBuffer();
}

describe("validation", () => {
	it("rejects unknown fields", () => expect400(errorReport({ api_key: "x" }), /Unknown field "api_key"/));
	it("rejects unknown exception fields", () =>
		expect400(errorReport({ exception: { type: "X", message: "", stack: "", inner: 1 } }), /exception\.inner/));
	it("rejects a bad kind", () => expect400(errorReport({ kind: "crash" }), /"kind"/));
	it("needs an exception for errors", () => expect400(errorReport({ exception: null }), /needs "exception"/));
	it("needs what_happened for problems", () => expect400(problemReport({ what_happened: null }), /needs "what_happened"/));
	it("checks the client id format", () => expect400(errorReport({ client_id: "not-a-guid" }), /"client_id" has an invalid format/));
	it("checks the version format", () => expect400(errorReport({ plugin_version: "0.2.0\n" }), /"plugin_version"/));
	it("checks types", () => expect400(errorReport({ automatic: "yes" }), /"automatic"/));
	it("limits field lengths", () => expect400(problemReport({ what_happened: "a".repeat(4001) }), /too long \(limit 4000/));
	it("limits the diag log to 64 KB", () => expect400(problemReport({ diag_log_tail: "a".repeat(65_537) }), /"diag_log_tail" is too long/));
	it("rejects nested settings", () => expect400(errorReport({ settings: { A: { b: 1 } } }), /"settings\.A"/));
	it("rejects arrays", () => expect400([errorReport()], /JSON object/));

	it("rejects bodies that aren't JSON", async () => {
		const res = await post("/v1/report", undefined, { raw: "{not json" });
		expect(res.status).toBe(400);
		expect(await res.json()).toMatchObject({ error: "The request body is not valid JSON." });
	});

	it("requires application/json", async () => {
		const res = await post("/v1/report", errorReport(), { headers: { "content-type": "text/plain" } });
		expect(res.status).toBe(415);
	});

	it("accepts nulls for optional fields and a UTF-8 BOM", async () => {
		const res = await post("/v1/report", undefined, {
			raw: "﻿" + JSON.stringify(problemReport({ flare_version: null, os: null, expected: null, settings: null, exception: null })),
		});
		expect(res.status).toBe(200);
	});
});

describe("size limits", () => {
	it("rejects a declared body over 256 KB", async () => {
		const raw = JSON.stringify(problemReport({ diag_log_tail: "a".repeat(300 * 1024) }));
		const res = await post("/v1/report", undefined, { raw });
		expect(res.status).toBe(413);
		expect(gh.requests).toHaveLength(0);
	});

	it("accepts gzip bodies", async () => {
		const raw = await gzip(JSON.stringify(problemReport()));
		const res = await post("/v1/report", undefined, { raw, headers: { "content-encoding": "gzip" } });
		expect(res.status).toBe(200);
	});

	it("rejects gzip bodies that inflate past 256 KB", async () => {
		const raw = await gzip(JSON.stringify(problemReport({ diag_log_tail: "a".repeat(2 * 1024 * 1024) })));
		expect(raw.byteLength).toBeLessThan(256 * 1024);
		const res = await post("/v1/report", undefined, { raw, headers: { "content-encoding": "gzip" } });
		expect(res.status).toBe(413);
	});
});

describe("rate limits", () => {
	it("allows 5 reports a minute per client", { timeout: 30_000 }, async () => {
		await stayInWindow(60);
		for (let i = 0; i < 5; i++) expect((await post("/v1/report", problemReport())).status).toBe(200);
		const res = await post("/v1/report", problemReport());
		expect(res.status).toBe(429);
		expect(Number(res.headers.get("retry-after"))).toBeGreaterThan(0);
		expect(await res.json()).toMatchObject({ ok: false, error: expect.stringMatching(/Too many reports/) });
		expect(gh.issues.size).toBe(5);
	});

	it("allows 20 reports an hour per IP, across clients", { timeout: 30_000 }, async () => {
		await stayInWindow(3600);
		const ip = freshIp();
		for (let i = 0; i < 20; i++) {
			const client = `${CLIENT_A.slice(0, -2)}${String(i).padStart(2, "0")}`;
			expect((await post("/v1/report", problemReport({ client_id: client }), { ip })).status).toBe(200);
		}
		const res = await post("/v1/report", problemReport({ client_id: "11111111-1111-4111-8111-111111111111" }), { ip });
		expect(res.status).toBe(429);
		expect(Number(res.headers.get("retry-after"))).toBeLessThanOrEqual(3600);
		// A different IP is unaffected.
		expect((await post("/v1/report", problemReport({ client_id: "22222222-2222-4222-8222-222222222222" }))).status).toBe(200);
	});
});
