namespace Loupedeck.DiscordSoundboardPlugin.Actions
{
    using System;
    using System.Linq;

    using Loupedeck.DiscordSoundboardPlugin.Discord;

    // A dial that cycles through favourite sounds (the dial display shows the current
    // pick) and plays it on press — all favourites reachable from one physical control.

    public class FavoriteSoundDial : PluginDynamicAdjustment
    {
        private Int32 _index;

        private DiscordSoundboardService Service => (this.Plugin as DiscordSoundboardPlugin)?.Soundboard;

        public FavoriteSoundDial()
            : base(displayName: "Favourite Sound Dial", description: "Rotate to pick a favourite sound, press to play it", groupName: "Sounds", hasReset: true)
        {
        }

        protected override Boolean OnLoad()
        {
            var service = this.Service;
            if (service != null)
            {
                service.SoundsChanged += this.OnSoundsChanged;
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

        protected override void ApplyAdjustment(String actionParameter, Int32 diff)
        {
            var favorites = this.Service?.GetFavoriteSounds();
            if (favorites == null || favorites.Count == 0)
            {
                return;
            }
            this._index = ((this._index + diff) % favorites.Count + favorites.Count) % favorites.Count;
            this.AdjustmentValueChanged();
        }

        // The dial press ("reset") plays the currently selected favourite.
        protected override void RunCommand(String actionParameter)
        {
            var favorites = this.Service?.GetFavoriteSounds();
            var sound = favorites?.ElementAtOrDefault(this._index);
            if (sound != null)
            {
                _ = this.Service.PlaySoundAsync(sound.Key);
            }
        }

        protected override String GetAdjustmentValue(String actionParameter)
        {
            var favorites = this.Service?.GetFavoriteSounds();
            if (favorites == null || favorites.Count == 0)
            {
                return "No favs";
            }
            var sound = favorites.ElementAtOrDefault(Math.Min(this._index, favorites.Count - 1));
            var name = sound?.Name ?? "?";
            return name.Length <= 12 ? name : name.Substring(0, 11) + "…";
        }

        private void OnSoundsChanged(Object sender, EventArgs e)
        {
            var count = this.Service?.GetFavoriteSounds().Count ?? 0;
            if (count > 0 && this._index >= count)
            {
                this._index = 0;
            }
            this.AdjustmentValueChanged();
        }
    }
}
