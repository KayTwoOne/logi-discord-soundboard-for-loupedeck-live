namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // A dynamic folder that shows the whole soundboard as a paged grid on the device.
    // The SDK's ButtonArea navigation supplies back/page controls automatically.

    public class SoundboardFolder : PluginDynamicFolder
    {
        private const String RefreshParameter = "~refresh";

        private Boolean _subscribed;

        public SoundboardFolder()
        {
            this.DisplayName = "Soundboard";
            this.Description = "Browse and play all your Discord soundboard sounds";
            this.GroupName = "Discord Soundboard";
        }

        private DiscordSoundboardService Service => (this.Plugin as DiscordSoundboardPlugin)?.Soundboard;

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) => PluginDynamicFolderNavigation.ButtonArea;

        public override Boolean Load()
        {
            this.EnsureSubscribed();
            return true;
        }

        public override Boolean Activate()
        {
            this.EnsureSubscribed();
            return true;
        }

        public override Boolean Unload()
        {
            var service = this.Service;
            if (this._subscribed && service != null)
            {
                service.SoundsChanged -= this.OnSoundsChanged;
                this._subscribed = false;
            }
            return true;
        }

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType deviceType)
        {
            this.EnsureSubscribed();
            var names = new List<String> { this.CreateCommandName(RefreshParameter) };
            var service = this.Service;
            if (service != null)
            {
                names.AddRange(service.GetSounds().Select(s => this.CreateCommandName(s.Key)));
            }
            return names;
        }

        public override void RunCommand(String actionParameter)
        {
            var service = this.Service;
            if (service == null)
            {
                return;
            }

            if (actionParameter == RefreshParameter)
            {
                _ = service.RefreshSoundsAsync();
                return;
            }

            _ = service.PlaySoundAsync(actionParameter);
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
            => actionParameter == RefreshParameter
                ? "Refresh"
                : this.Service?.FindSound(actionParameter)?.Name ?? "Sound";

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
            => actionParameter == RefreshParameter
                ? SoundTile.RenderLabel("Refresh", imageSize, new BitmapColor(45, 49, 54))
                : SoundTile.Render(this.Service?.FindSound(actionParameter), imageSize, this.Service?.GetConfig());

        public override BitmapImage GetButtonImage(PluginImageSize imageSize)
            => SoundTile.RenderLabel("Sound\nboard", imageSize, SoundTile.Blurple);

        private void EnsureSubscribed()
        {
            var service = this.Service;
            if (this._subscribed || service == null)
            {
                return;
            }
            service.SoundsChanged += this.OnSoundsChanged;
            this._subscribed = true;
        }

        private void OnSoundsChanged(Object sender, EventArgs e) => this.ButtonActionNamesChanged();
    }
}
