using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Translation;

internal static class ProgressiveRequests
{
	internal static readonly TimeSpan QuickAttemptTimeout = TimeSpan.FromSeconds(6);

	internal static async Task<IReadOnlyList<TranslationResult>> RunAsync(
		IReadOnlyList<string> texts, Func<string, CancellationToken, Task<TranslationResult>> translate,
		Action<int, TranslationResult>? onSuccess, CancellationToken cancellationToken)
	{
		if (onSuccess == null)
			return await Task.WhenAll(texts.Select(text => translate(text, cancellationToken))).ConfigureAwait(false);

		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(15));
		var pending = texts.Select((text, index) => CompleteAsync(text, index)).ToList();
		var ordered = new TranslationResult[texts.Count];
		while (pending.Count > 0)
		{
			var completed = await Task.WhenAny(pending).ConfigureAwait(false);
			pending.Remove(completed);
			var (index, result) = await completed.ConfigureAwait(false);
			ordered[index] = result;
			if (result.Success && !cancellationToken.IsCancellationRequested) onSuccess(index, result);
		}
		return ordered;

		async Task<(int, TranslationResult)> CompleteAsync(string text, int index) =>
			(index, await translate(text, deadline.Token).ConfigureAwait(false));
	}
}
