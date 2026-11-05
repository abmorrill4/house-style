import { HttpError, sha256Hex } from "./http";

// Fixed-window counters in D1. The Workers rate limiting binding only supports 10 s and 60 s periods, and the
// per-IP limit here is hourly, so a D1 counter is used for every limit. IPs and client ids are stored only as
// SHA-256 hashes inside the bucket name, and rows are pruned by the weekly cron once they expire.

export interface Limit {
	scope: string;
	max: number;
	windowSeconds: number;
}

export const LIMITS = {
	reportPerIp: { scope: "report-ip", max: 20, windowSeconds: 3600 },
	reportPerClient: { scope: "report-client", max: 5, windowSeconds: 60 },
	fpPerIp: { scope: "fp-ip", max: 60, windowSeconds: 3600 },
	fpPerClient: { scope: "fp-client", max: 30, windowSeconds: 3600 },
	// Everyone together, so an attacker with many addresses and client ids still can't flood the reports repo.
	reportGlobal: { scope: "report-all", max: 500, windowSeconds: 86400 },
	fpGlobal: { scope: "fp-all", max: 5000, windowSeconds: 86400 },
} satisfies Record<string, Limit>;

/**
 * The identity a per-address limit is keyed on. An IPv6 address is cut to its /64, because one customer normally gets
 * a whole /64 and can rotate through it freely. IPv4 and anything unrecognised stay as they are.
 */
export function networkKey(ip: string): string {
	const addr = ip.split("%")[0].toLowerCase();
	if (!addr.includes(":")) return addr;
	const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/.exec(addr);
	if (mapped) return mapped[1];
	const halves = addr.split("::");
	if (halves.length > 2) return addr;
	const head = halves[0] === "" ? [] : halves[0].split(":");
	const tail = halves.length === 2 && halves[1] !== "" ? halves[1].split(":") : [];
	if (halves.length === 1 && head.length !== 8) return addr;
	const fill = Array<string>(Math.max(0, 8 - head.length - tail.length)).fill("0");
	const groups = halves.length === 2 ? [...head, ...fill, ...tail] : head;
	if (groups.length !== 8 || !groups.every((g) => /^[0-9a-f]{1,4}$/.test(g))) return addr;
	return `${groups.slice(0, 4).map((g) => g.padStart(4, "0")).join(":")}::/64`;
}

/** Counts one hit against `limit` for `identity`; throws 429 once the window's budget is spent. */
export async function hit(db: D1Database, limit: Limit, identity: string, now = Date.now(), status = 429): Promise<void> {
	const nowS = Math.floor(now / 1000);
	const window = Math.floor(nowS / limit.windowSeconds);
	const expiresAt = (window + 1) * limit.windowSeconds;
	const bucket = `${limit.scope}:${(await sha256Hex(identity)).slice(0, 32)}:${window}`;
	const row = await db
		.prepare(
			`INSERT INTO rate_limits (bucket, count, expires_at) VALUES (?1, 1, ?2)
			 ON CONFLICT (bucket) DO UPDATE SET count = count + 1
			 RETURNING count`,
		)
		.bind(bucket, expiresAt)
		.first<{ count: number }>();
	if (row && row.count > limit.max) {
		const message = status === 429 ? "Too many reports from this computer. Try again later." : "The report server is busy right now. Try again later.";
		throw new HttpError(status, message, Math.max(1, expiresAt - nowS));
	}
}

export async function pruneExpired(db: D1Database, now = Date.now()): Promise<void> {
	await db.prepare("DELETE FROM rate_limits WHERE expires_at < ?1").bind(Math.floor(now / 1000)).run();
}
