#region License
// The PostgreSQL License
//
// Copyright (C) 2016 The Npgsql Development Team
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
#endregion

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Utilities;
using Npgsql;

namespace Microsoft.EntityFrameworkCore.Storage.Internal
{
    public class NpgsqlDatabaseCreator : RelationalDatabaseCreator
    {
        readonly NpgsqlRelationalConnection _connection;
        readonly IMigrationsSqlGenerator _migrationsSqlGenerator;
        readonly IRawSqlCommandBuilder _rawSqlCommandBuilder;
        readonly IMigrationsModelDiffer _modelDiffer;

        public NpgsqlDatabaseCreator(
            [NotNull] NpgsqlRelationalConnection connection,
            [NotNull] IMigrationsModelDiffer modelDiffer,
            [NotNull] IMigrationsSqlGenerator migrationsSqlGenerator,
            [NotNull] IMigrationCommandExecutor migrationCommandExecutor,
            [NotNull] IModel model,
            [NotNull] IRawSqlCommandBuilder rawSqlCommandBuilder)
            : base(model, connection, modelDiffer, migrationsSqlGenerator, migrationCommandExecutor)
        {
            Check.NotNull(rawSqlCommandBuilder, nameof(rawSqlCommandBuilder));

            _connection = connection;
            _modelDiffer = modelDiffer;
            _migrationsSqlGenerator = migrationsSqlGenerator;
            _rawSqlCommandBuilder = rawSqlCommandBuilder;
        }

        public override void Create()
        {
            using (var masterConnection = _connection.CreateMasterConnection())
            {
                MigrationCommandExecutor
                    .ExecuteNonQuery(CreateCreateOperations(), masterConnection);

                ClearPool();
            }
        }

        public override async Task CreateAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var masterConnection = _connection.CreateMasterConnection())
            {
                await MigrationCommandExecutor
                    .ExecuteNonQueryAsync(CreateCreateOperations(), masterConnection, cancellationToken);

                ClearPool();
            }
        }

        protected override bool HasTables()
            => (bool)CreateHasTablesCommand().ExecuteScalar(_connection);

        protected override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default(CancellationToken))
            => (bool)(await CreateHasTablesCommand().ExecuteScalarAsync(_connection, cancellationToken: cancellationToken));

        IRelationalCommand CreateHasTablesCommand()
            => _rawSqlCommandBuilder
                .Build(@"
                    SELECT CASE WHEN COUNT(*) = 0 THEN FALSE ELSE TRUE END
                    FROM information_schema.tables
                    WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')
                ");

        IReadOnlyList<MigrationCommand> CreateCreateOperations()
            => _migrationsSqlGenerator.Generate(new[] { new NpgsqlCreateDatabaseOperation { Name = _connection.DbConnection.Database, Template = Model.Npgsql().DatabaseTemplate } });

        public override bool Exists()
        {
            try
            {
                _connection.Open();
                _connection.Close();
                return true;
            }
            catch (PostgresException e)
            {
                if (IsDoesNotExist(e))
                {
                    return false;
                }

                throw;
            }
        }

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                await _connection.OpenAsync(cancellationToken);
                _connection.Close();
                return true;
            }
            catch (PostgresException e)
            {
                if (IsDoesNotExist(e))
                {
                    return false;
                }

                throw;
            }
        }

        // Login failed is thrown when database does not exist (See Issue #776)
        static bool IsDoesNotExist(PostgresException exception) => exception.SqlState == "3D000";

        public override void Delete()
        {
            ClearAllPools();

            using (var masterConnection = _connection.CreateMasterConnection())
            {
                MigrationCommandExecutor
                    .ExecuteNonQuery(CreateDropCommands(), masterConnection);
            }
        }

        public override async Task DeleteAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ClearAllPools();

            using (var masterConnection = _connection.CreateMasterConnection())
            {
                await MigrationCommandExecutor
                    .ExecuteNonQueryAsync(CreateDropCommands(), masterConnection, cancellationToken);
            }
        }

        public override void CreateTables()
        {
            var operations = _modelDiffer.GetDifferences(null, Model);
            var commands = _migrationsSqlGenerator.Generate(operations, Model);

            // Adding a PostgreSQL extension might define new types (e.g. hstore), which we
            // Npgsql to reload
            var reloadTypes = operations.Any(o => o is NpgsqlCreatePostgresExtensionOperation);

            MigrationCommandExecutor.ExecuteNonQuery(commands, Connection);

            if (reloadTypes)
            {
                var npgsqlConn = (NpgsqlConnection)Connection.DbConnection;
                if (npgsqlConn.FullState == ConnectionState.Open)
                    npgsqlConn.ReloadTypes();
            }
        }

        public override async Task CreateTablesAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var operations = _modelDiffer.GetDifferences(null, Model);
            var commands = _migrationsSqlGenerator.Generate(operations, Model);

            // Adding a PostgreSQL extension might define new types (e.g. hstore), which we
            // Npgsql to reload
            var reloadTypes = operations.Any(o => o is NpgsqlCreatePostgresExtensionOperation);

            await MigrationCommandExecutor.ExecuteNonQueryAsync(commands, Connection, cancellationToken);

            // TODO: Not async
            if (reloadTypes)
            {
                var npgsqlConn = (NpgsqlConnection)Connection.DbConnection;
                if (npgsqlConn.FullState == ConnectionState.Open)
                    npgsqlConn.ReloadTypes();
            }
        }

        IReadOnlyList<MigrationCommand> CreateDropCommands()
        {
            var operations = new MigrationOperation[]
            {
                // TODO Check DbConnection.Database always gives us what we want
                // Issue #775
                new NpgsqlDropDatabaseOperation { Name = _connection.DbConnection.Database }
            };

            var masterCommands = _migrationsSqlGenerator.Generate(operations);
            return masterCommands;
        }

        // Clear connection pools in case there are active connections that are pooled
        private static void ClearAllPools() => NpgsqlConnection.ClearAllPools();

        // Clear connection pool for the database connection since after the 'create database' call, a previously
        // invalid connection may now be valid.
        private void ClearPool() => NpgsqlConnection.ClearPool((NpgsqlConnection)_connection.DbConnection);
    }
}
