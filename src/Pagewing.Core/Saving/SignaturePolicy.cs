using Pagewing.Core.Pdf;

namespace Pagewing.Core.Saving;

/// <summary>Decides whether saving annotations needs a warning about signatures.</summary>
public static class SignaturePolicy
{
    /// <summary>
    /// No signature gives <see cref="SignatureAdvice.None"/>; DocMDP level 1 or 2 gives
    /// <see cref="SignatureAdvice.WouldInvalidate"/>; level 3 or a signature without DocMDP gives
    /// <see cref="SignatureAdvice.AppendedOnly"/> (an incremental save keeps the signed bytes intact).
    /// </summary>
    /// <param name="info">The declared signature state.</param>
    public static SignatureAdvice Evaluate(PdfSignatureInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.SignatureCount <= 0)
        {
            return SignatureAdvice.None;
        }

        return info.Permission is DocMdpLevel.NoChanges or DocMdpLevel.FormsOnly
            ? SignatureAdvice.WouldInvalidate
            : SignatureAdvice.AppendedOnly;
    }
}
