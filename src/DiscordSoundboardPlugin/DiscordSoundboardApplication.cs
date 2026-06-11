namespace Loupedeck.DiscordSoundboardPlugin
{
    using System;

    // The plugin loader requires a ClientApplication subclass in every plugin assembly,
    // even for universal plugins that declare HasNoApplication. The plugin talks to the
    // Discord desktop client itself over IPC, so no process linking is needed here.

    public class DiscordSoundboardApplication : ClientApplication
    {
        public DiscordSoundboardApplication()
        {
        }

        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";
    }
}
