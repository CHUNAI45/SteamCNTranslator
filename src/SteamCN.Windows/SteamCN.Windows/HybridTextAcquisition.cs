using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;

namespace SteamCN.Windows;

public sealed class HybridTextAcquisition : ITextAcquisition, IDisposable
{
    private readonly IOcrEngine? _ocrEngine;
    public HybridTextAcquisition(IOcrEngine? ocrEngine = null) => _ocrEngine = ocrEngine;

    public int LastUiaFragmentCount { get; private set; }
    public int LastOcrFragmentCount { get; private set; }
    public bool LastOcrAttempted => LastOcrFragmentCount > 0;
    public bool LastCaptureSuccess { get; private set; }
    public int LastMergedCount => LastUiaFragmentCount + LastOcrFragmentCount;
    public long LastUiaScanMs => 0;
    public bool LastUiaTimedOut => false;
    public double LastScrollDeltaY => 0;
    public bool IsScrolling => false;
    public CaptureMethod Method => CaptureMethod.Hybrid;

    public Task<IReadOnlyList<TextFragment>> AcquireAsync(SteamWindowInfo window, ScreenRect visibleRegion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = SteamPageReader.Current(window);
        LastCaptureSuccess = page != null;
        LastOcrFragmentCount = page?.Paragraphs.Count(p => p.Source == TextSource.Ocr) ?? 0;
        LastUiaFragmentCount = (page?.Paragraphs.Count ?? 0) - LastOcrFragmentCount;
        return Task.FromResult(page?.Paragraphs ?? Array.Empty<TextFragment>());
    }

    public Task<bool> IsUiaAvailableAsync(SteamWindowInfo window) =>
        Task.FromResult(SteamPageReader.Current(window) != null);

    public bool IsOcrAvailable() => _ocrEngine?.IsAvailable == true;
    public void ResetScrollDetector() { }
    public void Dispose() { }
}

