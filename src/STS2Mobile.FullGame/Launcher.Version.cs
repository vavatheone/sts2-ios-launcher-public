using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.GamePatcher;
using STS2Mobile.Launcher;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// The game version: what the home screen's row says, the version screen with Steam's current builds, and Update and Switch.
/// Steam is asked only when the player opens the screen; nothing is downloaded until they confirm.
/// </summary>
public partial class Launcher
{
    // Steam's builds as the version screen last read them in this launch, or null when they could not be read.
    private BuildsOnSteam? _steamNow;

    private static string LauncherFolder => ProjectSettings.GlobalizePath("user://launcher");

    private static InstalledBuild? Installed => InstalledBuild.Read(GamePreparation.GameFolderOnDisk);

    /// <summary>The home row: the version, its branch, and whether the version screen last saw another build on Steam.</summary>
    private static (string? Version, string? Branch, bool Update) InstalledSummary()
    {
        if (Installed is not { } installed) return default;
        string branch = InstalledBuild.BranchName(installed.Branch);
        return (installed.Version, branch.Length > 0 ? branch : null, VersionScreen.IsUpdateAvailable(installed, SteamBuildsSeen.Read(LauncherFolder)));
    }

    /// <summary>
    /// Checks Steam's builds after the game has reached its main menu. This is deliberately detached from startup:
    /// offline play is already running, and a failed or unavailable Steam request changes nothing.
    /// </summary>
    private async Task CheckVersionsInBackgroundAsync()
    {
        try
        {
            CancellationToken cancellationToken = _stop.Token;
            await _login.ConnectAndVerifyAsync(cancellationToken);
            BuildsOnSteam builds = await _install.ReadBuildsAsync(cancellationToken);
            try { SteamBuildsSeen.Write(LauncherFolder, builds); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                GD.PushError("Steam's background builds could not be kept: " + error.Message);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            GD.Print("Background Steam version check skipped: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>The Game version row: asks Steam which builds it has now, then shows the version screen, with the game here even if Steam cannot be reached.</summary>
    private void ShowManage() =>
        RunAsync(null, "Asking Steam which versions it has...", _ => Task.CompletedTask, CheckVersionsAsync).FireAndForget("Game version");

    private async Task CheckVersionsAsync(CancellationToken attempt)
    {
        string? failure = null;
        try
        {
            await _login.ConnectAndVerifyAsync(attempt);
            _steamNow = await _install.ReadBuildsAsync(attempt);
            try { SteamBuildsSeen.Write(LauncherFolder, _steamNow); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { GD.PushError("Steam's builds could not be kept: " + error.Message); }
        }
        catch (Exception error) when (error is not OperationCanceledException || !attempt.IsCancellationRequested)
        {
            GD.PushError("Steam's builds could not be read: " + error);
            _steamNow = null;
            failure = error.GetType().Name;
        }
        _view.ShowVersion(VersionScreen.For(new VersionFacts(Installed, _steamNow, failure)));
    }

    /// <summary>Update and Switch replace the game with Steam's current build of a branch, as a download from the first screen does, then prepare it.</summary>
    private void OnVersionAction(VersionAction action)
    {
        if (action == VersionAction.CheckAgain)
        {
            ShowManage();
            return;
        }
        var installed = Installed;
        string branch = VersionScreen.BranchOf(action, installed);
        GD.Print($"Version: {action} to {(branch.Length > 0 ? branch : "?")} from {(installed == null ? "an unknown game" : GameInstallMessages.Describe(installed))}.");
        RunAsync(null, "Verifying Steam ownership...", _login.ConnectAndVerifyAsync, attempt =>
        {
            _view.Step = SetupStep.Download;
            return FinishAsync(_install.ReplaceAsync(branch, installed, attempt));
        }).FireAndForget(action.ToString());
    }

    /// <summary>
    /// Loads the game's assembly and the host. An untested build that lacks what the host code is built against fails here rather than at Play:
    /// the first screen then says so, with the way to another version.
    /// </summary>
    private IGameEntry? LoadGame()
    {
        try { return GameLoader.Load(this); }
        catch (Exception error) when (error is TypeLoadException or MissingMemberException or ReflectionTypeLoadException or FileNotFoundException or BadImageFormatException)
        {
            GD.PushError("The game could not be loaded: " + error);
            _noGameReason = $"This game version does not run with this app ({error.GetType().Name}). Download another version.";
            return null;
        }
    }
}
