import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { hit, LIMITS, networkKey } from "../src/ratelimit";
import { reportLabels } from "../src/report";
import { parseReport } from "../src/validate";
import { clearDb, errorReport, FakeGitHub, post, problemReport, stayInWindow } from "./helpers";

let gh: FakeGitHub;
beforeEach(async () => {
	await clearDb();
	gh = new FakeGitHub();
	gh.install();
});
afterEach(() => {
	vi.restoreAllMocks();
	(env as { FEEDBACK_DISABLED: string }).FEEDBACK_DISABLED = "0";
});

describe("networkKey", () => {
	it("keeps IPv4 as is", () => expect(networkKey("203.0.113.9")).toBe("203.0.113.9"));
	it("cuts IPv6 to the /64", () => {
		const a = networkKey("2001:db8:1:2:aaaa:bbbb:cccc:dddd");
		expect(a).toBe("2001:0db8:0001:0002::/64");
		expect(networkKey("2001:DB8:1:2::1")).toBe(a);
		expect(networkKey("2001:db8:1:2:ffff::")).toBe(a);
		expect(networkKey("2001:db8:1:3::1")).not.toBe(a);
	});
	it("handles compressed and mapped forms", () => {
		expect(networkKey("::1")).toBe("0000:0000:0000:0000::/64");
		expect(networkKey("::ffff:203.0.113.9")).toBe("203.0.113.9");
		expect(networkKey("unknown")).toBe("unknown");
	});
});

describe("abuse limits", () => {
	it("limits an IPv6 /64 as one address", async () => {
		await stayInWindow(3600);
		for (let i = 0; i < LIMITS.reportPerIp.max; i++) {
			const res = await post("/v1/report", problemReport({ client_id: crypto.randomUUID() }), { ip: `2001:db8:5:6:${i.toString(16)}::1` });
			expect(res.status).toBe(200);
		}
		const res = await post("/v1/report", problemReport({ client_id: crypto.randomUUID() }), { ip: "2001:db8:5:6:ffff::7" });
		expect(res.status).toBe(429);
	});

	it("stops all reports once the global cap is spent", async () => {
		await stayInWindow(86400, 60);
		for (let i = 0; i < LIMITS.reportGlobal.max; i++) await hit(env.DB, LIMITS.reportGlobal, "all");
		const res = await post("/v1/report", problemReport());
		expect(res.status).toBe(503);
		expect(gh.issues.size).toBe(0);
	});

	it("answers 503 on both endpoints when the kill switch is on", async () => {
		(env as { FEEDBACK_DISABLED: string }).FEEDBACK_DISABLED = "1";
		expect((await post("/v1/report", problemReport())).status).toBe(503);
		expect((await post("/v1/false-positive", {})).status).toBe(503);
		expect(gh.requests.length).toBe(0);
	});

	it("only labels plain x.y.z versions", () => {
		const labels = (version: string) => reportLabels(parseReport(errorReport({ plugin_version: version })));
		expect(labels("0.2.0")).toContain("v0.2.0");
		expect(labels("0.2.0+abc")).toContain("v0.2.0");
		expect(labels("junk-version-123")).toEqual(["error"]);
	});
});
