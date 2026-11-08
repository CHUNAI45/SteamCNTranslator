using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamCN.Core.Pipeline;

public static class SteamPagePaths
{
	private static readonly Regex WorkshopProfilePath = new(
		@"^/(?:id/[a-z0-9_-]+|profiles/[0-9]{17})/myworkshopfiles/?$",
		RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

	public static bool IsWorkshopSubscriptions(Uri address) =>
		address.Host == "steamcommunity.com" && WorkshopProfilePath.IsMatch(address.AbsolutePath) &&
		address.Query.TrimStart('?').Split('&').Contains("browsefilter=mysubscriptions", StringComparer.OrdinalIgnoreCase);
}
