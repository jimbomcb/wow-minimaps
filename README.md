# Minimap Explorer

Automatic discovery and analysis of per-build WoW minimap data.

<img width="2560" height="1271" alt="image" src="https://github.com/user-attachments/assets/30f9ec24-f997-4103-b7d5-0862816db478" />

*Viewing the Atal'Aman map, the parent map gets rendered in the background for context*

Not yet deployed.

Inspired by [Marlamin](https://github.com/Marlamin)'s [WoWTools.Minimaps](https://github.com/Marlamin/WoWTools.Minimaps)

## Project Setup

- Minimaps.Aspire.AppHost: Dev orchestration, runs a local dev database, migrates, runs individual components.
- Minimaps.Frontend: Blazor frontend serving map data in a WebGL renderer.
- Minimaps.Services: Service worker monitoring for builds, scanning and publishing new/modified minimap data.
