namespace Scheduler.Infrastructure.Pdf;

public sealed record PdfTimetableParseOptions
{
    public const int DefaultMaxPages = 200;
    public const int DefaultMaxWordsPerPage = 100_000;
    public const long DefaultMaxFileBytes = 25L * 1024 * 1024;

    public int MaxPages { get; init; } = DefaultMaxPages;

    public int MaxWordsPerPage { get; init; } = DefaultMaxWordsPerPage;

    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    public double PageWidthTolerance { get; init; } = 2.0;

    public double PageHeightTolerance { get; init; } = 2.0;

    public double LineTolerance { get; init; } = 2.0;

    internal void Validate()
    {
        if (MaxPages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPages));
        }

        if (MaxWordsPerPage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxWordsPerPage));
        }

        if (MaxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFileBytes));
        }

        if (PageWidthTolerance <= 0 || PageHeightTolerance <= 0 || LineTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LineTolerance));
        }
    }
}
