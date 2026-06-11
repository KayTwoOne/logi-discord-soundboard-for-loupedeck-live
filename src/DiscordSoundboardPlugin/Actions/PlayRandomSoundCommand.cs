namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // Plays a randomly picked sound — chaos mode for the whole soundboard, or a tasteful
    // shuffle of the user's pinned favourites.

    public class PlayRandomSoundCommand : PluginDynamicCommand
    {
        private const String AnyParameter = "any";
        private const String FavoritesParameter = "favorites";

        private DiscordSoundboardService Service => (this.Plugin as DiscordSoundboardPlugin)?.Soundboard;

        public PlayRandomSoundCommand()
            : base(displayName: "Play Random Sound", description: "Plays a randomly chosen soundboard sound", groupName: "Sounds")
        {
            this.AddParameter(AnyParameter, "Random: Any Sound", "Random");
            this.AddParameter(FavoritesParameter, "Random: Favourite Sound", "Random");
        }

        protected override void RunCommand(String actionParameter)
            => _ = this.Service?.PlayRandomAsync(actionParameter == FavoritesParameter);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
            => SoundTile.RenderLabel(actionParameter == FavoritesParameter ? "Random\n(favs)" : "Random", imageSize, SoundTile.Blurple);
    }
}
