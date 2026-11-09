// Lunalyr listen-along relay: HTTP routing in front of one Durable Object per
// room. The wire format is in PROTOCOL.md. Nothing here logs song data.

import { Room, rejectSocket } from "./room";
import listenPage from "./listen.html";

export { Room };

export interface Env {
  ROOMS: DurableObjectNamespace<Room>;
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

async function createRoom(env: Env): Promise<Response> {
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
      return createRoom(env);
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
