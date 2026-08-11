# PDF Timetable Parser

This project parses the signed UET timetable PDF using coordinate-aware text
extraction. It is intentionally separate from the XLSX parser and maps into the
same domain and `TimetableParseResult` contract.

The pinned `PdfPig` dependency is Apache-2.0 licensed. It is sourced from the
official `UglyToad/PdfPig` repository at release `v0.1.15` and commit
`f131f642976936e06ee91cb19d3ed728f9dd18b6`. Do not replace it with iText
without an explicit licensing decision. The first implementation supports the
text-native UET timetable template only; scanned PDFs and OCR are out of scope.

The parser reads pages sequentially, groups words by PDF coordinates, detects the
repeated timetable header, merges wrapped rows, and maps into the same immutable
domain contract as the XLSX importer. It does not use whitespace token positions.
The signed reference PDF currently matches the XLSX scheduling semantic set for
both `ALL` and `CNTT` scopes: 1,659 total sessions for `ALL` and 402 sessions for
`CNTT` (including 50 and 14 online sessions respectively). The PDF logical-row
count is intentionally not compared with the XLSX source-row count because print
headers, totals, and skipped sections are represented differently.

The print template clips a small, known set of lecturer cells. The parser contains
an explicit profile-scoped alias table for those values; it never infers names from
arbitrary prefixes. A changed PDF template must fail profile/parity tests and receive
an explicit parser-profile update rather than silently using these aliases.
