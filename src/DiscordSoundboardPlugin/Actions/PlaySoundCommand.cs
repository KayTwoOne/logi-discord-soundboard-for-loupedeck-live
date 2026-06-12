namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;
    using System.Threading.Tasks;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // Exposes every indexed soundboard sound as an individually assignable action,
    // grouped by server name in the Loupedeck assignment UI.

    public class PlaySoundCommand : PluginDynamicCommand
    {
        private const Int32 LongPressMs = 500;

        private readonly System.Collections.Generic.HashSet<String> _longPressHandled = new System.Collections.Generic.HashSet<String>();

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
                service.PlayAttempted += this.OnPlayAttempted;
                service.EmojiCacheUpdated += this.OnEmojiCacheUpdated;
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
                service.PlayAttempted -= this.OnPlayAttempted;
                service.EmojiCacheUpdated -= this.OnEmojiCacheUpdated;
            }
            return true;
        }

        // Tap = play, long-press = toggle favourite (same gesture as inside the folder).
        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.Press:
                    this._longPressHandled.Remove(actionParameter);
                    return true;
                case DeviceButtonEventType.LongPress:
                    this._longPressHandled.Add(actionParameter);
                    this.ToggleFavorite(actionParameter);
                    return true;
                case DeviceButtonEventType.Release:
                    if (buttonEvent.PressDuration < LongPressMs)
                    {
                        this.RunCommand(actionParameter);
                    }
                    else if (!this._longPressHandled.Remove(actionParameter))
                    {
                        this.ToggleFavorite(actionParameter);
                    }
                    return true;
                default:
                    return true;
            }
        }

        private void ToggleFavorite(String actionParameter)
        {
            var service = this.Service;
            var sound = service?.FindSound(actionParameter);
            if (sound != null)
            {
                service.ToggleFavorite(sound.SoundId);
            }
        }


        protected override void RunCommand(String actionParameter)
            => _ = this.Service?.PlaySoundAsync(actionParameter);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
            => this.Service?.FindSound(actionParameter)?.Name ?? "Sound";

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var service = this.Service;
            var sound = service?.FindSound(actionParameter);
            var config = service?.GetConfig();
            var emoji = config?.ShowEmoji == true ? service?.GetEmojiImage(sound?.EmojiId) : null;
            return SoundTile.Render(sound, imageSize, config, service?.GetPlayFeedback(actionParameter), emoji,
                service?.IsFavorite(sound?.SoundId) == true);
        }

        // Redraw the pressed tile for the flash, then again once the flash window passes.
        private void OnPlayAttempted(Object sender, String key)
        {
            this.ActionImageChanged(key);
            _ = Task.Delay(900).ContinueWith(_ => this.ActionImageChanged(key));
        }

        private void OnEmojiCacheUpdated(Object sender, EventArgs e) => this.ActionImageChanged();

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
