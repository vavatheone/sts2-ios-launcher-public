using SteamKit2;
using SteamKit2.Authentication;
using STS2Mobile.Steam.Abstractions;
using STS2Mobile.Steam.Auth;
using STS2Mobile.Steam.Connection;

namespace STS2Mobile.Launcher;

/// <summary>
/// Steam sign-in and ownership verification for the launcher. Only a refresh token is ever stored,
/// in protected storage. A password is handed straight to SteamKit, which encrypts it for Steam; it
/// is never stored or logged here.
/// </summary>
public sealed class SteamLoginSession : IDisposable
{
    private const string DeviceName = "STS2 Mobile";
    private const int ConnectAttempts = 3;
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(30);
    private readonly SteamCredentialStore _credentials;
    private readonly ILogger _logger;
    private readonly Func<string, string, CancellationToken, Task> _revoke;
    private SteamConnection? _connection;

    /// <summary>Steam is about to be asked whether the account owns the game. May be raised on any thread.</summary>
    public event Action? VerifyingOwnership;

    public SteamLoginSession(string dataDirectory, ISecureStorage secure, ILogger logger) : this(dataDirectory, secure, logger, null) { }

    /// <param name="revoke">Asks Steam to revoke a refresh token, given the account name and the token. Tests stand in for Steam here.</param>
    internal SteamLoginSession(string dataDirectory, ISecureStorage secure, ILogger logger, Func<string, string, CancellationToken, Task>? revoke)
    {
        _logger = logger;
        _revoke = revoke ?? RevokeWithSteamAsync;
        Directory.CreateDirectory(dataDirectory);
        _credentials = new SteamCredentialStore(dataDirectory, secure, logger);
        _credentials.Load();
    }

    public SteamConnection Connection => _connection ?? throw new InvalidOperationException("Not signed in.");

    /// <summary>Creates the Steam service object without connecting; online services connect only when first used.</summary>
    public SteamConnection GetConnectionForServices()
    {
        if (!_credentials.HasCredentials) throw new InvalidOperationException("Steam sign-in required.");
        return _connection ??= new SteamConnection(_credentials.AccountName!, _credentials.RefreshToken!, _logger);
    }

    public bool HasCachedOwnership => _credentials.HasVerifiedOwnership;

    public ulong SteamId => _credentials.VerifiedSteamId
        ?? Connection.Client.SteamID?.ConvertToUInt64()
        ?? throw new InvalidOperationException("No authenticated Steam ID.");

    public string AccountName => _credentials.AccountName ?? throw new InvalidOperationException("Not signed in.");

    /// <summary>
    /// The name the game shows for the local player: the account's Steam name, as other players see it, or the sign-in's account
    /// name if Steam has not sent it.
    /// </summary>
    public string PlayerName => _credentials.VerifiedPlayerName ?? Connection.PersonaName ?? AccountName;

    public bool HasStoredCredentials => _credentials.HasCredentials;

    /// <summary>
    /// For players who have Steam on a second device to scan the challenge.
    /// <paramref name="displayChallenge"/> receives the challenge each time Steam issues or rotates
    /// it and may be called from a background thread.
    /// </summary>
    public async Task SignInQrAsync(Action<string> displayChallenge, CancellationToken cancellationToken)
    {
        using (var steam = new SteamSignInClient())
        {
            await steam.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            QrAuthSession session = await steam.Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = DeviceName,
                IsPersistentSession = true
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            session.ChallengeURLChanged = () => displayChallenge(session.ChallengeURL);
            displayChallenge(session.ChallengeURL);
            AuthPollResult result = await WaitForApprovalAsync(session, steam, cancellationToken).ConfigureAwait(false);
            Retain(result);
        }
        await ConnectAndVerifyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The flow that works on a single phone: Steam Guard completes through <paramref name="guard"/>,
    /// where the player approves in the Steam app or types its code.
    /// </summary>
    public async Task SignInWithPasswordAsync(string accountName, string password, ISteamGuardPrompt guard, CancellationToken cancellationToken)
    {
        using (var steam = new SteamSignInClient())
        {
            await steam.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var authenticator = new SteamGuardAuthenticator(guard);
            CredentialsAuthSession session = await steam.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = accountName,
                Password = password,
                Authenticator = authenticator,
                DeviceFriendlyName = DeviceName,
                IsPersistentSession = true
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            AuthPollResult result;
            try
            {
                result = await session.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
            }
            // Once the player's part is done, an interrupted wait only means iOS suspended the launcher
            // while they were in the Steam app. Keep polling the same session rather than starting over.
            catch (Exception error) when (authenticator.Submitted && IsInterruptedWait(error, cancellationToken))
            {
                result = await WaitForApprovalAsync(session, steam, cancellationToken).ConfigureAwait(false);
            }
            Retain(result);
        }
        await ConnectAndVerifyAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Retain(AuthPollResult result)
    {
        // Only the account name and refresh token are kept. Steam's Guard data is not: nothing reads it, as a stored sign-in renews with the token alone.
        if (!_credentials.Save(result.AccountName, result.RefreshToken))
            throw new InvalidOperationException("Could not retain Steam authentication securely.");
    }

    private static bool IsInterruptedWait(Exception error, CancellationToken cancellationToken) =>
        error is OperationCanceledException or AsyncJobFailedException or TimeoutException && !cancellationToken.IsCancellationRequested;

    private static async Task<AuthPollResult> WaitForApprovalAsync(AuthSession session, SteamSignInClient steam, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await steam.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                if (await session.PollAuthSessionStatusAsync().ConfigureAwait(false) is { } result) return result;
            }
            // A poll that timed out or lost its connection is expected when iOS suspends the launcher
            // while the player approves in the Steam app. The challenge stays valid, so poll again.
            // An expired or rejected challenge surfaces as AuthenticationException and ends the attempt.
            catch (Exception error) when (IsInterruptedWait(error, cancellationToken)) { }
            await Task.Delay(session.PollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> VerifyOwnershipAsync(CancellationToken cancellationToken) =>
        HasCachedOwnership ? Task.FromResult(true) : VerifyLiveOwnershipAsync(cancellationToken);

    private Task<bool> VerifyLiveOwnershipAsync(CancellationToken cancellationToken) =>
        OwnershipVerifier.VerifyAsync(GetConnectionForServices()).WaitAsync(cancellationToken);

    public async Task ConnectAndVerifyAsync(CancellationToken cancellationToken)
    {
        if (!_credentials.HasCredentials) throw new InvalidOperationException("Steam sign-in required.");
        VerifyingOwnership?.Invoke();
        bool owns;
        for (int attempt = 1; ; attempt++)
        {
            _connection?.Dispose();
            _connection = new SteamConnection(_credentials.AccountName!, _credentials.RefreshToken!, _logger);
            try
            {
                // The connection's first sign-in blocks its caller; keep it off the UI thread.
                // Connecting can hold that thread for its whole timeout, and Back should not wait for it.
                owns = await Task.Run(() => VerifyLiveOwnershipAsync(cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            // A server that does not answer says nothing about the account or the network: the next connection tries another.
            catch (TimeoutException) when (attempt < ConnectAttempts)
            {
                _logger.Info("Connection", "Steam did not answer; retrying");
            }
            // Steam sometimes answers a valid sign-in by sending the client to another of its servers.
            // That says nothing about the account, so it is retried instead of shown as a failure.
            catch (SteamLoginFailedException busy) when (IsServerBusy(busy.Result) && attempt < ConnectAttempts)
            {
                _logger.Info("Connection", "Steam asked for another server (" + busy.Result + "); retrying");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        if (!owns) throw new UnauthorizedAccessException("The signed-in Steam account does not own Slay the Spire 2.");
        ulong steamId = Connection.Client.SteamID?.ConvertToUInt64()
            ?? throw new InvalidOperationException("Steam did not provide an authenticated account identity.");
        string playerName = Connection.PersonaName ?? AccountName;
        if (!_credentials.SaveVerifiedIdentity(steamId, playerName))
            _logger.Warn("Credentials", "Steam ownership was verified, but the identity could not be persisted for offline launch.");
    }

    /// <summary>
    /// Signs out of Steam on this device: asks Steam to revoke the stored refresh token, then forgets the sign-in here whatever Steam
    /// answered, or after <see cref="RevokeTimeout"/> without an answer. The game and the saves are not touched.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for Steam; the sign-in is still forgotten here.</param>
    public async Task<SignOutResult> SignOutAsync(CancellationToken cancellationToken)
    {
        // Another connection signed in with the same token could be what Steam ends first.
        Dispose();
        bool revoked = false;
        if (_credentials.HasCredentials)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(RevokeTimeout);
                await _revoke(_credentials.AccountName!, _credentials.RefreshToken!, wait.Token).ConfigureAwait(false);
                revoked = true;
            }
            catch (Exception error)
            {
                _logger.Warn("SignOut", "Steam did not confirm revoking the sign-in", error);
            }
        }
        bool forgotten = _credentials.Delete();
        _logger.Info("SignOut", $"Signed out (revoked={revoked}, forgotten={forgotten})");
        return new SignOutResult(revoked, forgotten);
    }

    /// <summary>A connection of its own, signed in with the token one last time. It is closed once the request ends, even after the wait gives up.</summary>
    private async Task RevokeWithSteamAsync(string accountName, string refreshToken, CancellationToken cancellationToken)
    {
        var connection = new SteamConnection(accountName, refreshToken, _logger);
        // Connecting blocks its caller, so it runs off the UI thread.
        Task revoke = Task.Run(connection.RevokeRefreshTokenAsync);
        _ = revoke.ContinueWith(_ => connection.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        await revoke.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Results that mean "this server cannot take you now", never "this account is refused".</summary>
    public static bool IsServerBusy(EResult result) => result is EResult.TryAnotherCM or EResult.ServiceUnavailable;

    public void Dispose() { _connection?.Dispose(); _connection = null; }
}
