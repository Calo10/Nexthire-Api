using System.Data;
using Dapper;

namespace nexthire_api.WorkflowAutomation.Repositories;

/// <summary>
/// Provides the active workflow SQL connection/transaction (when inside <see cref="SqlWorkflowUnitOfWork"/>)
/// or an owned short-lived connection for standalone repository calls.
/// </summary>
public interface IWorkflowSqlSession
{
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Opens a scope for Dapper commands. Dispose releases owned connections only.
    /// </summary>
    WorkflowSqlScope OpenScope();
}

/// <summary>
/// Dapper command scope bound to an optional ambient workflow transaction.
/// </summary>
public sealed class WorkflowSqlScope : IDisposable
{
    private readonly bool _ownsConnection;
    private bool _disposed;

    public WorkflowSqlScope(IDbConnection connection, IDbTransaction? transaction, bool ownsConnection)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Transaction = transaction;
        _ownsConnection = ownsConnection;
    }

    public IDbConnection Connection { get; }
    public IDbTransaction? Transaction { get; }

    public CommandDefinition CreateCommand(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, Transaction, cancellationToken: cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_ownsConnection)
            return;

        Connection.Dispose();
    }
}
