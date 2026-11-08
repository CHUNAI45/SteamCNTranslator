using System;
using System.Collections.Generic;
using System.Linq;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

public class TextRegionTracker : ITextRegionTracker
{
	private class SessionState
	{
		public Dictionary<string, TextFragment> ActiveRegions { get; } = new Dictionary<string, TextFragment>();

		public Dictionary<string, ScreenRect> LastPositions { get; } = new Dictionary<string, ScreenRect>();

		public bool IsScrolling { get; set; }

		public DateTime ScrollStartTime { get; set; }
	}

	private readonly Dictionary<string, SessionState> _sessions = new Dictionary<string, SessionState>();

	private readonly object _lock = new object();

	private const double MoveThreshold = 10.0;

	public TextRegionUpdateResult UpdateRegions(string sessionId, IReadOnlyList<TextFragment> currentFragments)
	{
		lock (_lock)
		{
			SessionState orCreateSession = GetOrCreateSession(sessionId);
			if (orCreateSession.IsScrolling)
			{
				orCreateSession.LastPositions.Clear();
				foreach (TextFragment currentFragment in currentFragments)
				{
					orCreateSession.LastPositions[currentFragment.RegionId] = currentFragment.Bounds;
				}
				return new TextRegionUpdateResult
				{
					Added = Array.Empty<TextFragment>(),
					Unchanged = currentFragments,
					Removed = Array.Empty<TextFragment>(),
					Moved = Array.Empty<TextFragment>()
				};
			}
			HashSet<string> hashSet = new HashSet<string>(currentFragments.Select((TextFragment f) => f.RegionId));
			List<TextFragment> list = new List<TextFragment>();
			List<TextFragment> list2 = new List<TextFragment>();
			List<TextFragment> list3 = new List<TextFragment>();
			List<TextFragment> list4 = new List<TextFragment>();
			Dictionary<string, TextFragment> dictionary = new Dictionary<string, TextFragment>();
			string key;
			TextFragment value;
			foreach (KeyValuePair<string, TextFragment> activeRegion in orCreateSession.ActiveRegions)
			{
				activeRegion.Deconstruct(out key, out value);
				TextFragment textFragment = value;
				if (textFragment.FragmentId != null)
				{
					dictionary.TryAdd(textFragment.FragmentId, textFragment);
				}
			}
			HashSet<string> hashSet2 = new HashSet<string>();
			foreach (TextFragment currentFragment2 in currentFragments)
			{
				TextFragment value4;
				if (orCreateSession.ActiveRegions.TryGetValue(currentFragment2.RegionId, out TextFragment _))
				{
					hashSet2.Add(currentFragment2.RegionId);
					if (orCreateSession.LastPositions.TryGetValue(currentFragment2.RegionId, out var value3))
					{
						double num = Math.Abs(currentFragment2.Bounds.X - value3.X);
						double num2 = Math.Abs(currentFragment2.Bounds.Y - value3.Y);
						if (num > 10.0 || num2 > 10.0)
						{
							list3.Add(currentFragment2);
						}
						else
						{
							list2.Add(currentFragment2);
						}
					}
					else
					{
						list2.Add(currentFragment2);
					}
				}
				else if (currentFragment2.FragmentId != null && dictionary.TryGetValue(currentFragment2.FragmentId, out value4))
				{
					hashSet2.Add(value4.RegionId);
					list3.Add(currentFragment2);
				}
				else
				{
					list.Add(currentFragment2);
				}
			}
			foreach (KeyValuePair<string, TextFragment> activeRegion2 in orCreateSession.ActiveRegions)
			{
				activeRegion2.Deconstruct(out key, out value);
				string item = key;
				TextFragment item2 = value;
				if (!hashSet.Contains(item) && !hashSet2.Contains(item))
				{
					list4.Add(item2);
				}
			}
			orCreateSession.ActiveRegions.Clear();
			foreach (TextFragment currentFragment3 in currentFragments)
			{
				orCreateSession.ActiveRegions[currentFragment3.RegionId] = currentFragment3;
			}
			orCreateSession.LastPositions.Clear();
			foreach (TextFragment currentFragment4 in currentFragments)
			{
				orCreateSession.LastPositions[currentFragment4.RegionId] = currentFragment4.Bounds;
			}
			return new TextRegionUpdateResult
			{
				Added = list,
				Unchanged = list2,
				Removed = list4,
				Moved = list3
			};
		}
	}

	public void ClearSession(string sessionId)
	{
		lock (_lock)
		{
			_sessions.Remove(sessionId);
		}
	}

	public IReadOnlyList<TextFragment> GetActiveRegions(string sessionId)
	{
		lock (_lock)
		{
			if (!_sessions.TryGetValue(sessionId, out SessionState value))
			{
				return Array.Empty<TextFragment>();
			}
			return value.ActiveRegions.Values.ToList();
		}
	}

	public void NotifyScrollStarted(string sessionId)
	{
		lock (_lock)
		{
			SessionState orCreateSession = GetOrCreateSession(sessionId);
			orCreateSession.IsScrolling = true;
			orCreateSession.ScrollStartTime = DateTime.UtcNow;
		}
	}

	public void NotifyScrollEnded(string sessionId)
	{
		lock (_lock)
		{
			SessionState orCreateSession = GetOrCreateSession(sessionId);
			orCreateSession.IsScrolling = false;
			orCreateSession.LastPositions.Clear();
		}
	}

	private SessionState GetOrCreateSession(string sessionId)
	{
		if (!_sessions.TryGetValue(sessionId, out SessionState value))
		{
			value = new SessionState();
			_sessions[sessionId] = value;
		}
		return value;
	}
}
