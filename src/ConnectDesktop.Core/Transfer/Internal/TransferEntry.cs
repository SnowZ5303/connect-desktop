// Dono: g10. Estado interno de uma transferência (interno do FileTransferService). Mutado só sob o lock do serviço.
using System.Security.Cryptography;

namespace ConnectDesktop.Core.Transfer.Internal;

internal sealed class TransferEntry
{
    public TransferEntry(Guid id, string fileName, long size, TransferDirection direction, string? localPath)
    {
        Id = id;
        FileName = fileName;
        Size = size;
        Direction = direction;
        LocalPath = localPath;
        StartedUtc = DateTime.UtcNow;
    }

    public Guid Id { get; }
    public string FileName { get; set; }
    public long Size { get; set; }
    public TransferDirection Direction { get; }
    public long Transferred { get; set; }
    public TransferState State { get; set; } = TransferState.Pending;
    public string? Error { get; set; }
    /// <summary>Envio: arquivo de origem. Recebimento: caminho final.</summary>
    public string? LocalPath { get; set; }
    public DateTime StartedUtc { get; }
    public double BytesPerSecond { get; set; }
    public DateTime LastNotifiedUtc { get; set; } = DateTime.MinValue;
    public SpeedMeter Meter { get; } = new();

    public bool IsActive => State is TransferState.Pending or TransferState.InProgress;

    /// <summary>Cancela o trabalho em andamento (laço de envio e esperas).</summary>
    public CancellationTokenSource Cts { get; } = new();

    // Recebimento
    public string? PartPath { get; set; }
    public FileStream? Part { get; set; }
    public IncrementalHash? Hash { get; set; }

    // Envio: resultado vindo do outro PC (FileResult)
    public TaskCompletionSource<(bool Ok, string Error)>? Result { get; set; }
    /// <summary>FileResult(Ok) recebido antes de <see cref="Result"/> existir; consumido ao criar o Result.</summary>
    public (bool Ok, string Error)? EarlyResult { get; set; }

    public TransferInfo ToInfo() =>
        new(Id, FileName, Size, Transferred, Direction, State, Error, LocalPath, StartedUtc, BytesPerSecond);

    /// <summary>Fecha o .part (se houver) e apaga o arquivo. Melhor esforço: não lança.</summary>
    public void CloseAndDeletePart()
    {
        try { Part?.Dispose(); } catch (Exception) { }
        Part = null;
        try { Hash?.Dispose(); } catch (Exception) { }
        Hash = null;
        if (!string.IsNullOrEmpty(PartPath))
        {
            try { File.Delete(PartPath); } catch (Exception) { }
        }
        PartPath = null;
    }
}
