// Lunalyr listen-along relay: HTTP routing in front of one Durable Object per
// room. The wire format is in PROTOCOL.md. Nothing here logs song data.

import { Room, rejectSocket } from "./room";
import listenPage from "./listen.html";

export { Room };

export interface Env {
  ROOMS: DurableObjectNamespace<Room>;
  /** Workers rate-limiting binding for `POST /rooms` (see wrangler.toml). */
  ROOM_CREATE_LIMIT: RateLimit;
}

/** Room creations allowed per client per minute; must match wrangler.toml. */
export const ROOMS_PER_MINUTE = 5;

/**
 * The rate-limit key for a client: its IPv4 address, or the /64 prefix of its
 * IPv6 address (one home or phone usually gets a whole /64, so keying on the
 * full address would be trivially sidestepped). The key is hashed so the
 * address itself never leaves this function; nothing is stored or logged.
 */
export async function clientKey(request: Request): Promise<string> {
  const ip = (request.headers.get("CF-Connecting-IP") ?? "").trim().toLowerCase();
  const data = new TextEncoder().encode(`lunalyr-rooms:${ipPrefix(ip)}`);
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", data));
  return Array.from(digest.subarray(0, 16), (b) => b.toString(16).padStart(2, "0")).join("");
}

export function ipPrefix(ip: string): string {
  if (!ip.includes(":")) return ip || "unknown";
  const halves = ip.split("::");
  if (halves.length > 2) return ip;
  const head = halves[0] ? halves[0].split(":") : [];
  const tail = halves.length === 2 && halves[1] ? halves[1].split(":") : [];
  // An embedded IPv4 tail (::ffff:1.2.3.4) takes two groups; it is past /64 anyway.
  const tailGroups = tail.reduce((n, g) => n + (g.includes(".") ? 2 : 1), 0);
  const zeros = halves.length === 2 ? Math.max(0, 8 - head.length - tailGroups) : 0;
  const groups = [...head, ...Array<string>(zeros).fill("0"), ...tail];
  return groups
    .slice(0, 4)
    .map((g) => g.replace(/^0+(?=.)/, ""))
    .join(":");
}

export const ROOM_ID_PATTERN = /^[a-z0-9]{10}$/;
const ROOM_ALPHABET = "abcdefghijklmnopqrstuvwxyz0123456789";

/** 10 characters from [a-z0-9], uniformly distributed (rejection sampling). */
export function newRoomId(): string {
  let id = "";
  while (id.length < 10) {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    for (const b of bytes) {
      // 252 = 36 * 7, so bytes below it map evenly onto the alphabet.
      if (b < 252 && id.length < 10) id += ROOM_ALPHABET[b % 36];
    }
  }
  return id;
}

/** 32 lowercase hex characters (128 random bits). */
export function newHostKey(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

const CORS_HEADERS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, HEAD, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
  "Access-Control-Max-Age": "86400",
};

function json(body: unknown, status = 200, extra: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", ...extra },
  });
}

function isWebSocketUpgrade(request: Request): boolean {
  return request.headers.get("Upgrade")?.toLowerCase() === "websocket";
}

// Content-Security-Policy for the listen page. Scripts and frames are not
// pinned to hosts: Spotify's iFrame API loads code from its CDN and uses eval,
// and pinning would break the player whenever Spotify changes that. The page
// itself never inserts song data as HTML.
const PAGE_CSP = ["base-uri 'none'", "form-action 'none'", "object-src 'none'"].join("; ");

async function createRoom(request: Request, env: Env): Promise<Response> {
  // Each room is a Durable Object write plus an alarm, so an unthrottled loop
  // could spend the free plan's daily quota and take the relay down for
  // everyone. The binding is approximate and per Cloudflare location, which is
  // enough to stop one client hammering it. If the binding itself fails, let
  // the request through rather than refusing every host.
  let allowed = true;
  try {
    allowed = (await env.ROOM_CREATE_LIMIT.limit({ key: await clientKey(request) })).success;
  } catch {
    allowed = true;
  }
  if (!allowed) return json({ error: "rate_limited" }, 429, { "Retry-After": "60" });

  // A collision among 36^10 ids is vanishingly unlikely, but retry anyway.
  for (let attempt = 0; attempt < 5; attempt++) {
    const room = newRoomId();
    const hostKey = newHostKey();
    const stub = env.ROOMS.get(env.ROOMS.idFromName(room));
    const res = await stub.fetch("https://room/init", {
      method: "POST",
      body: JSON.stringify({ hostKey }),
    });
    if (res.ok) return json({ room, hostKey });
    if (res.status !== 409) return json({ error: "create_failed" }, 500);
  }
  return json({ error: "create_failed" }, 500);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    if (path === "/health") return new Response("ok", { headers: { "Content-Type": "text/plain" } });

    if (path === "/rooms" || path === "/rooms/") {
      if (method !== "POST") return json({ error: "method_not_allowed" }, 405, { Allow: "POST" });
      return createRoom(request, env);
    }

    const roomMatch = path.match(/^\/rooms\/([^/]+)\/(host|listen|state)$/);
    if (roomMatch) {
      const [, room, kind] = roomMatch;
      const isState = kind === "state";
      const extra = isState ? CORS_HEADERS : {};

      if (isState && method === "OPTIONS") return new Response(null, { status: 204, headers: CORS_HEADERS });
      if (method !== "GET" && !(isState && method === "HEAD")) {
        return json({ error: "method_not_allowed" }, 405, { Allow: isState ? "GET, HEAD, OPTIONS" : "GET", ...extra });
      }
      if (!isState && !isWebSocketUpgrade(request)) {
        return json({ error: "websocket_required" }, 426, { Upgrade: "websocket" });
      }
      if (!ROOM_ID_PATTERN.test(room)) {
        return isState ? json({ error: "unknown_room" }, 404, extra) : rejectSocket(4404, "unknown room");
      }

      const stub = env.ROOMS.get(env.ROOMS.idFromName(room));
      const inner = new URL(`https://room/${kind}`);
      const key = url.searchParams.get("key");
      if (key !== null) inner.searchParams.set("key", key);
      const res = await stub.fetch(new Request(inner.toString(), request));
      if (!isState) return res;
      const headers = new Headers(res.headers);
      for (const [k, v] of Object.entries(CORS_HEADERS)) headers.set(k, v);
      return new Response(method === "HEAD" ? null : res.body, { status: res.status, headers });
    }

    const pageMatch = path.match(/^\/listen\/([^/]+)\/?$/);
    if (pageMatch && (method === "GET" || method === "HEAD")) {
      // The page reads the room id from its own URL and reports an unknown
      // room itself, so the same HTML is served for every id.
      return new Response(method === "HEAD" ? null : listenPage, {
        headers: {
          "Content-Type": "text/html; charset=utf-8",
          "Cache-Control": "no-cache",
          "Content-Security-Policy": PAGE_CSP,
          "Referrer-Policy": "no-referrer",
          "X-Content-Type-Options": "nosniff",
        },
      });
    }

    if (path === "/" && method === "GET") {
      return new Response("Lunalyr listen-along relay.\n", {
        headers: { "Content-Type": "text/plain; charset=utf-8" },
      });
    }

    return json({ error: "not_found" }, 404);
  },
} satisfies ExportedHandler<Env>;
