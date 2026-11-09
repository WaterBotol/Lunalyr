# Lunalyr listen-along relay protocol, v1

Shared contract between the macOS app (`next/listen-app`) and the relay + web page (`next/listen-relay`). Neither side may change it alone; if a change is needed, note it in your final report and keep both sides compatible with what is written here.

## Relay

A Cloudflare Worker with one Durable Object per room. Base URL is a Lunalyr setting (`listenRelayURL`), EMPTY by default until the user deploys the relay. With no URL, every listen-along UI is hidden and nothing connects.

| Request | Who | Result |
|---|---|---|
| `POST /rooms` | host | `{"room":"<10 chars [a-z0-9]>","hostKey":"<32 hex>"}`, or 429 `{"error":"rate_limited"}` with `Retry-After: 60` |
| `GET /rooms/<room>/host?key=<hostKey>` (WebSocket) | host | publishes state |
| `GET /rooms/<room>/listen` (WebSocket) | Lunalyr listener or web page | gets the latest state at once, then every update |
| `GET /rooms/<room>/state` | anyone with the room ID | latest state JSON (with `Access-Control-Allow-Origin: *`), or 404 |
| `GET /listen/<room>` | browser | the web listen-along page |
| `GET /health` | anyone | `ok` |

- A room ends 10 minutes after the host socket closes without reconnecting, or at once when the host sends `{"type":"end"}`. Listeners then get `{"type":"ended"}` and are closed.
- Limits: 50 listeners per room, messages up to 64 KB, host at most 5 messages per second (excess dropped, not fatal). Unknown room: 404 / socket closed with code 4404.
- Room creation is rate-limited: at most 5 `POST /rooms` per minute per client (IPv4 address or IPv6 /64). Over that, the relay answers 429 `{"error":"rate_limited"}` with `Retry-After: 60`; the app waits that long before trying again.
- Listeners only receive. A listener socket that sends any message is closed with code 4400.
- Other close codes: 4403 the host key is wrong (the app makes a new room), 4429 the room is full (the app says so and does not retry).
- The relay sends the host `{"type":"listeners","count":N}` whenever the count changes.
- No accounts, no logging of song data beyond the room's latest state.
- **The latest state is public to anyone who has the room ID**: every field below, including the host's display name and the lyric text, can be read from `/rooms/<room>/state`, the listen socket and the web page. The app's Share settings say so.

## State message (host -> relay -> listeners)

```json
{
  "type": "state", "v": 1, "seq": 42,
  "track": {
    "title": "Blinding Lights", "artist": "The Weeknd", "album": "After Hours",
    "duration": 200.0,
    "spotifyId": "0VjIjW4GlUZAMYd2vXMi3b",
    "appleId": 1499378607,
    "isrc": "USUG11904206"
  },
  "playing": true,
  "position": 63.2,
  "lyrics": [{"start": 12.1, "end": 15.0, "text": "..."}],
  "host": {"name": "optional display name"}
}
```

- `track` is null when nothing plays. `spotifyId`, `appleId`, `isrc`, `album`, `lyrics`, `host` are optional.
- `appleId` is sent only when the host already has it (from the Discord cover or song-button search); the app never asks Apple just to fill it.
- `host.name` is the host's Discord display name, sent only while their Discord presence is on (at most 32 characters). Display only: it is not verified.
- `position` is seconds into the song at the moment the host sent it.
- `lyrics` is the lyric lines **with their text** and line timing (no word timing). It is sent only while the host has "Share lyrics with listeners" on (the default), with the first state of each track, and may be omitted after; listeners keep the last lyrics for the same track. With it off, `lyrics` is never sent and listeners show just the song.
- The host sends a state on every change (track, play/pause, seek of more than 1.5 s) and a heartbeat every 10 s.
- When forwarding, the relay adds `"age": <ms>`: how long it has held this state (0 for a live forward, more for the cached state a new listener receives). Listeners compute the current position as `position + age/1000 + (seconds since they received it)` while `playing`. No clock sync needed.

## Discord

- The host's activity gets `party: {id: <room>, size: [listeners+1, 51]}` and `secrets: {join: "<room>|<display name>"}` (or just `<room>` with no name) only while the user has turned listen-along sharing on and chosen the Join button. The relay never sees the secret.
- A friend who clicks Join gets `ACTIVITY_JOIN` with the secret in their Lunalyr, which asks "Listen along with <name>?" and only after they accept connects to `/rooms/<room>/listen` on its own relay URL. A code typed in the menu is asked about the same way.
