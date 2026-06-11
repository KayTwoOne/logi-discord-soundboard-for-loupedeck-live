# Discord Soundboard for Loupedeck — The Plain-English Guide

*No tech knowledge needed. Five-minute read.*

## What is this?

It puts your Discord soundboard onto the buttons of your Loupedeck Live.

Normally, playing a soundboard sound means clicking around in Discord mid-conversation.
With this plugin, every sound you have access to — Discord's built-in ones and the ones
from every server you're in — shows up on your Loupedeck. Press a button, the sound plays
in your voice chat. That's it.

## How does it actually work? (the honest, simple version)

Think of the plugin as a **remote control for the Discord app that's already on your PC**.

When you press a button on the Loupedeck, the plugin doesn't log into Discord, doesn't
pretend to be you on the internet, and doesn't mess with your microphone. It simply sends
a message to the Discord app running on your computer that says: *"play this soundboard
sound, please"* — the same as if you'd clicked the sound in Discord yourself.

Discord apps officially support this kind of "remote control" — it's the same system the
official Discord plugins for Stream Deck use. Because Discord itself does the playing:

- The sound plays **as you**, in whatever voice channel you're sitting in.
- Discord's normal rules still apply. If a sound needs Nitro and you don't have it,
  it won't play here either (those show up dimmed on the Loupedeck).
- If Discord isn't running, the buttons simply wait until it is.

## The one slightly fiddly setup step (and why it exists)

Discord requires any "remote control" to introduce itself with an ID badge. Big companies
get a shared badge from Discord. For personal use, you make your own — it's free, takes
about two minutes, and doesn't change anything about your Discord account:

1. Go to **discord.com/developers/applications** and log in (it's official Discord, just
   the side of it where these ID badges are made).
2. Click **New Application**, name it anything ("My Soundboard Remote" is fine).
3. On the **OAuth2** page you'll see two codes: a **Client ID** and a **Client Secret**.
   Copy both into the plugin's settings file (the README shows exactly where — it's a
   small text file the plugin creates for you, with two blanks to fill in).
   On that same OAuth2 page, find the **Redirects** box, add `http://127.0.0.1` and
   save — it's a formality Discord insists on; nothing ever uses that address.
4. A minute later, Discord itself pops up a window asking *"allow this app to control
   voice things?"* — click **Authorize**, once, and you're done forever.

Treat the Client Secret like a password: it's the key to *your* badge, so don't post it
anywhere. The plugin locks it away for you (next section).

## Is this safe? Will I get banned?

Short answers: **yes, it's safe**, and **no, this isn't bannable behaviour**.

- **It never touches your Discord password or login token.** The plugin literally has no
  way to see them.
- **It can't read your messages, send messages, or do anything outside of voice.** The
  only permission it ever asks Discord for is "control voice features locally".
- **It's not a "self-bot" or automation trick.** It uses the official, supported
  remote-control channel — the same one big-brand stream decks use.
- **Your codes are stored encrypted.** The moment the plugin reads your Client Secret, it
  locks it with Windows' built-in encryption, tied to your Windows account. Even if
  someone copied the file to another computer, it would be unreadable gibberish to them.
- **Nothing is sent anywhere** except to Discord itself. There's no server of ours, no
  account, no analytics, nothing in the cloud.

Worst-case scenarios, honestly stated: if someone had full control of your PC they could
misuse the plugin's stored codes — but someone with full control of your PC could already
do far worse directly. And if you ever think your Client Secret leaked, the Discord
website has a "Reset Secret" button that instantly makes the old one worthless.

## Everyday use

- **The Soundboard folder**: open it on the Loupedeck's screen and flip through pages of
  all your sounds. Each server gets its own button colour so you can tell them apart at
  a glance. There's a Refresh button if you've added new sounds in Discord.
- **Favourite buttons**: any individual sound can also be placed directly onto any button
  in any of your Loupedeck profiles — perfect for the five sounds you actually use.
- **Greyed-out buttons** mean Discord says that sound isn't available to you right now
  (usually a Nitro thing). They're still shown so you know they exist.

## If something doesn't work

| What you see | What it means | What to do |
| --- | --- | --- |
| Plugin shows an error about "config" | It's waiting for your two codes | Do the two-minute setup above |
| "Approve the popup in Discord" | Discord is asking for permission | Bring up the Discord window and click Authorize |
| Button press does nothing | You're not in a voice channel (or you're server-muted) | Join a voice channel and try again |
| A sound is greyed out | Discord says you can't use it (usually Nitro) | Nothing to fix — that's Discord's rule |
| Sounds are outdated | The list is cached | Press the Refresh button in the folder |

## One-sentence summary

It's a polite remote control for your own Discord app: your sounds on real buttons,
nothing creepy, nothing stored unprotected, and nothing your account could get in
trouble for.
