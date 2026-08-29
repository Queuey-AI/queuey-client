using System;
using System.Security.Cryptography;

namespace Queuey.Edge;

/// <summary>
/// Opt-in encryption of the payload BLOB at rest in the spool (AES-256-GCM).
///
/// <para>What it protects: the spool file, its WAL/SHM sidecars, a faulted
/// or salvaged copy, a disk pulled from a device, a backup that ignored the
/// "do not back up the spool" rule. What it does not protect: a process
/// that holds the key — the transfer loop must decrypt to send. The key is
/// therefore the customer's: read from an environment variable or a secret
/// store the device already has (a systemd credential, a TPM-backed
/// keychain). A key stored next to the spool protects nothing, and Edge
/// never writes one.</para>
///
/// <para>Wire shape, versioned so a later scheme can coexist:
/// <c>[0x01][12-byte nonce][ciphertext][16-byte tag]</c>. Rows carry
/// <c>payload_enc</c> (0 = plain, 1 = this scheme), so a spool written before
/// the key was set keeps draining, and a row that cannot be opened with the
/// configured key is quarantined — never sent, never silently dropped.</para>
/// </summary>
public static class SpoolPayloadProtection
{
    public const int KeyBytes = 32;
    public const byte SchemeAesGcmV1 = 1;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static byte[] Protect(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        RequireKey(key);

        var blob = new byte[1 + NonceBytes + plaintext.Length + TagBytes];
        blob[0] = SchemeAesGcmV1;
        var nonce = blob.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var cipher = blob.AsSpan(1 + NonceBytes, plaintext.Length);
        var tag = blob.AsSpan(1 + NonceBytes + plaintext.Length, TagBytes);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return blob;
    }

    /// <exception cref="CryptographicException">Wrong key, tampered blob, or an unknown scheme byte.</exception>
    public static byte[] Unprotect(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob)
    {
        RequireKey(key);
        if (blob.Length < 1 + NonceBytes + TagBytes || blob[0] != SchemeAesGcmV1)
            throw new CryptographicException("The spool row is not a payload this scheme can open.");

        var nonce = blob.Slice(1, NonceBytes);
        var cipher = blob.Slice(1 + NonceBytes, blob.Length - 1 - NonceBytes - TagBytes);
        var tag = blob.Slice(blob.Length - TagBytes, TagBytes);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    public static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
            throw new ArgumentException($"The spool payload key must be exactly {KeyBytes} bytes (got {key.Length}).");
    }
}
