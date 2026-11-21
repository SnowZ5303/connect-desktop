using System.Net;
using System.Text.Json;
using ConnectDesktop.Core.Update;
using Xunit;

namespace ConnectDesktop.Core.Tests;

public sealed class UpdateCheckerTests
{
    private const string Repo = "owner/repo";
    private const string InstallerName = "ConnectDesktop-Setup-win-x64.exe";
    private const string InstallerUrl = "https://example.invalid/downloads/ConnectDesktop-Setup-win-x64.exe";

    /// <summary>Handler falso: responde sempre o mesmo status/corpo e guarda as requisições.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static string ReleaseJson(string tag) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        html_url = $"https://github.com/{Repo}/releases/tag/{tag}",
        body = "Notas da versão",
        assets = new object[]
        {
            new { name = "ConnectDesktop-linux-x64.tar.gz", browser_download_url = "https://example.invalid/downloads/linux.tar.gz" },
            new { name = InstallerName, browser_download_url = InstallerUrl },
        },
    });

    private static UpdateChecker CreateChecker(StubHandler handler, string currentVersion = "1.0.41")
    {
        var http = new HttpClient(handler);
        return new UpdateChecker(Repo, new Version(currentVersion), http);
    }

    [Fact]
    public async Task Release_mais_nova_retorna_ReleaseInfo_com_o_instalador()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleaseJson("v1.0.42"));
        var checker = CreateChecker(handler);

        var release = await checker.CheckAsync();

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 0, 42), release!.Version);
        Assert.Equal("v1.0.42", release.TagName);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.0.42", release.HtmlUrl);
        Assert.Equal(InstallerUrl, release.InstallerUrl);
        Assert.Equal(InstallerName, release.InstallerName);
        Assert.Equal("Notas da versão", release.Notes);
    }

    [Fact]
    public async Task Consulta_a_api_de_releases_do_repositorio_configurado()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleaseJson("v1.0.42"));
        var checker = CreateChecker(handler);

        await checker.CheckAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", request.RequestUri!.ToString());
        Assert.Contains("ConnectDesktop-Updater", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Versao_igual_retorna_null()
    {
        var checker = CreateChecker(new StubHandler(HttpStatusCode.OK, ReleaseJson("v1.0.41")));
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task Versao_mais_velha_retorna_null()
    {
        var checker = CreateChecker(new StubHandler(HttpStatusCode.OK, ReleaseJson("v1.0.40")));
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task Resposta_404_retorna_null()
    {
        var checker = CreateChecker(new StubHandler(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task JSON_invalido_retorna_null()
    {
        var checker = CreateChecker(new StubHandler(HttpStatusCode.OK, "{ isto não é json"));
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task Tag_sem_v_tambem_e_reconhecida()
    {
        var checker = CreateChecker(new StubHandler(HttpStatusCode.OK, ReleaseJson("1.0.42")));

        var release = await checker.CheckAsync();

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 0, 42), release!.Version);
        Assert.Equal(InstallerUrl, release.InstallerUrl);
    }
}
