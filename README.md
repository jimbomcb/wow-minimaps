# Minimap Explorer

<img width="711" height="496" alt="image" src="https://github.com/user-attachments/assets/8d274a32-7102-404e-80f3-0357d90aaadf" />

Automatic discovery and analysis of per-build WoW minimap data.

Not yet deployed.

Inspired by [Marlamin](https://github.com/Marlamin)'s [WoWTools.Minimaps](https://github.com/Marlamin/WoWTools.Minimaps)

## Project Setup

- Minimaps.Aspire.AppHost: Dev orchestration, runs a local dev database, migrates, runs individual components.
- Minimaps.Frontend: Blazor frontend serving map data in a WebGL renderer.
- Minimaps.Services: Service worker monitoring for builds, scanning and publishing new/modified minimap data.
