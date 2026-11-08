using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Models;

namespace SteamCN.Core.Interfaces;

public interface IProgressiveTranslationProvider : ITranslationProvider
{
    Task<IReadOnlyList<TranslationResult>> TranslateBatchProgressivelyAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
        Action<int, TranslationResult> onSuccess, CancellationToken cancellationToken);
}
