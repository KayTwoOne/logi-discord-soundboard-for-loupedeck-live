namespace Loupedeck.DiscordSoundboardPlugin.Discord
{
    using System;
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

        // Sound ids pinned to the top of the list ("favourites" are not exposed over RPC,
        // so the plugin keeps its own).
        [JsonPropertyName("favorite_sound_ids")]
        public List<String> FavoriteSoundIds { get; set; } = new List<String>();
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

        public event EventHandler SoundsChanged;
        public event EventHandler StatusChanged;

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

        public IReadOnlyList<SoundboardSound> GetSounds()
        {
            lock (this._soundsLock)
            {
                return this._sounds.ToArray();
            }
        }

        public SoundboardSound FindSound(String key)
        {
            lock (this._soundsLock)
            {
                return this._sounds.FirstOrDefault(s => s.Key == key);
            }
        }

        // Drops the current connection; the run loop reconnects automatically.
        public void Reconnect() => this._client?.Dispose();

        // Forgets the cached OAuth token and reconnects, forcing the in-Discord approval modal.
        public void Reauthorize()
        {
            try
            {
                File.Delete(Path.Combine(this._dataDirectory, "token.json"));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not delete token.json");
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
                return false;
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
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to play '{sound.Name}' (are you in a voice channel?)");
                return false;
            }
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

                var favorites = this.LoadJson<PluginConfig>("config.json")?.FavoriteSoundIds ?? new List<String>();
                sounds = sounds
                    .OrderByDescending(s => favorites.Contains(s.SoundId))
                    .ThenBy(s => s.IsDefault)
                    .ThenBy(s => s.GroupLabel, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

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
                    var config = this.LoadJson<PluginConfig>("config.json");
                    if (String.IsNullOrEmpty(config?.ClientId) || String.IsNullOrEmpty(config?.ClientSecret))
                    {
                        this.WriteConfigTemplate();
                        this.SetStatus(PluginStatus.Error, $"Add your Discord application's client_id and client_secret to {this.ConfigFilePath}");
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

        private async Task AuthenticateAsync(DiscordRpcClient client, PluginConfig config, CancellationToken ct)
        {
            var token = this.LoadJson<OAuthToken>("token.json");

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
                this.SetStatus(PluginStatus.Warning, "Approve the authorization popup in Discord");
                using var authorizeDoc = await client.RequestAsync("AUTHORIZE", new Dictionary<String, Object>
                {
                    ["client_id"] = config.ClientId,
                    ["scopes"] = OAuthScopes,
                    ["prompt"] = "consent",
                }, timeoutSeconds: 120).ConfigureAwait(false);

                var code = authorizeDoc.RootElement.GetProperty("data").GetProperty("code").GetString();
                token = await this.ExchangeTokenAsync(config, new Dictionary<String, String>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                }, ct).ConfigureAwait(false);

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
            this.SaveJson("token.json", token);
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
