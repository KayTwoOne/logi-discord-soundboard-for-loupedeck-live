namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // Exposes every indexed soundboard sound as an individually assignable action,
    // grouped by server name in the Loupedeck assignment UI.

    public class PlaySoundCommand : PluginDynamicCommand
    {
        private DiscordSoundboardService Service => (this.Plugin as DiscordSoundboardPlugin)?.Soundboard;

        public PlaySoundCommand()
            : base(displayName: "Play Sound", description: "Plays a Discord soundboard sound in your current voice channel", groupName: "Sounds")
        {
        }

        protected override Boolean OnLoad()
        {
            var service = this.Service;
            if (service != null)
            {
                service.SoundsChanged += this.OnSoundsChanged;
                this.RebuildParameterList();
            }
            return true;
        }

        protected override Boolean OnUnload()
        {
            var service = this.Service;
            if (service != null)
            {
                service.SoundsChanged -= this.OnSoundsChanged;
            }
            return true;
        }

        protected override void RunCommand(String actionParameter)
            => _ = this.Service?.PlaySoundAsync(actionParameter);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
            => this.Service?.FindSound(actionParameter)?.Name ?? "Sound";

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
            => SoundTile.Render(this.Service?.FindSound(actionParameter), imageSize, this.Service?.GetConfig());

        private void OnSoundsChanged(Object sender, EventArgs e)
        {
            this.RebuildParameterList();
            this.ActionImageChanged();
        }

        private void RebuildParameterList()
        {
            this.RemoveAllParameters();
            foreach (var sound in this.Service.GetSounds())
            {
                this.AddParameter(sound.Key, sound.Name, sound.GroupLabel);
            }
            this.ParametersChanged();
        }
    }
}
