using System;
using SteamCN.Core.Interfaces;

namespace SteamCN.Translation;

public class LibreTranslateException : Exception
{
	public TranslationFailureType FailureType { get; }

	public LibreTranslateException(string message, TranslationFailureType failureType)
		: base(message)
	{
		FailureType = failureType;
	}
}
