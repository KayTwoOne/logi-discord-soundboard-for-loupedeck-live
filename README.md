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
   No redirect URI is needed.

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
sounds), and press. `favorite_sound_ids` (sound ids from `sounds.json` next to the config)
pins sounds to the front of the folder.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Plugin status error about `config.json` | Fill in client id/secret (step 3). |
| "Approve the authorization popup in Discord" forever | The dialog is in the Discord app window; approve it, or press **Re-authorize Discord**. |
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
- Tokens (`token.json`) and the sound cache (`sounds.json`) live next to `config.json`.
  The client secret and tokens are stored in plain text in your user profile — treat that
  folder accordingly.
