using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Model;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// EF Core based Unit of Work implementation.
    /// 
    /// TRANSACTION POLICY
    /// ------------------
    /// - Scoped lifetime: One instance per HTTP request or worker scope
    /// - Lazy transaction: Transaction created only during SaveChangesAsync
    /// - New transaction per save: Each SaveChanges creates independent transaction
    /// - Auto-rollback on error: Transaction rolled back automatically on failure
    /// 
    /// WORKFLOW (DAPPER와 동일한 패턴)
    /// --------
    /// 1. Repositories make changes via DbContext (Add/Update/Remove)
    /// 2. Call SaveChangesAsync() to persist all changes atomically
    /// 3. SaveChangesAsync internally: BeginTransaction → SaveChanges → Commit
    /// 4. On failure: Transaction rolled back, exception rethrown
    /// 
    /// USAGE EXAMPLE
    /// -------------
    /// // Repository
    /// _context.JobMasters.Add(entity);
    /// _context.ChunkMasters.AddRange(chunks);
    /// 
    /// // Service Layer
    /// await _unitOfWork.SaveChangesAsync();  // ← 여기서 Transaction 열고 Commit
    /// 
    /// LOGGING POLICY
    /// --------------
    /// - Minimal logging (infrastructure layer)
    /// - Log only: SaveChanges success/failure, Rollback execution
    /// 
    /// THREAD SAFETY
    /// -------------
    /// - NOT thread-safe
    /// - Designed for single-threaded request handling
    /// </summary>
    public class UnitOfWork : IUnitOfWork
    {
        private readonly OrchestrationDbContext _context;
        private readonly ILogger<UnitOfWork> _logger;
        private bool _disposed = false;

        public UnitOfWork(
            OrchestrationDbContext context,
            ILogger<UnitOfWork> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Saves all changes to the database within a transaction.
        /// 
        /// Behavior:
        /// 1. Begins new transaction
        /// 2. Saves all tracked changes
        /// 3. Commits transaction
        /// 4. On error: Rolls back transaction and rethrows
        /// 
        /// Same pattern as Dapper UnitOfWork.Commit()
        /// </summary>
        /// <returns>Number of state entries written to database</returns>
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // Transaction 생성
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // 변경사항 저장
                int affectedRows = await _context.SaveChangesAsync(cancellationToken);

                // Transaction Commit
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    AppLog.Log("[UnitOfWork] Transaction committed: RowsAffected={RowsAffected}"),
                    affectedRows);

                return affectedRows;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[UnitOfWork] Concurrency conflict detected"));

                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[UnitOfWork] Database update failed: {Message}"),
                    ex.InnerException?.Message ?? ex.Message);

                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[UnitOfWork] SaveChanges failed: {Message}"),
                    ex.Message);

                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        /// <summary>
        /// Rolls back any pending changes by detaching all tracked entities.
        /// Note: Since each SaveChanges creates its own transaction,
        /// this is mainly useful for discarding changes before save.
        /// </summary>
        public void Rollback()
        {
            try
            {
                // Discard all tracked changes
                foreach (var entry in _context.ChangeTracker.Entries())
                {
                    entry.State = EntityState.Detached;
                }

                _logger.LogWarning(AppLog.Log("[UnitOfWork] Tracked changes discarded"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[UnitOfWork] Rollback failed: {Message}"),
                    ex.Message);
            }
        }

        /// <summary>
        /// Provides access to the underlying DbContext for advanced scenarios.
        /// Use sparingly - prefer repository pattern for most operations.
        /// </summary>
        public OrchestrationDbContext Context => _context;

        public void Dispose()
        {
            if (_disposed) return;
            _context?.Dispose();
            _disposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            if (_context != null)
            {
                await _context.DisposeAsync();
            }
            _disposed = true;
        }
    }

    /// <summary>
    /// Updated IUnitOfWork interface for EF Core.
    /// Simplified to match Dapper version usage pattern.
    /// </summary>
    public interface IUnitOfWork : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Saves all changes to the database within a transaction.
        /// Equivalent to Dapper version's CommitAsync().
        /// </summary>
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Discards all pending changes (detaches tracked entities).
        /// </summary>
        void Rollback();

        /// <summary>
        /// Access to underlying DbContext (use sparingly).
        /// </summary>
        OrchestrationDbContext Context { get; }
    }
}