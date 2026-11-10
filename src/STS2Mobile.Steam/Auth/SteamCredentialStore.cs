using System.Text.Json;
using STS2Mobile.Steam.Abstractions;

namespace STS2Mobile.Steam.Auth;

/// <summary>The signed-in account's name and refresh token, encrypted with <see cref="ISecureStorage"/>.</summary>
public sealed class SteamCredentialStore
{
    private const string Tag = "Credentials";
    private readonly string _path;
    private readonly ISecureStorage _secure;
    private readonly ILogger _logger;
    private SteamCredentials? _credentials;

    public SteamCredentialStore(string dataDirectory, ISecureStorage secure, ILogger logger)
    {
        _path = Path.Combine(dataDirectory, "steam_credentials.enc");
        _secure = secure;
        _logger = logger;
    }

    public string? AccountName => _credentials?.AccountName;

    public string? RefreshToken => _credentials?.RefreshToken;

    public bool HasCredentials => _credentials is { AccountName: not null, RefreshToken: not null };

    /// <summary>A locally retained identity whose game ownership was successfully verified by Steam.</summary>
    public bool HasVerifiedOwnership => _credentials is { OwnershipVerified: true, SteamId: > 0, PlayerName: not null };

    public ulong? VerifiedSteamId => HasVerifiedOwnership ? _credentials!.SteamId : null;

    public string? VerifiedPlayerName => HasVerifiedOwnership ? _credentials!.PlayerName : null;

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            string? json = _secure.Decrypt(File.ReadAllText(_path));
            if (json == null)
            {
                // The key is gone (for example after a restore to another device), so the file can never be read again.
                _logger.Warn(Tag, "Stored credentials could not be decrypted; removing them");
                File.Delete(_path);
                return;
            }
            _credentials = JsonSerializer.Deserialize(json, SteamStorageJsonContext.Default.SteamCredentials);
        }
        // The Keychain reports its own failures as InvalidOperationException.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logger.Warn(Tag, "Stored credentials could not be read", error);
            _credentials = null;
        }
    }

    /// <summary>Replaces the stored credentials. False when they could not be encrypted or written.</summary>
    public bool Save(string accountName, string refreshToken)
    {
        var credentials = new SteamCredentials { AccountName = accountName, RefreshToken = refreshToken };
        return Write(credentials);
    }

    /// <summary>Persists the identity only after Steam has confirmed ownership of the game.</summary>
    public bool SaveVerifiedIdentity(ulong steamId, string playerName)
    {
        if (steamId == 0 || string.IsNullOrWhiteSpace(playerName) || !HasCredentials) return false;
        var credentials = new SteamCredentials
        {
            AccountName = _credentials!.AccountName,
            RefreshToken = _credentials.RefreshToken,
            SteamId = steamId,
            PlayerName = playerName,
            OwnershipVerified = true
        };
        return Write(credentials);
    }

    private bool Write(SteamCredentials credentials)
    {
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            string? encrypted = _secure.Encrypt(JsonSerializer.Serialize(credentials, SteamStorageJsonContext.Default.SteamCredentials));
            if (encrypted == null)
            {
                _logger.Warn(Tag, "Credentials could not be encrypted");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, encrypted);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Warn(Tag, "Credentials could not be saved", error);
            return false;
        }
        finally
        {
            File.Delete(temporary);
        }
        _credentials = credentials;
        return true;
    }

    /// <summary>
    /// Forgets the sign-in: deletes the stored file, then the key it was encrypted with. The sign-in is gone from memory either way. Either
    /// deletion is enough on its own, since a file whose key is gone is never read again (<see cref="Load"/> removes it), so this is false
    /// only when neither could be done and the sign-in would come back at the next launch.
    /// </summary>
    public bool Delete()
    {
        _credentials = null;
        bool fileGone = true, keyGone = true;
        try
        {
            File.Delete(_path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.Warn(Tag, "Stored credentials could not be deleted", error);
            fileGone = false;
        }
        try
        {
            _secure.DeleteKey();
        }
        // The Keychain reports its own failures as InvalidOperationException.
        catch (InvalidOperationException error)
        {
            _logger.Warn(Tag, "The credentials' key could not be deleted", error);
            keyGone = false;
        }
        return fileGone || keyGone;
    }

    /// <summary>What the file holds. Files written before Steam Guard data was dropped still carry a GuardData field, which reading skips.</summary>
    internal sealed class SteamCredentials
    {
        public string? AccountName { get; set; }

        public string? RefreshToken { get; set; }

        // Optional for backward compatibility with credential files written by older app versions.
        public ulong? SteamId { get; set; }

        public string? PlayerName { get; set; }

        public bool OwnershipVerified { get; set; }
    }
}
