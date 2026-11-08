using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SteamCN.Storage;

public class DatabaseManager : IDisposable
{
	private readonly string _connectionString;

	private SqliteConnection? _connection;

	private bool _disposed;

	public string DatabasePath { get; }

	public DatabaseManager(string databasePath)
	{
		DatabasePath = databasePath;
		_connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared,
			Pooling = false
		}.ToString();
	}

	public void Initialize()
	{
		string directoryName = Path.GetDirectoryName(DatabasePath);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		_connection = new SqliteConnection(_connectionString);
		_connection.Open();
		using (SqliteCommand sqliteCommand = _connection.CreateCommand())
		{
			sqliteCommand.CommandText = "PRAGMA journal_mode=WAL;";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "PRAGMA busy_timeout=5000;";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "PRAGMA foreign_keys=ON;";
			sqliteCommand.ExecuteNonQuery();
		}
		RunMigrations(_connection);
	}

	private void RunMigrations(SqliteConnection conn)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (\r\n    version INTEGER PRIMARY KEY,\r\n    applied_at TEXT NOT NULL DEFAULT (datetime('now'))\r\n);";
		sqliteCommand.ExecuteNonQuery();
		int num = 0;
		sqliteCommand.CommandText = "SELECT MAX(version) FROM schema_migrations;";
		object obj = sqliteCommand.ExecuteScalar();
		if (obj != null && obj != DBNull.Value)
		{
			num = Convert.ToInt32(obj);
		}
		if (num < 1)
		{
			ApplyMigrationV1(conn);
		}
	}

	private void ApplyMigrationV1(SqliteConnection conn)
	{
		using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
		try
		{
			using SqliteCommand sqliteCommand = conn.CreateCommand();
			sqliteCommand.Transaction = sqliteTransaction;
			sqliteCommand.CommandText = "CREATE TABLE IF NOT EXISTS translation_cache (\r\n    id INTEGER PRIMARY KEY AUTOINCREMENT,\r\n    source_hash TEXT NOT NULL,\r\n    normalized_source TEXT NOT NULL,\r\n    source_language TEXT NOT NULL,\r\n    target_language TEXT NOT NULL,\r\n    provider TEXT NOT NULL,\r\n    glossary_version TEXT NOT NULL,\r\n    app_id TEXT,\r\n    translated_text TEXT NOT NULL,\r\n    created_at TEXT NOT NULL DEFAULT (datetime('now')),\r\n    last_accessed_at TEXT NOT NULL DEFAULT (datetime('now'))\r\n);";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_cache_lookup\r\nON translation_cache(normalized_source, source_language, target_language, provider, glossary_version, COALESCE(app_id, ''));";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "CREATE INDEX IF NOT EXISTS idx_cache_hash ON translation_cache(source_hash);";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "CREATE INDEX IF NOT EXISTS idx_cache_lru ON translation_cache(last_accessed_at);";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "CREATE TABLE IF NOT EXISTS glossary_terms (\r\n    id INTEGER PRIMARY KEY AUTOINCREMENT,\r\n    scope TEXT NOT NULL DEFAULT 'global',\r\n    app_id TEXT,\r\n    source_term TEXT NOT NULL,\r\n    target_term TEXT NOT NULL,\r\n    action TEXT NOT NULL DEFAULT 'preserve',\r\n    case_sensitive INTEGER NOT NULL DEFAULT 0,\r\n    enabled INTEGER NOT NULL DEFAULT 1,\r\n    updated_at TEXT NOT NULL DEFAULT (datetime('now'))\r\n);";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_glossary_term\r\nON glossary_terms(scope, COALESCE(app_id, ''), source_term);";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "CREATE TABLE IF NOT EXISTS settings (\r\n    version INTEGER PRIMARY KEY,\r\n    key TEXT UNIQUE NOT NULL,\r\n    value TEXT\r\n);";
			sqliteCommand.ExecuteNonQuery();
			sqliteCommand.CommandText = "INSERT INTO schema_migrations (version) VALUES (1);";
			sqliteCommand.ExecuteNonQuery();
			sqliteTransaction.Commit();
		}
		catch
		{
			sqliteTransaction.Rollback();
			throw;
		}
	}

	public bool CheckAndRepair()
	{
		try
		{
			if (_connection == null)
			{
				return false;
			}
			using SqliteCommand sqliteCommand = _connection.CreateCommand();
			sqliteCommand.CommandText = "PRAGMA integrity_check;";
			return sqliteCommand.ExecuteScalar()?.ToString() == "ok";
		}
		catch
		{
			try
			{
				_connection?.Dispose();
				string destFileName = DatabasePath + $".corrupted.{DateTime.Now:yyyyMMddHHmmss}.bak";
				if (File.Exists(DatabasePath))
				{
					File.Copy(DatabasePath, destFileName, overwrite: true);
				}
				string[] array = new string[3] { "", "-wal", "-shm" };
				foreach (string text in array)
				{
					string path = DatabasePath + text;
					if (File.Exists(path))
					{
						File.Delete(path);
					}
				}
				Initialize();
				return true;
			}
			catch
			{
				return false;
			}
		}
	}

	public SqliteConnection GetConnection()
	{
		if (_connection == null)
		{
			throw new InvalidOperationException("Database not initialized.");
		}
		return _connection;
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_connection?.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}
