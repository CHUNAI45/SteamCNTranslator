using System;
using SteamCN.Core.Interfaces;

namespace SteamCN.Translation;

public class AzureTranslatorException : Exception
{
	public TranslationFailureType FailureType { get; }

	public int StatusCode { get; }

	public AzureTranslatorException(string message, TranslationFailureType failureType, int statusCode = 0)
		: base(message)
	{
		FailureType = failureType;
		StatusCode = statusCode;
	}
}
