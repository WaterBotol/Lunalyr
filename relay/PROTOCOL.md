# Lunalyr listen-along relay protocol, v1

Shared contract between the macOS app (`next/listen-app`) and the relay + web page (`next/listen-relay`). Neither side may change it alone; if a change is needed, note it in your final report and keep both sides compatible with what is written here.

## Relay

A Cloudflare Worker with one Durable Object per room. Base URL is a Lunalyr setting (`listenRelayURL`), EMPTY by default until the user deploys the relay. With no URL, every listen-along UI is hidden and nothing connects.

| Request | Who | Result |
|---|---|---|
| `POST /rooms` | host | `{"room":"<10 chars [a-z0-9]>","hostKey":"<32 hex>"}` |
| `GET /rooms/<room>/host?key=<hostKey>` (WebSocket) | host | publishes state |
| `GET /rooms/<room>/listen` (WebSocket) | Lunalyr listener or web page | gets the latest state at once, then every update |
| `GET /rooms/<room>/state` | anyone | latest state JSON, or 404 |
| `GET /listen/<room>` | browser | the web listen-along page |
| `GET /health` | anyone | `ok` |

- A room ends 10 minutes after the host socket closes without reconnecting, or at once when the host sends `{"type":"end"}`. Listeners then get `{"type":"ended"}` and are closed.
- Limits: 50 listeners per room, messages up to 64 KB, host at most 5 messages per second (excess dropped, not fatal). Unknown room: 404 / socket closed with code 4404.
- The relay sends the host `{"type":"listeners","count":N}` whenever the count changes.
- No accounts, no logging of song data beyond the room's in-memory latest state.

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
- `position` is seconds into the song at the moment the host sent it.
- `lyrics` (line timing only) is sent with the first state of each track and may be omitted after; listeners keep the last lyrics for the same track.
- The host sends a state on every change (track, play/pause, seek of more than 1.5 s) and a heartbeat every 10 s.
- When forwarding, the relay adds `"age": <ms>`: how long it has held this state (0 for a live forward, more for the cached state a new listener receives). Listeners compute the current position as `position + age/1000 + (seconds since they received it)` while `playing`. No clock sync needed.

## Discord

- The host's activity gets `party: {id: <room>, size: [listeners+1, 51]}` and `secrets: {join: <room>}` only while the user has turned listen-along sharing on.
- A friend who clicks Join gets `ACTIVITY_JOIN` with the secret in their Lunalyr, which connects to `/rooms/<room>/listen` on its own relay URL.
