namespace SteamCN.Core.Interfaces;

public enum TranslationFailureType
{
	None,
	AuthenticationFailed,
	RateLimited,
	Timeout,
	NetworkError,
	ServerError,
	QuotaExceeded,
	InvalidResponse,
	Cancelled
}
