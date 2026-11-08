using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Storage;

public class TranslationMemoryService : ITranslationMemory
{
	private readonly DatabaseManager _db;

	private readonly object _lock = new object();

	private long _count = -1L;

	public long Count
	{
		get
		{
			if (_count >= 0)
			{
				return _count;
			}
			lock (_lock)
			{
				if (_count >= 0)
				{
					return _count;
				}
				using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
				sqliteCommand.CommandText = "SELECT COUNT(*) FROM translation_cache;";
				_count = Convert.ToInt64(sqliteCommand.ExecuteScalar());
				return _count;
			}
		}
	}

	public TranslationMemoryService(DatabaseManager db)
	{
		_db = db;
	}

	public CachedTranslation? Lookup(string normalizedSource, string sourceLanguage, string targetLanguage, string provider, string glossaryVersion, string? appId)
	{
		lock (_lock)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "SELECT id, source_hash, normalized_source, source_language, target_language,\r\n       provider, glossary_version, app_id, translated_text, created_at, last_accessed_at\r\nFROM translation_cache\r\nWHERE normalized_source = @source\r\n  AND source_language = @srcLang\r\n  AND target_language = @tgtLang\r\n  AND provider = @provider\r\n  AND glossary_version = @glossVer\r\n  AND COALESCE(app_id, '') = COALESCE(@appId, '')\r\nLIMIT 1;";
			sqliteCommand.Parameters.AddWithValue("@source", normalizedSource);
			sqliteCommand.Parameters.AddWithValue("@srcLang", sourceLanguage);
			sqliteCommand.Parameters.AddWithValue("@tgtLang", targetLanguage);
			sqliteCommand.Parameters.AddWithValue("@provider", provider);
			sqliteCommand.Parameters.AddWithValue("@glossVer", glossaryVersion);
			sqliteCommand.Parameters.AddWithValue("@appId", ((object)appId) ?? ((object)DBNull.Value));
			using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			if (!sqliteDataReader.Read())
			{
				return null;
			}
			long @int = sqliteDataReader.GetInt64(0);
			UpdateLastAccessed(@int);
			return ReadCachedTranslation(sqliteDataReader);
		}
	}

	public IReadOnlyDictionary<string, CachedTranslation?> LookupBatch(IEnumerable<string> normalizedSources, string sourceLanguage, string targetLanguage, string provider, string glossaryVersion, string? appId)
	{
		Dictionary<string, CachedTranslation> dictionary = new Dictionary<string, CachedTranslation>();
		foreach (string normalizedSource in normalizedSources)
		{
			dictionary[normalizedSource] = Lookup(normalizedSource, sourceLanguage, targetLanguage, provider, glossaryVersion, appId);
		}
		return dictionary;
	}

	public void Store(CachedTranslation entry)
	{
		lock (_lock)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "INSERT INTO translation_cache\r\n    (source_hash, normalized_source, source_language, target_language, provider,\r\n     glossary_version, app_id, translated_text, created_at, last_accessed_at)\r\nVALUES\r\n    (@hash, @source, @srcLang, @tgtLang, @provider,\r\n     @glossVer, @appId, @translated, @created, @accessed)\r\nON CONFLICT(normalized_source, source_language, target_language, provider, glossary_version, COALESCE(app_id, ''))\r\nDO UPDATE SET translated_text = @translated, last_accessed_at = @accessed;";
			sqliteCommand.Parameters.AddWithValue("@hash", entry.SourceHash);
			sqliteCommand.Parameters.AddWithValue("@source", entry.NormalizedSource);
			sqliteCommand.Parameters.AddWithValue("@srcLang", entry.SourceLanguage);
			sqliteCommand.Parameters.AddWithValue("@tgtLang", entry.TargetLanguage);
			sqliteCommand.Parameters.AddWithValue("@provider", entry.Provider);
			sqliteCommand.Parameters.AddWithValue("@glossVer", entry.GlossaryVersion);
			sqliteCommand.Parameters.AddWithValue("@appId", ((object)entry.AppId) ?? ((object)DBNull.Value));
			sqliteCommand.Parameters.AddWithValue("@translated", entry.TranslatedText);
			sqliteCommand.Parameters.AddWithValue("@created", entry.CreatedAt.ToString("O"));
			sqliteCommand.Parameters.AddWithValue("@accessed", entry.LastAccessedAt.ToString("O"));
			sqliteCommand.ExecuteNonQuery();
			Interlocked.Increment(ref _count);
		}
	}

	public void StoreBatch(IEnumerable<CachedTranslation> entries)
	{
		foreach (CachedTranslation entry in entries)
		{
			Store(entry);
		}
	}

	public int InvalidateByGlossaryVersion(string oldVersion)
	{
		lock (_lock)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "DELETE FROM translation_cache WHERE glossary_version = @ver;";
			sqliteCommand.Parameters.AddWithValue("@ver", oldVersion);
			int result = sqliteCommand.ExecuteNonQuery();
			_count = -1L;
			return result;
		}
	}

	public int InvalidateByAppId(string appId)
	{
		lock (_lock)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "DELETE FROM translation_cache WHERE app_id = @appId;";
			sqliteCommand.Parameters.AddWithValue("@appId", appId);
			int result = sqliteCommand.ExecuteNonQuery();
			_count = -1L;
			return result;
		}
	}

	public void ClearAll()
	{
		lock (_lock)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "DELETE FROM translation_cache;";
			sqliteCommand.ExecuteNonQuery();
			_count = 0L;
		}
	}

	public int TrimToCapacity(long maxEntries)
	{
		lock (_lock)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "DELETE FROM translation_cache\r\nWHERE id IN (\r\n    SELECT id FROM translation_cache\r\n    ORDER BY last_accessed_at DESC\r\n    LIMIT -1 OFFSET @max\r\n);";
			sqliteCommand.Parameters.AddWithValue("@max", maxEntries);
			int result = sqliteCommand.ExecuteNonQuery();
			_count = -1L;
			return result;
		}
	}

	public Task<string> ExportToJsonAsync()
	{
		List<CachedTranslation> list = new List<CachedTranslation>();
		lock (_lock)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "SELECT id, source_hash, normalized_source, source_language, target_language,\r\n       provider, glossary_version, app_id, translated_text, created_at, last_accessed_at\r\nFROM translation_cache;";
			using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			while (sqliteDataReader.Read())
			{
				list.Add(ReadCachedTranslation(sqliteDataReader));
			}
		}
		return Task.FromResult(JsonSerializer.Serialize(list, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
	}

	public Task<int> ImportFromJsonAsync(string jsonContent)
	{
		List<CachedTranslation> list = JsonSerializer.Deserialize<List<CachedTranslation>>(jsonContent) ?? new List<CachedTranslation>();
		StoreBatch(list);
		return Task.FromResult(list.Count);
	}

	public bool CheckAndRepair()
	{
		return _db.CheckAndRepair();
	}

	private void UpdateLastAccessed(long id)
	{
		using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
		sqliteCommand.CommandText = "UPDATE translation_cache SET last_accessed_at = @now WHERE id = @id;";
		sqliteCommand.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
		sqliteCommand.Parameters.AddWithValue("@id", id);
		sqliteCommand.ExecuteNonQuery();
	}

	private static CachedTranslation ReadCachedTranslation(SqliteDataReader reader)
	{
		return new CachedTranslation
		{
			Id = reader.GetInt64(0),
			SourceHash = reader.GetString(1),
			NormalizedSource = reader.GetString(2),
			SourceLanguage = reader.GetString(3),
			TargetLanguage = reader.GetString(4),
			Provider = reader.GetString(5),
			GlossaryVersion = reader.GetString(6),
			AppId = (reader.IsDBNull(7) ? null : reader.GetString(7)),
			TranslatedText = reader.GetString(8),
			CreatedAt = DateTimeOffset.Parse(reader.GetString(9)),
			LastAccessedAt = DateTimeOffset.Parse(reader.GetString(10))
		};
	}
}
