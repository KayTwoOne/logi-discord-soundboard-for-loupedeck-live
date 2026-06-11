namespace Loupedeck.DiscordSoundboardPlugin.Discord
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed class PluginConfig
    {
        [JsonPropertyName("client_id")]
        public String ClientId { get; set; }

        [JsonPropertyName("client_secret")]
        public String ClientSecret { get; set; }

        // DPAPI-encrypted form of client_secret. Users paste the plaintext secret once;
        // the plugin encrypts it here and blanks the plaintext field on first load.
        [JsonPropertyName("client_secret_protected")]
        public String ClientSecretProtected { get; set; }

        // Discord requires a registered redirect URI for the authorize step even though
        // nothing is ever opened. Must match one registered under OAuth2 -> Redirects.
        [JsonPropertyName("redirect_uri")]
        public String RedirectUri { get; set; } = "http://127.0.0.1";

        // Sound ids pinned to the top of the list ("favourites" are not exposed over RPC,
        // so the plugin keeps its own).
        [JsonPropertyName("favorite_sound_ids")]
        public List<String> FavoriteSoundIds { get; set; } = new List<String>();

        // Guild ids whose sounds should not be listed (assigned buttons keep working).
        [JsonPropertyName("excluded_guild_ids")]
        public List<String> ExcludedGuildIds { get; set; } = new List<String>();

        // Hide Nitro-locked/unavailable sounds instead of showing them dimmed.
        [JsonPropertyName("hide_unavailable")]
        public Boolean HideUnavailable { get; set; }

        // "server" (group by server, default) or "name" (one flat A-Z list).
        [JsonPropertyName("sort_mode")]
        public String SortMode { get; set; } = "server";

        // Tile colour overrides: guild id -> "#RRGGBB" ("0" targets Discord default sounds).
        [JsonPropertyName("tile_colors")]
        public Dictionary<String, String> TileColors { get; set; } = new Dictionary<String, String>();

        // Show the sound's emoji on its tile. Custom (uploaded) emoji are fetched from
        // Discord's CDN and drawn as images; unicode emoji fall back to text.
        [JsonPropertyName("show_emoji")]
        public Boolean ShowEmoji { get; set; } = true;

        // Ignore presses arriving within this window after a play, to absorb double-taps.
        [JsonPropertyName("play_cooldown_ms")]
        public Int32 PlayCooldownMs { get; set; }
    }

    internal sealed class OAuthToken
    {
        [JsonPropertyName("access_token")]
        public String AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public String RefreshToken { get; set; }

        [JsonPropertyName("expires_at_utc")]
        public DateTime ExpiresAtUtc { get; set; }
    }

    // Owns the Discord connection on a background loop: connect -> authorize/authenticate ->
    // index sounds -> wait for disconnect -> retry. Sounds are cached to disk so buttons
    // render before Discord is reachable.

    internal sealed class DiscordSoundboardService : IDisposable
    {
        private static readonly String[] OAuthScopes = { "rpc", "rpc.voice.write" };
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private readonly Plugin _plugin;
        private readonly HttpClient _http = new HttpClient();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Object _soundsLock = new Object();

        private String _dataDirectory;
        private DiscordRpcClient _client;
        private List<SoundboardSound> _sounds = new List<SoundboardSound>();
        private PluginConfig _configCache;
        private DateTime _configCacheStamp;
        private DateTime _lastPlayUtc;
        private DateTime _authorizeBackoffUntilUtc;

        private static readonly TimeSpan FeedbackDuration = TimeSpan.FromMilliseconds(800);
        private readonly ConcurrentDictionary<String, (Boolean Success, DateTime UntilUtc)> _playFeedback =
            new ConcurrentDictionary<String, (Boolean, DateTime)>();
        private static readonly TimeSpan EmojiRetryDelay = TimeSpan.FromMinutes(15);
        private readonly ConcurrentDictionary<String, Byte[]> _emojiMemory = new ConcurrentDictionary<String, Byte[]>();
        private readonly ConcurrentDictionary<String, Boolean> _emojiFetching = new ConcurrentDictionary<String, Boolean>();
        private readonly ConcurrentDictionary<String, DateTime> _emojiFailedUtc = new ConcurrentDictionary<String, DateTime>();

        public event EventHandler SoundsChanged;
        public event EventHandler StatusChanged;

        // Raised after a play attempt resolves, with the sound key; tiles flash green/red.
        public event EventHandler<String> PlayAttempted;

        // Raised when a custom emoji image finishes downloading.
        public event EventHandler EmojiCacheUpdated;

        public PluginStatus Status { get; private set; } = PluginStatus.Warning;

        public String StatusMessage { get; private set; } = "Starting";

        public Boolean IsConnected => this._client?.IsConnected == true;

        public String ConfigFilePath => Path.Combine(this._dataDirectory ?? "", "config.json");

        public DiscordSoundboardService(Plugin plugin) => this._plugin = plugin;

        public void Start()
        {
            this._dataDirectory = this._plugin.GetPluginDataDirectory();
            Directory.CreateDirectory(this._dataDirectory);

            var cached = this.LoadJson<List<SoundboardSound>>("sounds.json");
            if (cached != null && cached.Count > 0)
            {
                lock (this._soundsLock)
                {
                    this._sounds = cached;
                }
                this.SoundsChanged?.Invoke(this, EventArgs.Empty);
            }

            Task.Run(this.RunAsync);
        }

        public void Dispose()
        {
            this._cts.Cancel();
            this._client?.Dispose();
            this._http.Dispose();
        }

        // Display/behaviour settings, re-read whenever config.json changes on disk, so
        // edits apply on the next redraw without reconnecting.
        internal PluginConfig GetConfig()
        {
            try
            {
                var path = this.ConfigFilePath;
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (this._configCache == null || stamp != this._configCacheStamp)
                {
                    this._configCache = this.LoadJson<PluginConfig>("config.json") ?? new PluginConfig();
                    this._configCacheStamp = stamp;
                }
            }
            catch
            {
                this._configCache ??= new PluginConfig();
            }
            return this._configCache;
        }

        // The user-facing sound list: exclusions, availability filter, sort mode and
        // favourite pinning are applied here, at read time, over the raw indexed list.
        public IReadOnlyList<SoundboardSound> GetSounds()
        {
            List<SoundboardSound> raw;
            lock (this._soundsLock)
            {
                raw = this._sounds.ToList();
            }

            var config = this.GetConfig();
            IEnumerable<SoundboardSound> view = raw;

            if (config.ExcludedGuildIds?.Count > 0)
            {
                view = view.Where(s => s.IsDefault || !config.ExcludedGuildIds.Contains(s.GuildId));
            }
            if (config.HideUnavailable)
            {
                view = view.Where(s => s.Available);
            }

            var ordered = String.Equals(config.SortMode, "name", StringComparison.OrdinalIgnoreCase)
                ? view.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : view.OrderBy(s => s.IsDefault)
                      .ThenBy(s => s.GroupLabel, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();

            var favorites = config.FavoriteSoundIds ?? new List<String>();
            if (favorites.Count > 0)
            {
                var pinned = ordered.Where(s => favorites.Contains(s.SoundId)).ToList();
                pinned.AddRange(ordered.Where(s => !favorites.Contains(s.SoundId)));
                ordered = pinned;
            }
            return ordered;
        }

        public SoundboardSound FindSound(String key)
        {
            lock (this._soundsLock)
            {
                return this._sounds.FirstOrDefault(s => s.Key == key);
            }
        }

        // Drops the current connection; the run loop reconnects automatically.
        public void Reconnect()
        {
            this._authorizeBackoffUntilUtc = DateTime.MinValue;
            this._client?.Dispose();
        }

        // Forgets the cached OAuth token and reconnects, forcing the in-Discord approval modal.
        public void Reauthorize()
        {
            try
            {
                File.Delete(Path.Combine(this._dataDirectory, "token.bin"));
                File.Delete(Path.Combine(this._dataDirectory, "token.json"));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not delete the stored token");
            }
            this.Reconnect();
        }

        public async Task<Boolean> PlaySoundAsync(String key)
        {
            var client = this._client;
            var sound = this.FindSound(key);
            if (client?.IsConnected != true || sound == null)
            {
                PluginLog.Warning($"Cannot play '{key}': {(sound == null ? "unknown sound" : "not connected to Discord")}");
                this.SetPlayFeedback(key, false);
                return false;
            }

            var config = this.GetConfig();
            if (config.PlayCooldownMs > 0)
            {
                var now = DateTime.UtcNow;
                if ((now - this._lastPlayUtc).TotalMilliseconds < config.PlayCooldownMs)
                {
                    // Deliberately no feedback flash: a debounced press is not a failure.
                    PluginLog.Verbose($"Ignored '{sound.Name}' (within {config.PlayCooldownMs}ms cooldown)");
                    return false;
                }
                this._lastPlayUtc = now;
            }

            try
            {
                var args = new Dictionary<String, Object> { ["sound_id"] = sound.SoundId };
                if (!sound.IsDefault)
                {
                    args["guild_id"] = sound.GuildId;
                }

                using var response = await client.RequestAsync("PLAY_SOUNDBOARD_SOUND", args).ConfigureAwait(false);
                PluginLog.Info($"Played soundboard sound '{sound.Name}'");
                this.SetPlayFeedback(key, true);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to play '{sound.Name}' (are you in a voice channel?)");
                this.SetPlayFeedback(key, false);
                return false;
            }
        }

        // Returns true/false while a recent play attempt's flash window is active, else null.
        public Boolean? GetPlayFeedback(String key)
            => this._playFeedback.TryGetValue(key, out var feedback) && DateTime.UtcNow < feedback.UntilUtc
                ? feedback.Success
                : null;

        private void SetPlayFeedback(String key, Boolean success)
        {
            this._playFeedback[key] = (success, DateTime.UtcNow + FeedbackDuration);
            this.PlayAttempted?.Invoke(this, key);
        }

        // Returns the PNG bytes of a custom emoji, fetching from Discord's CDN in the
        // background on first sight (EmojiCacheUpdated fires when it lands).
        public Byte[] GetEmojiImage(String emojiId)
        {
            // Ids are snowflakes; the digit check also guards the path/URL we build below.
            if (String.IsNullOrEmpty(emojiId) || this._dataDirectory == null || !emojiId.All(Char.IsDigit))
            {
                return null;
            }
            if (this._emojiMemory.TryGetValue(emojiId, out var bytes))
            {
                return bytes;
            }

            var path = Path.Combine(this._dataDirectory, "emoji", emojiId + ".png");
            if (File.Exists(path))
            {
                try
                {
                    bytes = File.ReadAllBytes(path);
                    this._emojiMemory[emojiId] = bytes;
                    return bytes;
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Could not read cached emoji {emojiId}");
                }
            }

            // Don't hammer the CDN from every redraw after a failure (offline, deleted emoji).
            if (this._emojiFailedUtc.TryGetValue(emojiId, out var failedAt) && DateTime.UtcNow - failedAt < EmojiRetryDelay)
            {
                return null;
            }

            this.FetchEmojiInBackground(emojiId, path);
            return null;
        }

        private void FetchEmojiInBackground(String emojiId, String path)
        {
            if (!this._emojiFetching.TryAdd(emojiId, true))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = await this._http.GetByteArrayAsync($"https://cdn.discordapp.com/emojis/{emojiId}.png?size=64").ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, bytes);
                    this._emojiMemory[emojiId] = bytes;
                    this._emojiFailedUtc.TryRemove(emojiId, out _);
                    this.EmojiCacheUpdated?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    this._emojiFailedUtc[emojiId] = DateTime.UtcNow;
                    PluginLog.Warning(ex, $"Could not fetch emoji {emojiId} from Discord CDN");
                }
                finally
                {
                    this._emojiFetching.TryRemove(emojiId, out _);
                }
            });
        }

        public Task<Boolean> PlayRandomAsync(Boolean favoritesOnly)
        {
            var favorites = this.GetConfig().FavoriteSoundIds ?? new List<String>();
            var candidates = this.GetSounds()
                .Where(s => s.Available && (!favoritesOnly || favorites.Contains(s.SoundId)))
                .ToList();
            if (candidates.Count == 0)
            {
                PluginLog.Warning(favoritesOnly
                    ? "No favourite sounds to pick from (favorite_sound_ids in config.json)"
                    : "No available sounds to pick from");
                return Task.FromResult(false);
            }
            return this.PlaySoundAsync(candidates[Random.Shared.Next(candidates.Count)].Key);
        }

        public async Task<Boolean> RefreshSoundsAsync()
        {
            var client = this._client;
            if (client?.IsConnected != true)
            {
                return false;
            }

            try
            {
                var guildNames = await this.FetchGuildNamesAsync(client).ConfigureAwait(false);

                using var doc = await client.RequestAsync("GET_SOUNDBOARD_SOUNDS", null).ConfigureAwait(false);
                var data = doc.RootElement.GetProperty("data");
                var array = data.ValueKind == JsonValueKind.Array
                    ? data
                    : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sounds", out var soundsProp)
                        ? soundsProp
                        : default;
                if (array.ValueKind != JsonValueKind.Array)
                {
                    throw new DiscordRpcException("Unexpected GET_SOUNDBOARD_SOUNDS response shape");
                }

                var sounds = new List<SoundboardSound>();
                foreach (var item in array.EnumerateArray())
                {
                    var sound = new SoundboardSound
                    {
                        SoundId = GetSnowflakeOrString(item, "sound_id"),
                        Name = GetSnowflakeOrString(item, "name") ?? "Unnamed",
                        GuildId = GetSnowflakeOrString(item, "guild_id"),
                        EmojiName = GetSnowflakeOrString(item, "emoji_name"),
                        EmojiId = GetSnowflakeOrString(item, "emoji_id"),
                        Available = !item.TryGetProperty("available", out var avail) || avail.ValueKind != JsonValueKind.False,
                    };
                    if (String.IsNullOrEmpty(sound.SoundId))
                    {
                        continue;
                    }
                    if (!sound.IsDefault && guildNames.TryGetValue(sound.GuildId, out var guildName))
                    {
                        sound.GuildName = guildName;
                    }
                    sounds.Add(sound);
                }

                lock (this._soundsLock)
                {
                    this._sounds = sounds;
                }
                this.SaveJson("sounds.json", sounds);
                PluginLog.Info($"Indexed {sounds.Count} soundboard sounds ({sounds.Count(s => !s.Available)} unavailable)");
                this.SoundsChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Failed to refresh soundboard sounds");
                return false;
            }
        }

        private async Task RunAsync()
        {
            var ct = this._cts.Token;
            while (!ct.IsCancellationRequested)
            {
                DiscordRpcClient client = null;
                try
                {
                    var config = this.LoadConfig();
                    if (String.IsNullOrEmpty(config?.ClientId) ||
                        (String.IsNullOrEmpty(config?.ClientSecret) && String.IsNullOrEmpty(config?.ClientSecretProtected)))
                    {
                        this.WriteConfigTemplate();
                        this.SetStatus(PluginStatus.Error, $"Add your Discord application's client_id and client_secret to {this.ConfigFilePath}");
                        await WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (String.IsNullOrEmpty(config.ClientSecret))
                    {
                        // A protected secret exists but could not be decrypted;
                        // LoadConfig already set the specific status message.
                        await WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                        continue;
                    }

                    this.SetStatus(PluginStatus.Warning, "Connecting to Discord...");
                    client = new DiscordRpcClient(config.ClientId);
                    var closedTcs = new TaskCompletionSource<String>(TaskCreationOptions.RunContinuationsAsynchronously);
                    client.Closed += (_, reason) => closedTcs.TrySetResult(reason);

                    using (await client.ConnectAsync(ct).ConfigureAwait(false))
                    {
                    }
                    this._client = client;

                    await this.AuthenticateAsync(client, config, ct).ConfigureAwait(false);
                    await this.RefreshSoundsAsync().ConfigureAwait(false);
                    this.SetStatus(PluginStatus.Normal, "Connected to Discord");

                    var closedReason = await closedTcs.Task.WaitAsync(ct).ConfigureAwait(false);
                    this.SetStatus(PluginStatus.Warning, $"Disconnected from Discord: {closedReason}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "Discord soundboard connection attempt failed");
                    this.SetStatus(PluginStatus.Warning, ex.Message);
                }
                finally
                {
                    this._client = null;
                    client?.Dispose();
                }

                await WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }

        // Reads config.json. If the user pasted a plaintext client_secret, it is encrypted
        // with DPAPI, persisted as client_secret_protected, and blanked on disk; the
        // decrypted secret lives only in memory from then on.
        private PluginConfig LoadConfig()
        {
            var readStamp = File.Exists(this.ConfigFilePath) ? File.GetLastWriteTimeUtc(this.ConfigFilePath) : DateTime.MinValue;
            var config = this.LoadJson<PluginConfig>("config.json");
            if (config == null)
            {
                return null;
            }

            if (!String.IsNullOrEmpty(config.ClientSecret))
            {
                var protectedSecret = Dpapi.TryProtect(config.ClientSecret);
                // Skip the rewrite if the file changed since we read it (the user may
                // still be editing); we encrypt on a later pass instead of clobbering.
                if (protectedSecret != null && File.GetLastWriteTimeUtc(this.ConfigFilePath) == readStamp)
                {
                    var plaintext = config.ClientSecret;
                    config.ClientSecretProtected = protectedSecret;
                    config.ClientSecret = "";
                    this.SaveJson("config.json", config);
                    config.ClientSecret = plaintext;
                    PluginLog.Info("client_secret encrypted at rest (client_secret_protected)");
                }
                return config;
            }

            if (!String.IsNullOrEmpty(config.ClientSecretProtected))
            {
                config.ClientSecret = Dpapi.TryUnprotect(config.ClientSecretProtected);
                if (config.ClientSecret == null)
                {
                    this.SetStatus(PluginStatus.Error, $"Could not decrypt the stored client secret. Re-paste client_secret into {this.ConfigFilePath}");
                }
            }
            return config;
        }

        private OAuthToken LoadToken()
        {
            var protectedPath = Path.Combine(this._dataDirectory, "token.bin");
            try
            {
                if (File.Exists(protectedPath))
                {
                    var json = Dpapi.TryUnprotect(File.ReadAllText(protectedPath));
                    return json != null ? JsonSerializer.Deserialize<OAuthToken>(json) : null;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not read token.bin");
            }

            // Migrate a plaintext token.json left over from older builds.
            var legacy = this.LoadJson<OAuthToken>("token.json");
            if (legacy != null)
            {
                this.SaveToken(legacy);
            }
            return legacy;
        }

        private void SaveToken(OAuthToken token)
        {
            var blob = Dpapi.TryProtect(JsonSerializer.Serialize(token, JsonOptions));
            if (blob == null)
            {
                this.SaveJson("token.json", token); // DPAPI unavailable; plaintext fallback
                return;
            }

            try
            {
                File.WriteAllText(Path.Combine(this._dataDirectory, "token.bin"), blob);
                File.Delete(Path.Combine(this._dataDirectory, "token.json"));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not write token.bin");
            }
        }

        private async Task AuthenticateAsync(DiscordRpcClient client, PluginConfig config, CancellationToken ct)
        {
            var token = this.LoadToken();

            if (token != null && DateTime.UtcNow >= token.ExpiresAtUtc)
            {
                token = await this.TryRefreshTokenAsync(config, token, ct).ConfigureAwait(false);
            }

            if (token != null && !await TryAuthenticateAsync(client, token).ConfigureAwait(false))
            {
                token = null;
            }

            if (token == null)
            {
                // A declined or failed authorize must not respawn the popup every loop
                // iteration; hold off so the user is asked at most ~once per 90 seconds.
                var backoff = this._authorizeBackoffUntilUtc - DateTime.UtcNow;
                if (backoff > TimeSpan.Zero)
                {
                    this.SetStatus(PluginStatus.Warning, $"Authorization was declined or failed; asking again in {(Int32)backoff.TotalSeconds}s (Re-authorize to retry now)");
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }

                this.SetStatus(PluginStatus.Warning, "Approve the authorization popup in Discord");
                var authorizeArgs = new Dictionary<String, Object>
                {
                    ["client_id"] = config.ClientId,
                    ["scopes"] = OAuthScopes,
                    ["prompt"] = "consent",
                };
                if (!String.IsNullOrEmpty(config.RedirectUri))
                {
                    authorizeArgs["redirect_uri"] = config.RedirectUri;
                }

                JsonDocument authorizeDoc;
                try
                {
                    authorizeDoc = await client.RequestAsync("AUTHORIZE", authorizeArgs, timeoutSeconds: 120).ConfigureAwait(false);
                }
                catch (DiscordRpcException)
                {
                    this._authorizeBackoffUntilUtc = DateTime.UtcNow.AddSeconds(90);
                    throw;
                }

                String code;
                using (authorizeDoc)
                {
                    code = authorizeDoc.RootElement.GetProperty("data").GetProperty("code").GetString();
                }

                var exchangeArgs = new Dictionary<String, String>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                };
                if (!String.IsNullOrEmpty(config.RedirectUri))
                {
                    exchangeArgs["redirect_uri"] = config.RedirectUri;
                }
                token = await this.ExchangeTokenAsync(config, exchangeArgs, ct).ConfigureAwait(false);
                this._authorizeBackoffUntilUtc = DateTime.MinValue;

                if (!await TryAuthenticateAsync(client, token).ConfigureAwait(false))
                {
                    throw new DiscordRpcException("Discord rejected a freshly issued access token");
                }
            }
        }

        private static async Task<Boolean> TryAuthenticateAsync(DiscordRpcClient client, OAuthToken token)
        {
            try
            {
                using var doc = await client.RequestAsync("AUTHENTICATE", new Dictionary<String, Object>
                {
                    ["access_token"] = token.AccessToken,
                }).ConfigureAwait(false);
                return true;
            }
            catch (DiscordRpcException ex)
            {
                PluginLog.Warning(ex, "Discord rejected the stored access token");
                return false;
            }
        }

        private async Task<OAuthToken> TryRefreshTokenAsync(PluginConfig config, OAuthToken token, CancellationToken ct)
        {
            if (String.IsNullOrEmpty(token.RefreshToken))
            {
                return null;
            }

            try
            {
                return await this.ExchangeTokenAsync(config, new Dictionary<String, String>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not refresh the Discord access token");
                return null;
            }
        }

        private async Task<OAuthToken> ExchangeTokenAsync(PluginConfig config, Dictionary<String, String> grant, CancellationToken ct)
        {
            var form = new Dictionary<String, String>(grant)
            {
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret,
            };

            using var response = await this._http.PostAsync("https://discord.com/api/v10/oauth2/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OAuth token exchange failed ({(Int32)response.StatusCode}): {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var token = new OAuthToken
            {
                AccessToken = root.GetProperty("access_token").GetString(),
                RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
                ExpiresAtUtc = DateTime.UtcNow
                    .AddSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetDouble() : 3600)
                    .AddMinutes(-5),
            };
            this.SaveToken(token);
            return token;
        }

        private async Task<Dictionary<String, String>> FetchGuildNamesAsync(DiscordRpcClient client)
        {
            var names = new Dictionary<String, String>();
            try
            {
                using var doc = await client.RequestAsync("GET_GUILDS", null).ConfigureAwait(false);
                if (doc.RootElement.GetProperty("data").TryGetProperty("guilds", out var guilds) && guilds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var guild in guilds.EnumerateArray())
                    {
                        var id = GetSnowflakeOrString(guild, "id");
                        var name = GetSnowflakeOrString(guild, "name");
                        if (!String.IsNullOrEmpty(id) && name != null)
                        {
                            names[id] = name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not fetch guild names, falling back to ids");
            }
            return names;
        }

        // Snowflakes may arrive as JSON strings or numbers depending on the client version.
        private static String GetSnowflakeOrString(JsonElement element, String property)
        {
            if (!element.TryGetProperty(property, out var value))
            {
                return null;
            }
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }

        private void WriteConfigTemplate()
        {
            if (!File.Exists(this.ConfigFilePath))
            {
                this.SaveJson("config.json", new PluginConfig());
            }
        }

        private void SetStatus(PluginStatus status, String message)
        {
            this.Status = status;
            this.StatusMessage = message;
            PluginLog.Info($"[{status}] {message}");
            this.StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private T LoadJson<T>(String fileName) where T : class
        {
            try
            {
                var path = Path.Combine(this._dataDirectory, fileName);
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Could not read {fileName}");
                return null;
            }
        }

        private void SaveJson<T>(String fileName, T value)
        {
            try
            {
                File.WriteAllText(Path.Combine(this._dataDirectory, fileName), JsonSerializer.Serialize(value, JsonOptions));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Could not write {fileName}");
            }
        }
    }
}
