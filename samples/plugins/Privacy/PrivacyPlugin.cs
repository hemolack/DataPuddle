using System.Security.Cryptography;
using System.Text;
using DataPuddle.Plugins;

namespace DataPuddle.Privacy;

/// <summary>
/// Sample plugin: de-identification and encryption helpers for SQL.
///
///   SELECT hash_value(ssn), mask_value(ssn), encrypt_value(name) FROM dbo.patient;
///
/// Settings (Plugins:Settings:Privacy):
///   Key       base64 of 32 random bytes. Required by every function. Prefer the environment variable
///             Plugins__Settings__Privacy__Key over putting it in a file.
///   MaskKeep  how many trailing characters mask_value leaves visible. Default 4.
/// </summary>
public sealed class PrivacyPlugin : IPuddlePlugin {
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private byte[] _key = Array.Empty<byte>();
    private int _maskKeep = 4;

    public string Name {
        get {
            return "Privacy";
        }
    }

    public string Description {
        get {
            return "hash_value, mask_value, encrypt_value and decrypt_value for de-identifying PHI";
        }
    }

    public void Initialize(IPluginContext context) {
        if (context.Settings.TryGetValue("MaskKeep", out string? keepText) && int.TryParse(keepText, out int keep) && keep >= 0) {
            _maskKeep = keep;
        }

        // Masking needs no key.
        context.RegisterStringFunction("mask_value", (string? value) => Mask(value),
            "Replaces all but the last few characters with '*'.");

        if (!context.Settings.TryGetValue("Key", out string? keyText) || string.IsNullOrWhiteSpace(keyText)) {
            context.Log("No Key configured; hash_value, encrypt_value and decrypt_value are not available. " +
                "Set Plugins__Settings__Privacy__Key to base64 of 32 random bytes.");
            return;
        }

        try {
            _key = Convert.FromBase64String(keyText);
        } catch (FormatException) {
            throw new InvalidOperationException("The Key setting is not valid base64.");
        }
        if (_key.Length != 32) {
            throw new InvalidOperationException("The Key setting must be base64 of exactly 32 bytes.");
        }

        context.RegisterStringFunction("hash_value", (string? value) => Hash(value),
            "Keyed one-way hash (HMAC-SHA256, hex). The same input always gives the same output, so joins still work.");
        context.RegisterStringFunction("encrypt_value", (string? value) => Encrypt(value),
            "AES-256-GCM encryption, returned as base64. A new random nonce is used each call.");
        context.RegisterStringFunction("decrypt_value", (string? value) => Decrypt(value),
            "Reverses encrypt_value. Fails if the text was altered or the key is different.");
    }

    private string? Mask(string? value) {
        if (value == null) {
            return null;
        }
        if (value.Length <= _maskKeep) {
            return new string('*', value.Length);
        }
        return new string('*', value.Length - _maskKeep) + value.Substring(value.Length - _maskKeep);
    }

    private string? Hash(string? value) {
        if (value == null) {
            return null;
        }
        byte[] hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string? Encrypt(string? value) {
        if (value == null) {
            return null;
        }
        byte[] plain = Encoding.UTF8.GetBytes(value);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using (AesGcm aes = new AesGcm(_key, TagSize)) {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        byte[] packed = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, packed, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(packed);
    }

    private string? Decrypt(string? value) {
        if (value == null) {
            return null;
        }
        byte[] packed = Convert.FromBase64String(value);
        if (packed.Length < NonceSize + TagSize) {
            throw new CryptographicException("The value is too short to be encrypted text.");
        }
        byte[] nonce = packed[..NonceSize];
        byte[] tag = packed[NonceSize..(NonceSize + TagSize)];
        byte[] cipher = packed[(NonceSize + TagSize)..];
        byte[] plain = new byte[cipher.Length];
        using (AesGcm aes = new AesGcm(_key, TagSize)) {
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        return Encoding.UTF8.GetString(plain);
    }
}
