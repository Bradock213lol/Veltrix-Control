using System.Security.Cryptography;
using VeltrixControl.Agent.Security;
using VeltrixControl.Agent.Transport;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Files;

namespace VeltrixControl.Agent.Transfers;

public sealed partial class TransferService(AgentOptions options, AgentApiClient apiClient, ILogger<TransferService> logger)
{
    private const int ChunkBytes = 256 * 1024;

    public async Task<int> ProcessPendingAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        var pending = await apiClient.GetPendingTransfersAsync(identity, cancellationToken);
        var processed = 0;
        foreach (var transfer in pending)
        {
            try
            {
                await ProcessAsync(identity, transfer, cancellationToken);
                processed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                HttpRequestException or CryptographicException or ArgumentException)
            {
                LogTransferFailed(logger, exception, transfer.Id);
                await apiClient.FailTransferAsync(identity, transfer.Id, exception.Message, cancellationToken);
            }
        }
        return processed;
    }

    private async Task ProcessAsync(DeviceIdentity identity, AgentPendingTransfer transfer, CancellationToken cancellationToken)
    {
        if (transfer.Direction == TransferDirection.Download)
        {
            await SendFileAsync(identity, transfer, cancellationToken);
        }
        else
        {
            await ReceiveFileAsync(identity, transfer, cancellationToken);
        }
    }

    private async Task SendFileAsync(DeviceIdentity identity, AgentPendingTransfer transfer, CancellationToken cancellationToken)
    {
        var path = PathGuard.ResolveWithinRoot(options.ManagedRoot, transfer.Path, allowRoot: false);
        if (!File.Exists(path)) throw new FileNotFoundException("The file does not exist on the node.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ChunkBytes, useAsync: true);
        var length = stream.Length;
        if (transfer.TotalBytes > 0 && transfer.TotalBytes != length) throw new InvalidDataException("The file size changed on the node.");
        var buffer = new byte[ChunkBytes];
        var offset = transfer.BytesTransferred;
        stream.Seek(offset, SeekOrigin.Begin);
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), cancellationToken);
            if (read <= 0) throw new IOException("The file could not be read completely.");
            var last = offset + read >= length;
            var sha = last ? ComputeHash(path) : null;
            await apiClient.PushTransferChunkAsync(identity, new TransferChunkPush(transfer.Id, offset, Convert.ToBase64String(buffer, 0, read), last, sha), cancellationToken);
            offset += read;
        }
        if (length == 0)
        {
            await apiClient.PushTransferChunkAsync(identity, new TransferChunkPush(transfer.Id, 0, string.Empty, true, ComputeHash(path)), cancellationToken);
        }
    }

    private async Task ReceiveFileAsync(DeviceIdentity identity, AgentPendingTransfer transfer, CancellationToken cancellationToken)
    {
        var destination = PathGuard.ResolveWithinRoot(options.ManagedRoot, transfer.Path, allowRoot: false);
        var partDirectory = Path.Combine(options.DataDirectory, "transfers");
        Directory.CreateDirectory(partDirectory);
        var partPath = Path.Combine(partDirectory, $"{transfer.Id:D}.part");
        var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (offset > transfer.BytesTransferred) throw new InvalidDataException("The local transfer state is inconsistent with the controller.");

        await using (var stream = new FileStream(partPath, offset == 0 ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true))
        {
            stream.Seek(offset, SeekOrigin.Begin);
            while (offset < transfer.TotalBytes)
            {
                var response = await apiClient.PullTransferChunkAsync(identity, new TransferChunkPull(transfer.Id, offset, ChunkBytes), cancellationToken);
                var data = Convert.FromBase64String(response.DataBase64);
                if (data.Length == 0)
                {
                    if (response.Last) break;
                    await stream.FlushAsync(cancellationToken);
                    return;
                }
                await stream.WriteAsync(data, cancellationToken);
                offset += data.Length;
                if (response.Last) break;
            }
            await stream.FlushAsync(cancellationToken);
        }

        if (transfer.TotalBytes > 0 && offset != transfer.TotalBytes) throw new InvalidDataException("The received size does not match the requested size.");
        if (transfer.Sha256 is not null && !string.Equals(transfer.Sha256, ComputeHash(partPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The received file failed checksum verification.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(partPath, destination, overwrite: true);
        LogTransferCompleted(logger, transfer.Id, transfer.Path);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [LoggerMessage(20, LogLevel.Warning, "Transfer {transferId} failed")]
    private static partial void LogTransferFailed(ILogger logger, Exception exception, Guid transferId);

    [LoggerMessage(21, LogLevel.Information, "Transfer {transferId} stored {path}")]
    private static partial void LogTransferCompleted(ILogger logger, Guid transferId, string path);
}
