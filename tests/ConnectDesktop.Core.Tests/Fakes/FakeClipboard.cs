using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Core.Tests.Fakes;

/// <summary>Área de transferência falsa (texto em memória) com registro de SetText.</summary>
public sealed class FakeClipboard : IClipboard
{
    private readonly object _lock = new();
    private readonly List<string> _setCalls = new();
    private string? _text;

    public FakeClipboard(string? text = null)
    {
        _text = text;
    }

    public string? Text
    {
        get { lock (_lock) return _text; }
        set { lock (_lock) _text = value; }
    }

    /// <summary>Todos os textos escritos com SetText, na ordem.</summary>
    public IReadOnlyList<string> SetCalls
    {
        get { lock (_lock) return _setCalls.ToArray(); }
    }

    public string? GetText() => Text;

    public void SetText(string text)
    {
        lock (_lock)
        {
            _text = text;
            _setCalls.Add(text);
        }
    }
}
