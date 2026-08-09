using Azure;
using Azure.Data.Tables;

namespace LoopOrchestrator.State;

/// <summary>Second, tiny entity kind in the same table — holds the single "last successful run"
/// timestamp. A dedicated resource for one row would be overkill; a second partition key in the
/// same table is the idiomatic Table Storage way to keep a handful of unrelated entity kinds
/// together.</summary>
internal sealed class RunStateEntity : ITableEntity
{
    public const string PartitionKeyValue = "run-state";
    public const string SingletonRowKey = "singleton";

    public string PartitionKey { get; set; } = PartitionKeyValue;
    public string RowKey { get; set; } = SingletonRowKey;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset? LastSuccessfulRunAt { get; set; }
}

public sealed class TableStorageStateStore(TableServiceClient tableServiceClient) : ITenderStateStore
{
    private const string TableName = "TenderReview";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private TableClient? _tableClient;

    public async Task<HashSet<string>> GetSeenTenderIdsAsync(CancellationToken cancellationToken)
    {
        var table = await GetTableAsync(cancellationToken);
        var ids = new HashSet<string>();

        var pages = table.QueryAsync<TenderReviewEntity>(
            e => e.PartitionKey == TenderReviewEntity.PartitionKeyValue,
            select: ["RowKey"],
            cancellationToken: cancellationToken);

        await foreach (var entity in pages)
        {
            ids.Add(entity.RowKey);
        }

        return ids;
    }

    public async Task UpsertAsync(TenderReviewRecord record, CancellationToken cancellationToken)
    {
        var table = await GetTableAsync(cancellationToken);
        var entity = TenderReviewMapper.ToEntity(record);
        await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task<TenderReviewRecord?> GetAsync(string tenderId, CancellationToken cancellationToken)
    {
        var table = await GetTableAsync(cancellationToken);
        try
        {
            var response = await table.GetEntityAsync<TenderReviewEntity>(
                TenderReviewEntity.PartitionKeyValue, TenderReviewEntity.EscapeKey(tenderId),
                cancellationToken: cancellationToken);
            return TenderReviewMapper.ToRecord(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<DateTimeOffset?> GetLastSuccessfulRunAtAsync(CancellationToken cancellationToken)
    {
        var table = await GetTableAsync(cancellationToken);
        try
        {
            var response = await table.GetEntityAsync<RunStateEntity>(
                RunStateEntity.PartitionKeyValue, RunStateEntity.SingletonRowKey, cancellationToken: cancellationToken);
            return response.Value.LastSuccessfulRunAt;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SetLastSuccessfulRunAtAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var table = await GetTableAsync(cancellationToken);
        await table.UpsertEntityAsync(
            new RunStateEntity { LastSuccessfulRunAt = timestamp }, TableUpdateMode.Replace, cancellationToken);
    }

    private async Task<TableClient> GetTableAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return _tableClient!;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return _tableClient!;

            var table = tableServiceClient.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync(cancellationToken);
            _tableClient = table;
            _initialized = true;
            return table;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
