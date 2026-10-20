using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Infrastructure;

public sealed record IdempotencyExecution<TResponse>(int StatusCode, TResponse Body, bool Replayed);

/// <summary>Stores and replays a command response in the same database transaction as its effect.</summary>
public static class IdempotencyExecutor
{
    public static async Task<IdempotencyExecution<TResponse>> ExecuteAsync<TRequest, TResponse>(
        BillingDbContext db,
        Guid businessId,
        string scope,
        string? idempotencyKey,
        TRequest request,
        Func<Task<(int StatusCode, TResponse Body)>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(action);
        if (businessId == Guid.Empty || string.IsNullOrWhiteSpace(scope) || scope.Length > 100)
        {
            throw new ArgumentException("A business and bounded command scope are required.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var (statusCode, body) = await action().ConfigureAwait(false);
            return new IdempotencyExecution<TResponse>(statusCode, body, false);
        }

        var key = idempotencyKey.Trim();
        if (key.Length > 128)
        {
            throw new ArgumentException("Idempotency keys may contain at most 128 characters.", nameof(idempotencyKey));
        }

        var payloadHash = CanonicalJson.ComputeFingerprint(JsonSerializer.SerializeToElement(request, ContractJson.Options));
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Serialize retries for this key before checking the receipt, so concurrent duplicate requests
            // cannot both execute the command body.
            var lockName = $"{businessId:N}|{scope}|{key}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))",
                cancellationToken).ConfigureAwait(false);

            var existing = await db.IdempotencyReceipts.AsNoTracking()
                .SingleOrDefaultAsync(receipt => receipt.BusinessId == businessId
                    && receipt.Scope == scope
                    && receipt.IdempotencyKey == key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    BillingMetrics.RecordIdempotencyReplay(scope, "conflict");
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        QtsBilling.Contracts.Abstractions.ApiErrorCodes.SameKeyDifferentPayload,
                        "This idempotency key was already used for a different request.",
                        "Use a new key when the command payload changes.");
                }

                var replayBody = JsonSerializer.Deserialize<TResponse>(existing.ResponseJson, ContractJson.Options)
                    ?? throw new InvalidOperationException("The stored idempotency response could not be deserialized.");
                BillingMetrics.RecordIdempotencyReplay(scope, "replayed");
                return new IdempotencyExecution<TResponse>(existing.StatusCode, replayBody, true);
            }

            var (resultStatus, resultBody) = await action().ConfigureAwait(false);
            db.IdempotencyReceipts.Add(new IdempotencyReceipt
            {
                BusinessId = businessId,
                Scope = scope,
                IdempotencyKey = key,
                PayloadHash = payloadHash,
                StatusCode = resultStatus,
                ResponseJson = JsonSerializer.Serialize(resultBody, ContractJson.Options),
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new IdempotencyExecution<TResponse>(resultStatus, resultBody, false);
        }).ConfigureAwait(false);
    }
}
