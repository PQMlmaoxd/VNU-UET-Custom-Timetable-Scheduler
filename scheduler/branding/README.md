# Product Branding Assets

The canonical product logo is `source/logo.png`.

- Format: transparent RGBA PNG
- Dimensions: 253x193 pixels
- SHA-256: `75a809f7bd7d3ef23ac57f41f709525be0c143291191ed5512aaa1b00f609605`

The landscape logo is used where its aspect ratio can be preserved, including
the React header and WPF startup surface. `generated/app.ico` is a deliberately
padded square composition for Windows shell surfaces; the landscape image must
not be stretched into a square.

Tracked copies under frontend and desktop project directories are checked by
`scheduler/desktop/scripts/verify-branding-assets.ps1`. Do not replace them by
files copied from `dist`, `bin`, or `artifacts`.

The logo is used as a project brand asset. It does not by itself imply official
endorsement by VNU or UET; that status must be confirmed separately before
distribution.
