namespace Loupedeck.DiscordSoundboardPlugin.Discord
{
    using System;

    // One soundboard sound as reported by the Discord client over RPC.
    // Default (Discord-provided) sounds come back with guild_id 0.

    internal sealed class SoundboardSound
    {
        public String SoundId { get; set; }

        public String Name { get; set; }

        public String GuildId { get; set; }

        // Resolved via GET_GUILDS; null when the guild name is unknown.
        public String GuildName { get; set; }

        public String EmojiName { get; set; }

        // False when Discord reports the sound as unusable for this user
        // (e.g. a Nitro-gated external sound after Nitro lapsed).
        public Boolean Available { get; set; } = true;

        public Boolean IsDefault => String.IsNullOrEmpty(this.GuildId) || this.GuildId == "0";

        // Stable identifier used as the Loupedeck action parameter.
        public String Key => $"{(this.IsDefault ? "0" : this.GuildId)}:{this.SoundId}";

        public String GroupLabel => this.IsDefault ? "Discord Default" : this.GuildName ?? $"Server {this.GuildId}";
    }
}
