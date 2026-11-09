import { env, exports } from "cloudflare:workers";
import { evictDurableObject, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import { ROOM_ID_PATTERN, ROOMS_PER_MINUTE, ipPrefix } from "../src/index";
import { MAX_LISTENERS, ROOM_EXPIRY_MS } from "../src/room";

const BASE = "https://relay.test";
const worker = (exports as unknown as { default: Fetcher }).default;

type Json = Record<string, any>;

interface TestSocket {
  ws: WebSocket;
  messages: Json[];
  closed: Promise<{ code: number; reason: string }>;
  isClosed: () => boolean;
  /** Resolves with the first message (from `from` on) matching `pred`. */
  next: (pred?: (m: Json) => boolean, timeoutMs?: number) => Promise<Json>;
  send: (m: unknown) => void;
}

const sockets: TestSocket[] = [];
afterEach(() => {
  for (const s of sockets.splice(0)) {
    try {
      s.ws.close(1000, "test done");
    } catch {}
  }
});

let ipCounter = 0;
/** A fresh client address per call, so tests don't share a rate-limit bucket. */
function nextIp(): string {
  ipCounter++;
  return `10.${(ipCounter >> 16) & 255}.${(ipCounter >> 8) & 255}.${ipCounter & 255}`;
}

function postRooms(ip: string): Promise<Response> {
  return worker.fetch(`${BASE}/rooms`, { method: "POST", headers: { "CF-Connecting-IP": ip } });
}

async function createRoom(): Promise<{ room: string; hostKey: string }> {
  const res = await postRooms(nextIp());
  expect(res.status).toBe(200);
  return res.json();
}

async function open(path: string): Promise<TestSocket> {
  const res = await worker.fetch(`${BASE}${path}`, { headers: { Upgrade: "websocket" } });
  expect(res.status).toBe(101);
  const ws = res.webSocket!;
  const messages: Json[] = [];
  const waiters: { pred: (m: Json) => boolean; resolve: (m: Json) => void; seen: number }[] = [];
  let closedFlag = false;
  let cursor = 0;
  const closed = new Promise<{ code: number; reason: string }>((resolve) => {
    ws.addEventListener("close", (ev) => {
      closedFlag = true;
      resolve({ code: ev.code, reason: ev.reason });
    });
  });
  ws.addEventListener("message", (ev) => {
    const msg = JSON.parse(ev.data as string);
    messages.push(msg);
    for (const w of [...waiters]) {
      if (w.pred(msg)) {
        waiters.splice(waiters.indexOf(w), 1);
        w.resolve(msg);
      }
    }
  });
  ws.accept();
  const socket: TestSocket = {
    ws,
    messages,
    closed,
    isClosed: () => closedFlag,
    next(pred = () => true, timeoutMs = 2000) {
      // Look at messages not yet consumed by an earlier next() first.
      for (; cursor < messages.length; ) {
        const m = messages[cursor++];
        if (pred(m)) return Promise.resolve(m);
      }
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`timed out waiting for message on ${path}`)), timeoutMs);
        waiters.push({
          pred,
          seen: 0,
          resolve: (m) => {
            clearTimeout(timer);
            cursor = messages.length;
            resolve(m);
          },
        });
      });
    },
    send: (m) => ws.send(typeof m === "string" ? m : JSON.stringify(m)),
  };
  sockets.push(socket);
  return socket;
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function state(seq: number, extra: Json = {}): Json {
  return {
    type: "state",
    v: 1,
    seq,
    track: { title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200, spotifyId: "0VjIjW4GlUZAMYd2vXMi3b" },
    playing: true,
    position: 63.2,
    ...extra,
  };
}

const LYRICS = [
  { start: 12.1, end: 15.0, text: "first line" },
  { start: 15.0, end: 18.0, text: "second line" },
];

function stubFor(room: string) {
  return env.ROOMS.get(env.ROOMS.idFromName(room));
}

describe("rooms", () => {
  it("serves /health", async () => {
    const res = await worker.fetch(`${BASE}/health`);
    expect(res.status).toBe(200);
    expect(await res.text()).toBe("ok");
  });

  it("creates rooms with the right id and key formats", async () => {
    const a = await createRoom();
    const b = await createRoom();
    expect(a.room).toMatch(ROOM_ID_PATTERN);
    expect(a.hostKey).toMatch(/^[0-9a-f]{32}$/);
    expect(a.room).not.toBe(b.room);
    expect(a.hostKey).not.toBe(b.hostKey);
    // A new room has no state yet.
    expect((await worker.fetch(`${BASE}/rooms/${a.room}/state`)).status).toBe(404);
  });

  it("only accepts POST on /rooms", async () => {
    expect((await worker.fetch(`${BASE}/rooms`)).status).toBe(405);
  });

  it("rejects a host with the wrong key or no key (close 4403)", async () => {
    const { room, hostKey } = await createRoom();
    const wrong = hostKey.replace(/^./, hostKey[0] === "a" ? "b" : "a");
    const bad = await open(`/rooms/${room}/host?key=${wrong}`);
    expect((await bad.closed).code).toBe(4403);
    const missing = await open(`/rooms/${room}/host`);
    expect((await missing.closed).code).toBe(4403);
    const short = await open(`/rooms/${room}/host?key=abc`);
    expect((await short.closed).code).toBe(4403);
  });

  it("closes sockets to unknown rooms with 4404 and 404s their state", async () => {
    const listener = await open(`/rooms/zzzzzzzzzz/listen`);
    expect((await listener.closed).code).toBe(4404);
    const host = await open(`/rooms/zzzzzzzzzz/host?key=${"0".repeat(32)}`);
    expect((await host.closed).code).toBe(4404);
    const malformed = await open(`/rooms/NOT-A-ROOM/listen`);
    expect((await malformed.closed).code).toBe(4404);
    expect((await worker.fetch(`${BASE}/rooms/zzzzzzzzzz/state`)).status).toBe(404);
  });

  it("requires a WebSocket upgrade for host and listen", async () => {
    const { room } = await createRoom();
    expect((await worker.fetch(`${BASE}/rooms/${room}/listen`)).status).toBe(426);
  });
});

describe("state relay", () => {
  it("forwards live state with age 0 and serves it with CORS from /state", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    expect(await host.next((m) => m.type === "listeners")).toEqual({ type: "listeners", count: 0 });
    const listener = await open(`/rooms/${room}/listen`);
    expect(await host.next((m) => m.type === "listeners")).toEqual({ type: "listeners", count: 1 });

    host.send(state(1, { lyrics: LYRICS, host: { name: "Tommy" } }));
    const got = await listener.next((m) => m.type === "state");
    expect(got.age).toBe(0);
    expect(got.seq).toBe(1);
    expect(got.track.title).toBe("Blinding Lights");
    expect(got.lyrics).toEqual(LYRICS);
    expect(got.host).toEqual({ name: "Tommy" });

    const res = await worker.fetch(`${BASE}/rooms/${room}/state`, { headers: { Origin: "https://example.com" } });
    expect(res.status).toBe(200);
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe("*");
    const body = await res.json<Json>();
    expect(body.seq).toBe(1);
    expect(typeof body.age).toBe("number");

    const preflight = await worker.fetch(`${BASE}/rooms/${room}/state`, { method: "OPTIONS" });
    expect(preflight.status).toBe(204);
    expect(preflight.headers.get("Access-Control-Allow-Origin")).toBe("*");
  });

  it("gives a late listener the cached state with its age, and keeps the track's lyrics", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    host.send(state(1, { lyrics: LYRICS }));
    await sleep(50);
    // A heartbeat for the same track without lyrics.
    host.send(state(2, { position: 63.25 }));
    await sleep(300);

    const late = await open(`/rooms/${room}/listen`);
    const got = await late.next((m) => m.type === "state");
    expect(got.seq).toBe(2);
    expect(got.age).toBeGreaterThanOrEqual(250);
    expect(got.age).toBeLessThan(5000);
    expect(got.lyrics).toEqual(LYRICS);

    // A new track drops the old lyrics.
    host.send(state(3, { track: { title: "Other", artist: "Someone", duration: 100 }, position: 0 }));
    await late.next((m) => m.seq === 3);
    const later = await open(`/rooms/${room}/listen`);
    const cached = await later.next((m) => m.type === "state");
    expect(cached.seq).toBe(3);
    expect(cached.lyrics).toBeUndefined();
  });

  it("relays a null track", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const listener = await open(`/rooms/${room}/listen`);
    host.send({ type: "state", v: 1, seq: 7, track: null, playing: false, position: 0 });
    const got = await listener.next((m) => m.type === "state");
    expect(got.track).toBeNull();
    expect(got.age).toBe(0);
  });

  it("keeps the latest state across hibernation", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const listener = await open(`/rooms/${room}/listen`);
    host.send(state(1, { lyrics: LYRICS }));
    await listener.next((m) => m.seq === 1);

    await evictDurableObject(stubFor(room));

    // A heartbeat after waking: forwarded live, and the late listener still gets lyrics.
    host.send(state(2, { position: 64 }));
    expect((await listener.next((m) => m.seq === 2)).age).toBe(0);
    const late = await open(`/rooms/${room}/listen`);
    const got = await late.next((m) => m.type === "state");
    expect(got.lyrics).toEqual(LYRICS);
    expect(got.track.title).toBe("Blinding Lights");
  });

  it("closes a listener that sends anything (close 4400) and never relays it", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const a = await open(`/rooms/${room}/listen`);
    const b = await open(`/rooms/${room}/listen`);
    await host.next((m) => m.type === "listeners" && m.count === 2);
    a.send(state(99));
    expect((await a.closed).code).toBe(4400);
    // The host hears the count drop, and the other listener is untouched.
    expect((await host.next((m) => m.type === "listeners")).count).toBe(1);
    host.send(state(1));
    expect((await b.next((m) => m.type === "state")).seq).toBe(1);
    expect(b.messages.some((m) => m.seq === 99)).toBe(false);
    expect(b.isClosed()).toBe(false);
    // The room's cached state is the host's, not the listener's.
    const res = await worker.fetch(`${BASE}/rooms/${room}/state`);
    expect((await res.json<Json>()).seq).toBe(1);
  });

  it("closes a listener that sends binary or junk too", async () => {
    const { room } = await createRoom();
    const a = await open(`/rooms/${room}/listen`);
    a.ws.send(new Uint8Array([1, 2, 3]));
    expect((await a.closed).code).toBe(4400);
    const b = await open(`/rooms/${room}/listen`);
    b.send("x");
    expect((await b.closed).code).toBe(4400);
  });

  it("ignores unknown host message types", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const b = await open(`/rooms/${room}/listen`);
    host.send({ type: "something-else" });
    host.send("not json");
    host.send(state(1));
    expect((await b.next((m) => m.type === "state")).seq).toBe(1);
    expect(b.messages.filter((m) => m.type === "state").length).toBe(1);
  });
});

describe("listener counts", () => {
  it("tells the host whenever the count changes", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    expect((await host.next((m) => m.type === "listeners")).count).toBe(0);
    const a = await open(`/rooms/${room}/listen`);
    expect((await host.next((m) => m.type === "listeners")).count).toBe(1);
    await open(`/rooms/${room}/listen`);
    expect((await host.next((m) => m.type === "listeners")).count).toBe(2);
    a.ws.close(1000, "bye");
    expect((await host.next((m) => m.type === "listeners")).count).toBe(1);
  });

  it(`caps a room at ${MAX_LISTENERS} listeners (close 4429)`, async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    for (let i = 0; i < MAX_LISTENERS; i++) await open(`/rooms/${room}/listen`);
    await host.next((m) => m.type === "listeners" && m.count === MAX_LISTENERS);
    const extra = await open(`/rooms/${room}/listen`);
    expect((await extra.closed).code).toBe(4429);

    // Everyone inside still gets state.
    host.send(state(1));
    const last = sockets[sockets.length - 2];
    expect((await last.next((m) => m.type === "state")).seq).toBe(1);
  });
});

describe("limits", () => {
  it("drops host messages beyond 5 per second without disconnecting", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const listener = await open(`/rooms/${room}/listen`);
    for (let i = 1; i <= 10; i++) host.send(state(i));
    await listener.next((m) => m.seq === 5);
    await sleep(300);
    expect(listener.messages.filter((m) => m.type === "state").map((m) => m.seq)).toEqual([1, 2, 3, 4, 5]);
    expect(host.isClosed()).toBe(false);

    await sleep(1000);
    host.send(state(11));
    expect((await listener.next((m) => m.seq === 11)).seq).toBe(11);
  });

  it("drops messages over 64 KB without disconnecting", async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const listener = await open(`/rooms/${room}/listen`);
    const big = state(1, { lyrics: [{ start: 0, end: 1, text: "x".repeat(70 * 1024) }] });
    host.send(big);
    // Multi-byte text that is under 64 KB in UTF-16 units but over it in UTF-8.
    host.send(state(2, { lyrics: [{ start: 0, end: 1, text: "é".repeat(33 * 1024) }] }));
    // Just under the limit passes.
    const fits = state(3);
    fits.lyrics = [{ start: 0, end: 1, text: "" }];
    const room64 = 64 * 1024 - JSON.stringify(fits).length;
    fits.lyrics[0].text = "y".repeat(room64);
    expect(JSON.stringify(fits).length).toBe(64 * 1024);
    host.send(fits);
    const got = await listener.next((m) => m.type === "state");
    expect(got.seq).toBe(3);
    expect(host.isClosed()).toBe(false);
  });
});

describe("room creation rate limit", () => {
  it(`allows ${ROOMS_PER_MINUTE} rooms a minute per address, then answers 429`, async () => {
    const ip = "203.0.113.7";
    for (let i = 0; i < ROOMS_PER_MINUTE; i++) expect((await postRooms(ip)).status).toBe(200);
    const res = await postRooms(ip);
    expect(res.status).toBe(429);
    expect(res.headers.get("Retry-After")).toBe("60");
    expect(await res.json()).toEqual({ error: "rate_limited" });
    // Another address is unaffected.
    expect((await postRooms("203.0.113.8")).status).toBe(200);
  });

  it("counts a whole IPv6 /64 as one client", async () => {
    for (let i = 0; i < ROOMS_PER_MINUTE; i++) {
      expect((await postRooms(`2001:db8:aa:1::${(i + 1).toString(16)}`)).status).toBe(200);
    }
    expect((await postRooms("2001:0db8:00aa:0001:ffff:1:2:3")).status).toBe(429);
    expect((await postRooms("2001:db8:aa:2::1")).status).toBe(200);
  });

  it("does not limit the other routes", async () => {
    const { room } = await createRoom();
    for (let i = 0; i < ROOMS_PER_MINUTE * 2; i++) {
      expect((await worker.fetch(`${BASE}/rooms/${room}/state`)).status).toBe(404);
    }
  });

  it("reduces IPv6 addresses to their /64 prefix", () => {
    expect(ipPrefix("2001:db8::1")).toBe("2001:db8:0:0");
    expect(ipPrefix("2001:0db8:0000:0001:0:0:0:1")).toBe("2001:db8:0:1");
    expect(ipPrefix("2001:db8:1:2:3:4:5:6")).toBe("2001:db8:1:2");
    expect(ipPrefix("::1")).toBe("0:0:0:0");
    expect(ipPrefix("::ffff:192.0.2.1")).toBe("0:0:0:0");
    expect(ipPrefix("192.0.2.1")).toBe("192.0.2.1");
    expect(ipPrefix("")).toBe("unknown");
  });
});

describe("ending", () => {
  it('ends at once on {"type":"end"}', async () => {
    const { room, hostKey } = await createRoom();
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const listener = await open(`/rooms/${room}/listen`);
    host.send(state(1));
    await listener.next((m) => m.type === "state");
    host.send({ type: "end" });
    expect(await listener.next((m) => m.type === "ended")).toEqual({ type: "ended" });
    expect((await listener.closed).code).toBe(1000);
    await host.closed;

    expect((await worker.fetch(`${BASE}/rooms/${room}/state`)).status).toBe(404);
    const again = await open(`/rooms/${room}/listen`);
    expect((await again.closed).code).toBe(4404);
    const hostAgain = await open(`/rooms/${room}/host?key=${hostKey}`);
    expect((await hostAgain.closed).code).toBe(4404);
  });

  it("expires 10 minutes after the host disconnects", async () => {
    const { room, hostKey } = await createRoom();
    const stub = stubFor(room);
    const host = await open(`/rooms/${room}/host?key=${hostKey}`);
    const listener = await open(`/rooms/${room}/listen`);
    host.send(state(1));
    await listener.next((m) => m.type === "state");

    // Connected host: no expiry pending.
    expect(await runInDurableObject(stub, (_i, s) => s.storage.getAlarm())).toBeNull();

    const before = Date.now();
    host.ws.close(1000, "bye");
    let alarm: number | null = null;
    for (let i = 0; i < 50 && alarm === null; i++) {
      await sleep(20);
      alarm = await runInDurableObject(stub, (_i, s) => s.storage.getAlarm());
    }
    expect(alarm).not.toBeNull();
    expect(alarm! - before).toBeGreaterThanOrEqual(ROOM_EXPIRY_MS - 1000);
    expect(alarm! - before).toBeLessThanOrEqual(ROOM_EXPIRY_MS + 5000);

    // Listeners still get the cached state while the host is away.
    expect((await worker.fetch(`${BASE}/rooms/${room}/state`)).status).toBe(200);

    expect(await runDurableObjectAlarm(stub)).toBe(true);
    expect(await listener.next((m) => m.type === "ended")).toEqual({ type: "ended" });
    expect((await listener.closed).code).toBe(1000);
    expect((await worker.fetch(`${BASE}/rooms/${room}/state`)).status).toBe(404);
  });

  it("cancels the expiry when the host reconnects", async () => {
    const { room, hostKey } = await createRoom();
    const stub = stubFor(room);
    const first = await open(`/rooms/${room}/host?key=${hostKey}`);
    first.ws.close(1000, "network change");
    for (let i = 0; i < 50; i++) {
      await sleep(20);
      if (await runInDurableObject(stub, (_i, s) => s.storage.getAlarm())) break;
    }
    const second = await open(`/rooms/${room}/host?key=${hostKey}`);
    await second.next((m) => m.type === "listeners");
    expect(await runInDurableObject(stub, (_i, s) => s.storage.getAlarm())).toBeNull();
    expect(await runDurableObjectAlarm(stub)).toBe(false);
    const listener = await open(`/rooms/${room}/listen`);
    second.send(state(5));
    expect((await listener.next((m) => m.type === "state")).seq).toBe(5);
  });

  it("expires a room whose host never connects", async () => {
    const { room } = await createRoom();
    const stub = stubFor(room);
    const alarm = await runInDurableObject(stub, (_i, s) => s.storage.getAlarm());
    expect(alarm).not.toBeNull();
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    const listener = await open(`/rooms/${room}/listen`);
    expect((await listener.closed).code).toBe(4404);
  });

  it("replaces an older host connection with a newer one", async () => {
    const { room, hostKey } = await createRoom();
    const first = await open(`/rooms/${room}/host?key=${hostKey}`);
    const second = await open(`/rooms/${room}/host?key=${hostKey}`);
    expect((await first.closed).code).toBe(4000);
    expect(second.isClosed()).toBe(false);
  });
});

describe("listen page", () => {
  it("serves the HTML page", async () => {
    const res = await worker.fetch(`${BASE}/listen/abcdefghij`);
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Type")).toContain("text/html");
    const html = await res.text();
    expect(html).toContain("Start listening");
    expect(html).toContain("https://open.spotify.com/embed/iframe-api/v1");
    expect(res.headers.get("Content-Security-Policy")).toContain("object-src 'none'");
  });

  it("404s unknown paths", async () => {
    expect((await worker.fetch(`${BASE}/nope`)).status).toBe(404);
  });
});
