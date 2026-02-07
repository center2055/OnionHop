# OnionHop Website (Static)

This folder contains a modern static landing page for OnionHop with:

- English + German UI (language toggle + auto-detect)
- Subtle animations + scroll reveals (respects reduced-motion)
- Auto-fetch of latest GitHub release + asset downloads
- GitHub stars + estimated download counts (GitHub release asset `download_count`)
- Screenshot gallery (repo screenshots + placeholders)

Open `website/index.html` in a browser, or host the `website/` folder on any static host.

If the GitHub API calls don’t work when opening the file directly, run a tiny local server instead:

```powershell
cd website
python -m http.server 5173
```

Then open `http://localhost:5173`.

Or just run `website/serve.bat` (optional port: `serve.bat 5173`).

Notes:
- GitHub’s API is rate-limited for anonymous clients; the page falls back gracefully if calls fail.
- “Downloads” are derived from GitHub release asset `download_count` and may not match other analytics.
