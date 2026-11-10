using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// Composition root of the full-game host: one flow from signing in to the main menu. Presentation lives in
/// <see cref="LauncherView"/>, the save offer in <see cref="CloudSaveImportFlow"/>, the download in <see cref="GameInstallFlow"/>,
/// game start behind <see cref="GameLoader"/>, Steam in STS2Mobile.Launcher.
/// </summary>
public partial class Launcher : Control
{
    private const string Reopen = "The game is ready.\nClose the app and open it again from the Home Screen, and the game starts.";
    private const string ReopenAfterFailure = "Close the app, then open it again.";
    private const string NoGame = EntryScreen.NoGame;
    private const string Damaged = "The game's pack on this iPhone could not be opened. Download the game again.";
    private const string Unsupported = "This game version does not run with this app.\nOpen the app again and choose another version under Game version.";
    private const string OpenGameNext = "user://launcher/open-game-next-launch";
    private readonly CancellationTokenSource _stop = new();
    private readonly ShaderWarmup _warmup = new();
    private LauncherView _view = null!;
    private SteamLoginSession _login = null!;
    private SteamLobbies? _lobbies;
    private CloudSaveImportFlow _saves = null!;
    private GameInstaller _installer = null!;
    private GameInstallFlow _install = null!;
    private IGameEntry? _entry;
    // Why there is no game to start, shown on the first screen until the player gets one.
    private string? _noGameReason;
    private CancellationTokenSource? _attempt;
    private SteamGuardDialog? _guard;
    private bool _gameStarted;
    // The last attempt got past signing in and failed starting the game, so the sign-in is not what to blame.
    private bool _gameStartFailed;

    public override void _Ready()
    {
        SessionStats.Mark("launcher-ready");
        GetTree().Root.CallDeferred(Node.MethodName.AddChild, new SessionStats());
        LauncherOrientation.UsePortrait(GetWindow());
        _view = new LauncherView(this);
        GD.Print($"Screen kept on: {DisplayServer.ScreenIsKeptOn()}.");
        if (OS.GetEnvironment("STS2_SCREEN") is { Length: > 0 } preview)
        {
            LauncherPreview.Show(_view, preview);
            return;
        }
        if (GameMount.NeedsRelaunch)
        {
            ShowReopen(ReopenAfterFailure);
            return;
        }
        _view.ShowWorking("Preparing the game...");
        StartupAsync().FireAndForget("Launcher startup");
    }

    /// <summary>Nothing may load the game's assembly until preparation has made it, so the game comes after it.</summary>
    private async Task StartupAsync()
    {
        if (OS.GetEnvironment("STS2_CHECK_RELAY_RUNTIME") is { Length: > 0 } relayCheck) await RelayRuntimeCheck.RunAsync(relayCheck);
        if (OS.GetEnvironment("STS2_CHECK_VOICE") == "1") await VoiceRuntimeCheck.RunAsync();
        ModLaunchSettings.Capture(LauncherFolder, OS.GetEnvironment("STS2_MODS"));
        OS.SetEnvironment("STS2_MODS", ModLaunchSettings.Enabled ? "1" : "0");
        GD.Print($"Mods: {(ModLaunchSettings.Enabled ? "on" : "off")}; game assembly: {ModLaunchSettings.AssemblyFile}.");
        string game = GamePreparation.GameFolderOnDisk;
        _login = new SteamLoginSession(ProjectSettings.GlobalizePath("user://launcher"), new KeychainSecureStorage(), GodotSteamLogger.Instance);
        _saves = new CloudSaveImportFlow(_view, _login);
        _installer = new GameInstaller(game, GameProfile.Known, () => DeviceStorage.FreeBytes(Path.GetDirectoryName(game)!), folder => DeviceStorage.ExcludeFromBackup(folder));
        _install = new GameInstallFlow(_view, _login, _installer, game);
        WireScreens();
        WireCloudSaves();

        var prepared = await PrepareGameAsync();
        _modsPrepared = prepared.Status is PreparationStatus.Prepared or PreparationStatus.Cached;
        // QuickMatch that does not fit this game: the game starts without it, and the player is told why.
        if (prepared.Notice != null && ModLaunchSettings.Enabled) _view.SetNotice(Notice.Failure("Quick Match is off", prepared.Notice));
        if (prepared.ModRefused && ModLaunchSettings.Enabled)
        {
            _modRefusal = prepared.Message;
            ShowMods();
            return;
        }
        if (prepared.Status == PreparationStatus.Prepared)
        {
            await FinishSetupAsync();
            return;
        }
        _noGameReason = prepared.Status switch
        {
            PreparationStatus.Refused => prepared.Message,
            PreparationStatus.NoGame => NoGame,
            _ => GameLoader.GameIsPresent ? null : Damaged,
        };
        // A warmup that a kill cut short, or a game whose shaders changed: it ends in a reopen like the first one, so the game loads after that.
        if (_noGameReason == null && ShaderWarmup.IsNeeded)
        {
            await FinishSetupAsync();
            return;
        }
        if (_noGameReason == null) _entry = LoadGame();
        if (TakeOpenGameNext() && _noGameReason == null && _login.HasStoredCredentials)
        {
            _view.Step = SetupStep.Play;
            Play();
        }
        else ShowEntry();
    }

    private void WireScreens()
    {
        _login.VerifyingOwnership += () => Callable.From(ShowVerifying).CallDeferred();
        _view.PasswordSignInRequested += (account, password) => RunAsync(SetupStep.SignIn, "Connecting to Steam...", attempt =>
        {
            _guard = new SteamGuardDialog(_view, attempt);
            return _login.SignInWithPasswordAsync(account, password, _guard, attempt);
        }, AfterSignInAsync).FireAndForget("Password sign-in");
        _view.QrSignInRequested += () => RunAsync(SetupStep.SignIn, "Connecting to Steam...", attempt =>
            _login.SignInQrAsync(challenge => Callable.From(() => ShowChallenge(challenge, attempt)).CallDeferred(), attempt), AfterSignInAsync)
            .FireAndForget("QR sign-in");
        _view.GuardCodeSubmitted += code => _guard?.Submit(code);
        _view.BackRequested += GoBack;
        _view.PlayRequested += Play;
        _view.ManageRequested += ShowManage;
        _view.ModsToggleRequested += ToggleMods;
        WireMods();
        _view.VersionActionRequested += OnVersionAction;
        _view.SignInAgainRequested += () => _view.ShowStart(_noGameReason == null ? EntryScreen.Welcome : EntryScreen.WelcomeNoGame, canGoBack: true);
        _view.SignOutRequested += () => RunAsync(null, "", _ => Task.CompletedTask, SignOutAsync).FireAndForget("Sign out");
        _view.CloudGetRequested += () => RunAsync(null, "Connecting to Steam...", _login.ConnectAndVerifyAsync, GetCloudSavesAsync).FireAndForget("Cloud saves");
        _view.DownloadRequested += carryOn => RunAsync(SetupStep.Ownership, "Verifying Steam ownership...", _login.ConnectAndVerifyAsync, carryOn ? AfterSignInAsync : ChooseVersionAsync).FireAndForget("Download");
        _view.DeleteRequested += () => RunAsync(null, "", _ => Task.CompletedTask, DeleteGameAsync).FireAndForget("Delete");
    }

    /// <summary>Signing in has finished, or was not needed: Steam is being asked about the game.</summary>
    private void ShowVerifying()
    {
        if (_attempt == null) return;
        // Leaves a launch past setup, and the last step of one going straight to the game, as they are.
        if (_view.Step != SetupStep.Play && (_noGameReason != null || _view.Step != null)) _view.Step = SetupStep.Ownership;
        _view.ShowProgress("Verifying Steam ownership...", -1, canGoBack: true);
    }

    private void Play() => RunAsync(_view.Step == null ? null : SetupStep.Play,
        _login.HasCachedOwnership ? "Starting the game..." : "Verifying Steam ownership...",
        _login.HasCachedOwnership ? _ => Task.CompletedTask : _login.ConnectAndVerifyAsync,
        StartGameAsync).FireAndForget("Play");

    /// <summary>Makes the game's assembly and pack from what is in the folder, offering to try again if that fails.</summary>
    private async Task<PreparationResult> PrepareGameAsync()
    {
        while (true)
        {
            try
            {
                return await GamePreparation.RunAsync((done, total) => Callable.From(() => ShowTextures(done, total)).CallDeferred());
            }
            catch (Exception error)
            {
                GD.PushError("Game preparation failed: " + error);
                var answer = new TaskCompletionSource<bool>();
                void Answered(bool first) => answer.TrySetResult(first);
                _view.ChoiceMade += Answered;
                // What it already did is kept, so trying again does not start over.
                _view.ShowChoice(PreparationFailure(error), "Try again");
                await answer.Task;
                _view.ChoiceMade -= Answered;
                _view.ShowProgress("Preparing the game...", -1);
            }
        }
    }

    private static string PreparationFailure(Exception error) => error switch
    {
        IOException { HResult: 28 } => "This iPhone ran out of space while preparing the game. Free up some space and try again.",
        _ => "The game could not be prepared (" + error.GetType().Name + "). What was done is kept, so trying again carries on.",
    };

    private void ShowTextures(int done, int total)
    {
        _view.Step = SetupStep.Prepare;
        if (done < total) _view.ShowProgress($"Converting the game's textures for this iPhone: {done} of {total}.\nThis happens once. If you leave this app, it carries on from here when you come back.", done / (double)total);
        else _view.ShowProgress("Writing the game's pack.\nThis happens once and is nearly done.", -1);
    }

    /// <summary>
    /// The pack is new, or the warmup did not finish: mounts what preparation made, warms up the shaders, and asks for the one reopen. The warmup runs before the
    /// reopen because the phone's GPU may stop drawing during it until the app starts again, so this way that costs no second one.
    /// </summary>
    private async Task FinishSetupAsync()
    {
        if (!GameMount.MountPrepared())
        {
            _noGameReason = Damaged;
            ShowEntry();
            return;
        }
        if (ShaderWarmup.IsNeeded)
        {
            _view.Step = SetupStep.Effects;
            if (_warmup.GetParent() == null) AddChild(_warmup);
            // Behind the launcher's own screen, which was there first.
            MoveChild(_warmup, 0);
            try { await WarmShadersAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
        }
        ShowReopen(Reopen, openGameNext: true);
    }

    /// <summary>The screen the player closes the app from.</summary>
    /// <param name="openGameNext">The next launch goes straight on to the game, if there is a sign-in to do that with. Not after a failure, which would only repeat it.</param>
    private void ShowReopen(string status, bool openGameNext = false)
    {
        _view.Step = openGameNext ? SetupStep.Reopen : null;
        if (openGameNext && _login?.HasStoredCredentials == true)
        {
            try { File.WriteAllText(ProjectSettings.GlobalizePath(OpenGameNext), ""); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { GD.PushError("Could not note the next launch: " + error.Message); }
        }
        _view.ShowReopen(status);
    }

    private static bool TakeOpenGameNext()
    {
        string path = ProjectSettings.GlobalizePath(OpenGameNext);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>The first screen for where things stand. Nothing goes to Steam until the player chooses.</summary>
    /// <param name="failed">The message is why the last attempt failed, so the stored sign-in may need replacing.</param>
    private void ShowEntry(string? message = null, bool failed = false)
    {
        bool underWay = _installer.Pending != null;
        // The player left a download or it stopped: the game that was here may be gone.
        if (underWay) _noGameReason ??= NoGame;
        bool signedIn = _login.HasStoredCredentials;
        var (version, branch, update) = _noGameReason == null ? InstalledSummary() : default;
        var screen = EntryScreen.For(new LauncherFacts(signedIn, signedIn ? _login.AccountName : null, _noGameReason, underWay, version, branch, update, ModLaunchSettings.Enabled), message, failed);
        _view.Step = screen.Kind switch
        {
            EntryKind.SignIn => _noGameReason != null ? SetupStep.SignIn : null,
            EntryKind.GetGame => SetupStep.Download,
            _ => null,
        };
        _view.ShowEntry(screen);
    }

    private void ToggleMods()
    {
        bool enabled = !ModLaunchSettings.Enabled;
        try
        {
            ModLaunchSettings.Write(LauncherFolder, enabled);
            GD.Print($"Mods saved: {(enabled ? "on" : "off")}; reopen required.");
            ShowReopen($"Mods will be {(enabled ? "on" : "off")}.\nClose the app and open it again to play {(enabled ? "with" : "without")} mods.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _view.SetNotice(Notice.Failure("Mods unchanged", "The choice could not be saved. Try again."));
            ShowMods();
        }
    }

    private async Task GetCloudSavesAsync(CancellationToken attempt)
    {
        var (result, status) = await _saves.GetAsync(attempt);
        _view.SetNotice(result);
        ShowEntry(status);
    }

    /// <summary>Runs one attempt: connecting, then what the player asked for. Only one runs at a time.</summary>
    /// <param name="step">The step this attempt is, on a launch still in setup. Null leaves the row of steps as it is.</param>
    private async Task RunAsync(SetupStep? step, string status, Func<CancellationToken, Task> signIn, Func<CancellationToken, Task> then)
    {
        if (_attempt != null) return;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _attempt = attempt;
        // Something new has started, so how the last thing ended no longer applies.
        _view.SetNotice(null);
        if (step != null && (_noGameReason != null || _view.Step != null)) _view.Step = step;
        if (status.Length > 0) _view.ShowProgress(status, -1, canGoBack: true);
        try
        {
            await signIn(attempt.Token);
            await then(attempt.Token);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            ShowEntry();
        }
        catch (Exception error)
        {
            ShowFailure(error);
        }
        finally
        {
            _attempt = null;
            _guard?.Dispose();
            _guard = null;
        }
    }

    /// <summary>Signed in: the game if there is one, otherwise the way to get one.</summary>
    private Task AfterSignInAsync(CancellationToken attempt)
    {
        if (_noGameReason == null) return StartGameAsync(attempt);
        _view.Step = SetupStep.Download;
        return _installer.Pending is { } pending ? FinishAsync(_install.ContinueAsync(pending, attempt)) : ChooseVersionAsync(attempt);
    }

    private Task ChooseVersionAsync(CancellationToken attempt)
    {
        _view.Step = SetupStep.Download;
        return FinishAsync(_install.ChooseAndDownloadAsync(attempt));
    }

    /// <summary>After a download: makes the assembly and pack and warms up, in this launch, ending at the reopen. A download that stopped goes back to the first screen, with why.</summary>
    private async Task FinishAsync(Task<string?> download)
    {
        if (await download is { } failure)
        {
            _view.SetNotice(GameInstallMessages.NotDownloaded(failure));
            ShowEntry();
            return;
        }
        // The game that was there is gone, so nothing can go back to it.
        _noGameReason = NoGame;
        _view.Step = SetupStep.Prepare;
        _view.ShowProgress("Patching the game's code for this iPhone.", -1);
        var prepared = await PrepareGameAsync();
        if (prepared.Status == PreparationStatus.Prepared) await FinishSetupAsync();
        else
        {
            _view.SetNotice(Notice.Failure("Not prepared", prepared.Message ?? "The downloaded game could not be prepared. Download the game again."));
            ShowEntry();
        }
    }

    private async Task DeleteGameAsync(CancellationToken attempt)
    {
        await _install.DeleteAsync(attempt);
        _noGameReason = NoGame;
        _view.SetNotice(GameInstallMessages.DeletedNotice);
        ShowEntry();
    }

    /// <summary>
    /// Asks first, then signs out: Steam is asked to end the sign-in, and the sign-in is deleted here even if Steam cannot be told. Ends on
    /// the sign-in screen with how it went. Back on the question leaves everything as it was.
    /// </summary>
    private async Task SignOutAsync(CancellationToken attempt)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Answered(bool first) => answer.TrySetResult(first);
        _view.ChoiceMade += Answered;
        try
        {
            _view.ShowChoice(SignOut.Question, SignOut.ConfirmLabel, SignOut.CancelLabel, canGoBack: true, heading: SignOut.Heading);
            if (!await answer.Task.WaitAsync(attempt))
            {
                ShowEntry();
                return;
            }
        }
        finally { _view.ChoiceMade -= Answered; }
        _view.ShowWorking(SignOut.Working);
        var result = await _login.SignOutAsync(attempt);
        _view.SetNotice(SignOut.Result(result));
        ShowEntry();
    }

    private async Task StartGameAsync(CancellationToken attempt)
    {
        // A connection already made for the initial ownership check may offer Cloud saves as before.
        // A cached/offline launch must never wait for Steam Cloud before entering the game.
        if (_login.IsConnected) await _saves.OfferAsync(attempt);
        _view.ShowProgress("Starting the game...", -1);
        RuntimeServices.BindOwnershipVerifier(() => _login.VerifyOwnershipAsync(_stop.Token));
        RuntimeServices.BindAccount(_login.SteamId, _login.PlayerName);
        // Steam lobbies for a built-in multiplayer mod. Binding sends nothing; the first lobby request connects.
        _lobbies = new SteamLobbies(_login.GetConnectionForServices(), _login.SteamId, GodotSteamLogger.Instance);
        RuntimeServices.BindLobbies(_lobbies);
        SessionStats.Mark("game-start");
        try
        {
            await LauncherOrientation.UseLandscapeAsync(GetWindow());
            await StartEntryAsync();
        }
        catch when (_entry?.GameCreated != true)
        {
            // The game did not begin, so the player can try again, perhaps with another account, back on the launcher's portrait screens.
            RuntimeServices.Unbind();
            _lobbies?.Dispose();
            _lobbies = null;
            LauncherOrientation.UsePortrait(GetWindow());
            _gameStartFailed = true;
            throw;
        }
        _gameStarted = true;
        _view.Hide();
        CheckVersionsInBackgroundAsync().FireAndForget("Background Steam version check");
        GD.Print("The game reached its main menu.");
        SessionStats.Mark("main-menu");
    }

    /// <summary>Starts the game. <c>STS2_START_FAILURE</c> stands in for a failure at this point, to test the words shown with nobody there.</summary>
    private Task StartEntryAsync() => OS.GetEnvironment("STS2_START_FAILURE") switch
    {
        "unsupported" => throw new MissingMethodException("Test of the unsupported version message."),
        "start" => throw new InvalidOperationException("Test of a failure before the game begins."),
        _ => _entry!.StartAsync(),
    };

    /// <summary>Compiles shaders ahead of play. A failure costs smoothness, never the game. The progress is on UIKit's screen, since the engine's may stop drawing.</summary>
    private async Task WarmShadersAsync(CancellationToken attempt)
    {
        try
        {
            await _warmup.RunAsync((done, total) =>
            {
                if (done % 10 == 0 || done == total)
                    GD.Print($"Warmup: {done} of {total} at {DateTime.Now:HH:mm:ss}. Footprint {MemoryFootprint.Read().Current / (1024 * 1024)} MB, thermal state {DevicePower.ThermalState()}.");
                _view.ShowOverEngine(
                $"Preparing effects for this iPhone so the game does not pause during play: step {done} of {total}.\nThis happens once and takes a few minutes. If you leave this app, it carries on from here when you come back.", done / (double)Math.Max(total, 1));
            }, attempt);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GD.PushError("Shader warmup stopped early: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void ShowChallenge(string challenge, CancellationToken attempt)
    {
        if (attempt.IsCancellationRequested) return;
        using Image code = LauncherView.RenderChallenge(challenge);
        _view.ShowQr(code, "Scan this code with the Steam app on another device and approve the sign-in.");
    }

    /// <summary>Back leaves an attempt that is waiting on the player, or returns from a form to the first screen.</summary>
    private void GoBack()
    {
        if (_modQuestion != null) { _modQuestion.TrySetResult(false); return; }
        if (_attempt != null) _attempt.Cancel();
        else
        {
            _view.SetNotice(null);
            ShowEntry();
        }
    }

    private void ShowFailure(Exception error)
    {
        GD.PushError("Full-game startup failed: " + error);
        // A tested build's references were checked on the Mac, so for one this should not happen. An untested build whose game no longer has what the
        // host code calls ends here: a plain answer, and the way to another version.
        if (error is MissingMemberException or TypeLoadException)
            ShowReopen(Unsupported);
        else if (_entry?.GameCreated == true)
            ShowReopen("The game could not finish starting (" + error.GetType().Name + ").\n" + ReopenAfterFailure);
        else
        {
            bool atStart = _gameStartFailed;
            _gameStartFailed = false;
            _view.SetNotice(atStart
                ? Notice.Failure("Game did not start", SignInFailure.DescribeGameStart(error))
                : Notice.Failure("Could not continue", SignInFailure.Describe(error)));
            ShowEntry(failed: !atStart);
        }
    }

    public override void _Notification(int what)
    {
        // Against the progress lines' times, these show whether iOS really stopped the work while the player was away.
        if (what == NotificationApplicationPaused) GD.Print($"App left at {DateTime.Now:HH:mm:ss}.");
        else if (what == NotificationApplicationResumed) GD.Print($"App back at {DateTime.Now:HH:mm:ss}.");
        if (what == NotificationApplicationPaused && _gameStarted) _entry!.FlushSaves();
    }

    public override void _ExitTree()
    {
        _stop.Cancel();
        // Leave any lobby while the connection is still there, so other players see us go.
        _lobbies?.Dispose();
        _login?.Dispose();
    }
}
