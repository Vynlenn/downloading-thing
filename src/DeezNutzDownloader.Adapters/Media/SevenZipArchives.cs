using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Adapters.Media;

/// <summary>The archive needs a password, or the one given was wrong.</summary>
public sealed class ArchivePasswordRequiredException : Exception
{
    public ArchivePasswordRequiredException(string archivePath, bool wrongPassword)
        : base(wrongPassword
            ? $"The password for {Path.GetFileName(archivePath)} is wrong."
            : $"{Path.GetFileName(archivePath)} is password-protected.")
    {
        ArchivePath = archivePath;
        WrongPassword = wrongPassword;
    }

    public string ArchivePath { get; }

    public bool WrongPassword { get; }
}

public sealed record SevenZipEntry(string Path, bool IsFolder, long Size, bool Encrypted);

/// <summary>
/// RAR and 7z extraction through an installed 7-Zip (7z.exe).
/// Same rules as the built-in ZIP extractor: every entry name is
/// checked before anything is written, the whole archive must fit
/// on the drive, existing files are never overwritten, and the
/// archive's Mark of the Web is carried onto what comes out.
/// </summary>
public sealed class SevenZipArchives
{
    private const int MaximumEntryCount = 100_000;

    // 7-Zip treats a password on an unencrypted archive as unused,
    // and a wrong one on an encrypted archive as "wrong password":
    // passing a placeholder keeps it from ever waiting on a prompt.
    private const string NoPasswordPlaceholder = "dnd-no-password";

    private static readonly Regex PartNumber = new(
        @"\.part0*(\d+)\.rar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _executable;

    public SevenZipArchives(string executable)
    {
        _executable = executable;
    }

    /// <summary>
    /// The configured 7z.exe, else a standard 7-Zip install, else
    /// 7z.exe on PATH. Null when 7-Zip is not installed.
    /// </summary>
    public static string? FindExecutable(string? configuredPath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath.Trim().Trim('"'));
        }

        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86
                 })
        {
            string root = Environment.GetFolderPath(folder);

            if (!string.IsNullOrEmpty(root))
            {
                candidates.Add(Path.Combine(root, "7-Zip", "7z.exe"));
            }
        }

        foreach (string directory in
                 (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(directory.Trim(), "7z.exe"));
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// .rar, .7z and the first part of a split set (.part1.rar,
    /// .7z.001). Later parts are opened through the first one.
    /// </summary>
    public static bool IsSupported(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string name = System.IO.Path.GetFileName(path);

        if (name.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Match part = PartNumber.Match(name);
        return !part.Success || part.Groups[1].Value == "1";
    }

    public static string DefaultOutputDirectory(string archivePath)
    {
        string name = System.IO.Path.GetFileName(archivePath);
        string stem = Regex.Replace(
            name,
            @"(\.part0*1)?\.rar$|\.7z(\.001)?$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(archivePath)) ?? ".",
            stem + ".extracted");
    }

    public async Task<IReadOnlyList<SevenZipEntry>> ListAsync(
        string archivePath,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(
                new[]
                {
                    "l", "-slt", "-sccUTF-8",
                    "-p" + (password ?? NoPasswordPlaceholder),
                    "--", archivePath
                },
                TimeSpan.FromMinutes(2),
                cancellationToken)
            .ConfigureAwait(false);

        ThrowIfPasswordProblem(result, archivePath, password);

        if (result.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"7-Zip could not read {System.IO.Path.GetFileName(archivePath)}: " +
                Tail(result.Output));
        }

        return ParseListing(result.Output);
    }

    public async Task<ArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string? password,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string fullArchive = System.IO.Path.GetFullPath(archivePath);
        IReadOnlyList<SevenZipEntry> entries =
            await ListAsync(fullArchive, password, cancellationToken).ConfigureAwait(false);

        if (entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException(
                $"The archive has {entries.Count:N0} entries " +
                $"(limit {MaximumEntryCount:N0}); it was not extracted.");
        }

        // Refuse the whole archive rather than let 7-Zip decide how
        // to write a hostile name (traversal, ADS, device names).
        SevenZipEntry? unsafeEntry = entries.FirstOrDefault(
            entry => ArchiveExtraction.SafeRelativePath(entry.Path) is null);

        if (unsafeEntry is not null)
        {
            throw new InvalidDataException(
                $"The archive contains an unsafe path ({unsafeEntry.Path}); " +
                "it was not extracted.");
        }

        if (entries.Any(entry => entry.Encrypted) && password is null)
        {
            throw new ArchivePasswordRequiredException(fullArchive, wrongPassword: false);
        }

        string output = outputDirectory is null
            ? DefaultOutputDirectory(fullArchive)
            : System.IO.Path.GetFullPath(outputDirectory);

        long declared = entries.Where(entry => !entry.IsFolder).Sum(entry => Math.Max(0, entry.Size));
        Directory.CreateDirectory(output);

        string? root = System.IO.Path.GetPathRoot(output);

        if (!string.IsNullOrEmpty(root))
        {
            long free = new DriveInfo(root).AvailableFreeSpace;

            if (declared > free)
            {
                throw new IOException(
                    $"The archive unpacks to {declared / (1024d * 1024 * 1024):0.##} GB " +
                    "but the drive does not have that much free space; it was not extracted.");
            }
        }

        HashSet<string> before = SnapshotFiles(output);

        ProcessResult result = await RunAsync(
                new[]
                {
                    "x", "-y", "-aos", "-sccUTF-8",
                    "-p" + (password ?? NoPasswordPlaceholder),
                    "-o" + output,
                    "--", fullArchive
                },
                TimeSpan.FromHours(6),
                cancellationToken)
            .ConfigureAwait(false);

        ThrowIfPasswordProblem(result, fullArchive, password);

        if (result.ExitCode is not (0 or 1))
        {
            throw new InvalidDataException(
                $"7-Zip could not extract {System.IO.Path.GetFileName(fullArchive)}: " +
                Tail(result.Output));
        }

        int extracted = 0;

        foreach (string file in SnapshotFiles(output))
        {
            if (before.Contains(file))
            {
                continue;
            }

            MarkOfTheWeb.TryPropagate(fullArchive, file);
            extracted++;
        }

        int fileEntries = entries.Count(entry => !entry.IsFolder);

        return new ArchiveExtractionResult(
            fullArchive,
            output,
            extracted,
            Math.Max(0, fileEntries - extracted));
    }

    internal static IReadOnlyList<SevenZipEntry> ParseListing(string output)
    {
        var entries = new List<SevenZipEntry>();
        bool inEntries = false;
        var current = new Dictionary<string, string>(StringComparer.Ordinal);

        void Flush()
        {
            if (current.TryGetValue("Path", out string? path) && path.Length > 0)
            {
                entries.Add(new SevenZipEntry(
                    path,
                    current.TryGetValue("Folder", out string? folder) && folder == "+",
                    current.TryGetValue("Size", out string? size) && long.TryParse(size, out long bytes) ? bytes : 0,
                    current.TryGetValue("Encrypted", out string? encrypted) && encrypted == "+"));
            }

            current.Clear();
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("----------", StringComparison.Ordinal))
            {
                // Everything before the separator describes the
                // archive itself, not its entries.
                inEntries = true;
                current.Clear();
                continue;
            }

            if (!inEntries)
            {
                continue;
            }

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            int separator = line.IndexOf(" = ", StringComparison.Ordinal);

            if (separator > 0)
            {
                current[line[..separator]] = line[(separator + 3)..];
            }
        }

        Flush();
        return entries;
    }

    private static void ThrowIfPasswordProblem(
        ProcessResult result,
        string archivePath,
        string? password)
    {
        string text = result.Output;

        bool passwordProblem =
            text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Cannot open encrypted archive", StringComparison.OrdinalIgnoreCase);

        if (passwordProblem)
        {
            throw new ArchivePasswordRequiredException(
                archivePath,
                wrongPassword: password is not null);
        }
    }

    private static HashSet<string> SnapshotFiles(string directory)
    {
        return Directory.Exists(directory)
            ? new HashSet<string>(
                Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
                StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Tail(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : "…" + trimmed[^300..];
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private async Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Never let 7-Zip sit on an interactive prompt.
        process.StandardInput.Close();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("7-Zip took too long and was stopped.");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
    }
}
