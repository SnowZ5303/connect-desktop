// Dono: g07. Assinaturas públicas FIXAS.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ConnectDesktop.Core.Update;

/// <summary>Release encontrada no GitHub.</summary>
public sealed record ReleaseInfo(Version Version, string TagName, string HtmlUrl, string? InstallerUrl, string? InstallerName, string Notes);

/// <summary>
/// Atualizador pelo GitHub Releases.
/// - Repositório: [AssemblyMetadata("GitHubRepo")] do assembly de entrada (Assembly.GetEntryAssembly()), formato "owner/repo".
/// - Consulta GET https://api.github.com/repos/{repo}/releases/latest (User-Agent "ConnectDesktop-Updater", Accept application/vnd.github+json).
/// - Versão da release = TagName sem o "v" inicial (ex.: "v1.0.42" -> 1.0.42).
/// - Instalador = asset cujo nome é exatamente <see cref="WindowsInstallerAssetName"/>.
/// </summary>
public sealed class UpdateChecker
{
    public const string WindowsInstallerAssetName = "ConnectDesktop-Setup-win-x64.exe";

    private const string UserAgent = "ConnectDesktop-Updater";
    private const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    private static readonly HttpClient SharedHttp = new();

    private readonly HttpClient _http;

    /// <param name="repo">"owner/repo"; null = ler do AssemblyMetadata.</param>
    /// <param name="currentVersion">null = versão do assembly de entrada (3 campos: Major.Minor.Build).</param>
    /// <param name="http">Opcional (testes).</param>
    public UpdateChecker(string? repo = null, Version? currentVersion = null, HttpClient? http = null)
    {
        Repo = repo ?? ReadRepoMetadata() ?? string.Empty;
        CurrentVersion = Normalize(currentVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
        _http = http ?? SharedHttp;
    }

    public string Repo { get; }
    public Version CurrentVersion { get; }

    /// <summary>Retorna a release se for MAIS NOVA que a atual; null se já está atualizado ou se der erro de rede (não lança).</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Repo) || Repo.StartsWith("OWNER/", StringComparison.Ordinal))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ParseRelease(doc.RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Baixa o instalador para Path.GetTempPath()/ConnectDesktop-Update/ e retorna o caminho.
    /// <paramref name="progress"/> recebe 0..1. Lança em erro.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.InstallerUrl))
            throw new InvalidOperationException("A release não tem instalador para Windows.");

        var dir = Path.Combine(Path.GetTempPath(), "ConnectDesktop-Update");
        Directory.CreateDirectory(dir);

        var finalPath = Path.Combine(dir, release.InstallerName ?? WindowsInstallerAssetName);
        var tmpPath = finalPath + ".tmp";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total is > 0)
                        progress?.Report((double)received / total.Value);
                }
            }

            File.Move(tmpPath, finalPath, overwrite: true);
            progress?.Report(1.0);
            return finalPath;
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    /// <summary>
    /// Só Windows: inicia o instalador com "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
    /// (o Inno Setup fecha o app e atualiza; a entrada [Run] com Check: WizardSilent reabre). O chamador deve encerrar o app logo depois.
    /// Em Mac/Linux lança PlatformNotSupportedException (o app abre <see cref="ReleaseInfo.HtmlUrl"/> no navegador).
    /// </summary>
    public void LaunchInstaller(string installerPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A instalação automática só é suportada no Windows.");

        using var process = Process.Start(new ProcessStartInfo(installerPath, InstallerArguments) { UseShellExecute = true });
    }

    private ReleaseInfo? ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (GetBool(root, "draft") || GetBool(root, "prerelease"))
            return null;

        var tag = GetString(root, "tag_name");
        if (tag is null)
            return null;

        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var parsed))
            return null;

        var version = Normalize(parsed);
        if (version <= CurrentVersion)
            return null;

        string? installerUrl = null;
        string? installerName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (GetString(asset, "name") == WindowsInstallerAssetName)
                {
                    installerName = WindowsInstallerAssetName;
                    installerUrl = GetString(asset, "browser_download_url");
                    break;
                }
            }
        }

        return new ReleaseInfo(
            version,
            tag,
            GetString(root, "html_url") ?? string.Empty,
            installerUrl,
            installerName,
            GetString(root, "body") ?? string.Empty);
    }

    private static string? ReadRepoMetadata()
        => Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepo")?.Value;

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Limpeza best-effort do arquivo temporário.
        }
    }
}
