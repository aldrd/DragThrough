#nullable enable

namespace ZombieBar;

/// <summary>
/// Single place for every external address the app links to. Change these for your
/// distribution - nothing here is read from a file or registry, it is all compiled in.
/// <para>
/// !!!Warning!!! Some settings duplicated in installer\build.ps1
/// </para>
/// </summary>
public static class AppLinks
{
    // GitHub repository base, e.g. "https://github.com/owner/repo". The project, releases
    // and issue links below are derived from it, so usually this is the only line to change.
    private const string RepoUrl = "https://github.com/aldrd/DragThrough";

    // Project home page, shared by the tray's "Share the app" submenu.
    public const string ProjectUrl = RepoUrl; // + "/releases/latest";

    // Releases page, opened as a fallback when an automatic install can't proceed.
    public const string ReleasesPageUrl = RepoUrl + "/releases";

    // "New issue" page of the repo, opened (pre-filled) by the tray's feedback form.
    public const string NewIssueUrl = RepoUrl + "/issues/new";

    // Static JSON manifest describing the latest release (schema: see Updater.UpdateManifest).
    // Host it anywhere static, e.g. the GitHub Pages of your repo.
    public const string PublishManifestUrl = RepoUrl + "/releases/latest/download/publish.json"; //"https://aldrd.github.io/DragThrough/publish.json";

    // "Buy me a coffee" donation page for the developer.
    public const string BuyMeACoffeeUrl = "https://buymeacoffee.com/redozubov";
}
