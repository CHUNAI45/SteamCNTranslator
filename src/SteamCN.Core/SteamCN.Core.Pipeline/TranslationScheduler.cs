using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Core.Pipeline;

public class TranslationScheduler : IDisposable
{
	private readonly ITextAcquisition _textAcquisition;

	private readonly IPageClassifier _pageClassifier;

	private readonly ITextRegionTracker _regionTracker;

	private readonly IGlossaryProcessor _glossaryProcessor;

	private readonly ITranslationMemory _translationMemory;

	private readonly ITranslationProvider _translationProvider;

	private readonly IOverlayRenderer _overlayRenderer;

	private readonly ISettingsManager _settingsManager;

	private readonly RegionClassifier _regionClassifier = new RegionClassifier();

	private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessionCts = new ConcurrentDictionary<string, CancellationTokenSource>();

	private readonly ConcurrentDictionary<string, Task> _sessionTasks = new ConcurrentDictionary<string, Task>();

	private readonly ConcurrentDictionary<string, byte> _inFlightRequests = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, byte> _dirtySessions = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<nint, string> _windowSessions = new();
	private readonly ConcurrentDictionary<string, HashSet<string>> _activeSources = new();

	private readonly SemaphoreSlim _concurrencyLimiter;

	private readonly object _stateLock = new object();

	private bool _isPaused;

	private bool _disposed;

	public TranslationAnchorManager AnchorManager { get; } = new TranslationAnchorManager();

	public bool IsPaused => _isPaused;

	public bool IsProcessing => _sessionTasks.Values.Any((Task t) => !t.IsCompleted);

	public int QueueDepth => _inFlightRequests.Count;

	public event EventHandler<TranslationResult>? TranslationCompleted;

	public event EventHandler<PipelineDiagnostics>? PipelineDiagnostics;

	public event EventHandler<Exception>? PipelineError;

	public TranslationScheduler(ITextAcquisition textAcquisition, IPageClassifier pageClassifier, ITextRegionTracker regionTracker, IGlossaryProcessor glossaryProcessor, ITranslationMemory translationMemory, ITranslationProvider translationProvider, IOverlayRenderer overlayRenderer, ISettingsManager settingsManager)
	{
		_textAcquisition = textAcquisition;
		_pageClassifier = pageClassifier;
		_regionTracker = regionTracker;
		_glossaryProcessor = glossaryProcessor;
		_translationMemory = translationMemory;
		_translationProvider = translationProvider;
		_overlayRenderer = overlayRenderer;
		_settingsManager = settingsManager;
		_concurrencyLimiter = new SemaphoreSlim(settingsManager.Settings.Translation.MaxConcurrency, settingsManager.Settings.Translation.MaxConcurrency);
	}

	public void Pause()
	{
		_isPaused = true;
	}

	public void Resume()
	{
		_isPaused = false;
	}

	public void CancelWindow(nint handle)
	{
		if (_windowSessions.TryRemove(handle, out string? sessionId)) CancelSession(sessionId, hideTranslations: false);
	}

	public void ProcessVerifiedText(SteamWindowInfo window, IReadOnlyList<TextFragment> fragments)
	{
		string sessionId = window.CurrentSessionId;
		if (fragments.Count == 0 || fragments[0].SessionId != sessionId) return;
		if (_activeSources.TryGetValue(sessionId, out var sources) &&
			!fragments.Any(fragment => sources.Contains(fragment.ContentHash)) &&
			_sessionCts.TryGetValue(sessionId, out var pending))
		{
			_dirtySessions.TryAdd(sessionId, 1);
			try { pending.Cancel(); }
			catch (ObjectDisposedException) { }
		}
		ProcessWindow(window);
	}

	public void ProcessWindow(SteamWindowInfo window)
	{
		if (!_disposed && !_isPaused && window.IsForeground && window.PageUrl != null && _settingsManager.Settings.AutoTranslate)
		{
			string currentSessionId = window.CurrentSessionId;
			if (_windowSessions.TryGetValue(window.Handle, out var previous) && previous != currentSessionId)
				CancelSession(previous);
			_windowSessions[window.Handle] = currentSessionId;
			lock (_stateLock)
			{
				if (_sessionTasks.TryGetValue(currentSessionId, out Task? active) && !active.IsCompleted)
					_dirtySessions.TryAdd(currentSessionId, 1);
				else
					StartProcessing(window, currentSessionId);
			}
		}
	}

	private void StartProcessing(SteamWindowInfo window, string sessionId)
	{
		if (_sessionCts.TryRemove(sessionId, out CancellationTokenSource value))
		{
			try
			{
				value.Cancel();
			}
			catch
			{
			}
			value.Dispose();
		}
		CancellationTokenSource cts = new CancellationTokenSource();
		_sessionCts[sessionId] = cts;
		CancellationToken token = cts.Token;
		Task value2 = Task.Run(() => ProcessWindowInternalAsync(window, sessionId, token), token);
		_sessionTasks[sessionId] = value2;
	}

	public void CancelSession(string sessionId) => CancelSession(sessionId, hideTranslations: true);

	private void CancelSession(string sessionId, bool hideTranslations)
	{
		_dirtySessions.TryRemove(sessionId, out _);
		_sessionTasks.TryRemove(sessionId, out _);
		if (_sessionCts.TryRemove(sessionId, out CancellationTokenSource value2))
		{
			value2.Cancel();
			value2.Dispose();
		}
		_regionTracker.ClearSession(sessionId);
		AnchorManager.ClearSession(sessionId);
		if (hideTranslations) _overlayRenderer.HideTranslations(sessionId);
	}

	private async Task ProcessWindowInternalAsync(SteamWindowInfo window, string sessionId, CancellationToken ct)
	{
		Stopwatch swTotal = Stopwatch.StartNew();
		PipelineDiagnostics diag = new PipelineDiagnostics
		{
			SessionId = sessionId
		};
		HashSet<string> myInFlightHashes = new HashSet<string>();
		byte value;
		try
		{
			AppSettings settings = _settingsManager.Settings;
			ScreenRect bounds = window.Bounds;
			Stopwatch swAcquire = Stopwatch.StartNew();
			IReadOnlyList<TextFragment> fragments = await _textAcquisition.AcquireAsync(window, bounds, ct);
			swAcquire.Stop();
			diag.AcquireMs = swAcquire.ElapsedMilliseconds;
			diag.FragmentCount = fragments.Count;
			diag.UiaCount = _textAcquisition.LastUiaFragmentCount;
			diag.OcrCount = _textAcquisition.LastOcrFragmentCount;
			diag.OcrAttempted = _textAcquisition.LastOcrAttempted;
			diag.CaptureSuccess = _textAcquisition.LastCaptureSuccess;
			if (_textAcquisition.IsScrolling)
			{
				double lastScrollDeltaY = _textAcquisition.LastScrollDeltaY;
				_overlayRenderer.ShiftBlocks(sessionId, lastScrollDeltaY);
				AnchorManager.ShiftAnchors(sessionId, lastScrollDeltaY);
				diag.FilterReason = $"滚动中(deltaY={lastScrollDeltaY:F0}px)，仅平移译文";
				diag.TotalMs = swTotal.ElapsedMilliseconds;
				PipelineDiagnostics?.Invoke(this, diag);
				return;
			}
			if (ct.IsCancellationRequested || window.CurrentSessionId != sessionId || !window.IsForeground || window.PageUrl == null || fragments.Count == 0)
			{
				diag.TotalMs = swTotal.ElapsedMilliseconds;
				PipelineDiagnostics?.Invoke(this, diag);
				return;
			}
			if (settings.Privacy.ExcludeSensitiveRegions)
			{
				fragments = fragments.Where((TextFragment f) => !f.IsSensitive).ToList();
			}
			fragments = _regionClassifier.Classify(fragments, window.Bounds.Height);
			diag.RegionTypes = string.Join(",", (from f in fragments
				group f by f.RegionType into g
				select $"{g.Key}={g.Count()}").Take(8));
			AnchorUpdateResult anchorResult = AnchorManager.UpdateAnchors(sessionId, fragments);
			diag.RegionAdded = anchorResult.NewAnchors.Count;
			diag.RegionMoved = anchorResult.MovedAnchors.Count;
			diag.RegionRemoved = anchorResult.ExpiredAnchors.Count;
			if (anchorResult.ExpiredAnchors.Count > 0)
			{
				_overlayRenderer.RemoveBlocks(sessionId, anchorResult.ExpiredAnchors.Select((TranslationAnchor a) => a.AnchorId).ToList());
			}
			if (anchorResult.MovedAnchors.Count > 0)
			{
				List<(string, ScreenRect)> movedAnchors = anchorResult.MovedAnchors.Select((TranslationAnchor a) => (AnchorId: a.AnchorId, SourceBounds: a.SourceBounds)).ToList();
				_overlayRenderer.UpdateAnchorPositions(sessionId, movedAnchors);
			}
			Stopwatch swClassify = Stopwatch.StartNew();
			PageClassification classification = await _pageClassifier.ClassifyAsync(window, fragments, ct);
			swClassify.Stop();
			diag.ClassifyMs = swClassify.ElapsedMilliseconds;
			diag.PageArea = classification.Area.ToString();
			diag.ClassifyEvidence = classification.Evidence;
			if (ct.IsCancellationRequested || window.CurrentSessionId != sessionId || !window.IsForeground || window.PageUrl == null)
			{
				return;
			}
			if (classification.Area == PageArea.Unknown || !classification.IsConfident)
			{
				diag.FilterReason = "页面类型未确认";
				_overlayRenderer.HideTranslations(sessionId);
				AnchorManager.ClearSession(sessionId);
				PipelineDiagnostics?.Invoke(this, diag);
				return;
			}
			if (classification.Area != PageArea.Unknown && !settings.Areas.IsEnabled(classification.Area))
			{
				diag.FilterReason = $"页面区域 {classification.Area} 已禁用";
				_overlayRenderer.HideTranslations(sessionId);
				diag.TotalMs = swTotal.ElapsedMilliseconds;
				PipelineDiagnostics?.Invoke(this, diag);
				return;
			}
			var activeRegions = anchorResult.ActiveAnchors
				.Where(a => a.RegionId != null && !string.IsNullOrWhiteSpace(a.TranslationText))
				.ToDictionary(a => a.RegionId!);
			int reusedCount = 0;
			if (activeRegions.Count > 0)
			{
				var previous = new List<TranslationResult>();
				foreach (var fragment in fragments)
				{
					if (!activeRegions.TryGetValue(fragment.RegionId, out var anchor) ||
						anchor.ContentHash != fragment.ContentHash) continue;
					previous.Add(new TranslationResult
					{
						NormalizedSource = fragment.NormalizedText,
						SourceHash = fragment.ContentHash,
						SourceBounds = fragment.Bounds,
						RegionId = fragment.RegionId,
						AnchorId = anchor.AnchorId,
						TranslatedText = anchor.TranslationText!,
						SourceLanguage = fragment.DetectedLanguage ?? "auto",
						TargetLanguage = settings.TargetLanguage,
						Provider = _translationProvider.Name,
						SessionId = sessionId,
						Area = classification.Area
					});
				}
				reusedCount = previous.Count;
				_overlayRenderer.UpdateTranslations(sessionId, previous, fragments);
			}
			Dictionary<string, string> pendingAnchorByRegionId = new Dictionary<string, string>();
			foreach (TranslationAnchor activeAnchor in anchorResult.ActiveAnchors)
			{
				if (activeAnchor.Status == AnchorStatus.Pending && activeAnchor.RegionId != null)
				{
					pendingAnchorByRegionId[activeAnchor.RegionId] = activeAnchor.AnchorId;
				}
			}
			List<IGrouping<string, TextFragment>> list = (from f in (from f in fragments
					where pendingAnchorByRegionId.ContainsKey(f.RegionId)
					where !TextNormalizer.IsUntranslatable(f.Text)
					where !TextNormalizer.IsSimplifiedChinese(f.Text)
					where LanguageDetector.NeedsTranslation(f.DetectedLanguage, settings.TargetLanguage)
					group f by f.RegionId into g
					select g.First()).ToList()
				group f by f.ContentHash into g
				orderby GetTranslationPriority(g.First().RegionType)
				select g).ToList();
			int num = (diag.CandidateCount = list.Count);
			diag.DetectedLanguages = string.Join(",", (from f in fragments
				select f.DetectedLanguage into l
				where l != null
				select l).Distinct().Take(5));
			if (num == 0)
			{
				diag.SuccessCount = reusedCount;
				diag.FilterReason = reusedCount > 0 ? $"复用 {reusedCount} 条已翻译段落" : "当前没有符合条件的待翻译段落";
				diag.TotalMs = swTotal.ElapsedMilliseconds;
				PipelineDiagnostics?.Invoke(this, diag);
				return;
			}
			List<TranslationResult> displayedResults = new List<TranslationResult>();
			List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)> list2 = new List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)>();
			foreach (IGrouping<string, TextFragment> item in list)
			{
				if (ct.IsCancellationRequested || window.CurrentSessionId != sessionId || !window.IsForeground || window.PageUrl == null)
				{
					return;
				}
				TextFragment textFragment = item.First();
				List<TextFragment> list3 = item.ToList();
				string text = TextNormalizer.Normalize(textFragment.Text);
				if (string.IsNullOrEmpty(text) || !_inFlightRequests.TryAdd(textFragment.ContentHash, 1))
				{
					continue;
				}
				myInFlightHashes.Add(textFragment.ContentHash);
				GlossaryProcessResult glossaryProcessResult = (settings.Glossary ? _glossaryProcessor.ProtectTerms(text, classification.AppId) : new GlossaryProcessResult
				{
					ProtectedText = text,
					Placeholders = new Dictionary<string, GlossaryTerm>()
				});
				if (settings.Translation.CacheFirst)
				{
					CachedTranslation cachedTranslation = _translationMemory.Lookup(text, textFragment.DetectedLanguage ?? "auto", settings.TargetLanguage, _translationProvider.Name, _glossaryProcessor.Version, classification.AppId);
					if (cachedTranslation != null && cachedTranslation.NormalizedSource == text)
					{
						_inFlightRequests.TryRemove(textFragment.ContentHash, out value);
						myInFlightHashes.Remove(textFragment.ContentHash);
						diag.CacheHits++;
						foreach (TextFragment item2 in list3)
						{
							string valueOrDefault = pendingAnchorByRegionId.GetValueOrDefault(item2.RegionId);
							displayedResults.Add(new TranslationResult
							{
								NormalizedSource = text,
								SourceHash = item2.ContentHash,
								RegionId = item2.RegionId,
								AnchorId = valueOrDefault,
								TranslatedText = cachedTranslation.TranslatedText,
								SourceLanguage = cachedTranslation.SourceLanguage,
								TargetLanguage = cachedTranslation.TargetLanguage,
								Provider = cachedTranslation.Provider,
								FromCache = true,
								SourceBounds = item2.Bounds,
								SessionId = sessionId,
								Area = classification.Area
							});
							if (valueOrDefault != null)
							{
								AnchorManager.SetTranslation(sessionId, valueOrDefault, cachedTranslation.TranslatedText);
							}
						}
						continue;
					}
				}
				list2.Add((list3, glossaryProcessResult.ProtectedText, glossaryProcessResult.Placeholders));
			}
			diag.UncachedCount = list2.Count;
			if (displayedResults.Count > 0 && window.CurrentSessionId == sessionId && window.IsForeground)
			{
				Stopwatch stopwatch = Stopwatch.StartNew();
				_overlayRenderer.UpdateTranslations(sessionId, displayedResults.ToArray(), fragments);
				stopwatch.Stop();
				diag.RenderMs += stopwatch.ElapsedMilliseconds;
				foreach (TranslationResult item3 in displayedResults)
				{
					TranslationCompleted?.Invoke(this, item3);
				}
			}
			if (list2.Count > 0)
			{
				var list4 = list2.GroupBy(item => item.Item1[0].DetectedLanguage ?? "auto")
					.SelectMany(group => BuildBatches(group.ToList(), settings.Translation.BatchCharacters)).ToList();
				diag.BatchCount = list4.Count;
				_activeSources[sessionId] = list2.SelectMany(item => item.Item1)
					.Select(fragment => fragment.ContentHash).ToHashSet(StringComparer.Ordinal);
				foreach (List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)> item4 in list4)
				{
					if (ct.IsCancellationRequested || window.CurrentSessionId != sessionId || !window.IsForeground || window.PageUrl == null)
					{
						break;
					}
					long monthlyCharacterBudget = settings.Translation.MonthlyCharacterBudget;
					if (monthlyCharacterBudget > 0 && _translationProvider.TotalCharactersSubmitted >= monthlyCharacterBudget)
					{
						break;
					}
					Stopwatch swTranslate = Stopwatch.StartNew();
					void PublishReady(IReadOnlyList<TranslationResult> ready)
					{
						if (ct.IsCancellationRequested || window.CurrentSessionId != sessionId || !window.IsForeground) return;
						foreach (var result in ready)
						{
							result.AnchorId = pendingAnchorByRegionId.GetValueOrDefault(result.RegionId);
							if (result.AnchorId != null)
								AnchorManager.SetTranslation(sessionId, result.AnchorId, result.TranslatedText);
						}
						if (diag.FirstOnlineResultMs == null) diag.FirstOnlineResultMs = swTotal.ElapsedMilliseconds;
						_overlayRenderer.UpdateTranslations(sessionId, ready, fragments);
					}

					List<TranslationResult> list5 = await TranslateOneBatchAsync(item4, sessionId, classification, PublishReady, ct).ConfigureAwait(false);
					diag.TranslateMs += swTranslate.ElapsedMilliseconds;
					displayedResults.AddRange(list5);
				}
			}
			if (!ct.IsCancellationRequested)
			{
				diag.TotalMs = swTotal.ElapsedMilliseconds;
				diag.SuccessCount = reusedCount + Enumerable.Count(displayedResults, (TranslationResult r) => r.Success);
				diag.FailCount = Enumerable.Count(displayedResults, (TranslationResult r) => !r.Success);
				diag.LastError = displayedResults.FirstOrDefault((TranslationResult r) => !r.Success && !string.IsNullOrEmpty(r.ErrorMessage))?.ErrorMessage;
				PipelineDiagnostics?.Invoke(this, diag);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception e)
		{
			PipelineError?.Invoke(this, e);
		}
		finally
		{
			_activeSources.TryRemove(sessionId, out _);
			foreach (string item6 in myInFlightHashes)
			{
				_inFlightRequests.TryRemove(item6, out value);
			}

			lock (_stateLock)
			{
				if (!_disposed && !_isPaused && _dirtySessions.TryRemove(sessionId, out value) &&
					window.CurrentSessionId == sessionId && window.IsForeground)
					StartProcessing(window, sessionId);
			}
		}
	}

	private async Task<List<TranslationResult>> TranslateOneBatchAsync(List<(IReadOnlyList<TextFragment> Positions, string ProtectedText, IReadOnlyDictionary<string, GlossaryTerm> Placeholders)> batch, string sessionId, PageClassification classification, Action<IReadOnlyList<TranslationResult>> publishReady, CancellationToken ct)
	{
		List<TranslationResult> results = new List<TranslationResult>();
		AppSettings settings = _settingsManager.Settings;
		await _concurrencyLimiter.WaitAsync(ct);
		try
		{
			List<string> texts = batch.Select(((IReadOnlyList<TextFragment> Positions, string ProtectedText, IReadOnlyDictionary<string, GlossaryTerm> Placeholders) b) => b.ProtectedText).ToList();
			string language = batch[0].Positions[0].DetectedLanguage ?? "auto";
			string cacheProvider = _translationProvider.Name;
			var handled = new bool[batch.Count];
			void HandleResult(int num, TranslationResult translationResult)
			{
				if (ct.IsCancellationRequested || handled[num]) return;
				handled[num] = true;
				(IReadOnlyList<TextFragment> Positions, string ProtectedText, IReadOnlyDictionary<string, GlossaryTerm> Placeholders) tuple = batch[num];
				IReadOnlyList<TextFragment> item = tuple.Positions;
				string item2 = tuple.ProtectedText;
				IReadOnlyDictionary<string, GlossaryTerm> item3 = tuple.Placeholders;

				TextFragment textFragment = item.First();
				if (!translationResult.Success)
				{
					foreach (TextFragment item4 in item)
					{
						TranslationResult translationResult2 = new TranslationResult
						{
							NormalizedSource = TextNormalizer.Normalize(item4.Text),
							SourceHash = item4.ContentHash,
							RegionId = item4.RegionId,
							TranslatedText = string.Empty,
							SourceLanguage = translationResult.SourceLanguage,
							TargetLanguage = settings.TargetLanguage,
							Provider = translationResult.Provider,
							FromCache = false,
							CharacterCount = 0,
							SourceBounds = item4.Bounds,
							SessionId = sessionId,
							Area = classification.Area,
							Success = false,
							ErrorMessage = (translationResult.ErrorMessage ?? "翻译失败（未知原因）")
						};
						results.Add(translationResult2);
						TranslationCompleted?.Invoke(this, translationResult2);
					}
					return;
				}
				string translatedText = _glossaryProcessor.RestoreTerms(translationResult.TranslatedText, item3);
				int firstReady = results.Count;
				foreach (TextFragment item5 in item)
				{
					TranslationResult translationResult3 = new TranslationResult
					{
						NormalizedSource = TextNormalizer.Normalize(item5.Text),
						SourceHash = item5.ContentHash,
						RegionId = item5.RegionId,
						TranslatedText = translatedText,
						SourceLanguage = translationResult.SourceLanguage,
						TargetLanguage = settings.TargetLanguage,
						Provider = translationResult.Provider,
						FromCache = false,
						CharacterCount = item2.Length,
						SourceBounds = item5.Bounds,
						SessionId = sessionId,
						Area = classification.Area
					};
					results.Add(translationResult3);
					TranslationCompleted?.Invoke(this, translationResult3);
				}
				if (!string.IsNullOrWhiteSpace(translatedText)) publishReady(results.GetRange(firstReady, results.Count - firstReady));
				if (settings.TranslationMemory)
				{
					_translationMemory.Store(new CachedTranslation
					{
						SourceHash = textFragment.ContentHash,
						NormalizedSource = TextNormalizer.Normalize(textFragment.Text),
						SourceLanguage = language,
						TargetLanguage = settings.TargetLanguage,
						Provider = cacheProvider,
						GlossaryVersion = _glossaryProcessor.Version,
						AppId = classification.AppId,
						TranslatedText = translatedText
					});
				}
			}

			var translations = await (_translationProvider is IProgressiveTranslationProvider progressive
				? progressive.TranslateBatchProgressivelyAsync(texts, language, settings.TargetLanguage, HandleResult, ct)
				: _translationProvider.TranslateBatchAsync(texts, language, settings.TargetLanguage, ct)).ConfigureAwait(false);
			for (int index = 0; index < batch.Count && index < translations.Count; index++)
				HandleResult(index, translations[index]);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_concurrencyLimiter.Release();
		}
		return results;
	}

	private static int GetTranslationPriority(RegionType type)
	{
		switch (type)
		{
		case RegionType.PageTitle:
		case RegionType.ItemTitle:
		case RegionType.CardTitle:
			return 0;
		case RegionType.DescriptionBody:
		case RegionType.CardBody:
			return 1;
		case RegionType.CommentBody:
		case RegionType.CommentAuthor:
			return 2;
		case RegionType.Tag:
		case RegionType.Navigation:
		case RegionType.Button:
			return 3;
		default:
			return 4;
		}
	}

	private static List<List<(IReadOnlyList<TextFragment> Positions, string ProtectedText, IReadOnlyDictionary<string, GlossaryTerm> Placeholders)>> BuildBatches(List<(IReadOnlyList<TextFragment> Positions, string ProtectedText, IReadOnlyDictionary<string, GlossaryTerm> Placeholders)> items, int maxCharacters)
	{
		List<List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)>> list = new List<List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)>>();
		List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)> list2 = new List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)>();
		int num = 0;
		foreach (var item in items)
		{
			int length = item.ProtectedText.Length;
			if (list2.Count > 0 && num + length > maxCharacters)
			{
				list.Add(list2);
				list2 = new List<(IReadOnlyList<TextFragment>, string, IReadOnlyDictionary<string, GlossaryTerm>)>();
				num = 0;
			}
			list2.Add(item);
			num += length;
		}
		if (list2.Count > 0)
		{
			list.Add(list2);
		}
		return list;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (CancellationTokenSource value in _sessionCts.Values)
		{
			value.Cancel();
			value.Dispose();
		}
		_sessionCts.Clear();
		_dirtySessions.Clear();
		_inFlightRequests.Clear();
		_concurrencyLimiter.Dispose();
		GC.SuppressFinalize(this);
	}
}







