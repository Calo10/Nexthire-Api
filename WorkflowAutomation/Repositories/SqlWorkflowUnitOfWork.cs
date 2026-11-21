using System.Data;
using System.Data.Common;
using nexthire_api.Data;
using Nexa.WorkflowAutomation.Contracts.Persistence;

namespace nexthire_api.WorkflowAutomation.Repositories;

/// <summary>
/// Real SQL unit of work: one connection + one transaction per <see cref="ExecuteAsync"/>.
/// Also exposes <see cref="IWorkflowSqlSession"/> so workflow repositories share that ambient transaction.
/// </summary>
public sealed class SqlWorkflowUnitOfWork : IWorkflowUnitOfWork, IWorkflowSqlSession, IAsyncDisposable
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<SqlWorkflowUnitOfWork> _logger;

    private IDbConnection? _connection;
    private IDbTransaction? _transaction;
    private bool _completed;
    private int _depth;

    public SqlWorkflowUnitOfWork(
        IDbConnectionFactory connectionFactory,
        ILogger<SqlWorkflowUnitOfWork> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public bool HasActiveTransaction => _transaction is not null;

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A workflow SQL transaction is already active.");

        _connection = _connectionFactory.CreateConnection();
        await OpenConnectionAsync(_connection, cancellationToken).ConfigureAwait(false);
        _transaction = _connection.BeginTransaction();
        _completed = false;
        _depth = 1;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (_transaction is null)
            throw new InvalidOperationException("No active workflow SQL transaction to commit.");

        _transaction.Commit();
        _completed = true;
        DisposeTransactionResources();
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (_transaction is null)
            return Task.CompletedTask;

        try
        {
            if (!_completed)
                _transaction.Rollback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow SQL transaction rollback failed.");
        }
        finally
        {
            DisposeTransactionResources();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null && !_completed)
            await RollbackAsync().ConfigureAwait(false);
        else
            DisposeTransactionResources();
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ExecuteAsync(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Re-entrant: engine does not nest today; join ambient if present.
        if (_transaction is not null)
        {
            _depth++;
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _depth--;
            }
        }

        await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            await CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public WorkflowSqlScope OpenScope()
    {
        if (_transaction is not null && _connection is not null)
            return new WorkflowSqlScope(_connection, _transaction, ownsConnection: false);

        var connection = _connectionFactory.CreateConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();

        return new WorkflowSqlScope(connection, transaction: null, ownsConnection: true);
    }

    private void DisposeTransactionResources()
    {
        try
        {
            _transaction?.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            _connection?.Dispose();
        }
        catch
        {
            // ignored
        }

        _transaction = null;
        _connection = null;
        _depth = 0;
        _completed = false;
    }

    private static async Task OpenConnectionAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Open)
            return;

        if (connection is DbConnection dbConnection)
        {
            await dbConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        connection.Open();
    }
}
