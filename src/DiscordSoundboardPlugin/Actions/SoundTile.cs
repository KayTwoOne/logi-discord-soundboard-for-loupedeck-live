namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // Renders soundboard sounds as touch-button tiles. Tiles are colour-coded per server
    // (stable hash of the guild id), Discord blurple for default sounds, dimmed when the
    // sound is unavailable to the user (e.g. Nitro-gated).

    internal static class SoundTile
    {
        public static readonly BitmapColor Blurple = new BitmapColor(88, 101, 242);
        private static readonly BitmapColor DimText = new BitmapColor(150, 150, 150);

        private static readonly BitmapColor[] Palette =
        {
            new BitmapColor(36, 92, 128),   // steel blue
            new BitmapColor(46, 110, 74),   // green
            new BitmapColor(130, 86, 38),   // amber
            new BitmapColor(120, 52, 84),   // plum
            new BitmapColor(52, 72, 130),   // indigo
            new BitmapColor(126, 56, 48),   // brick
            new BitmapColor(38, 104, 104),  // teal
            new BitmapColor(94, 70, 128),   // violet
        };

        public static BitmapImage Render(SoundboardSound sound, PluginImageSize imageSize)
        {
            using var builder = new BitmapBuilder(imageSize);

            if (sound == null)
            {
                builder.Clear(new BitmapColor(45, 49, 54));
                builder.DrawText("...", DimText);
                return builder.ToImage();
            }

            var background = sound.IsDefault ? Blurple : Palette[StableHash(sound.GuildId) % Palette.Length];
            if (!sound.Available)
            {
                background = new BitmapColor(background.R / 3, background.G / 3, background.B / 3);
            }

            builder.Clear(background);
            builder.DrawText(Truncate(sound.Name, 24), sound.Available ? BitmapColor.White : DimText);
            return builder.ToImage();
        }

        public static BitmapImage RenderLabel(String label, PluginImageSize imageSize, BitmapColor background)
        {
            using var builder = new BitmapBuilder(imageSize);
            builder.Clear(background);
            builder.DrawText(label, BitmapColor.White);
            return builder.ToImage();
        }

        private static String Truncate(String text, Int32 maxLength)
            => String.IsNullOrEmpty(text) || text.Length <= maxLength ? text : text.Substring(0, maxLength - 1) + "…";

        private static Int32 StableHash(String text)
        {
            var hash = 17;
            foreach (var c in text ?? "")
            {
                hash = unchecked(hash * 31 + c);
            }
            return hash & 0x7FFFFFFF;
        }
    }
}
