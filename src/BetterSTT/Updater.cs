using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>A newer BetterSTT installer, downloaded and ready to run.</summary>
public sealed record UpdateInfo(Version Version, string InstallerPath);

/// <summary>
/// Finds new versions on the public GitHub Releases page, downloads the installer in the background and
/// runs it silently. The installer keeps settings, models and shortcuts, and reopens the app afterwards.
/// </summary>
public static class Updater
{
    const string LatestReleaseApi = "https://api.github.com/repos/yello-gun/BetterSTT/releases/latest";
    const string DownloadPrefix = "https://github.com/yello-gun/BetterSTT/releases/download/";
    const string InstallerPrefix = "BetterSTT-Setup-";

    static string Folder => Path.Combine(AppPaths.Data, "updates");
    /// <summary>The last version an install was started for, so a failing installer isn't rerun on every launch.</summary>
    static string AttemptFile => Path.Combine(Folder, "attempted.txt");

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Reads "v2.6.1", "2.6.1" or "2.6" as a version.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var m = Regex.Match(text ?? "", @"^v?(\d+)\.(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return true;
    }

    /// <summary>The newest downloaded installer that is newer than this version. Older ones are deleted.</summary>
    public static UpdateInfo? Ready()
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            UpdateInfo? best = null;
            foreach (string file in Directory.GetFiles(Folder, InstallerPrefix + "*.exe"))
            {
                if (!TryParseVersion(Path.GetFileNameWithoutExtension(file)[InstallerPrefix.Length..], out var v)) continue;
                if (v <= Current)
                {
                    TryDelete(file);
                    continue;
                }
                if (best == null || v > best.Version) best = new UpdateInfo(v, file);
            }
            if (best == null) TryDelete(AttemptFile);
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>False if an install of this version was already started and didn't take, so it isn't retried on its own.</summary>
    public static bool ShouldAutoInstall(UpdateInfo update)
    {
        try
        {
            return !File.Exists(AttemptFile) || File.ReadAllText(AttemptFile).Trim() != update.Version.ToString();
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Starts the installer silently. The caller must exit right away so its files can be replaced; the
    /// installer then reopens BetterSTT (with its window, or in the tray).
    /// </summary>
    public static bool Install(UpdateInfo update, bool openWindow)
    {
        try
        {
            File.WriteAllText(AttemptFile, update.Version.ToString());
            Log.Write($"Installing update {update.Version}");
            Process.Start(new ProcessStartInfo(update.InstallerPath,
                $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH={(openWindow ? "window" : "background")}")
            {
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not start the update installer: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Asks GitHub for the latest release and, if it's newer, downloads its installer. Returns the ready
    /// update, or null when this is the latest version.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BetterSTT/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi, ct));
        var release = ParseRelease(doc.RootElement);
        if (release == null || release.Version <= Current)
        {
            Ready(); // clears installers this version has caught up with
            return null;
        }
        if (Ready() is { } ready && ready.Version >= release.Version) return ready;

        Directory.CreateDirectory(Folder);
        string target = Path.Combine(Folder, $"{InstallerPrefix}{release.Version}.exe");
        string partial = target + ".part";
        Log.Write($"Downloading update {release.Version}");
        using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file, ct);
        }

        string? problem = Verify(partial, release);
        if (problem != null)
        {
            TryDelete(partial);
            throw new InvalidDataException(problem);
        }
        File.Move(partial, target, overwrite: true);
        Log.Write($"Update {release.Version} downloaded");
        return new UpdateInfo(release.Version, target);
    }

    public sealed record Release(Version Version, string Url, long Size, string? Sha256);

    /// <summary>The version and installer of a GitHub "latest release" response, or null if it has no installer yet.</summary>
    public static Release? ParseRelease(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tag) || !TryParseVersion(tag.GetString(), out var version)) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            // Only BetterSTT's own installer, only from this repository's releases.
            if (!Regex.IsMatch(name, @"^BetterSTT-Setup-[\d.]+\.exe$") || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                continue;
            long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            return new Release(version, url, size, sha);
        }
        return null;
    }

    /// <summary>Checks the download against the size and SHA-256 checksum GitHub lists for it.</summary>
    static string? Verify(string path, Release release)
    {
        long length = new FileInfo(path).Length;
        if (release.Size > 0 && length != release.Size)
            return $"The download was incomplete ({length} of {release.Size} bytes).";
        if (release.Sha256 != null)
        {
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                return "The download didn't match its published checksum.";
        }
        return null;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* in use or already gone */ }
    }
}
