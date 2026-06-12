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

        private static readonly BitmapColor FlashSuccess = new BitmapColor(59, 165, 93);
        private static readonly BitmapColor FlashFailure = new BitmapColor(218, 62, 82);
        private static readonly BitmapColor FavoriteStar = new BitmapColor(255, 200, 60);

        public static BitmapImage Render(SoundboardSound sound, PluginImageSize imageSize, PluginConfig config = null, Boolean? playFeedback = null, Byte[] emojiImage = null, Boolean isFavorite = false)
        {
            using var builder = new BitmapBuilder(imageSize);

            if (sound == null)
            {
                builder.Clear(new BitmapColor(45, 49, 54));
                builder.DrawText("...", DimText);
                return builder.ToImage();
            }

            BitmapColor background;
            if (playFeedback.HasValue)
            {
                background = playFeedback.Value ? FlashSuccess : FlashFailure;
            }
            else
            {
                var guildKey = sound.IsDefault ? "0" : sound.GuildId;
                if (!(config?.TileColors != null && config.TileColors.TryGetValue(guildKey, out var hex) && TryParseHexColor(hex, out background)))
                {
                    background = sound.IsDefault ? Blurple : Palette[StableHash(sound.GuildId) % Palette.Length];
                }
                if (!sound.Available)
                {
                    background = new BitmapColor(background.R / 3, background.G / 3, background.B / 3);
                }
            }

            builder.Clear(background);
            var textColor = sound.Available || playFeedback.HasValue ? BitmapColor.White : DimText;
            var text = Truncate(sound.Name, 24);

            if (emojiImage != null)
            {
                // Custom emoji image on the upper half, name below.
                var w = builder.Width;
                var h = builder.Height;
                var size = h * 45 / 100;
                builder.DrawImage(emojiImage, (w - size) / 2, h * 6 / 100, size, size);
                builder.DrawText(text, 0, h * 52 / 100, w, h * 44 / 100, textColor);
            }
            else
            {
                // Unicode emoji have no CDN image; show the character as a text line.
                if (config?.ShowEmoji == true && String.IsNullOrEmpty(sound.EmojiId) && !String.IsNullOrEmpty(sound.EmojiName))
                {
                    text = sound.EmojiName + "\n" + text;
                }
                builder.DrawText(text, textColor);
            }

            if (isFavorite)
            {
                var starSize = builder.Height * 24 / 100;
                builder.DrawText("★", builder.Width - starSize - 2, 0, starSize, starSize, FavoriteStar, fontSize: starSize * 80 / 100);
            }
            return builder.ToImage();
        }

        // Accepts "#RRGGBB" (leading '#' optional).
        private static Boolean TryParseHexColor(String hex, out BitmapColor color)
        {
            color = default;
            var h = hex?.TrimStart('#');
            if (h?.Length != 6 || !UInt32.TryParse(h, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            {
                return false;
            }
            color = new BitmapColor((Int32)((rgb >> 16) & 0xFF), (Int32)((rgb >> 8) & 0xFF), (Int32)(rgb & 0xFF));
            return true;
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
