using System.Text.RegularExpressions;
using ConnectDesktop.Core.Security;
using ConnectDesktop.Core.Tests.Fakes;
using Xunit;

namespace ConnectDesktop.Core.Tests;

public sealed class PairingStoreTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void Arquivo_inexistente_comeca_vazio()
    {
        var store = new PairingStore(_temp.File("nao-existe.json"));
        Assert.Empty(store.List());
        Assert.Null(store.GetKey("qualquer"));
    }

    [Fact]
    public void SetKey_GetKey_List_e_Remove()
    {
        var store = new PairingStore(_temp.File("pairings.json"));
        var key1 = Key(1);
        var key2 = Key(100);

        store.SetKey("pc-1", "Escritorio", key1);
        store.SetKey("pc-2", "Notebook", key2);

        Assert.Equal(key1, store.GetKey("pc-1"));
        Assert.Equal(key2, store.GetKey("pc-2"));
        Assert.Null(store.GetKey("pc-3"));

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(("pc-1", "Escritorio"), list);
        Assert.Contains(("pc-2", "Notebook"), list);

        store.Remove("pc-1");
        Assert.Null(store.GetKey("pc-1"));
        Assert.Equal(key2, store.GetKey("pc-2"));
        Assert.DoesNotContain(store.List(), p => p.DeviceId == "pc-1");

        // remover um id que não existe não deve lançar
        store.Remove("pc-inexistente");
    }

    [Fact]
    public void SetKey_no_mesmo_id_substitui_a_chave()
    {
        var store = new PairingStore(_temp.File("pairings.json"));
        store.SetKey("pc-1", "Antigo", Key(1));
        store.SetKey("pc-1", "Novo", Key(7));

        Assert.Equal(Key(7), store.GetKey("pc-1"));
        Assert.Single(store.List());
        Assert.Equal("Novo", store.List()[0].DeviceName);
    }

    [Fact]
    public void Persiste_entre_instancias()
    {
        var path = _temp.File("pairings.json");
        var key = Key(42);

        new PairingStore(path).SetKey("pc-9", "Persistido", key);

        var reopened = new PairingStore(path);
        Assert.Equal(key, reopened.GetKey("pc-9"));
        Assert.Contains(("pc-9", "Persistido"), reopened.List());
    }

    [Fact]
    public void Remocao_persiste_entre_instancias()
    {
        var path = _temp.File("pairings.json");
        var first = new PairingStore(path);
        first.SetKey("pc-1", "A", Key(1));
        first.Remove("pc-1");

        Assert.Null(new PairingStore(path).GetKey("pc-1"));
    }

    [Fact]
    public void Arquivo_corrompido_vira_lista_vazia()
    {
        var path = _temp.File("pairings.json");
        File.WriteAllText(path, "{ isto não é json [[[");

        var store = new PairingStore(path);
        Assert.Empty(store.List());
        Assert.Null(store.GetKey("pc-1"));
    }

    [Fact]
    public void Arquivo_corrompido_nao_impede_salvar_nova_chave()
    {
        var path = _temp.File("pairings.json");
        File.WriteAllText(path, "lixo total");

        var store = new PairingStore(path);
        store.SetKey("pc-1", "Novo", Key(5));

        Assert.Equal(Key(5), new PairingStore(path).GetKey("pc-1"));
    }

    [Fact]
    public void PairingCode_Generate_tem_6_digitos()
    {
        var regex = new Regex("^[0-9]{6}$");
        for (var i = 0; i < 200; i++)
        {
            var code = PairingCode.Generate();
            Assert.Equal(6, code.Length);
            Assert.Matches(regex, code);
        }
    }
}
