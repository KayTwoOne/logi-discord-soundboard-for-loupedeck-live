namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // Maintenance actions: re-index sounds, drop/rebuild the Discord connection,
    // or force the OAuth approval popup again.

    public class SoundboardControlCommand : PluginDynamicCommand
    {
        private const String RefreshParameter = "refresh";
        private const String ReconnectParameter = "reconnect";
        private const String ReauthorizeParameter = "reauthorize";

        private DiscordSoundboardService Service => (this.Plugin as DiscordSoundboardPlugin)?.Soundboard;

        public SoundboardControlCommand()
            : base(displayName: "Soundboard Control", description: "Refresh, reconnect or re-authorize the Discord soundboard", groupName: "Control")
        {
            this.AddParameter(RefreshParameter, "Refresh Sounds", "Control");
            this.AddParameter(ReconnectParameter, "Reconnect to Discord", "Control");
            this.AddParameter(ReauthorizeParameter, "Re-authorize Discord", "Control");
        }

        protected override void RunCommand(String actionParameter)
        {
            var service = this.Service;
            if (service == null)
            {
                return;
            }

            switch (actionParameter)
            {
                case RefreshParameter:
                    _ = service.RefreshSoundsAsync();
                    break;
                case ReconnectParameter:
                    service.Reconnect();
                    break;
                case ReauthorizeParameter:
                    service.Reauthorize();
                    break;
            }
        }
    }
}
