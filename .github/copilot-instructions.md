# Copilot Instructions

## Project Guidelines
- When resizing the window, aspect ratio must follow the actual video stream dimensions, not a hardcoded 16:9 ratio. Prioritize UI stability over aggressive or frequent queued automatic resizing.
- ReconnectDelaySec means delay before reconnect attempts, not RTSP connection timeout.
- Use only built-in WiX capabilities for installer security features; avoid embedding custom password mechanisms in MSI.
- Provide a single installer package with language selection at startup, rather than separate per-language MSI outputs.
- For this project installer, keep separate fully localized EN and HU MSIs without an in-installer language selection page, and place outputs together with distinct names (e.g., hg5c_cam.Telepítő.msi for HU).
- The password dialog must remain at the end of the setup, before the Completed screen, and should not be moved to the beginning of the setup.

## Localization Guidelines
- Use the language selected in Settings for localization, not the current UI culture/environment language.

## Code Review Guidelines
- Review the workspace code before performing error analysis to provide specific insights rather than general tips.
- Verify function names against the current file content before reporting them, because recent reversions may remove previously added methods.