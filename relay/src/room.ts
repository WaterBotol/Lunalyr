// One Durable Object per listen-along room, using the WebSocket Hibernation
// API so an idle room costs no duration. See PROTOCOL.md.
//
// Storage holds only what a room needs while it lives: the host key and the
// latest state (so a listener who joins after the object was evicted from
// memory still gets it at once). Everything is deleted when the room ends.
// Nothing is logged.

import { DurableObject } from "cloudflare:workers";
import type { Env } from "./index";

export const MAX_LISTENERS = 50;
export const MAX_MESSAGE_BYTES = 64 * 1024;
export const HOST_MESSAGES_PER_SECOND = 5;
export const ROOM_EXPIRY_MS = 10 * 60 * 1000;
/** A seek smaller than this is not worth a storage write (matches the host's own 1.5 s rule). */
const PERSIST_POSITION_SLACK_S = 1.5;

const HOST_TAG = "host";
const LISTENER_TAG = "listener";
const OPEN = 1; // WebSocket.READY_STATE_OPEN

export const CLOSE_UNKNOWN_ROOM = 4404;
export const CLOSE_BAD_KEY = 4403;
export const CLOSE_ROOM_FULL = 4429;
export const CLOSE_REPLACED = 4000;
export const CLOSE_LISTENER_SENT = 4400;

type StateMessage = Record<string, unknown> & { type: "state" };

interface Meta {
  hostKey: string;
  createdAt: number;
}

interface Cached {
  /** The latest state, with lyrics carried over from earlier states of the same track. */
  state: StateMessage;
  /** Date.now() when the relay received it; `age` is measured from here. */
  receivedAt: number;
}

/** Accepts a WebSocket only to close it at once with an application close code. */
export function rejectSocket(code: number, reason: string): Response {
  const pair = new WebSocketPair();
  const server = pair[1];
  server.accept();
  server.close(code, reason);
  return new Response(null, { status: 101, webSocket: pair[0] });
}

const encoder = new TextEncoder();

function byteLength(message: string | ArrayBuffer): number {
  if (typeof message !== "string") return message.byteLength;
  // Every UTF-16 unit is at most 3 UTF-8 bytes, and at least 1.
  if (message.length > MAX_MESSAGE_BYTES) return message.length;
  if (message.length * 3 <= MAX_MESSAGE_BYTES) return message.length;
  return encoder.encode(message).byteLength;
}

/** Constant-time string comparison (for the host key). */
function keysMatch(given: string, expected: string): boolean {
  const a = encoder.encode(given);
  const b = encoder.encode(expected);
  if (a.byteLength !== b.byteLength) {
    // Still do a comparison of equal cost so length mismatches take the same time.
    crypto.subtle.timingSafeEqual(b, b);
    return false;
  }
  return crypto.subtle.timingSafeEqual(a, b);
}

function trackIdentity(track: unknown): string | null {
  if (!track || typeof track !== "object") return null;
  const t = track as Record<string, unknown>;
  return JSON.stringify([t.title ?? null, t.artist ?? null]);
}

function withAge(state: StateMessage, ageMs: number): string {
  return JSON.stringify({ ...state, age: Math.max(0, Math.round(ageMs)) });
}

function safeSend(ws: WebSocket, data: string): void {
  try {
    ws.send(data);
  } catch {
    // The socket is already closing; its close handler cleans up.
  }
}

function safeClose(ws: WebSocket, code: number, reason: string): void {
  try {
    ws.close(code, reason);
  } catch {
    // Already closed.
  }
}

export class Room extends DurableObject<Env> {
  // `undefined` means "not loaded from storage yet" (fresh or after hibernation).
  private meta: Meta | null | undefined;
  private cached: Cached | null | undefined;
  /** The copy last written to storage; heartbeats that change nothing else are not written. */
  private persisted: Cached | null | undefined;
  /** Receive times of the host's recent messages, for the 5-per-second limit. */
  private hostMessageTimes: number[] = [];

  private async loadMeta(): Promise<Meta | null> {
    if (this.meta === undefined) this.meta = (await this.ctx.storage.get<Meta>("meta")) ?? null;
    return this.meta;
  }

  private async loadCached(): Promise<Cached | null> {
    if (this.cached === undefined) {
      this.cached = (await this.ctx.storage.get<Cached>("cached")) ?? null;
      this.persisted = this.cached;
    }
    return this.cached;
  }

  private openSockets(tag: string, except?: WebSocket): WebSocket[] {
    return this.ctx.getWebSockets(tag).filter((ws) => ws !== except && ws.readyState === OPEN);
  }

  private notifyListenerCount(except?: WebSocket): void {
    const count = this.openSockets(LISTENER_TAG, except).length;
    const message = JSON.stringify({ type: "listeners", count });
    for (const host of this.openSockets(HOST_TAG, except)) safeSend(host, message);
  }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    switch (url.pathname) {
      case "/init":
        return this.init(request);
      case "/state":
        return this.getState();
      case "/host":
        return this.connectHost(url.searchParams.get("key") ?? "");
      case "/listen":
        return this.connectListener();
      default:
        return new Response("not found", { status: 404 });
    }
  }

  private async init(request: Request): Promise<Response> {
    if (await this.loadMeta()) return new Response("exists", { status: 409 });
    const { hostKey } = await request.json<{ hostKey: string }>();
    this.meta = { hostKey, createdAt: Date.now() };
    this.cached = null;
    this.persisted = null;
    await this.ctx.storage.put("meta", this.meta);
    // A room whose host never connects expires like one whose host left.
    await this.ctx.storage.setAlarm(Date.now() + ROOM_EXPIRY_MS);
    return new Response("created", { status: 201 });
  }

  private async getState(): Promise<Response> {
    const headers = { "Content-Type": "application/json", "Cache-Control": "no-store" };
    if (!(await this.loadMeta())) {
      return new Response(JSON.stringify({ error: "unknown_room" }), { status: 404, headers });
    }
    const cached = await this.loadCached();
    if (!cached) return new Response(JSON.stringify({ error: "no_state" }), { status: 404, headers });
    return new Response(withAge(cached.state, Date.now() - cached.receivedAt), { headers });
  }

  private async connectHost(key: string): Promise<Response> {
    const meta = await this.loadMeta();
    if (!meta) return rejectSocket(CLOSE_UNKNOWN_ROOM, "unknown room");
    if (!keysMatch(key, meta.hostKey)) return rejectSocket(CLOSE_BAD_KEY, "bad host key");

    const previous = this.openSockets(HOST_TAG);
    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], [HOST_TAG]);
    for (const old of previous) safeClose(old, CLOSE_REPLACED, "replaced by a newer host connection");

    await this.ctx.storage.deleteAlarm();
    this.hostMessageTimes = [];
    safeSend(pair[1], JSON.stringify({ type: "listeners", count: this.openSockets(LISTENER_TAG).length }));
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  private async connectListener(): Promise<Response> {
    if (!(await this.loadMeta())) return rejectSocket(CLOSE_UNKNOWN_ROOM, "unknown room");
    if (this.openSockets(LISTENER_TAG).length >= MAX_LISTENERS) return rejectSocket(CLOSE_ROOM_FULL, "room full");

    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], [LISTENER_TAG]);
    const cached = await this.loadCached();
    if (cached) safeSend(pair[1], withAge(cached.state, Date.now() - cached.receivedAt));
    this.notifyListenerCount();
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    // Listeners have nothing to say. Each message would wake (and bill) this
    // object, so a listener that sends anything is disconnected at once.
    if (!this.ctx.getTags(ws).includes(HOST_TAG)) {
      safeClose(ws, CLOSE_LISTENER_SENT, "listeners may not send");
      this.notifyListenerCount(ws);
      return;
    }
    if (byteLength(message) > MAX_MESSAGE_BYTES) return;

    let parsed: unknown;
    try {
      parsed = JSON.parse(typeof message === "string" ? message : new TextDecoder().decode(message));
    } catch {
      return;
    }
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return;
    const type = (parsed as { type?: unknown }).type;

    if (type === "end") {
      await this.endRoom();
      return;
    }

    // At most 5 messages in any one-second window; the excess is dropped.
    const now = Date.now();
    this.hostMessageTimes = this.hostMessageTimes.filter((t) => now - t < 1000);
    if (this.hostMessageTimes.length >= HOST_MESSAGES_PER_SECOND) return;
    this.hostMessageTimes.push(now);

    if (type !== "state") return;
    if (!(await this.loadMeta())) return;
    await this.acceptState(parsed as StateMessage, now);
  }

  private async acceptState(incoming: StateMessage, now: number): Promise<void> {
    const live: StateMessage = { ...incoming };
    delete live.age;

    // Lyrics come only with a track's first state. Keep them for listeners who
    // join later, who only ever get the cached state.
    const previous = await this.loadCached();
    const merged: StateMessage = { ...live };
    if (
      !Array.isArray(merged.lyrics) &&
      previous &&
      Array.isArray(previous.state.lyrics) &&
      trackIdentity(merged.track) !== null &&
      trackIdentity(merged.track) === trackIdentity(previous.state.track)
    ) {
      merged.lyrics = previous.state.lyrics;
    }

    const next: Cached = { state: merged, receivedAt: now };
    this.cached = next;
    if (this.needsPersist(next)) {
      this.persisted = next;
      await this.ctx.storage.put("cached", next);
    }

    const forward = withAge(live, 0);
    for (const listener of this.openSockets(LISTENER_TAG)) safeSend(listener, forward);
  }

  /**
   * Writes are kept to real changes (track, play/pause, lyrics, a seek) so a
   * room's 10-second heartbeats don't spend the free plan's daily write quota.
   * A heartbeat is fully described by the last written state plus elapsed time.
   */
  private needsPersist(next: Cached): boolean {
    const last = this.persisted;
    if (!last) return true;
    const strip = (s: StateMessage) => JSON.stringify({ ...s, seq: null, position: null });
    if (strip(last.state) !== strip(next.state)) return true;
    const lastPos = Number(last.state.position) || 0;
    const nextPos = Number(next.state.position) || 0;
    const elapsed = last.state.playing === true ? (next.receivedAt - last.receivedAt) / 1000 : 0;
    return Math.abs(lastPos + elapsed - nextPos) > PERSIST_POSITION_SLACK_S;
  }

  async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    // Complete the closing handshake (codes 1005/1006 can't be sent back).
    safeClose(ws, code === 1005 || code === 1006 ? 1000 : code, reason);
    await this.socketGone(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.socketGone(ws);
  }

  private async socketGone(ws: WebSocket): Promise<void> {
    const tags = this.ctx.getTags(ws);
    if (tags.includes(LISTENER_TAG)) {
      this.notifyListenerCount(ws);
      return;
    }
    if (tags.includes(HOST_TAG)) {
      if (!(await this.loadMeta())) return;
      if (this.openSockets(HOST_TAG, ws).length === 0) {
        await this.ctx.storage.setAlarm(Date.now() + ROOM_EXPIRY_MS);
      }
    }
  }

  async alarm(): Promise<void> {
    if (!(await this.loadMeta())) return;
    // The host came back in time; its connect cancelled the alarm, but be safe.
    if (this.openSockets(HOST_TAG).length > 0) return;
    await this.endRoom();
  }

  private async endRoom(): Promise<void> {
    const ended = JSON.stringify({ type: "ended" });
    for (const ws of this.ctx.getWebSockets()) {
      safeSend(ws, ended);
      safeClose(ws, 1000, "ended");
    }
    this.meta = null;
    this.cached = null;
    this.persisted = null;
    this.hostMessageTimes = [];
    await this.ctx.storage.deleteAlarm();
    await this.ctx.storage.deleteAll();
  }
}
