namespace Loupedeck.DiscordSoundboardPlugin
{
    using System;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    public class DiscordSoundboardPlugin : Plugin
    {
        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        internal DiscordSoundboardService Soundboard { get; }

        public DiscordSoundboardPlugin()
        {
            PluginLog.Init(this.Log);

            // Created here (not in Load) so actions can subscribe to it from their OnLoad
            // regardless of initialization order. It only touches Discord after Start().
            this.Soundboard = new DiscordSoundboardService(this);
        }

        public override void Load()
        {
            this.Soundboard.StatusChanged += this.OnSoundboardStatusChanged;
            this.Soundboard.Start();
        }

        public override void Unload()
        {
            this.Soundboard.StatusChanged -= this.OnSoundboardStatusChanged;
            this.Soundboard.Dispose();
        }

        private void OnSoundboardStatusChanged(Object sender, EventArgs e)
            => this.OnPluginStatusChanged(this.Soundboard.Status, this.Soundboard.StatusMessage);
    }
}
