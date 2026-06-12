namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // A dynamic folder that shows the whole soundboard as a paged grid on the device.
    // Tap plays, long-press toggles favourite. Paging is on the physical dials by
    // default (folder_navigation "encoder") so swiping across live sound tiles is
    // never needed.

    public class SoundboardFolder : PluginDynamicFolder
    {
        private const String RefreshParameter = "~refresh";
        private const Int32 LongPressMs = 500;

        private Boolean _subscribed;
        private readonly HashSet<String> _longPressHandled = new HashSet<String>();

        public SoundboardFolder()
        {
            this.DisplayName = "Soundboard";
            this.Description = "Browse and play all your Discord soundboard sounds";
            this.GroupName = "Discord Soundboard";
        }

        private DiscordSoundboardService Service => (this.Plugin as DiscordSoundboardPlugin)?.Soundboard;

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _)
            => String.Equals(this.Service?.GetConfig().FolderNavigation, "buttons", StringComparison.OrdinalIgnoreCase)
                ? PluginDynamicFolderNavigation.ButtonArea
                : PluginDynamicFolderNavigation.EncoderArea;

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
                service.PlayAttempted -= this.OnPlayAttempted;
                service.EmojiCacheUpdated -= this.OnEmojiCacheUpdated;
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

        // Tap = play, long-press = toggle favourite. The default handling fires
        // RunCommand on its own schedule, so sound tiles are handled manually here.
        public override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            if (IsControlParameter(actionParameter))
            {
                return base.ProcessButtonEvent2(actionParameter, buttonEvent);
            }

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
                        // Held long enough but no LongPress event arrived on this path.
                        this.ToggleFavorite(actionParameter);
                    }
                    return true;
                default:
                    return true;
            }
        }

        public override Boolean ProcessTouchEvent(String actionParameter, DeviceTouchEvent touchEvent)
        {
            if (IsControlParameter(actionParameter))
            {
                return base.ProcessTouchEvent(actionParameter, touchEvent);
            }

            switch (touchEvent.EventType)
            {
                case DeviceTouchEventType.Tap:
                    this.RunCommand(actionParameter);
                    return true;
                case DeviceTouchEventType.LongPress:
                    this.ToggleFavorite(actionParameter);
                    return true;
                case DeviceTouchEventType.TouchDown:
                case DeviceTouchEventType.TouchUp:
                case DeviceTouchEventType.Press:
                case DeviceTouchEventType.LongRelease:
                    return true;
                default:
                    return base.ProcessTouchEvent(actionParameter, touchEvent);
            }
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
            => actionParameter == RefreshParameter
                ? "Refresh"
                : this.Service?.FindSound(actionParameter)?.Name ?? "Sound";

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (actionParameter == RefreshParameter)
            {
                return SoundTile.RenderLabel("Refresh", imageSize, new BitmapColor(45, 49, 54));
            }

            var service = this.Service;
            var sound = service?.FindSound(actionParameter);
            var config = service?.GetConfig();
            var emoji = config?.ShowEmoji == true ? service?.GetEmojiImage(sound?.EmojiId) : null;
            return SoundTile.Render(sound, imageSize, config, service?.GetPlayFeedback(actionParameter), emoji,
                service?.IsFavorite(sound?.SoundId) == true);
        }

        public override BitmapImage GetButtonImage(PluginImageSize imageSize)
            => SoundTile.RenderLabel("Sound\nboard", imageSize, SoundTile.Blurple);

        private static Boolean IsControlParameter(String actionParameter)
            => actionParameter == RefreshParameter || actionParameter?.StartsWith("~") == true;

        private void ToggleFavorite(String actionParameter)
        {
            var service = this.Service;
            var sound = service?.FindSound(actionParameter);
            if (sound != null)
            {
                service.ToggleFavorite(sound.SoundId);
            }
        }

        private void EnsureSubscribed()
        {
            var service = this.Service;
            if (this._subscribed || service == null)
            {
                return;
            }
            service.SoundsChanged += this.OnSoundsChanged;
            service.PlayAttempted += this.OnPlayAttempted;
            service.EmojiCacheUpdated += this.OnEmojiCacheUpdated;
            this._subscribed = true;
        }

        private void OnSoundsChanged(Object sender, EventArgs e) => this.ButtonActionNamesChanged();

        // Redraw the pressed tile for the flash, then again once the flash window passes.
        private void OnPlayAttempted(Object sender, String key)
        {
            this.CommandImageChanged(key);
            _ = Task.Delay(900).ContinueWith(_ => this.CommandImageChanged(key));
        }

        private void OnEmojiCacheUpdated(Object sender, EventArgs e) => this.ButtonActionNamesChanged();
    }
}
