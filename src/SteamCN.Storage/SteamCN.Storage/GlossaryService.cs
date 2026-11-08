using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Storage;

public class GlossaryService : IGlossaryProcessor
{
	private readonly DatabaseManager _db;

	private string _version = string.Empty;

	private List<GlossaryTerm>? _cachedTerms;

	public string Version => _version;

	public event EventHandler? GlossaryChanged;

	public GlossaryService(DatabaseManager db)
	{
		_db = db;
		RefreshVersion();
	}

	public GlossaryProcessResult ProtectTerms(string text, string? appId)
	{
		IReadOnlyList<GlossaryTerm> activeTerms = GetActiveTerms(appId);
		if (activeTerms.Count == 0)
		{
			return new GlossaryProcessResult
			{
				ProtectedText = text,
				Placeholders = new Dictionary<string, GlossaryTerm>()
			};
		}
		Dictionary<string, GlossaryTerm> dictionary = new Dictionary<string, GlossaryTerm>();
		string text2 = text;
		int num = 0;
		foreach (GlossaryTerm item in activeTerms.OrderByDescending((GlossaryTerm t) => t.SourceTerm.Length))
		{
			if (text2.Contains(item.SourceTerm, item.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
			{
				string text3 = $"⟦SCT{num:D3}⟧";
				num++;
				string text4 = text2;
				text2 = ReplaceTerm(text2, item.SourceTerm, text3, item.CaseSensitive);
				if ((object)text4 != text2 && !(text4 == text2))
				{
					dictionary[text3] = item;
				}
			}
		}
		return new GlossaryProcessResult
		{
			ProtectedText = text2,
			Placeholders = dictionary
		};
	}

	public string RestoreTerms(string translatedText, IReadOnlyDictionary<string, GlossaryTerm> placeholders)
	{
		if (placeholders.Count == 0)
		{
			return translatedText;
		}
		string text = translatedText;
		foreach (KeyValuePair<string, GlossaryTerm> placeholder in placeholders)
		{
			placeholder.Deconstruct(out var key, out var value);
			string text2 = key;
			GlossaryTerm glossaryTerm = value;
			key = glossaryTerm.Action switch
			{
				GlossaryAction.Preserve => glossaryTerm.SourceTerm, 
				GlossaryAction.Force => glossaryTerm.TargetTerm, 
				GlossaryAction.Context => glossaryTerm.TargetTerm, 
				_ => glossaryTerm.SourceTerm, 
			};
			string newValue = key;
			text = text.Replace(text2, newValue, StringComparison.Ordinal);
			string oldValue = text2.Replace("⟦", "[").Replace("⟧", "]");
			text = text.Replace(oldValue, newValue, StringComparison.Ordinal);
		}
		return text;
	}

	public IReadOnlyList<GlossaryTerm> GetTerms(string? appId = null)
	{
		lock (this)
		{
			List<GlossaryTerm> list = new List<GlossaryTerm>();
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			if (appId != null)
			{
				sqliteCommand.CommandText = "SELECT id, scope, app_id, source_term, target_term, action, case_sensitive, enabled, updated_at\r\nFROM glossary_terms\r\nWHERE enabled = 1 AND (scope = 'global' OR (scope = 'app' AND app_id = @appId))\r\nORDER BY LENGTH(source_term) DESC;";
				sqliteCommand.Parameters.AddWithValue("@appId", appId);
			}
			else
			{
				sqliteCommand.CommandText = "SELECT id, scope, app_id, source_term, target_term, action, case_sensitive, enabled, updated_at\r\nFROM glossary_terms\r\nWHERE scope = 'global'\r\nORDER BY LENGTH(source_term) DESC;";
			}
			using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			while (sqliteDataReader.Read())
			{
				list.Add(ReadGlossaryTerm(sqliteDataReader));
			}
			return list;
		}
	}

	private IReadOnlyList<GlossaryTerm> GetActiveTerms(string? appId)
	{
		if (_cachedTerms != null)
		{
			return _cachedTerms;
		}
		_cachedTerms = (List<GlossaryTerm>)GetTerms(appId);
		return _cachedTerms;
	}

	public void UpsertTerm(GlossaryTerm term)
	{
		lock (this)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "INSERT INTO glossary_terms (scope, app_id, source_term, target_term, action, case_sensitive, enabled, updated_at)\r\nVALUES (@scope, @appId, @source, @target, @action, @caseSens, @enabled, @updated)\r\nON CONFLICT(scope, COALESCE(app_id, ''), source_term)\r\nDO UPDATE SET target_term = @target, action = @action, case_sensitive = @caseSens, enabled = @enabled, updated_at = @updated;";
			sqliteCommand.Parameters.AddWithValue("@scope", term.Scope);
			sqliteCommand.Parameters.AddWithValue("@appId", ((object)term.AppId) ?? ((object)DBNull.Value));
			sqliteCommand.Parameters.AddWithValue("@source", term.SourceTerm);
			sqliteCommand.Parameters.AddWithValue("@target", term.TargetTerm);
			sqliteCommand.Parameters.AddWithValue("@action", term.Action.ToString().ToLowerInvariant());
			sqliteCommand.Parameters.AddWithValue("@caseSens", term.CaseSensitive ? 1 : 0);
			sqliteCommand.Parameters.AddWithValue("@enabled", term.Enabled ? 1 : 0);
			sqliteCommand.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
			sqliteCommand.ExecuteNonQuery();
		}
		InvalidateCache();
	}

	public bool DeleteTerm(long id)
	{
		lock (this)
		{
			using SqliteCommand sqliteCommand = _db.GetConnection().CreateCommand();
			sqliteCommand.CommandText = "DELETE FROM glossary_terms WHERE id = @id;";
			sqliteCommand.Parameters.AddWithValue("@id", id);
			sqliteCommand.ExecuteNonQuery();
		}
		InvalidateCache();
		return true;
	}

	public Task<int> ImportFromJsonAsync(string jsonContent, string? appId = null)
	{
		List<GlossaryTerm>? obj = JsonSerializer.Deserialize<List<GlossaryTerm>>(jsonContent) ?? new List<GlossaryTerm>();
		int num = 0;
		foreach (GlossaryTerm item in obj)
		{
			GlossaryTerm term = new GlossaryTerm
			{
				Scope = ((appId != null) ? "app" : item.Scope),
				AppId = (appId ?? item.AppId),
				SourceTerm = item.SourceTerm,
				TargetTerm = item.TargetTerm,
				Action = item.Action,
				CaseSensitive = item.CaseSensitive,
				Enabled = item.Enabled
			};
			UpsertTerm(term);
			num++;
		}
		return Task.FromResult(num);
	}

	public Task<string> ExportToJsonAsync(string? appId = null)
	{
		return Task.FromResult(JsonSerializer.Serialize(GetTerms(appId), new JsonSerializerOptions
		{
			WriteIndented = true
		}));
	}

	public void RefreshVersion()
	{
		IReadOnlyList<GlossaryTerm> terms = GetTerms();
		StringBuilder stringBuilder = new StringBuilder();
		foreach (GlossaryTerm item in terms.OrderBy((GlossaryTerm t) => t.SourceTerm))
		{
			stringBuilder.Append(item.SourceTerm).Append('|').Append(item.TargetTerm)
				.Append('|')
				.Append(item.Action)
				.Append(';');
		}
		byte[] inArray = SHA256.HashData(Encoding.UTF8.GetBytes(stringBuilder.ToString()));
		_version = Convert.ToHexString(inArray, 0, 8).ToLowerInvariant();
	}

	private void InvalidateCache()
	{
		_cachedTerms = null;
		RefreshVersion();
		GlossaryChanged?.Invoke(this, EventArgs.Empty);
	}

	private static string ReplaceTerm(string text, string source, string replacement, bool caseSensitive)
	{
		StringComparison comparisonType = (caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
		RegexOptions options = ((!caseSensitive) ? RegexOptions.IgnoreCase : RegexOptions.None);
		if (source.All((char c) => c < '\u0080' && (char.IsLetterOrDigit(c) || c == ' ' || c == '-')) && source.Length > 1)
		{
			try
			{
				string pattern = "\\b" + Regex.Escape(source) + "\\b";
				return Regex.Replace(text, pattern, replacement, options);
			}
			catch
			{
			}
		}
		return text.Replace(source, replacement, comparisonType);
	}

	private static GlossaryTerm ReadGlossaryTerm(SqliteDataReader reader)
	{
		return new GlossaryTerm
		{
			Id = reader.GetInt64(0),
			Scope = reader.GetString(1),
			AppId = (reader.IsDBNull(2) ? null : reader.GetString(2)),
			SourceTerm = reader.GetString(3),
			TargetTerm = reader.GetString(4),
			Action = Enum.Parse<GlossaryAction>(reader.GetString(5), ignoreCase: true),
			CaseSensitive = (reader.GetInt64(6) != 0),
			Enabled = (reader.GetInt64(7) != 0),
			UpdatedAt = DateTimeOffset.Parse(reader.GetString(8))
		};
	}
}
