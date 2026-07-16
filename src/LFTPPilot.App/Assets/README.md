# Application artwork

`LFTPPilot.Master.png` is the reviewed high-resolution design source created
specifically for this repository with OpenAI's built-in image generation on
2026-07-15. The prompt described a polished Windows 11 application icon with
two file panels, cyan bidirectional transfer arrows, a pilot compass, deep
navy/blue materials, a square composition, and no text.

`build/Generate-PlaceholderAssets.ps1` creates the required MSIX logo and
splash dimensions from that master. The master remains in source control for
review but is excluded from the installed package; only the generated package
assets ship.
