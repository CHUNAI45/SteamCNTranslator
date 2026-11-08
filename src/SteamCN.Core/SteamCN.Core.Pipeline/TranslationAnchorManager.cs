using System;
using System.Collections.Generic;
using System.Linq;
using SteamCN.Core.Models;

namespace SteamCN.Core.Pipeline;

public class TranslationAnchorManager
{
	private class SessionAnchors
	{
		public Dictionary<string, TranslationAnchor> ById { get; } = new Dictionary<string, TranslationAnchor>();

		public Dictionary<string, List<TranslationAnchor>> ByKey { get; } = new Dictionary<string, List<TranslationAnchor>>();

		public Dictionary<string, int> NextSeq { get; } = new Dictionary<string, int>();
	}

	private const int ExpireMissThreshold = 5;

	private const double MoveThreshold = 6.0;

	private const double TextChangeOverlapRatio = 0.25;

	private readonly Dictionary<string, SessionAnchors> _sessions = new Dictionary<string, SessionAnchors>();

	private readonly Dictionary<string, DateTime> _sessionLastUpdate = new Dictionary<string, DateTime>();

	private readonly object _lock = new object();

	private static readonly TimeSpan SessionExpiry = TimeSpan.FromMinutes(2.0);

	public AnchorUpdateResult UpdateAnchors(string sessionId, IReadOnlyList<TextFragment> currentFragments)
	{
		lock (_lock)
		{
			SessionAnchors orCreateSession = GetOrCreateSession(sessionId);
			_sessionLastUpdate[sessionId] = DateTime.UtcNow;
			List<TranslationAnchor> list = new List<TranslationAnchor>();
			List<TranslationAnchor> list2 = new List<TranslationAnchor>();
			List<TranslationAnchor> list3 = new List<TranslationAnchor>();
			HashSet<string> hashSet = new HashSet<string>();
			Dictionary<string, List<TextFragment>> dictionary = new Dictionary<string, List<TextFragment>>();
			foreach (TextFragment currentFragment in currentFragments)
			{
				if (!string.IsNullOrEmpty(currentFragment.ContentHash))
				{
					string key = TranslationAnchor.BuildAnchorKey(currentFragment.ContentHash, currentFragment.Bounds, currentFragment.Source);
					if (!dictionary.TryGetValue(key, out var value))
					{
						value = (dictionary[key] = new List<TextFragment>());
					}
					value.Add(currentFragment);
				}
			}
			foreach (var (text2, source) in dictionary)
			{
				List<TranslationAnchor> list6 = (orCreateSession.ByKey.TryGetValue(text2, out List<TranslationAnchor> value2) ? (from a in value2
					where a.Status != AnchorStatus.Expired
					orderby a.SourceBounds.Y
					select a).ToList() : new List<TranslationAnchor>());
				List<TextFragment> list7 = source.OrderBy((TextFragment f) => f.Bounds.Y).ToList();
				HashSet<string> hashSet2 = new HashSet<string>();
				foreach (TextFragment item in list7)
				{
					TranslationAnchor translationAnchor = null;
					double num = double.MaxValue;
					foreach (TranslationAnchor item2 in list6)
					{
						if (!hashSet2.Contains(item2.AnchorId))
						{
							double num2 = Math.Abs(item2.SourceBounds.Y - item.Bounds.Y);
							if (num2 < num)
							{
								num = num2;
								translationAnchor = item2;
							}
						}
					}
					if (translationAnchor != null && num < Math.Max(200.0, item.Bounds.Height * 8.0))
					{
						hashSet2.Add(translationAnchor.AnchorId);
						hashSet.Add(translationAnchor.AnchorId);
						translationAnchor.MissCount = 0;
						double num3 = Math.Abs(translationAnchor.SourceBounds.X - item.Bounds.X);
						double num4 = Math.Abs(translationAnchor.SourceBounds.Y - item.Bounds.Y);
						if (num3 > 6.0 || num4 > 6.0)
						{
							translationAnchor.SourceBounds = item.Bounds;
							translationAnchor.LastPositionUpdate = DateTime.UtcNow;
							translationAnchor.Status = ((translationAnchor.Status != AnchorStatus.Pending) ? AnchorStatus.Moved : AnchorStatus.Pending);
							translationAnchor.RegionId = item.RegionId;
							list2.Add(translationAnchor);
						}
						else
						{
							translationAnchor.RegionId = item.RegionId;
						}
					}
					else
					{
						TranslationAnchor translationAnchor2 = CreateAnchor(orCreateSession, sessionId, text2, item);
						list.Add(translationAnchor2);
						hashSet.Add(translationAnchor2.AnchorId);
					}
				}
			}

			foreach (TranslationAnchor item3 in orCreateSession.ById.Values.ToList())
			{
				if (item3.Status != AnchorStatus.Expired && !hashSet.Contains(item3.AnchorId))
				{
					item3.MissCount++;
					if (item3.MissCount >= 1)
					{
						item3.Status = AnchorStatus.Expired;
						list3.Add(item3);
						RemoveAnchorFromIndexes(orCreateSession, item3);
					}
				}
			}
			foreach (TranslationAnchor item4 in list)
			{
				foreach (TranslationAnchor item5 in orCreateSession.ById.Values.ToList())
				{
					if (item5.Status != AnchorStatus.Expired && !(item5.AnchorKey == item4.AnchorKey) && item5.OverlapsWith(item4.SourceBounds, 0.25))
					{
						item5.Status = AnchorStatus.Expired;
						list3.Add(item5);
						RemoveAnchorFromIndexes(orCreateSession, item5);
					}
				}
			}
			List<TranslationAnchor> activeAnchors = orCreateSession.ById.Values.Where((TranslationAnchor a) => a.Status != AnchorStatus.Expired).ToList();
			return new AnchorUpdateResult
			{
				NewAnchors = list,
				MovedAnchors = list2,
				ExpiredAnchors = list3,
				ActiveAnchors = activeAnchors
			};
		}
	}

	private TranslationAnchor CreateAnchor(SessionAnchors session, string sessionId, string anchorKey, TextFragment frag)
	{
		int num = (session.NextSeq.TryGetValue(anchorKey, out var value) ? value : 0);
		session.NextSeq[anchorKey] = num + 1;
		TranslationAnchor translationAnchor = new TranslationAnchor
		{
			AnchorId = $"{anchorKey}:{num}",
			AnchorKey = anchorKey,
			PageId = sessionId,
			SourceText = frag.Text,
			NormalizedText = frag.NormalizedText,
			ContentHash = frag.ContentHash,
			SourceBounds = frag.Bounds,
			Source = frag.Source,
			RegionType = frag.RegionType,
			DetectedLanguage = frag.DetectedLanguage,
			IsParagraph = frag.IsParagraph,
			RegionId = frag.RegionId,
			Status = AnchorStatus.Pending
		};
		session.ById[translationAnchor.AnchorId] = translationAnchor;
		if (!session.ByKey.TryGetValue(anchorKey, out List<TranslationAnchor> value2))
		{
			value2 = new List<TranslationAnchor>();
			session.ByKey[anchorKey] = value2;
		}
		value2.Add(translationAnchor);
		return translationAnchor;
	}

	private static void RemoveAnchorFromIndexes(SessionAnchors session, TranslationAnchor anchor)
	{
		session.ById.Remove(anchor.AnchorId);
		if (session.ByKey.TryGetValue(anchor.AnchorKey, out List<TranslationAnchor> value))
		{
			value.RemoveAll((TranslationAnchor a) => a.AnchorId == anchor.AnchorId);
			if (value.Count == 0)
			{
				session.ByKey.Remove(anchor.AnchorKey);
			}
		}
	}

	public void SetTranslation(string sessionId, string anchorId, string translatedText)
	{
		lock (_lock)
		{
			if (_sessions.TryGetValue(sessionId, out SessionAnchors value) && value.ById.TryGetValue(anchorId, out TranslationAnchor value2))
			{
				value2.TranslationText = translatedText;
				value2.Status = AnchorStatus.Active;
			}
		}
	}

	public IReadOnlyList<TranslationAnchor> GetActiveAnchors(string sessionId)
	{
		lock (_lock)
		{
			if (!_sessions.TryGetValue(sessionId, out SessionAnchors value))
			{
				return Array.Empty<TranslationAnchor>();
			}
			return value.ById.Values.Where((TranslationAnchor a) => a.Status != AnchorStatus.Expired).ToList();
		}
	}

	public IReadOnlyList<TranslationAnchor> GetTranslatedAnchors(string sessionId)
	{
		lock (_lock)
		{
			if (!_sessions.TryGetValue(sessionId, out SessionAnchors value))
			{
				return Array.Empty<TranslationAnchor>();
			}
			return value.ById.Values.Where((TranslationAnchor a) => a.Status != AnchorStatus.Expired && !string.IsNullOrEmpty(a.TranslationText)).ToList();
		}
	}

	public void ShiftAnchors(string sessionId, double deltaY)
	{
		lock (_lock)
		{
			if (!_sessions.TryGetValue(sessionId, out SessionAnchors value))
			{
				return;
			}
			foreach (TranslationAnchor value2 in value.ById.Values)
			{
				if (value2.Status != AnchorStatus.Expired)
				{
					value2.SourceBounds = new ScreenRect(value2.SourceBounds.X, value2.SourceBounds.Y + deltaY, value2.SourceBounds.Width, value2.SourceBounds.Height);
					value2.LastPositionUpdate = DateTime.UtcNow;
				}
			}
		}
	}

	public void ClearSession(string sessionId)
	{
		lock (_lock)
		{
			_sessions.Remove(sessionId);
			_sessionLastUpdate.Remove(sessionId);
		}
	}

	public void ClearAllExcept(string keepSessionId)
	{
		lock (_lock)
		{
			foreach (string item in _sessions.Keys.Where((string id) => id != keepSessionId).ToList())
			{
				_sessions.Remove(item);
				_sessionLastUpdate.Remove(item);
			}
		}
	}

	public int PruneStaleSessions()
	{
		lock (_lock)
		{
			if (_sessionLastUpdate.Count == 0)
			{
				return 0;
			}
			DateTime now = DateTime.UtcNow;
			List<string> list = (from kvp in _sessionLastUpdate
				where now - kvp.Value > SessionExpiry
				select kvp.Key).ToList();
			foreach (string item in list)
			{
				_sessions.Remove(item);
				_sessionLastUpdate.Remove(item);
			}
			return list.Count;
		}
	}

	public (int Total, int Pending, int Active, int Moved, int Expired) GetStats(string sessionId)
	{
		lock (_lock)
		{
			if (!_sessions.TryGetValue(sessionId, out SessionAnchors value))
			{
				return (Total: 0, Pending: 0, Active: 0, Moved: 0, Expired: 0);
			}
			List<TranslationAnchor> list = value.ById.Values.ToList();
			return (Total: list.Count, Pending: Enumerable.Count(list, (TranslationAnchor a) => a.Status == AnchorStatus.Pending), Active: Enumerable.Count(list, (TranslationAnchor a) => a.Status == AnchorStatus.Active), Moved: Enumerable.Count(list, (TranslationAnchor a) => a.Status == AnchorStatus.Moved), Expired: Enumerable.Count(list, (TranslationAnchor a) => a.Status == AnchorStatus.Expired));
		}
	}

	private SessionAnchors GetOrCreateSession(string sessionId)
	{
		if (!_sessions.TryGetValue(sessionId, out SessionAnchors value))
		{
			value = new SessionAnchors();
			_sessions[sessionId] = value;
		}
		return value;
	}
}

