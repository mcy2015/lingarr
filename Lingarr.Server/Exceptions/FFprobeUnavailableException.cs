namespace Lingarr.Server.Exceptions;

/// <summary>
/// Raised when the embedded-track scan cannot launch ffprobe — the binary is
/// not on PATH (and LINGARR_FFPROBE_PATH does not point at one). Deliberately
/// distinct from a per-file probe failure (which is logged as a warning and
/// skipped): a missing binary voids the whole scan, so the job fails with an
/// actionable message instead of marking every file "probed, zero tracks".
/// </summary>
public class FFprobeUnavailableException : Exception
{
    public FFprobeUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
