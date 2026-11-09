// Smoke test against a running relay (default: `npx wrangler dev` on :8787).
//   node scripts/smoke.mjs [baseURL]
// Creates a room, connects a host and a listener, sends one state, checks the
// listener gets it and a late listener gets the cached copy with an age, then
// ends the room. Needs Node 22+ (global WebSocket).

const base = (process.argv[2] || "http://127.0.0.1:8787").replace(/\/$/, "");
const wsBase = base.replace(/^http/, "ws");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function socket(url) {
  const ws = new WebSocket(url);
  const messages = [];
  const waiters = [];
  ws.addEventListener("message", (ev) => {
    const m = JSON.parse(ev.data);
    messages.push(m);
    for (const w of waiters.splice(0)) w();
  });
  const opened = new Promise((resolve, reject) => {
    ws.addEventListener("open", resolve, { once: true });
    ws.addEventListener("error", reject, { once: true });
  });
  const closed = new Promise((resolve) => ws.addEventListener("close", (ev) => resolve(ev.code), { once: true }));
  async function next(pred, timeout = 3000) {
    const end = Date.now() + timeout;
    for (;;) {
      const hit = messages.find(pred);
      if (hit) return hit;
      if (Date.now() > end) throw new Error("timeout waiting on " + url);
      await Promise.race([new Promise((r) => waiters.push(r)), sleep(100)]);
    }
  }
  return { ws, opened, closed, next, messages };
}

function check(cond, label) {
  if (!cond) throw new Error("FAILED: " + label);
  console.log("ok  " + label);
}

const health = await fetch(base + "/health");
check(health.ok && (await health.text()) === "ok", "GET /health -> ok");

const created = await (await fetch(base + "/rooms", { method: "POST" })).json();
check(/^[a-z0-9]{10}$/.test(created.room) && /^[0-9a-f]{32}$/.test(created.hostKey), `POST /rooms -> room ${created.room}`);

const bad = socket(`${wsBase}/rooms/${created.room}/host?key=${"0".repeat(32)}`);
check((await bad.closed) === 4403, "wrong host key closes with 4403");

const host = socket(`${wsBase}/rooms/${created.room}/host?key=${created.hostKey}`);
await host.opened;
check((await host.next((m) => m.type === "listeners")).count === 0, "host connected, 0 listeners");

const listener = socket(`${wsBase}/rooms/${created.room}/listen`);
await listener.opened;
check((await host.next((m) => m.type === "listeners" && m.count === 1)).count === 1, "host told 1 listener");

const state = {
  type: "state", v: 1, seq: 1,
  track: { title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200, spotifyId: "0VjIjW4GlUZAMYd2vXMi3b", appleId: 1499378607 },
  playing: true, position: 63.2,
  lyrics: [{ start: 60, end: 64, text: "I said, ooh, I'm blinded by the lights" }],
  host: { name: "Smoke test" },
};
host.ws.send(JSON.stringify(state));
const live = await listener.next((m) => m.type === "state");
check(live.seq === 1 && live.age === 0 && live.track.title === "Blinding Lights", "listener got live state with age 0");

await sleep(400);
host.ws.send(JSON.stringify({ ...state, seq: 2, position: 63.6, lyrics: undefined }));
await listener.next((m) => m.seq === 2);
await sleep(300);
const late = socket(`${wsBase}/rooms/${created.room}/listen`);
const cached = await late.next((m) => m.type === "state");
check(cached.seq === 2 && cached.age >= 250 && Array.isArray(cached.lyrics), `late listener got cached state, age ${cached.age} ms, lyrics kept`);

const st = await fetch(`${base}/rooms/${created.room}/state`);
check(st.status === 200 && st.headers.get("access-control-allow-origin") === "*", "GET /state -> 200 with CORS");

const page = await fetch(`${base}/listen/${created.room}`);
const html = await page.text();
check(page.status === 200 && html.includes("Start listening"), `GET /listen/${created.room} -> page (${html.length} bytes)`);

console.log(`\nPage for a browser: ${base}/listen/${created.room}`);
if (process.env.KEEP_OPEN) {
  console.log("KEEP_OPEN set: host stays connected; Ctrl-C to stop.");
  let seq = 3;
  const started = Date.now();
  setInterval(() => {
    host.ws.send(JSON.stringify({ ...state, seq: seq++, position: 63.2 + (Date.now() - started) / 1000, lyrics: undefined }));
  }, 10000);
} else {
  host.ws.send(JSON.stringify({ type: "end" }));
  check((await listener.next((m) => m.type === "ended")).type === "ended", "end -> listeners get {type: ended}");
  check((await listener.closed) === 1000, "listener closed after end");
  const gone = await fetch(`${base}/rooms/${created.room}/state`);
  check(gone.status === 404, "ended room's /state -> 404");
  console.log("\nAll smoke checks passed.");
  process.exit(0);
}
