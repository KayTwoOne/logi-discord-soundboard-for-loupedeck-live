# Discord Soundboard for Loupedeck Live

Puts your entire Discord soundboard — default sounds and every server's sounds — on
Loupedeck Live buttons. One press plays the sound in your current voice channel, as you,
through the Discord desktop client itself. No VoiceMod, no virtual audio cables, no bots.

See [DESIGN.md](DESIGN.md) for how it works under the hood (Discord client RPC over the
local IPC pipe — the same sanctioned mechanism the official Discord plugin uses for
mute/deafen).

## What you get

- **Soundboard folder** — a paged grid of every indexed sound on the device's touch screen,
  colour-coded per server, with a Refresh tile. Unavailable (Nitro-gated) sounds appear dimmed.
- **Play Sound actions** — every sound is also an individually assignable action, grouped by
  server name in the Loupedeck software, so you can pin favourites to any button in any profile.
- **Soundboard Control actions** — Refresh Sounds, Reconnect to Discord, Re-authorize Discord.
- Sounds are cached on disk, so buttons render immediately after a reboot.

## Requirements

- Windows with the Loupedeck 6.x / Logi Plugin Service software (Loupedeck Live, Live S, CT,
  or Razer Stream Controller).
- Discord **desktop** app, logged in.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build.
- A (free) Discord application of your own — 2 minutes to create, steps below.

## Setup

### 1. Build and install the plugin

```powershell
dotnet build DiscordSoundboardPlugin.sln -c Debug
```

The build drops a `DiscordSoundboardPlugin.link` dev link into
`%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\` and asks the service to (re)load the
plugin. You should see **Discord Soundboard** appear in the Loupedeck software.

### 2. Create your Discord application

Discord only honours the RPC scopes this plugin needs for applications *you own*, so each
user brings their own app id (a public release would need Discord's RPC approval, like the
official plugin has):

1. Go to <https://discord.com/developers/applications> → **New Application** → name it
   anything (e.g. `Loupedeck Soundboard`).
2. Open the **OAuth2** tab. Copy the **Client ID** and reset/copy the **Client Secret**.
3. Still on the OAuth2 tab, under **Redirects** add `http://127.0.0.1` and save.
   Nothing is ever opened at that address — Discord refuses the authorize step
   ("Missing redirect_uri") unless the app has a redirect registered, yet also rejects
   requests that actually *send* one, so the plugin never transmits it. Any URL works;
   it just has to exist.

### 3. Configure the plugin

Edit `%LOCALAPPDATA%\Logi\LogiPluginService\PluginData\DiscordSoundboard\config.json`
(the plugin creates the template on first run):

```json
{
  "client_id": "your application id",
  "client_secret": "your client secret",
  "favorite_sound_ids": []
}
```

Within ~10 seconds the plugin connects and **Discord pops an authorization dialog** — click
**Authorize**. That's a one-time step; the token is cached and refreshed automatically.

### 4. Play

Join a voice channel, open the Soundboard folder on the device (or assign individual
sounds), and press.

## Customization

All options live in the same `config.json` and apply on the next redraw / folder open —
no reconnect needed. Sound ids come from `sounds.json` next to the config.

| Key | Default | What it does |
| --- | --- | --- |
| `favorite_sound_ids` | `[]` | Pins these sounds to the front of the folder, and feeds "Random: Favourite Sound". |
| `excluded_guild_ids` | `[]` | Hides entire servers from the list (already-assigned buttons keep working). |
| `hide_unavailable` | `false` | Hide Nitro-locked sounds entirely instead of showing them dimmed. |
| `sort_mode` | `"discord"` | `"discord"` keeps Discord's own order with the server you're currently in voice with floated to the front (updates live as you switch channels) and default sounds last; `"server"` groups by server name A–Z; `"name"` is one flat A–Z list. Favourites are always pinned first. |
| `tile_colors` | `{}` | Per-server button colours, e.g. `{ "123456789012345678": "#FF6600", "0": "#3BA55D" }` (`"0"` = Discord's default sounds). |
| `show_emoji` | `true` | Show each sound's emoji on its tile. Custom (uploaded) server emoji are downloaded from Discord's CDN and drawn as real images (cached in the plugin data folder); unicode emoji fall back to a text line. |
| `play_cooldown_ms` | `0` | Ignore presses within this many ms of the last play — absorbs accidental double-taps (try `750`). |

There is also a **Play Random Sound** action with two variants: any sound, or favourites
only.

Tiles flash **green** when a sound plays and **red** when it fails (not in a voice
channel, disconnected, etc.), so you get instant feedback without looking at Discord.

## Distribution (.lplug4)

```powershell
powershell -ExecutionPolicy Bypass -File package.ps1
```

This produces `bin\DiscordSoundboard.lplug4` — a self-contained installable package.
Anyone can install it by double-clicking it (or via the Loupedeck/Logi software's
plugin installer), no build tools needed. They still create their own free Discord
application (see Setup), which keeps every user's credentials their own.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Plugin status error about `config.json` | Fill in client id/secret (step 3). |
| "Approve the authorization popup in Discord" forever | The dialog is in the Discord app window; approve it, or press **Re-authorize Discord**. |
| `OAuth2 Error: invalid_request: Missing "redirect_uri"` in the log | Add `http://127.0.0.1` under **OAuth2 → Redirects** in your Discord application. |
| Authorization popup reappears | A declined/failed authorize retries after 90 s. Fix the cause (see log), or press **Re-authorize Discord** to retry immediately. |
| Button press does nothing | You must be in a voice channel, not server-muted. Check the log below. |
| Sound tile is dimmed | Discord reports it unavailable to you (usually a Nitro restriction on cross-server sounds). |
| Sounds out of date | Press the **Refresh** tile or the **Refresh Sounds** action. |

Plugin log: `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\DiscordSoundboard.log`

## Development notes

- `dotnet build` is the whole dev loop: it rebuilds, updates the `.link`, and reloads the
  plugin in the running service.
- The loader silently requires a `ClientApplication` subclass in the plugin assembly — even
  for universal plugins (`HasNoApplication => true`). Remove
  [DiscordSoundboardApplication.cs](src/DiscordSoundboardPlugin/DiscordSoundboardApplication.cs)
  and you get an unexplained `Cannot load plugin` in the log. Ask me how I know.
- `dotnet clean` removes the dev link and unloads the plugin.
- **Secrets are encrypted at rest with Windows DPAPI** (CurrentUser scope). You paste
  `client_secret` in plaintext once; on first load the plugin rewrites it as
  `client_secret_protected` and blanks the plaintext field. OAuth tokens are stored only as
  an encrypted `token.bin`. The blobs are useless on any other machine or Windows account.
  The decrypted secret exists only in plugin memory while running. (On non-Windows hosts
  DPAPI is unavailable and storage falls back to plaintext.)
- The sound cache (`sounds.json`) is not sensitive and stays readable.
