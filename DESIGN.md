# Discord Soundboard Plugin for Loupedeck Live — Design

A Loupedeck plugin that indexes the Discord soundboard sounds available to *you* (default
sounds + every server's sounds) and puts them on Loupedeck Live buttons, so playing a sound
is one physical button press — no VoiceMod, no audio routing, no virtual microphone.

## The key insight

The official Discord plugin for Loupedeck (mute/deafen/channel switching) does not talk to
Discord's web API at all — it talks to the **Discord desktop client** over a local IPC named
pipe using Discord's RPC protocol. The same protocol turns out to expose exactly the two
commands this project needs, even though they're absent from Discord's official docs:

| RPC command            | What it does                                  | Scopes               |
| ---------------------- | --------------------------------------------- | -------------------- |
| `GET_SOUNDBOARD_SOUNDS`| Returns every soundboard sound the logged-in user can see (default sounds report `guild_id: 0`) | `rpc` |
| `PLAY_SOUNDBOARD_SOUND`| Plays a sound in your current voice channel — args `{ guild_id?, sound_id }` | `rpc` + `rpc.voice.write` |

Because the *client* performs the action, this approach:

- plays the sound **as you**, in whatever voice channel you're in;
- inherits Discord's own Nitro gating (cross-server sounds work exactly when they'd work in
  the app — we index everything and mark what's unavailable);
- never touches your user token, never scrapes, never self-bots. It is the same mechanism
  Discord sanctions for the official Loupedeck/Stream Deck plugins.

## Architecture

```
┌──────────────────────────── Logi Plugin Service (.NET 8 host) ───────────────────────────┐
│                                                                                          │
│  DiscordSoundboardPlugin (Loupedeck.Plugin)                                              │
│   ├── Actions/                                                                           │
│   │    ├── SoundboardFolder      PluginDynamicFolder — browse ALL sounds on the device   │
│   │    ├── PlaySoundCommand      PluginDynamicCommand — one assignable action per sound  │
│   │    └── ReconnectCommand      manual reconnect / re-auth button                       │
│   │                                                                                      │
│   └── Discord/DiscordSoundboardService          (background worker, owns the connection) │
│        ├── DiscordRpcClient       frame codec, handshake, nonce↔response correlation     │
│        │     └── NamedPipeClientStream  \\.\pipe\discord-ipc-{0..9}                      │
│        ├── OAuth helper           code → token exchange, refresh (discord.com/api)       │
│        └── JSON persistence       config.json / token.json / sounds.json cache           │
└──────────────────────────────────────────────────────────────────────────────────────────┘
                       │  IPC frames: [op:int32 LE][len:int32 LE][utf8 json]
                       ▼
              Discord desktop client  ──────►  your current voice channel
```

### IPC protocol (verified against userdoccers + reference implementations)

- Pipe: `\\.\pipe\discord-ipc-N`, N = 0–9 (first one that accepts wins).
- Frame: 8-byte header — two little-endian `int32`s (opcode, payload length) — then UTF-8 JSON.
- Opcodes: `0` HANDSHAKE, `1` FRAME, `2` CLOSE, `3` PING, `4` PONG.
- Handshake payload: `{"v":1,"client_id":"<your app id>"}` → server replies with a
  `DISPATCH`/`READY` frame.
- Commands: `{"cmd":"...","args":{...},"nonce":"<guid>"}`; the response carries the same
  `nonce`, errors arrive as `"evt":"ERROR"` with `data.code`/`data.message`.

### Auth flow

1. `AUTHORIZE` over the pipe with scopes `["rpc","rpc.voice.write"]` → Discord pops an
   approval modal **inside the Discord app** → returns a one-time `code`.
2. Exchange the code at `POST https://discord.com/api/v10/oauth2/token`
   (`grant_type=authorization_code`, client id + secret; no redirect URI needed for RPC).
3. `AUTHENTICATE` over the pipe with the access token. Tokens are cached in `token.json`
   and silently refreshed (`grant_type=refresh_token`), so the modal appears once.

**The catch:** the `rpc` scope is only honoured for applications you own (Discord whitelists
RPC for third parties). So each user of this plugin creates their *own* (free) application at
discord.com/developers, and gives the plugin its client id + secret via `config.json`. For a
public release we'd apply to Discord for RPC approval, exactly like the official plugin did.

### Sound indexing

- `GET_SOUNDBOARD_SOUNDS` returns sound objects: `sound_id`, `name`, `guild_id`,
  `emoji_name`, `volume`, `available`.
- `GET_GUILDS` (also `rpc` scope) maps `guild_id → guild name` so sounds group by server
  name in the Loupedeck assignment UI instead of raw snowflakes.
- Default sounds (`guild_id == 0`) are grouped as "Discord Default".
- `available == false` (e.g. lost Nitro, sound deleted) → still indexed, rendered dimmed.
- Results are cached to `sounds.json` so buttons render immediately on boot, before the
  Discord connection is up.
- Re-indexing: automatic on every (re)connect, manual via the Refresh tile / command.

### Button UX

- **Soundboard folder**: opens to a paged grid of all sounds (SDK `ButtonArea` navigation
  paginates for free on the Live's 4×3 touch grid). First tile is Refresh.
- **Per-sound actions**: every sound is also a parameter of `PlaySoundCommand`, so users can
  drag individual sounds onto any button in any profile — that's the "assign your favourites"
  workflow.
- Tiles are colour-coded per server (stable hash of guild id → palette), Discord blurple for
  default sounds, dimmed grey for unavailable ones.

### Failure handling

- Discord not running → status `Warning`, retry every 5 s.
- Pipe closes (Discord quit/restart) → reconnect loop with the cached token.
- No `config.json` → status `Error` with instructions; everything else stays inert.
- Not in a voice channel → `PLAY_SOUNDBOARD_SOUND` errors; logged, button flashes name only.

## Alternatives considered (and why they lost)

| Approach | Verdict |
| --- | --- |
| **Bot account** (`POST /channels/{id}/send-soundboard-sound`) | ToS-clean but the bot must join your voice channel and the sound plays *as the bot*; needs the bot invited to every server; can't see your default-sound entitlements. Good fallback, wrong feel. |
| **User-token REST calls** | Works, but is self-botting — against Discord ToS, account-ban risk. Rejected. |
| **UI automation of the Discord window** | Fragile, focus-stealing, breaks on every redesign. Rejected. |
| **Client RPC (chosen)** | Native, sanctioned mechanism; user-scoped; the only cost is the one-time developer-app setup. |

## Known limitations / roadmap

- **Starred favourites**: Discord doesn't expose your starred sounds over RPC, so v1 ships a
  plugin-side favourites list (`favorite_sound_ids` in `config.json`, pinned first). Usage-
  frequency sorting is a natural follow-up.
- **Emoji on tiles**: sound emoji arrive as unicode/custom-emoji ids; tile rendering of
  emoji glyphs is a polish item.
- **Dial support**: a dial that scrolls/pages the folder, or per-sound volume preview.
- **Distribution**: publishing to the Logi Marketplace requires Discord RPC approval for a
  shared application id.
