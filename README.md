# wow-minimaps

WoW minimap generation & browsing, with a focus on comparing changes over time and a more optimized backend image store.

Inspired by [Marlamin](https://github.com/Marlamin)'s [WoWTools.Minimaps](https://github.com/Marlamin/WoWTools.Minimaps)

## Project Setup

- Minimaps.Aspire.AppHost: Dev orchestration, runs a local dev database, migrates, runs individual components.
- Minimaps.CLI: Legacy generator, proof of concept
- Minimaps.Service: Will monitor for new build publishing, new builds will trigger a new processing of minimap data.
- Minimaps.Web.API: Handle publishing of map data and tiles from the service worker, provides map & tile data to the browser app - WIP.
