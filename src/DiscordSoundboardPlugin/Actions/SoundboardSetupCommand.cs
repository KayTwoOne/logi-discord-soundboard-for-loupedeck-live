namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // In-app setup: assign this action to any button, type the Discord application
    // credentials into the action editor panel, then press the button once to apply.
    // No JSON editing required; the secret is encrypted at rest immediately after.

    public class SoundboardSetupCommand : ActionEditorCommand
    {
        private DiscordSoundboardService Service => (this.Plugin as DiscordSoundboardPlugin)?.Soundboard;

        public SoundboardSetupCommand()
            : base(DeviceType.All)
        {
            this.DisplayName = "Soundboard Setup";
            this.Description = "Enter your Discord application's credentials, assign to a button, press once to apply";
            this.GroupName = "Setup";

            this.ActionEditor.AddControlEx(new ActionEditorTextbox("client_id", "Client ID",
                "Application ID from discord.com/developers/applications").SetRequired());
            this.ActionEditor.AddControlEx(new ActionEditorTextbox("client_secret", "Client Secret",
                "OAuth2 client secret; stored encrypted with Windows DPAPI").SetPasswordMode());
        }

        protected override Boolean RunCommand(ActionEditorActionParameters actionParameters)
        {
            actionParameters.TryGetString("client_id", out var clientId);
            actionParameters.TryGetString("client_secret", out var clientSecret);
            return this.Service?.ApplyCredentials(clientId, clientSecret) == true;
        }

        protected override String GetCommandDisplayName(ActionEditorActionParameters actionParameters) => "Apply\nSetup";
    }
}
