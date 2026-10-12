namespace Lingarr.Core.Enum;

/// <summary>
/// Where a translation request originated: from the *arr media library
/// (automation or a media entry action) or from a manually imported
/// subtitle file (import workbench).
/// </summary>
public enum TranslationRequestSource
{
    Library,
    Import
}
