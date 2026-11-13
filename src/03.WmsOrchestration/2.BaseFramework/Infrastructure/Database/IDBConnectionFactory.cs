using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Npgsql;
using Portfolio.WmsOrchestration.Model;
using System.Data;
using System.Data.SqlClient;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Factory interface for creating database connections.
    /// </summary>
    public interface IDBConnectionFactory
    {
        /// <summary>
        /// Creates a database connection for the specified database type.
        /// </summary>
        IDbConnection CreateConnection(DatabaseType databaseType);
    }

    /// <summary>
    /// Database connection factory implementation.
    /// 
    /// RESPONSIBILITIES
    /// ----------------
    /// - Create typed database connections based on DatabaseType
    /// - Retrieve connection strings from configuration
    /// - Support multiple database providers (MSSQL, PostgreSQL)
    /// 
    /// SUPPORTED DATABASES
    /// -------------------
    /// - Microsoft SQL Server
    /// - PostgreSQL (standard and with authentication)
    /// </summary>
    public class DBConnectionFactory : IDBConnectionFactory
    {
        private readonly IConfiguration _configuration;

        public DBConnectionFactory(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Creates a database connection based on the specified database type.
        /// </summary>
        /// <param name="dbType">Type of database to connect to.</param>
        /// <returns>Configured database connection.</returns>
        /// <exception cref="ArgumentException">Thrown when database type is not supported.</exception>
        public IDbConnection CreateConnection(DatabaseType dbType)
        {
            return dbType switch
            {
                DatabaseType.MSSQL =>
                    new SqlConnection(_configuration.GetConnectionString("MSSQL")),

                DatabaseType.PostgreSQL =>
                    new NpgsqlConnection(_configuration.GetConnectionString("PostgreSQL")),

                DatabaseType.PostgreSQL_NT_Auth =>
                    new NpgsqlConnection(_configuration.GetConnectionString("PostgreSQL_NT_Auth")),

                _ => throw new ArgumentException($"Unsupported database type: {dbType}", nameof(dbType))
            };
        }
    }
}