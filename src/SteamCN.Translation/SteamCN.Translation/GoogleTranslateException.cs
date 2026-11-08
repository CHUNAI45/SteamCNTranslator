using System;
using SteamCN.Core.Interfaces;

namespace SteamCN.Translation;

public class GoogleTranslateException : Exception
{
	public TranslationFailureType FailureType { get; }

	public GoogleTranslateException(string message, TranslationFailureType failureType)
		: base(message)
	{
		FailureType = failureType;
	}
}
