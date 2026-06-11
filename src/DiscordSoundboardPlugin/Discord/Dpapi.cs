namespace Loupedeck.DiscordSoundboardPlugin.Discord
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    // Encrypts secrets at rest with Windows DPAPI (CurrentUser scope): the resulting blobs
    // can only be decrypted by the same Windows user on the same machine, so copying the
    // plugin data folder elsewhere yields nothing usable. Returns null where DPAPI is
    // unavailable (e.g. macOS) so callers can fall back to plaintext rather than break.

    internal static class Dpapi
    {
        private static readonly Byte[] Entropy = Encoding.UTF8.GetBytes("Loupedeck.DiscordSoundboardPlugin.v1");

        public static String TryProtect(String plaintext)
        {
            try
            {
                var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(blob);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "DPAPI protect failed; falling back to plaintext storage");
                return null;
            }
        }

        public static String TryUnprotect(String base64)
        {
            try
            {
                var blob = ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(blob);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "DPAPI unprotect failed (data from another user/machine?)");
                return null;
            }
        }
    }
}
