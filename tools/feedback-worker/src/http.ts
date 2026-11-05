/** Largest accepted request body, after gzip decompression. */
export const MAX_BODY_BYTES = 256 * 1024;

/** An error with an HTTP status and a plain message that is safe to show the writer. */
export class HttpError extends Error {
	constructor(
		readonly status: number,
		message: string,
		readonly retryAfter?: number,
	) {
		super(message);
	}
}

export function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
	return new Response(JSON.stringify(body), {
		status,
		headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...headers },
	});
}

export function errorResponse(err: HttpError): Response {
	const body: Record<string, unknown> = { ok: false, error: err.message };
	const headers: Record<string, string> = {};
	if (err.retryAfter !== undefined) {
		body.retry_after = err.retryAfter;
		headers["retry-after"] = String(err.retryAfter);
	}
	return json(body, err.status, headers);
}

/**
 * Reads the body as JSON, never buffering more than MAX_BODY_BYTES. Accepts `Content-Encoding: gzip`
 * (the cap then applies to the decompressed size, so a zip bomb stops at 256 KB).
 */
export async function readJsonBody(request: Request): Promise<unknown> {
	const type = request.headers.get("content-type") ?? "";
	if (!/^application\/json\b/i.test(type)) throw new HttpError(415, "Send the report as application/json.");

	const declared = Number(request.headers.get("content-length") ?? "0");
	if (declared > MAX_BODY_BYTES) throw new HttpError(413, "The report is too large (limit 256 KB).");
	if (!request.body) throw new HttpError(400, "The request body is empty.");

	const encoding = (request.headers.get("content-encoding") ?? "identity").toLowerCase();
	let stream: ReadableStream<Uint8Array> = request.body;
	if (encoding === "gzip") stream = stream.pipeThrough(new DecompressionStream("gzip"));
	else if (encoding !== "identity") throw new HttpError(415, "Only gzip content encoding is supported.");

	const reader = stream.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	try {
		for (;;) {
			const { done, value } = await reader.read();
			if (done) break;
			total += value.byteLength;
			if (total > MAX_BODY_BYTES) {
				await reader.cancel();
				throw new HttpError(413, "The report is too large (limit 256 KB).");
			}
			chunks.push(value);
		}
	} catch (e) {
		if (e instanceof HttpError) throw e;
		throw new HttpError(400, "The request body could not be read.");
	}

	const bytes = new Uint8Array(total);
	let offset = 0;
	for (const c of chunks) {
		bytes.set(c, offset);
		offset += c.byteLength;
	}
	let text: string;
	try {
		text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes);
	} catch {
		throw new HttpError(400, "The request body is not valid UTF-8.");
	}
	if (text.charCodeAt(0) === 0xfeff) text = text.slice(1); // .NET writers sometimes add a BOM
	try {
		return JSON.parse(text);
	} catch {
		throw new HttpError(400, "The request body is not valid JSON.");
	}
}

/** Hex SHA-256 of a string. */
export async function sha256Hex(value: string): Promise<string> {
	const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
	return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** HMAC-SHA-256 as hex. */
export async function hmacHex(secret: string, value: string): Promise<string> {
	const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
	const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(value));
	return [...new Uint8Array(mac)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * Hash for an IP address or install id before it is stored. With the HASH_SECRET Worker secret set it is an HMAC, which
 * can't be reversed by trying every IPv4 address. Without the secret it falls back to plain SHA-256.
 */
export function identityHash(env: Env, value: string): Promise<string> {
	const secret = (env as { HASH_SECRET?: string }).HASH_SECRET;
	return secret ? hmacHex(secret, value) : sha256Hex(value);
}

/** Structured log line. Callers pass only non-sensitive fields: no IPs, client ids, report text or tokens. */
export function log(event: string, fields: Record<string, string | number | boolean | undefined> = {}): void {
	console.log(JSON.stringify({ event, ...fields }));
}
