// Dono: g10. Assinaturas públicas FIXAS.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Channels;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Protocol;
using ConnectDesktop.Core.Transfer.Internal;

namespace ConnectDesktop.Core.Transfer;

public enum TransferDirection { Outgoing, Incoming }
public enum TransferState { Pending, InProgress, Completed, Failed, Canceled }

/// <summary>Foto (imutável) do estado de uma transferência.</summary>
public sealed record TransferInfo(
    Guid Id,
    string FileName,
    long Size,
    long Transferred,
    TransferDirection Direction,
    TransferState State,
    string? Error,
    string? LocalPath,
    DateTime StartedUtc,
    double BytesPerSecond);

/// <summary>
/// Envia e recebe arquivos pelo canal já autenticado (qualquer sentido).
/// - Envio: fila; um arquivo por vez. FileOffer → FileChunk (64 KB, em ordem) → FileDone(SHA-256).
///   Cada bloco é enviado com await channel.SendAsync (o canal serializa; os blocos se intercalam com o mouse).
///   Ao receber FileResult: Completed (Ok) ou Failed(Error). Ao receber FileCancel: Canceled.
/// - Recebimento: aceita automaticamente (o canal só existe entre PCs pareados). Grava em
///   <see cref="DownloadDir"/>/&lt;nome&gt;.part e, no FileDone, confere o SHA-256, renomeia para o nome final e responde FileResult.
///   Nome higienizado: Path.GetFileName, remove caracteres inválidos e nomes reservados do Windows (CON, NUL...),
///   nunca sai da pasta; se já existir, usa "nome (1).ext", "nome (2).ext"...
///   Erros (disco cheio, offset fora de ordem, hash diferente) → apaga o .part, Failed e envia FileResult(Ok=false).
/// - Canal caiu (Attach(null) ou troca de canal) no meio → transferências em andamento viram Failed ("Conexão perdida.") e .part apagado.
/// - TransferChanged dispara em mudança de estado e, durante o progresso, no máximo a cada 200 ms por transferência.
/// - Mantém o histórico das últimas 100 transferências em memória.
/// Thread-safe. Nunca lança a partir de OnRemoteMessage.
/// </summary>
public sealed class FileTransferService : IDisposable
{
    private const int MaxHistory = 100;
    private const int ProgressIntervalMs = 200;
    private const int MaxNameAttempts = 10000;
    private const int PartBufferSize = 81920;
    private const int MaxFolderEntries = 100000;
    private const long MaxFolderBytes = 50L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(60);

    private const string MsgConnectionLost = "Conexão perdida.";
    private const string MsgCancelledByUser = "Cancelado pelo usuário.";
    private const string MsgCancelledByRemote = "Cancelado pelo outro PC.";
    private const string MsgNoResponse = "Sem resposta do outro PC.";
    private const string MsgCorrupted = "Arquivo corrompido na transferência.";
    private const string MsgNoChannel = "Nenhum PC conectado.";

    private readonly object _lock = new();
    private readonly List<TransferEntry> _entries = new();
    private readonly Channel<TransferEntry> _queue = Channel.CreateUnbounded<TransferEntry>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private IMessageChannel? _channel;
    private Action? _closedHandler;
    private bool _disposed;

    /// <param name="downloadDir">Pasta de destino (criada se não existir). null = <see cref="DefaultDownloadDir"/>.</param>
    public FileTransferService(string? downloadDir = null)
    {
        DownloadDir = downloadDir ?? DefaultDownloadDir;
        try
        {
            Directory.CreateDirectory(DownloadDir);
        }
        catch (Exception)
        {
            // Se não conseguir criar agora, o erro aparece na transferência ao receber o arquivo.
        }
        try
        {
            // Órfãos de execuções anteriores (envios de pasta interrompidos).
            var sendRoot = Path.Combine(Path.GetTempPath(), "ConnectDesktop-Send");
            if (Directory.Exists(sendRoot))
                Directory.Delete(sendRoot, true);
        }
        catch (Exception)
        {
            // Melhor esforço.
        }
        _worker = Task.Run(() => RunQueueAsync());
    }

    /// <summary>Downloads do usuário + "Connect Desktop" (Windows: %USERPROFILE%\Downloads; Mac/Linux: ~/Downloads).</summary>
    public static string DefaultDownloadDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Connect Desktop");

    public string DownloadDir { get; set; } = "";

    /// <summary>true quando há canal ligado.</summary>
    public bool IsConnected
    {
        get { lock (_lock) return _channel != null; }
    }

    /// <summary>Liga/desliga o canal (chamado pelas sessões ao iniciar e ao terminar).</summary>
    public void Attach(IMessageChannel? channel)
    {
        var batch = new Batch();
        lock (_lock)
        {
            if (ReferenceEquals(channel, _channel))
                return;

            ReleaseChannelLocked();
            if (channel != null)
            {
                // Registra o Closed antes de publicar o canal: se ele cair agora, OnChannelClosed espera o lock
                // e encontra o canal já publicado.
                IMessageChannel current = channel;
                Action handler = () => OnChannelClosed(current);
                current.Closed += handler;
                _closedHandler = handler;
            }
            _channel = channel;
            FailActiveLocked(MsgConnectionLost, batch);
        }
        Flush(batch);
    }

    /// <summary>
    /// Desliga só se <paramref name="channel"/> for o canal atual (comparação por referência).
    /// Se for, age como <see cref="Attach"/>(null); se não for, não faz nada. Usar ao terminar uma sessão,
    /// para que uma sessão antiga que termina atrasada não desligue a sessão nova.
    /// </summary>
    public void Detach(IMessageChannel channel)
    {
        var batch = new Batch();
        lock (_lock)
        {
            if (!ReferenceEquals(channel, _channel))
                return;

            ReleaseChannelLocked();
            _channel = null;
            FailActiveLocked(MsgConnectionLost, batch);
        }
        Flush(batch);
    }

    /// <summary>Chamado pelas sessões para mensagens FileOffer/FileChunk/FileDone/FileCancel/FileResult.</summary>
    public void OnRemoteMessage(Message message)
    {
        try
        {
            var batch = new Batch();
            lock (_lock)
            {
                if (_disposed)
                    return;

                switch (message)
                {
                    case FileOfferMessage m:
                        HandleOfferLocked(m, batch);
                        break;
                    case FileChunkMessage m:
                        HandleChunkLocked(m, batch);
                        break;
                    case FileDoneMessage m:
                        HandleDoneLocked(m, batch);
                        break;
                    case FileCancelMessage m:
                        HandleRemoteCancelLocked(m, batch);
                        break;
                    case FileResultMessage m:
                        HandleResultLocked(m, batch);
                        break;
                }
            }
            Flush(batch);
        }
        catch (Exception)
        {
            // Nunca lança a partir daqui: o canal de sessão não pode cair por causa de uma transferência.
        }
    }

    /// <summary>Põe o arquivo na fila de envio e retorna o Id. Lança FileNotFoundException se não existir e
    /// InvalidOperationException("Nenhum PC conectado.") se não houver canal.</summary>
    public Guid SendFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Arquivo não encontrado.", path);

        var batch = new Batch();
        TransferEntry entry;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FileTransferService));
            if (_channel == null)
                throw new InvalidOperationException(MsgNoChannel);

            var full = Path.GetFullPath(path);
            entry = new TransferEntry(Guid.NewGuid(), Path.GetFileName(full), new FileInfo(full).Length,
                                      TransferDirection.Outgoing, full);
            AddEntryLocked(entry);
            batch.Notify.Add(entry.ToInfo());
        }
        Flush(batch);
        _queue.Writer.TryWrite(entry);
        return entry.Id;
    }

    /// <summary>Sufixo do arquivo usado para enviar pastas: "&lt;pasta&gt;" + FolderArchiveSuffix.</summary>
    public const string FolderArchiveSuffix = ".cdfolder.zip";

    /// <summary>
    /// v1.0.4: compacta a pasta (ZipFile, CompressionLevel.Fastest, incluindo a pasta raiz) num arquivo temporário
    /// "&lt;nome da pasta&gt;.cdfolder.zip" em Path.GetTempPath()/ConnectDesktop-Send/, enfileira com SendFile e apaga o
    /// temporário quando a transferência terminar (qualquer estado final). Retorna o Id.
    /// Lança DirectoryNotFoundException / InvalidOperationException("Nenhum PC conectado.").
    /// No RECEBIMENTO: um arquivo concluído cujo nome termina em FolderArchiveSuffix é extraído (ZipFile.ExtractToDirectory,
    /// que já bloqueia caminhos fora do destino) para DownloadDir/&lt;nome sem o sufixo&gt; (nome livre: "x (1)" etc.),
    /// o .zip é apagado e TransferInfo.LocalPath passa a ser a pasta extraída. Erro na extração → Failed("Erro ao extrair a pasta: {msg}").
    /// Limite de segurança na extração: no máximo 100 000 entradas e 50 GB descompactados; acima disso → Failed.
    /// </summary>
    public Guid SendFolder(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("Pasta não encontrada.");

        var folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var folderName = Path.GetFileName(folderPath);
        if (folderName.Length == 0)
            folderName = "pasta";
        var archiveName = folderName + FolderArchiveSuffix;

        var batch = new Batch();
        TransferEntry entry;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FileTransferService));
            if (_channel == null)
                throw new InvalidOperationException(MsgNoChannel);

            // Pending sem LocalPath até a compactação terminar; a compactação roda fora da chamada.
            entry = new TransferEntry(Guid.NewGuid(), archiveName, 0, TransferDirection.Outgoing, null);
            AddEntryLocked(entry);
            batch.Notify.Add(entry.ToInfo());
        }
        Flush(batch);
        _ = Task.Run(() => CompressFolderAsync(entry, folderPath, archiveName));
        return entry.Id;
    }

    /// <summary>Cancela (envio ou recebimento). Envia FileCancel ao outro lado. Ignora Id desconhecido.</summary>
    public void Cancel(Guid id)
    {
        var batch = new Batch();
        lock (_lock)
        {
            var entry = FindLocked(id);
            if (entry == null || !entry.IsActive || entry.Extracting)
                return;

            if (entry.Direction == TransferDirection.Incoming)
                entry.CloseAndDeletePart();
            FinishLocked(entry, TransferState.Canceled, MsgCancelledByUser, batch);
            batch.ToCancel.Add(entry.Cts);
            if (_channel != null)
                batch.Sends.Add((_channel, new FileCancelMessage(id, MsgCancelledByUser)));
        }
        Flush(batch);
    }

    /// <summary>Remove do histórico as transferências que não estão em andamento.</summary>
    public void ClearFinished()
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => !e.IsActive);
        }
    }

    public IReadOnlyList<TransferInfo> Transfers
    {
        get { lock (_lock) return _entries.Select(e => e.ToInfo()).ToList(); }
    }

    public event Action<TransferInfo>? TransferChanged;

    public void Dispose()
    {
        var batch = new Batch();
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseChannelLocked();
            _channel = null;
            FailActiveLocked(MsgConnectionLost, batch);
            // Só o Dispose interrompe a extração; o .zip fica no destino.
            foreach (var entry in _entries)
            {
                if (entry.Extracting && entry.IsActive)
                    batch.ToCancel.Add(entry.Cts);
            }
        }
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        Flush(batch);
    }

    // ---- Envio ----

    private async Task RunQueueAsync()
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                await SendEntryAsync(entry);
                DeleteTempFolder(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose: a fila termina.
        }
        finally
        {
            while (_queue.Reader.TryRead(out var left))
                DeleteTempFolder(left);
        }
    }

    private async Task SendEntryAsync(TransferEntry entry)
    {
        IMessageChannel? channel;
        var batch = new Batch();
        lock (_lock)
        {
            if (entry.State != TransferState.Pending)
                return;
            channel = _channel;
            if (channel == null)
                FinishLocked(entry, TransferState.Failed, MsgConnectionLost, batch);
        }
        Flush(batch);
        if (channel == null)
            return;

        FileStream stream;
        try
        {
            stream = new FileStream(entry.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                                    FileTransferLimits.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            FailOutgoing(entry, $"Erro ao ler o arquivo: {ex.Message}", tellRemote: false);
            return;
        }

        using (stream)
        {
            await SendStreamAsync(entry, channel, stream);
        }
    }

    private async Task SendStreamAsync(TransferEntry entry, IMessageChannel channel, FileStream stream)
    {
        var ct = entry.Cts.Token;
        try
        {
            var size = stream.Length;
            var start = new Batch();
            lock (_lock)
            {
                if (entry.State != TransferState.Pending)
                    return;
                entry.Size = size;
                entry.State = TransferState.InProgress;
                entry.LastNotifiedUtc = DateTime.UtcNow;
                start.Notify.Add(entry.ToInfo());
            }
            Flush(start);

            // Envios sem token: cancelar no meio de um frame faz o SecureChannel fechar a conexão.
            // O cancelamento é respeitado pelo laço (ct.ThrowIfCancellationRequested) e pela espera do FileResult.
            await channel.SendAsync(new FileOfferMessage(entry.Id, entry.FileName, size));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileTransferLimits.ChunkSize];
            long offset = 0;
            while (offset < size)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(FileTransferLimits.ChunkSize, size - offset);

                int read;
                try
                {
                    read = await ReadFullAsync(stream, buffer, want, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FailOutgoing(entry, $"Erro ao ler o arquivo: {ex.Message}", tellRemote: true);
                    return;
                }
                if (read < want)
                {
                    FailOutgoing(entry, "Erro ao ler o arquivo: o arquivo mudou durante o envio.", tellRemote: true);
                    return;
                }

                var data = buffer.AsSpan(0, read).ToArray();
                hash.AppendData(data);
                await channel.SendAsync(new FileChunkMessage(entry.Id, offset, data));
                offset += read;

                var progress = new Batch();
                lock (_lock)
                {
                    entry.Transferred = offset;
                    NotifyProgressLocked(entry, progress);
                }
                Flush(progress);
            }

            ct.ThrowIfCancellationRequested();
            var sha256 = hash.GetHashAndReset();
            var result = new TaskCompletionSource<(bool Ok, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (entry.State != TransferState.InProgress)
                    return;
                entry.Result = result;
                // FileResult(Ok) que chegou antes de o Result existir: consome agora.
                if (entry.EarlyResult is { } early)
                    result.TrySetResult(early);
            }
            await channel.SendAsync(new FileDoneMessage(entry.Id, sha256));

            var (ok, error) = await result.Task.WaitAsync(ResultTimeout, ct);
            var finish = new Batch();
            lock (_lock)
            {
                if (entry.State == TransferState.InProgress)
                {
                    if (ok)
                        FinishLocked(entry, TransferState.Completed, null, finish);
                    else
                        FinishLocked(entry, TransferState.Failed, string.IsNullOrEmpty(error) ? "Erro desconhecido." : error, finish);
                }
            }
            Flush(finish);
        }
        catch (TimeoutException)
        {
            // Avisa o outro PC para ele apagar o .part.
            FailOutgoing(entry, MsgNoResponse, tellRemote: true);
        }
        catch (OperationCanceledException)
        {
            // Cancelado pelo usuário, pelo outro PC ou pela queda do canal: o estado já foi definido.
        }
        catch (Exception)
        {
            FailOutgoing(entry, MsgConnectionLost, tellRemote: false);
        }
    }

    private void FailOutgoing(TransferEntry entry, string error, bool tellRemote)
    {
        var batch = new Batch();
        lock (_lock)
        {
            if (!entry.IsActive)
                return;
            var wasSending = entry.State == TransferState.InProgress;
            FinishLocked(entry, TransferState.Failed, error, batch);
            batch.ToCancel.Add(entry.Cts);
            if (tellRemote && wasSending && _channel != null)
                batch.Sends.Add((_channel, new FileCancelMessage(entry.Id, error)));
        }
        Flush(batch);
    }

    private static async Task<int> ReadFullAsync(FileStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    /// <summary>Compacta a pasta num temporário e enfileira o .zip. Roda numa Task própria (não bloqueia o chamador).</summary>
    private void CompressFolderAsync(TransferEntry entry, string folderPath, string archiveName)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ConnectDesktop-Send", Guid.NewGuid().ToString("N"));
            entry.TempDir = tempDir;
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, archiveName);
            ZipFile.CreateFromDirectory(folderPath, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);
            var size = new FileInfo(zipPath).Length;

            var batch = new Batch();
            var ready = false;
            lock (_lock)
            {
                if (entry.State == TransferState.Pending)
                {
                    entry.LocalPath = zipPath;
                    entry.Size = size;
                    batch.Notify.Add(entry.ToInfo());
                    ready = true;
                }
            }
            Flush(batch);
            if (ready && _queue.Writer.TryWrite(entry))
                return;
            DeleteTempFolder(entry);
        }
        catch (Exception ex)
        {
            FailOutgoing(entry, $"Erro ao ler o arquivo: {ex.Message}", tellRemote: false);
            DeleteTempFolder(entry);
        }
    }

    private static void DeleteTempFolder(TransferEntry entry)
    {
        var dir = entry.TempDir;
        entry.TempDir = null;
        if (dir == null)
            return;
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception)
        {
            // Melhor esforço: a pasta temporária some na próxima limpeza do sistema.
        }
    }

    /// <summary>
    /// Extrai o .zip recebido numa pasta de trabalho dentro do destino, confere os limites ANTES de extrair
    /// (entradas e soma dos Length) e move a pasta para um nome livre. Sucesso: Completed com LocalPath = pasta e .zip apagado.
    /// Erro: Failed("Erro ao extrair a pasta: ...") e o .zip fica. Cancelamento/queda: limpa a pasta de trabalho e o .zip.
    /// </summary>
    private void RunFolderExtraction(TransferEntry entry, string zipPath, string root, string baseName, CancellationToken ct)
    {
        var stagingDir = Path.Combine(root, "." + Guid.NewGuid().ToString("N") + ".extract");
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count > MaxFolderEntries)
                    throw new InvalidDataException("pasta com entradas demais.");
                // Tamanhos declarados: conferidos ANTES de extrair (sem estouro, com checked).
                long declared = 0;
                foreach (var item in zip.Entries)
                {
                    if (item.Length < 0 || item.Length > MaxFolderBytes)
                        throw new InvalidDataException("pasta grande demais.");
                    declared = checked(declared + item.Length);
                    if (declared > MaxFolderBytes)
                        throw new InvalidDataException("pasta grande demais.");
                }

                Directory.CreateDirectory(stagingDir);
                var stagingRoot = stagingDir + Path.DirectorySeparatorChar;
                long written = 0;
                foreach (var item in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Path.GetFullPath(Path.Combine(stagingRoot, item.FullName));
                    if (!dest.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"entrada fora da pasta: {item.FullName}");

                    if (item.FullName.EndsWith('/') || item.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(dest);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        written = CopyEntry(item, dest, written, ct);
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            // O remetente inclui a pasta raiz: se o zip tem uma única pasta no topo, ela vira o destino.
            var children = Directory.GetFileSystemEntries(stagingDir);
            var source = children.Length == 1 && Directory.Exists(children[0]) ? children[0] : stagingDir;
            var target = PickFreeFolder(root, baseName);
            Directory.Move(source, target);
            if (source != stagingDir)
                Directory.Delete(stagingDir);
            try
            {
                File.Delete(zipPath);
            }
            catch (Exception)
            {
                // A pasta já está pronta; o .zip que sobrou não derruba a extração.
            }

            var batch = new Batch();
            var keep = false;
            lock (_lock)
            {
                if (entry.State == TransferState.InProgress)
                {
                    keep = true;
                    entry.LocalPath = target;
                    entry.FileName = Path.GetFileName(target);
                    FinishLocked(entry, TransferState.Completed, null, batch);
                }
            }
            if (!keep)
            {
                try
                {
                    Directory.Delete(target, true);
                }
                catch (Exception)
                {
                }
            }
            Flush(batch);
        }
        catch (OperationCanceledException)
        {
            // Só o Dispose chega aqui: limpa a pasta de trabalho e mantém o .zip no destino.
            CleanupStaging(stagingDir);
            var batch = new Batch();
            lock (_lock)
            {
                if (entry.State == TransferState.InProgress)
                    FinishLocked(entry, TransferState.Failed, "Erro ao extrair a pasta: extração interrompida.", batch);
            }
            Flush(batch);
        }
        catch (Exception ex)
        {
            CleanupStaging(stagingDir);
            var batch = new Batch();
            lock (_lock)
            {
                if (entry.State == TransferState.InProgress)
                    FinishLocked(entry, TransferState.Failed, $"Erro ao extrair a pasta: {ex.Message}", batch);
            }
            Flush(batch);
        }
    }

    /// <summary>
    /// Copia uma entrada contando os bytes reais. Aborta se passar do Length declarado da entrada
    /// (cabeçalho mentiroso) ou do limite total de 50 GB. Devolve o total acumulado.
    /// </summary>
    private static long CopyEntry(ZipArchiveEntry item, string dest, long written, CancellationToken ct)
    {
        using var src = item.Open();
        using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, PartBufferSize);
        var buffer = new byte[PartBufferSize];
        long entryBytes = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            entryBytes = checked(entryBytes + n);
            if (entryBytes > item.Length)
                throw new InvalidDataException("pasta grande demais.");
            written = checked(written + n);
            if (written > MaxFolderBytes)
                throw new InvalidDataException("pasta grande demais.");
            dst.Write(buffer, 0, n);
        }
        return written;
    }

    private static void CleanupStaging(string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
        }
        catch (Exception)
        {
        }
    }

    private static string PickFreeFolder(string root, string baseName)
    {
        for (var i = 0; i < MaxNameAttempts; i++)
        {
            var candidate = Path.Combine(root, i == 0 ? baseName : $"{baseName} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }
        throw new IOException("não há nome livre na pasta de destino.");
    }

    private static bool IsFolderArchive(string fileName) =>
        fileName.EndsWith(FolderArchiveSuffix, StringComparison.OrdinalIgnoreCase);

    private string DownloadRootLocked()
    {
        var root = Path.GetFullPath(DownloadDir);
        if (!Path.EndsInDirectorySeparator(root))
            root += Path.DirectorySeparatorChar;
        return root;
    }

    // ---- Recebimento (sempre sob o lock) ----

    private void HandleOfferLocked(FileOfferMessage m, Batch batch)
    {
        if (_channel == null || FindLocked(m.TransferId) != null)
            return;

        var entry = new TransferEntry(m.TransferId, FileNameSanitizer.Sanitize(m.FileName), m.Size,
                                      TransferDirection.Incoming, null);
        FileStream? part = null;
        var partPath = string.Empty;
        try
        {
            if (m.Size < 0)
                throw new IOException("tamanho inválido.");

            Directory.CreateDirectory(DownloadDir);
            var root = Path.GetFullPath(DownloadDir);
            if (!Path.EndsInDirectorySeparator(root))
                root += Path.DirectorySeparatorChar;

            var stem = Path.GetFileNameWithoutExtension(entry.FileName);
            var ext = Path.GetExtension(entry.FileName);
            var finalPath = string.Empty;
            for (var i = 0; i < MaxNameAttempts && part == null; i++)
            {
                var candidate = Path.Combine(root, i == 0 ? entry.FileName : $"{stem} ({i}){ext}");
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("caminho fora da pasta de destino.");

                var candidatePart = candidate + ".part";
                if (File.Exists(candidate) || File.Exists(candidatePart) || IsIncomingInUseLocked(candidate))
                    continue;
                try
                {
                    part = new FileStream(candidatePart, FileMode.CreateNew, FileAccess.Write, FileShare.None, PartBufferSize);
                    partPath = candidatePart;
                    finalPath = candidate;
                }
                catch (IOException) when (File.Exists(candidatePart) || File.Exists(candidate))
                {
                    // Outro arquivo surgiu com esse nome: tenta o próximo.
                }
            }
            if (part == null)
                throw new IOException("não há nome livre na pasta de destino.");

            entry.FileName = Path.GetFileName(finalPath);
            entry.LocalPath = finalPath;
            entry.PartPath = partPath;
            entry.Part = part;
            entry.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            entry.State = TransferState.InProgress;
            AddEntryLocked(entry);
            batch.Notify.Add(entry.ToInfo());
        }
        catch (Exception ex)
        {
            entry.Part = part;
            entry.PartPath = partPath;
            AddEntryLocked(entry);
            FailIncomingLocked(entry, $"Erro ao gravar o arquivo: {ex.Message}", batch);
        }
    }

    private void HandleChunkLocked(FileChunkMessage m, Batch batch)
    {
        var entry = FindLocked(m.TransferId);
        if (entry == null || entry.Direction != TransferDirection.Incoming
            || entry.State != TransferState.InProgress || entry.Part == null || entry.Hash == null)
            return;

        try
        {
            if (m.Offset != entry.Transferred)
                throw new IOException("bloco fora de ordem.");
            if (entry.Transferred + m.Data.Length > entry.Size)
                throw new IOException("arquivo maior que o anunciado.");

            entry.Part.Write(m.Data, 0, m.Data.Length);
            entry.Hash.AppendData(m.Data);
            entry.Transferred += m.Data.Length;
            NotifyProgressLocked(entry, batch);
        }
        catch (Exception ex)
        {
            FailIncomingLocked(entry, $"Erro ao gravar o arquivo: {ex.Message}", batch);
        }
    }

    private void HandleDoneLocked(FileDoneMessage m, Batch batch)
    {
        var entry = FindLocked(m.TransferId);
        if (entry == null || entry.Direction != TransferDirection.Incoming
            || entry.State != TransferState.InProgress || entry.Part == null || entry.Hash == null)
            return;

        try
        {
            var actual = entry.Hash.GetHashAndReset();
            if (entry.Transferred != entry.Size || !actual.SequenceEqual(m.Sha256))
            {
                FailIncomingLocked(entry, MsgCorrupted, batch);
                return;
            }

            entry.Part.Dispose();
            entry.Part = null;
            entry.Hash.Dispose();
            entry.Hash = null;
            File.Move(entry.PartPath!, entry.LocalPath!, false);
            entry.PartPath = null;

            if (IsFolderArchive(entry.FileName))
            {
                // Pasta: o .zip chegou íntegro. O remetente é avisado já agora; a extração roda em segundo plano
                // e só muda o estado local (Completed com a pasta, ou Failed).
                var zipPath = entry.LocalPath!;
                var root = DownloadRootLocked();
                var baseName = entry.FileName[..^FolderArchiveSuffix.Length];
                if (baseName.Length == 0)
                    baseName = "pasta";
                var token = entry.Cts.Token;
                entry.Extracting = true;
                entry.LastNotifiedUtc = DateTime.UtcNow;
                batch.Notify.Add(entry.ToInfo());
                batch.Start.Add(() => RunFolderExtraction(entry, zipPath, root, baseName, token));
            }
            else
            {
                FinishLocked(entry, TransferState.Completed, null, batch);
            }
            if (_channel != null)
                batch.Sends.Add((_channel, new FileResultMessage(entry.Id, true, string.Empty)));
        }
        catch (Exception ex)
        {
            FailIncomingLocked(entry, $"Erro ao gravar o arquivo: {ex.Message}", batch);
        }
    }

    private void HandleRemoteCancelLocked(FileCancelMessage m, Batch batch)
    {
        var entry = FindLocked(m.TransferId);
        if (entry == null || !entry.IsActive || entry.Extracting)
            return;

        if (entry.Direction == TransferDirection.Incoming)
            entry.CloseAndDeletePart();
        FinishLocked(entry, TransferState.Canceled, MsgCancelledByRemote, batch);
        batch.ToCancel.Add(entry.Cts);
    }

    private void HandleResultLocked(FileResultMessage m, Batch batch)
    {
        var entry = FindLocked(m.TransferId);
        if (entry == null || entry.Direction != TransferDirection.Outgoing || !entry.IsActive)
            return;

        // Erro do outro PC vale mesmo antes do fim do envio (ex.: recusou logo no FileOffer).
        if (!m.Ok)
        {
            FinishLocked(entry, TransferState.Failed, string.IsNullOrEmpty(m.Error) ? "Erro desconhecido." : m.Error, batch);
            batch.ToCancel.Add(entry.Cts);
            return;
        }

        if (entry.Result != null)
            entry.Result.TrySetResult((true, m.Error));
        else
            entry.EarlyResult = (true, m.Error);
    }

    private void FailIncomingLocked(TransferEntry entry, string error, Batch batch)
    {
        entry.CloseAndDeletePart();
        FinishLocked(entry, TransferState.Failed, error, batch);
        if (_channel != null)
            batch.Sends.Add((_channel, new FileResultMessage(entry.Id, false, error)));
    }

    // ---- Canal ----

    private void ReleaseChannelLocked()
    {
        if (_channel != null && _closedHandler != null)
            _channel.Closed -= _closedHandler;
        _closedHandler = null;
    }

    private void OnChannelClosed(IMessageChannel channel)
    {
        var batch = new Batch();
        lock (_lock)
        {
            if (!ReferenceEquals(channel, _channel))
                return;
            ReleaseChannelLocked();
            _channel = null;
            FailActiveLocked(MsgConnectionLost, batch);
        }
        Flush(batch);
    }

    // ---- Estado (sempre sob o lock) ----

    private void FailActiveLocked(string error, Batch batch)
    {
        foreach (var entry in _entries)
        {
            // Extração de pasta em andamento: fase local, só o Dispose interrompe.
            if (!entry.IsActive || entry.Extracting)
                continue;
            if (entry.Direction == TransferDirection.Incoming)
                entry.CloseAndDeletePart();
            FinishLocked(entry, TransferState.Failed, error, batch);
            batch.ToCancel.Add(entry.Cts);
        }
    }

    private TransferEntry? FindLocked(Guid id) => _entries.FirstOrDefault(e => e.Id == id);

    private bool IsIncomingInUseLocked(string path) =>
        _entries.Any(e => e.Direction == TransferDirection.Incoming && e.IsActive
                          && string.Equals(e.LocalPath, path, StringComparison.OrdinalIgnoreCase));

    private void AddEntryLocked(TransferEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaxHistory)
        {
            var index = _entries.FindIndex(e => !e.IsActive);
            if (index < 0)
                break;
            _entries.RemoveAt(index);
        }
    }

    private static void FinishLocked(TransferEntry entry, TransferState state, string? error, Batch batch)
    {
        entry.State = state;
        entry.Error = error;
        if (state == TransferState.Completed)
            entry.Transferred = entry.Size;
        entry.LastNotifiedUtc = DateTime.UtcNow;
        batch.Notify.Add(entry.ToInfo());
    }

    private static void NotifyProgressLocked(TransferEntry entry, Batch batch)
    {
        var now = DateTime.UtcNow;
        entry.BytesPerSecond = entry.Meter.Add(entry.Transferred, now);
        if (now - entry.LastNotifiedUtc >= TimeSpan.FromMilliseconds(ProgressIntervalMs))
        {
            entry.LastNotifiedUtc = now;
            batch.Notify.Add(entry.ToInfo());
        }
    }

    // ---- Efeitos colhidos sob o lock e executados fora dele ----

    /// <summary>Tudo que deve acontecer depois de soltar o lock: cancelar trabalho, enviar mensagens e disparar eventos.</summary>
    private sealed class Batch
    {
        public List<TransferInfo> Notify { get; } = new();
        public List<CancellationTokenSource> ToCancel { get; } = new();
        public List<(IMessageChannel Channel, Message Message)> Sends { get; } = new();
        public List<Action> Start { get; } = new();
    }

    /// <summary>Chamar sempre fora do lock.</summary>
    private void Flush(Batch batch)
    {
        foreach (var cts in batch.ToCancel)
            cts.Cancel();
        foreach (var (channel, message) in batch.Sends)
            _ = SendQuietlyAsync(channel, message);
        foreach (var start in batch.Start)
            _ = Task.Run(start);
        foreach (var info in batch.Notify)
            RaiseChanged(info);
    }

    private static async Task SendQuietlyAsync(IMessageChannel channel, Message message)
    {
        try
        {
            await channel.SendAsync(message);
        }
        catch (Exception)
        {
            // Melhor esforço: se o canal caiu, a transferência já é tratada pelo Attach/Closed.
        }
    }

    private void RaiseChanged(TransferInfo info)
    {
        var handler = TransferChanged;
        if (handler == null)
            return;
        try
        {
            handler(info);
        }
        catch (Exception)
        {
            // Um ouvinte com erro não pode derrubar a transferência.
        }
    }
}
