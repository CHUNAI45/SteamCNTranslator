using System;
using SteamCN.Core.Interfaces;

namespace SteamCN.Translation;

public class BingTranslateException : Exception
{
	public TranslationFailureType FailureType { get; }

	public BingTranslateException(string message, TranslationFailureType failureType)
		: base(message)
	{
		FailureType = failureType;
	}
}
