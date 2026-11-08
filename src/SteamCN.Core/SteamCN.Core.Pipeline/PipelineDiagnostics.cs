using System;

namespace SteamCN.Core.Pipeline;

public class PipelineDiagnostics
{
	public string SessionId { get; set; } = "";

	public long AcquireMs { get; set; }

	public long ClassifyMs { get; set; }

	public long TranslateMs { get; set; }

	public long RenderMs { get; set; }

	public long TotalMs { get; set; }

	public int FragmentCount { get; set; }

	public int CacheHits { get; set; }

	public int UncachedCount { get; set; }

	public int SuccessCount { get; set; }
	public long? FirstOnlineResultMs { get; set; }

	public int FailCount { get; set; }

	public string? LastError { get; set; }

	public int CandidateCount { get; set; }

	public string? DetectedLanguages { get; set; }

	public string? PageArea { get; set; }

	public string? ClassifyEvidence { get; set; }

	public string? FilterReason { get; set; }

	public int UiaCount { get; set; }

	public int OcrCount { get; set; }

	public bool OcrAttempted { get; set; }

	public bool CaptureSuccess { get; set; }

	public string? RegionTypes { get; set; }

	public string? ParagraphStats { get; set; }

	public int RegionAdded { get; set; }

	public int RegionMoved { get; set; }

	public int RegionRemoved { get; set; }

	public int BatchCount { get; set; }

	public double CacheHitRate
	{
		get
		{
			if (CandidateCount <= 0)
			{
				return 0.0;
			}
			return Math.Round((double)CacheHits * 100.0 / (double)CandidateCount, 1);
		}
	}

	public override string ToString()
	{
		return $"采集={FragmentCount}条(UIA={UiaCount}/OCR={OcrCount}/截图={(CaptureSuccess ? "OK" : "FAIL")})/{AcquireMs}ms 候选={CandidateCount} 缓存命中={CacheHits}({CacheHitRate}%) 翻译={UncachedCount}条/{TranslateMs}ms 成功={SuccessCount} 失败={FailCount} 渲染={RenderMs}ms 总计={TotalMs}ms" + ((DetectedLanguages != null) ? (" 语言=" + DetectedLanguages) : "") + ((PageArea != null) ? (" 页面=" + PageArea) : "") + ((RegionTypes != null) ? (" 类型=" + RegionTypes) : "") + $" 区域[+{RegionAdded}/~{RegionMoved}/-{RegionRemoved}]" + ((FilterReason != null) ? (" 原因=" + FilterReason) : "") + ((LastError != null) ? (" 错误=" + LastError) : "");
	}
}

