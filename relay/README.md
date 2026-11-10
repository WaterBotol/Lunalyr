# Lunalyr listen-along relay

This folder holds the small server behind listen-along. When you share what you're playing, Lunalyr sends the song, its position and its lyric timing to this relay, and the relay passes it on to everyone listening along. Friends can listen in their own Lunalyr, through Discord's Join button, or on a web page at `https://<your relay>/listen/<room>`.

It runs on [Cloudflare Workers](https://developers.cloudflare.com/workers/) with one [Durable Object](https://developers.cloudflare.com/durable-objects/) per room. The free Cloudflare plan is enough, and you need no credit card.

- `src/index.ts`: HTTP routes (`/rooms`, `/rooms/<room>/host|listen|state`, `/listen/<room>`, `/health`)
- `src/room.ts`: the room (WebSocket Hibernation API, limits, expiry)
- `wrangler.toml`: the deploy config, including the room-creation rate limit
- `src/listen.html`: the web listen-along page, served as is (no build step)
- `PROTOCOL.md`: the wire format shared with the macOS app
- `test/`: tests that run in the real Workers runtime (`npm test`)
- `scripts/smoke.mjs`: a quick end-to-end check against a running relay

Lunalyr shows no listen-along controls until you enter a relay URL, so nothing connects anywhere until you have deployed your own relay.

## Deploy your own relay

You do this once. It takes about ten minutes.

### 1. What you need

- A Mac with [Node.js](https://nodejs.org/) 20 or later. In Terminal, `node -v` should print `v20` or higher. If it doesn't, install the LTS version from nodejs.org.
- A free Cloudflare account. Sign up at <https://dash.cloudflare.com/sign-up> and confirm your email address. The free plan is all you need.

### 2. Get the code and install the tools

In Terminal, go to this folder and install its dependencies:

```sh
cd path/to/Lunalyr/relay
npm install
```

This downloads Wrangler, Cloudflare's command-line tool, into this folder only. Nothing is installed system-wide.

### 3. Log in to Cloudflare

```sh
npx wrangler login
```

A browser window opens and asks you to allow Wrangler to access your Cloudflare account. Click **Allow**, then return to Terminal. It should say `Successfully logged in`.

### 4. Deploy

```sh
npx wrangler deploy
```

The first time, Wrangler may ask you to pick a **workers.dev subdomain**. This is a name for your account, such as `tommy`, and it appears in the relay's URL. Choose anything that's free. When the deploy finishes, the output ends with lines like these:

```
Uploaded lunalyr-relay
Deployed lunalyr-relay triggers
  https://lunalyr-relay.tommy.workers.dev
```

That last line is your **relay URL**. You can also find it later in the Cloudflare dashboard: open **Workers & Pages**, click **lunalyr-relay**, and look under **Domains & Routes** (or the URL shown at the top of its overview page).

### 5. Check that it works

Open `https://lunalyr-relay.<your-subdomain>.workers.dev/health` in a browser. It should show `ok`.

For a fuller check, run the smoke test against it:

```sh
node scripts/smoke.mjs https://lunalyr-relay.<your-subdomain>.workers.dev
```

It creates a room, sends a song through it, checks that a listener receives it, then ends the room. The last line should be `All smoke checks passed.`

### 6. Put the URL in Lunalyr

In Lunalyr, open **Settings > Share**, paste the URL (for example `https://lunalyr-relay.tommy.workers.dev`, without a trailing path) into **Relay URL**, and turn listen-along sharing on.

### Updating later

When a new version of the relay comes with Lunalyr, run `npm install` and `npx wrangler deploy` again in this folder. The URL stays the same.

### Removing it

In the Cloudflare dashboard, open **Workers & Pages > lunalyr-relay > Settings** and choose **Delete**. Then clear the Relay URL in Lunalyr.

## Free plan limits

These are the [Workers Free plan](https://developers.cloudflare.com/workers/platform/limits/) and [Durable Objects pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/) limits as of November 2026. They reset every day at 00:00 UTC. When one runs out, the relay stops answering until the reset; there's no surprise bill on the free plan.

| Limit (per day) | Free plan | What uses it here |
|---|---|---|
| Requests | 100,000 | One for each room created, each host or listener connection, each web page load and each `/state` fetch. Incoming WebSocket messages count 20 to 1, so a host's 10-second heartbeat costs about 18 requests an hour. Messages sent **to** listeners are free. |
| Durable Object duration | 13,000 GB-s | Only while a room is handling a message. Rooms use the Hibernation API, so a room that's waiting between messages costs nothing. |
| SQLite rows written | 100,000 | The latest state is saved only when something real changes (a new song, play/pause, a seek, new lyrics), not on every heartbeat. That's a few writes per song. |
| SQLite rows read | 5,000,000 | A few per listener join. |
| Storage | 5 GB total | Each room keeps only its latest state (usually a few kilobytes) and deletes it when the room ends. |

What this means in practice:

- **Listeners per room:** 50 at a time, set by the relay itself (and by Discord's party size). Extra listeners are turned away with "room full" and the web page retries.
- **Cost of listeners:** sending updates to listeners is free, so 50 listeners cost about the same as one. Each listener costs one request when they join, plus one more each time they reconnect.
- **Rough budget:** a host sharing for 8 hours uses about 150 to 200 requests. Each listener adds 1 to 3 (the page load, the connection, an occasional reconnect). 100,000 requests a day covers hundreds of listening sessions with dozens of listeners each, far more than a personal relay sees.
- Other free-plan limits that matter here: Durable Objects must use SQLite storage (this relay does), and a Worker gets 10 ms of CPU per request (routing takes well under 1 ms).

If you ever outgrow it, the Workers Paid plan raises all of these. Nothing in the relay needs to change.

## Abuse limits

Anyone who knows your relay URL can call it, so the relay protects your daily quota itself:

- **Room creation is rate limited.** `POST /rooms` allows 5 new rooms a minute from one client (one IPv4 address, or one IPv6 `/64` network), and answers `429` with `Retry-After: 60` beyond that. A host creates one room each time sharing starts, so this never gets in the way of normal use. It uses Cloudflare's [Workers rate-limiting binding](https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/) (the `[[ratelimits]]` block in `wrangler.toml`), which needs no setup beyond that block. As of November 2026 Cloudflare's docs name no plan restriction for it and it isn't billed separately, so it runs on the free plan. If `wrangler deploy` ever rejects the block, delete it and the relay still works, just without this limit. The address is only hashed into a counter key, never stored or logged. The count is kept separately in each Cloudflare location and is approximate, so it stops one client in a loop, not a determined attacker with many addresses.
- **Listeners can't send.** A listener connection that sends anything is closed (code `4400`), so it can't keep waking the room.
- **Hosts** are limited to 5 messages a second and 64 KB a message, and each room to 50 listeners.

### Optional: a WAF rule as an extra layer

Requests the Worker turns away with `429` still count toward the 100,000 a day. A Cloudflare WAF rate-limiting rule stops them before they reach the Worker, so they cost nothing. This needs the relay on **a domain of your own** that's on Cloudflare; WAF rules don't apply to `*.workers.dev` URLs. If you only use `workers.dev`, skip this: the built-in limit above is enough for a personal relay.

1. In the Cloudflare dashboard, open your domain, then **Security > WAF > Rate limiting rules**, and click **Create rule**.
2. Name it, for example `lunalyr rooms`.
3. Under **If incoming requests match**, choose **Edit expression** and enter:

   ```
   (http.request.uri.path eq "/rooms")
   ```

   On the free plan the expression may only use the path. On a paid plan you can narrow it with `and http.request.method eq "POST"` and your relay's hostname.
4. **With the same characteristics:** IP. **When rate exceeds:** 3 requests per 10 seconds (on the free plan, 10 seconds is the only period).
5. **Then take action:** Block, for 10 seconds (the free plan's only duration; longer on paid plans). Click **Deploy**.

The free plan includes one rate-limiting rule. Keep the relay's own limit in place as well; the two work together.

## Privacy

- No accounts. A room is a random 10-character id, and only the holder of its random 32-character host key can publish to it.
- The relay keeps only the room's latest state (song, position, lyric timing, the optional display name). It's deleted when the host ends the room, or 10 minutes after the host disconnects.
- The relay logs nothing, and `wrangler.toml` turns Cloudflare's Workers Logs off.
- The web page asks Apple's iTunes Search API for the cover. Only after the visitor clicks **Start listening** does it load a player: Spotify's, or Apple Music's embed for a song the host plays from Apple Music. The links to other services are plain links built from the song's IDs or name; nothing is sent to song.link. The page sets `no-referrer`, so those services don't learn the room's URL.

## Development

```sh
npm install
npm test            # vitest in the Workers runtime (Miniflare)
npm run typecheck   # tsc
npx wrangler dev    # local relay on http://localhost:8787
node scripts/smoke.mjs          # against the local relay
```

`npx wrangler dev` runs entirely on your Mac and needs no Cloudflare login. To try the page, run `KEEP_OPEN=1 node scripts/smoke.mjs` and open the `/listen/...` URL it prints.

### Notes on the web page

- Browsers block autoplay, so listening starts with a **Start listening** button. That loads Spotify's [iFrame API](https://developer.spotify.com/documentation/embeds/references/iframe-api) and keeps the embed on the host's song: it follows track changes and play/pause, and seeks when it drifts more than 2 seconds from the host.
- Full songs need a Spotify login in that browser. Without one, Spotify plays 30-second previews; the page detects this, says so, and stops trying to seek.
- When the host sends no `spotifyId` (for example while playing from Apple Music), Spotify's player can't follow. If the song has an Apple ID (sent by the host, or found through the iTunes Search API), the page shows Apple Music's embed instead (`embed.music.apple.com/<storefront>/song/<id>`, the visitor's storefront from their browser language). It plays a preview without sign-in and can't follow the host's position or play state. The page always offers Spotify, Apple Music, YouTube Music and song.link page links too. song.link's API is not used: it refuses keyless requests (`401 PUBLIC_API_ACCESS_DEPRECATED`) as of November 2026.
