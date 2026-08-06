using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

public enum InstallOutcome
{
    /// <summary>The installer ran; the user still has to reboot or replug for it to appear.</summary>
    InstallerLaunched,

    /// <summary>The user dismissed the Windows elevation prompt.</summary>
    CancelledByUser,

    /// <summary>Something went wrong; the download page was opened instead.</summary>
    FellBackToBrowser
}

/// <summary>
/// Offers to fetch VB-CABLE for the user.
///
/// Project Soundboard does not bundle the driver: VB-CABLE is donationware from VB-Audio and
/// redistributing it needs their permission. What we do instead is download their official
/// package, check it really is signed by VB-Audio, and hand it to Windows' own elevation
/// prompt so the user consents to the install at the OS level. Anything unexpected — a moved
/// URL, a failed signature, no network — falls back to simply opening the download page.
/// </summary>
public sealed class VirtualCableInstaller
{
    /// <summary>VB-Audio's product page. Always safe to open, and the fallback for everything.</summary>
    public const string DownloadPageUrl = "https://vb-audio.com/Cable/";

    /// <summary>
    /// Direct package URL. Versioned, so it will eventually rot — that is expected and
    /// handled, not a bug to chase.
    /// </summary>
    private const string PackageUrl =
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";

    /// <summary>Only a package signed by this publisher is ever launched.</summary>
    private const string RequiredPublisher = "VB-Audio";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public string? LastError { get; private set; }

    /// <summary>Where the package was unpacked, for the "show me the files" escape hatch.</summary>
    public string? ExtractedPath { get; private set; }

    public async Task<InstallOutcome> RunAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            progress?.Report("Downloading VB-CABLE from vb-audio.com…");
            var zipPath = await DownloadAsync(ct).ConfigureAwait(false);

            progress?.Report("Unpacking…");
            var folder = Extract(zipPath);
            ExtractedPath = folder;

            var setup = FindSetup(folder);
            if (setup is null)
            {
                LastError = "The downloaded package did not contain the expected installer.";
                return OpenDownloadPage();
            }

            progress?.Report("Checking the installer's signature…");
            if (!IsSignedByVbAudio(setup, out var signatureError))
            {
                // Refusing here is the whole point of downloading it ourselves rather than
                // just telling the user to go and find an installer.
                LastError = $"The download was not signed by VB-Audio and was not run. {signatureError}";
                Log.Warn(LastError);
                return OpenDownloadPage();
            }

            progress?.Report("Waiting for Windows to ask permission…");
            return Launch(setup);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn($"Automatic VB-CABLE install failed: {ex.Message}");
            return OpenDownloadPage();
        }
    }

    private static async Task<string> DownloadAsync(CancellationToken ct)
    {
        var target = Path.Combine(Path.GetTempPath(), "ProjectSoundboard-VBCABLE.zip");

        using var http = new HttpClient { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectSoundboard/1.0");

        using var response = await http.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(target);
        await source.CopyToAsync(file, ct).ConfigureAwait(false);

        return target;
    }

    private static string Extract(string zipPath)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ProjectSoundboard-VBCABLE");

        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch { /* a previous copy is still open; ZipFile will overwrite what it can */ }
        }

        Directory.CreateDirectory(folder);
        ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true);
        return folder;
    }

    /// <summary>Prefer the 64-bit setup, falling back to whatever setup executable is present.</summary>
    private static string? FindSetup(string folder)
    {
        var executables = Directory
            .EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
            .ToList();

        return executables.FirstOrDefault(f =>
                   Path.GetFileName(f).Contains("x64", StringComparison.OrdinalIgnoreCase))
               ?? executables.FirstOrDefault(f =>
                   Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase))
               ?? executables.FirstOrDefault();
    }

    /// <summary>
    /// Verify the Authenticode signature names VB-Audio and that the certificate chain is
    /// trusted by this machine.
    /// </summary>
    private static bool IsSignedByVbAudio(string path, out string error)
    {
        error = string.Empty;

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));

            if (!certificate.Subject.Contains(RequiredPublisher, StringComparison.OrdinalIgnoreCase))
            {
                error = $"It is signed by '{certificate.Subject}' instead.";
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            // Code signing certificates outlive the binaries they sign, so an expired
            // certificate on an otherwise valid chain is not a reason to refuse.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

            if (chain.Build(certificate)) return true;

            var problems = chain.ChainStatus
                .Where(s => s.Status != X509ChainStatusFlags.NoError)
                .Select(s => s.StatusInformation.Trim());

            error = $"Its certificate chain could not be validated: {string.Join("; ", problems)}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"The signature could not be read ({ex.Message}).";
            return false;
        }
    }

    private InstallOutcome Launch(string setupPath)
    {
        try
        {
            // "runas" is what raises the Windows elevation prompt. Installing a driver needs
            // administrator rights, which Project Soundboard itself deliberately never has.
            var process = Process.Start(new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? Path.GetTempPath()
            });

            if (process is null)
            {
                LastError = "Windows did not start the installer.";
                return OpenDownloadPage();
            }

            Log.Info("VB-CABLE installer launched.");
            return InstallOutcome.InstallerLaunched;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user said no at the UAC prompt. Not an error.
            Log.Info("VB-CABLE install declined at the elevation prompt.");
            return InstallOutcome.CancelledByUser;
        }
    }

    private InstallOutcome OpenDownloadPage()
    {
        OpenDownloadPageInBrowser();
        return InstallOutcome.FellBackToBrowser;
    }

    public static void OpenDownloadPageInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the download page: {ex.Message}");
        }
    }

    /// <summary>Open the unpacked folder so the user can run the installer by hand.</summary>
    public void ShowExtractedFolder()
    {
        if (ExtractedPath is null || !Directory.Exists(ExtractedPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo(ExtractedPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the extracted folder: {ex.Message}");
        }
    }
}
