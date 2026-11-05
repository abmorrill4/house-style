import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { hmacHex, sha256Hex } from "../src/http";
import { pruneRetention, RETENTION_DAYS } from "../src/ratelimit";
import { CLIENT_A, clearDb, errorReport, FakeGitHub, post } from "./helpers";

type Secret = { HASH_SECRET?: string };

beforeEach(async () => {
	await clearDb();
	new FakeGitHub().install();
});
afterEach(() => {
	vi.restoreAllMocks();
	delete (env as Secret).HASH_SECRET;
});

async function storedClientHash(): Promise<string> {
	const row = await env.DB.prepare("SELECT client_hash FROM reports").first<{ client_hash: string }>();
	return row!.client_hash;
}

describe("identifier hashing", () => {
	it("uses an HMAC when HASH_SECRET is set, so the hash can't be matched against plain SHA-256", async () => {
		(env as Secret).HASH_SECRET = "test-secret-not-real";
		await post("/v1/report", errorReport());
		const stored = await storedClientHash();
		expect(stored).toBe((await hmacHex("test-secret-not-real", CLIENT_A)).slice(0, 16));
		expect(stored).not.toBe((await sha256Hex(CLIENT_A)).slice(0, 16));
	});

	it("falls back to SHA-256 without the secret", async () => {
		await post("/v1/report", errorReport());
		expect(await storedClientHash()).toBe((await sha256Hex(CLIENT_A)).slice(0, 16));
	});
});

describe("retention", () => {
	it("deletes old reports, summarised false positives and stale signatures, and keeps the rest", async () => {
		const now = Date.now();
		const nowS = Math.floor(now / 1000);
		const old = nowS - (RETENTION_DAYS + 1) * 86400;
		const fresh = nowS - 86400;
		const db = env.DB;
		await db.batch([
			db.prepare("INSERT INTO reports (id, kind, client_hash, plugin_version, issue_number, created_at) VALUES ('old', 'error', 'h', '0.2.0', 1, ?1)").bind(old),
			db.prepare("INSERT INTO reports (id, kind, client_hash, plugin_version, issue_number, created_at) VALUES ('new', 'error', 'h', '0.2.0', 1, ?1)").bind(fresh),
			db.prepare("INSERT INTO false_positives (id, created_at, client_hash, plugin_version, rule_id, rule_name, reason, flagged_text, summarised_at) VALUES ('fp-old-done', ?1, 'h', '0.2.0', 'r', 'n', 'text_correct', 't', ?1)").bind(old),
			db.prepare("INSERT INTO false_positives (id, created_at, client_hash, plugin_version, rule_id, rule_name, reason, flagged_text) VALUES ('fp-old-pending', ?1, 'h', '0.2.0', 'r', 'n', 'text_correct', 't')").bind(old),
			db.prepare("INSERT INTO error_signatures (signature, issue_number, title, first_seen, last_seen) VALUES ('stale', 1, 't', ?1, ?1)").bind(old),
			db.prepare("INSERT INTO error_signatures (signature, issue_number, title, first_seen, last_seen) VALUES ('live', 2, 't', ?1, ?1)").bind(fresh),
			db.prepare("INSERT INTO signature_clients (signature, client_hash) VALUES ('stale', 'h'), ('live', 'h')"),
		]);
		await pruneRetention(db, now);
		const ids = async (sql: string) => (await db.prepare(sql).all<{ k: string }>()).results.map((r) => r.k).sort();
		expect(await ids("SELECT id AS k FROM reports")).toEqual(["new"]);
		expect(await ids("SELECT id AS k FROM false_positives")).toEqual(["fp-old-pending"]);
		expect(await ids("SELECT signature AS k FROM error_signatures")).toEqual(["live"]);
		expect(await ids("SELECT signature AS k FROM signature_clients")).toEqual(["live"]);
	});
});
