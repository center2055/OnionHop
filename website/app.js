/* eslint-disable no-console */
(() => {
  const REPO = { owner: "center2055", repo: "OnionHop" };

  const STRINGS = {
    en: {
      skip_to_content: "Skip to content",
      brand_tag: "Tor routing, simplified",
      github: "GitHub",
      discord: "Discord",
      kicker: "Privacy-first connectivity.",
      headline: "Hop into Tor with a clean, modern Windows client.",
      subhead:
        "Proxy mode for simplicity. TUN/VPN mode for system-wide routing. Bridges when networks say “no”.",
      download_latest: "Download latest",
      see_downloads: "See downloads",
      stat_stars: "Stars",
      stat_downloads: "Downloads",
      stat_release: "Latest",
      badge_latest_release: "Latest Release",
      badge_fmhy: "Featured on FMHY",
      chip_proxy: "Proxy",
      chip_tun: "TUN/VPN",
      chip_bridges: "Bridges",
      chip_split: "Split tunneling",
      features_title: "Built for real-world networks",
      features_lead:
        "OnionHop focuses on sensible defaults, powerful controls, and clear status — without the clutter.",
      f_proxy_title: "Proxy mode (recommended)",
      f_proxy_text:
        "Quickly route proxy-aware apps via Tor by setting the Windows proxy to a local SOCKS endpoint.",
      f_tun_title: "TUN/VPN mode (admin)",
      f_tun_text: "System-wide routing via sing-box + Wintun for apps that ignore proxy settings.",
      f_split_title: "Split tunneling",
      f_split_text: "Choose which apps go through Tor and which go direct (Hybrid mode).",
      f_bridges_title: "Bridges + transports",
      f_bridges_text: "snowflake, obfs4, meek-azure, webtunnel, and custom bridges for restrictive networks.",
      f_killswitch_title: "Kill Switch",
      f_killswitch_text: "Optional firewall rules help prevent accidental leaks (strict TUN only).",
      f_logs_title: "Logs + diagnostics",
      f_logs_text: "See what’s happening and why — useful for troubleshooting bridges and connectivity.",
      shots_title: "Screenshots",
      shots_lead: "Some real shots from the repo, plus placeholders you can replace with your own.",
      downloads_title: "Downloads",
      downloads_lead: "This page fetches the newest release from GitHub and picks the best asset for your device.",
      download_primary: "Download",
      download_all: "All releases",
      api_note: "Counts come from GitHub’s public API (rate limits apply).",
      faq_title: "Quick notes",
      faq_legal_q: "Is Tor legal?",
      faq_legal_a:
        "Tor usage can be restricted or illegal in some jurisdictions. You are responsible for complying with local laws and regulations.",
      faq_onion_q: "Do .onion sites work?",
      faq_onion_a:
        "Use a Tor-aware client (Tor Browser recommended) or enable remote DNS over SOCKS in your browser/app.",
      faq_admin_q: "Why does TUN/VPN need admin?",
      faq_admin_a: "TUN mode uses a system network driver (Wintun) and may add firewall rules for the kill switch.",
      footer_text: "Built for privacy-minded users. Provided as-is.",
      footer_releases: "Releases",
      footer_features: "Features",
      footer_screenshots: "Screenshots",
      shot_real: "From repo",
      shot_placeholder: "Placeholder",
      shot_hint: "Drop your screenshot here",
      asset_downloads: "downloads",
      latest_release_label: "Latest release",
      updated_label: "Updated",
      failed_hint: "Couldn’t load GitHub data right now.",
      get_asset: "Get",
    },
    de: {
      skip_to_content: "Zum Inhalt springen",
      brand_tag: "Tor-Routing, vereinfacht",
      github: "GitHub",
      discord: "Discord",
      kicker: "Privatsphäre zuerst.",
      headline: "Mit einem modernen Windows-Client einfach in Tor starten.",
      subhead:
        "Proxy-Modus für Einfachheit. TUN/VPN-Modus für systemweites Routing. Bridges, wenn das Netzwerk „nein“ sagt.",
      download_latest: "Neueste Version laden",
      see_downloads: "Downloads ansehen",
      stat_stars: "Sterne",
      stat_downloads: "Downloads",
      stat_release: "Neueste",
      badge_latest_release: "Neueste Release",
      badge_fmhy: "Featured auf FMHY",
      chip_proxy: "Proxy",
      chip_tun: "TUN/VPN",
      chip_bridges: "Bridges",
      chip_split: "Split-Tunneling",
      features_title: "Für echte Netzwerke gebaut",
      features_lead:
        "OnionHop setzt auf sinnvolle Defaults, starke Kontrolle und klare Statusanzeigen — ohne Ballast.",
      f_proxy_title: "Proxy-Modus (empfohlen)",
      f_proxy_text:
        "Proxy-fähige Apps schnell über Tor leiten, indem der Windows-Proxy auf einen lokalen SOCKS-Endpunkt gesetzt wird.",
      f_tun_title: "TUN/VPN-Modus (Admin)",
      f_tun_text: "Systemweites Routing via sing-box + Wintun für Apps, die Proxy-Einstellungen ignorieren.",
      f_split_title: "Split-Tunneling",
      f_split_text: "Festlegen, welche Apps über Tor gehen und welche direkt (Hybrid-Modus).",
      f_bridges_title: "Bridges + Transports",
      f_bridges_text: "snowflake, obfs4, meek-azure, webtunnel und eigene Bridges für restriktive Netzwerke.",
      f_killswitch_title: "Kill Switch",
      f_killswitch_text: "Optionale Firewall-Regeln helfen gegen versehentliche Leaks (nur strikter TUN).",
      f_logs_title: "Logs + Diagnose",
      f_logs_text: "Sieh, was passiert — hilfreich bei Bridge- und Verbindungsproblemen.",
      shots_title: "Screenshots",
      shots_lead: "Ein paar echte Shots aus dem Repo, plus Platzhalter, die du ersetzen kannst.",
      downloads_title: "Downloads",
      downloads_lead: "Diese Seite lädt das neueste Release von GitHub und wählt das beste Asset für dein Gerät.",
      download_primary: "Download",
      download_all: "Alle Releases",
      api_note: "Zähler stammen aus der öffentlichen GitHub-API (Rate-Limits möglich).",
      faq_title: "Kurze Hinweise",
      faq_legal_q: "Ist Tor legal?",
      faq_legal_a:
        "Tor kann in manchen Ländern eingeschränkt oder illegal sein. Du bist für die Einhaltung lokaler Gesetze verantwortlich.",
      faq_onion_q: "Funktionieren .onion-Seiten?",
      faq_onion_a:
        "Nutze einen Tor-fähigen Client (Tor Browser empfohlen) oder aktiviere Remote-DNS über SOCKS in deinem Browser/deiner App.",
      faq_admin_q: "Warum braucht TUN/VPN Admin-Rechte?",
      faq_admin_a:
        "TUN nutzt einen System-Netzwerktreiber (Wintun) und kann Firewall-Regeln für den Kill Switch setzen.",
      footer_text: "Für privacy-orientierte Nutzer. Ohne Gewähr.",
      footer_releases: "Releases",
      footer_features: "Features",
      footer_screenshots: "Screenshots",
      shot_real: "Aus dem Repo",
      shot_placeholder: "Platzhalter",
      shot_hint: "Screenshot hier hinzufügen",
      asset_downloads: "Downloads",
      latest_release_label: "Neueste Release",
      updated_label: "Aktualisiert",
      failed_hint: "GitHub-Daten konnten gerade nicht geladen werden.",
      get_asset: "Laden",
    },
  };

  const els = {
    langToggle: document.getElementById("langToggle"),
    langLabel: document.getElementById("langLabel"),
    starsValue: document.getElementById("starsValue"),
    downloadsValue: document.getElementById("downloadsValue"),
    latestValue: document.getElementById("latestValue"),
    releaseHint: document.getElementById("releaseHint"),
    heroShot: document.getElementById("heroShot"),
    downloadLatest: document.getElementById("downloadLatest"),
    downloadMeta: document.getElementById("downloadMeta"),
    dlHeadline: document.getElementById("dlHeadline"),
    dlMeta: document.getElementById("dlMeta"),
    dlPrimary: document.getElementById("dlPrimary"),
    dlSecondary: document.getElementById("dlSecondary"),
    assetList: document.getElementById("assetList"),
    shotsGrid: document.getElementById("shotsGrid"),
    apiNote: document.getElementById("apiNote"),
  };

  function fmtCompactNumber(value) {
    if (typeof value !== "number" || Number.isNaN(value)) return "—";
    try {
      return new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 }).format(value);
    } catch {
      return String(value);
    }
  }

  function fmtNumber(value) {
    if (typeof value !== "number" || Number.isNaN(value)) return "—";
    try {
      return new Intl.NumberFormat(undefined).format(value);
    } catch {
      return String(value);
    }
  }

  function fmtBytes(bytes) {
    if (typeof bytes !== "number" || bytes <= 0) return "";
    const units = ["B", "KB", "MB", "GB"];
    let unitIndex = 0;
    let n = bytes;
    while (n >= 1024 && unitIndex < units.length - 1) {
      n /= 1024;
      unitIndex += 1;
    }
    return `${n.toFixed(unitIndex === 0 ? 0 : 1)} ${units[unitIndex]}`;
  }

  function parseLangFromUrl() {
    const url = new URL(window.location.href);
    const q = url.searchParams.get("lang");
    if (q === "de" || q === "en") return q;
    return null;
  }

  function getInitialLang() {
    const fromUrl = parseLangFromUrl();
    if (fromUrl) return fromUrl;
    const saved = localStorage.getItem("onionhop.lang");
    if (saved === "de" || saved === "en") return saved;
    const nav = (navigator.language || "en").toLowerCase();
    return nav.startsWith("de") ? "de" : "en";
  }

  let lang = getInitialLang();

  function t(key) {
    const dict = STRINGS[lang] || STRINGS.en;
    return dict[key] ?? STRINGS.en[key] ?? key;
  }

  function applyI18n() {
    document.documentElement.lang = lang;
    if (els.langLabel) els.langLabel.textContent = lang.toUpperCase();

    for (const node of document.querySelectorAll("[data-i18n]")) {
      const k = node.getAttribute("data-i18n");
      if (!k) continue;
      node.textContent = t(k);
    }
  }

  function setupLangToggle() {
    if (!els.langToggle) return;
    els.langToggle.addEventListener("click", () => {
      lang = lang === "en" ? "de" : "en";
      localStorage.setItem("onionhop.lang", lang);
      applyI18n();
      renderScreenshots();
      renderAssets(lastRelease?.assets ?? []);
      updateReleaseCopy(lastRelease);
    });
  }

  function setupReveal() {
    const nodes = document.querySelectorAll(".reveal");
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) {
          if (e.isIntersecting) {
            e.target.classList.add("is-in");
            io.unobserve(e.target);
          }
        }
      },
      { threshold: 0.12 }
    );
    nodes.forEach((n) => io.observe(n));
  }

  const GITHUB_RAW_BASE = `https://raw.githubusercontent.com/${REPO.owner}/${REPO.repo}/master`;

  const SCREENSHOTS = [
    {
      kind: "real",
      title: "OnionHop V2 UI",
      src: "assets/onionhop-v2-ui.png",
      fallback: `${GITHUB_RAW_BASE}/assets/onionhop-v2-ui.png`,
    },
    {
      kind: "real",
      title: "OnionHop UI",
      src: "assets/onionhop-ui.png",
      fallback: `${GITHUB_RAW_BASE}/assets/onionhop-ui.png`,
    },
  ];

  function escapeSvgText(text) {
    return String(text || "").replace(/[<>&"]/g, (m) => {
      if (m === "<") return "";
      if (m === ">") return "";
      if (m === "&") return "";
      return "";
    });
  }

  function placeholderDataUri(label) {
    const safe = escapeSvgText(label);
    const svg = `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="800" viewBox="0 0 1280 800">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#2a145a"/>
      <stop offset="1" stop-color="#0b0713"/>
    </linearGradient>
    <radialGradient id="ring" cx="0.7" cy="0.35" r="0.7">
      <stop offset="0" stop-color="rgba(167,139,250,0.35)"/>
      <stop offset="1" stop-color="rgba(124,58,237,0.05)"/>
    </radialGradient>
  </defs>
  <rect width="1280" height="800" rx="40" fill="url(#bg)"/>
  <circle cx="940" cy="270" r="420" fill="url(#ring)"/>
  <circle cx="940" cy="270" r="320" fill="none" stroke="rgba(167,139,250,0.28)" stroke-width="10"/>
  <circle cx="940" cy="270" r="220" fill="none" stroke="rgba(124,58,237,0.24)" stroke-width="10"/>
  <g fill="rgba(255,255,255,0.88)" font-family="ui-sans-serif,system-ui,Segoe UI,Roboto,Arial" font-weight="800">
    <text x="70" y="120" font-size="42">OnionHop</text>
    <text x="70" y="178" font-size="22" fill="rgba(255,255,255,0.72)">${t("shot_placeholder")} • ${t(
      "shot_hint"
    )}</text>
    <text x="70" y="270" font-size="34">${safe}</text>
  </g>
  <g fill="rgba(255,255,255,0.14)">
    <rect x="70" y="330" width="560" height="18" rx="9"/>
    <rect x="70" y="366" width="500" height="18" rx="9"/>
    <rect x="70" y="402" width="440" height="18" rx="9"/>
    <rect x="70" y="460" width="280" height="18" rx="9"/>
  </g>
</svg>`;
    return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
  }

  function renderScreenshots() {
    // Screenshot gallery removed
    const firstReal = SCREENSHOTS.find((s) => s.kind === "real");
    if (els.heroShot && firstReal) {
      const trySet = (src) => {
        els.heroShot.style.backgroundImage = `url("${src}")`;
        els.heroShot.style.backgroundSize = "contain";
        els.heroShot.style.backgroundPosition = "center";
        els.heroShot.style.backgroundRepeat = "no-repeat";
      };

      const img = new Image();
      img.onload = () => trySet(firstReal.src);
      img.onerror = () => trySet(firstReal.fallback || firstReal.src);
      img.src = firstReal.src;
    }
  }

  async function fetchJson(url, { timeoutMs = 9000 } = {}) {
    const ctrl = new AbortController();
    const to = setTimeout(() => ctrl.abort(), timeoutMs);
    try {
      const res = await fetch(url, {
        signal: ctrl.signal,
        headers: { Accept: "application/vnd.github+json" },
      });
      if (!res.ok) {
        const text = await res.text().catch(() => "");
        throw new Error(`HTTP ${res.status}: ${text.slice(0, 140)}`);
      }
      return await res.json();
    } finally {
      clearTimeout(to);
    }
  }

  function platformHint() {
    const platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
    const ua = navigator.userAgent || "";
    const s = `${platform} ${ua}`.toLowerCase();
    if (s.includes("win")) return "windows";
    if (s.includes("mac") || s.includes("darwin")) return "mac";
    if (s.includes("linux")) return "linux";
    return "other";
  }

  function assetScore(name, platform) {
    const n = (name || "").toLowerCase();
    let score = 0;

    if (platform === "windows") {
      if (n.endsWith(".exe")) score += 80;
      if (n.endsWith(".msi")) score += 70;
      if (n.endsWith(".zip")) score += 30;
    } else if (platform === "mac") {
      if (n.endsWith(".dmg")) score += 80;
      if (n.endsWith(".pkg")) score += 70;
      if (n.endsWith(".zip")) score += 30;
    } else if (platform === "linux") {
      if (n.endsWith(".appimage")) score += 80;
      if (n.endsWith(".tar.gz") || n.endsWith(".tgz")) score += 60;
      if (n.endsWith(".deb") || n.endsWith(".rpm")) score += 55;
      if (n.endsWith(".zip")) score += 25;
    } else {
      if (n.endsWith(".zip")) score += 40;
    }

    if (n.includes("setup") || n.includes("installer")) score += 18;
    if (n.includes("portable")) score += 6;
    if (n.includes("debug")) score -= 30;
    if (n.includes("symbols") || n.includes("pdb")) score -= 40;
    return score;
  }

  function pickBestAsset(assets) {
    const platform = platformHint();
    const items = Array.isArray(assets) ? assets : [];
    let best = null;
    let bestScore = -Infinity;
    for (const a of items) {
      const s = assetScore(a?.name, platform);
      if (s > bestScore) {
        bestScore = s;
        best = a;
      }
    }
    return best;
  }

  function renderAssets(assets) {
    if (!els.assetList) return;
    els.assetList.innerHTML = "";

    if (!assets?.length) {
      const empty = document.createElement("div");
      empty.className = "asset";
      empty.innerHTML = `
        <div class="asset__left">
          <div class="asset__name">${t("failed_hint")}</div>
          <div class="asset__meta">—</div>
        </div>
      `;
      els.assetList.appendChild(empty);
      return;
    }

    for (const a of assets) {
      const row = document.createElement("div");
      row.className = "asset";

      const left = document.createElement("div");
      left.className = "asset__left";

      const name = document.createElement("div");
      name.className = "asset__name";
      name.textContent = a.name || "Asset";

      const meta = document.createElement("div");
      meta.className = "asset__meta";
      const size = fmtBytes(a.size || 0);
      const downloads = typeof a.download_count === "number" ? fmtNumber(a.download_count) : "—";
      meta.textContent = `${size || "—"} • ${downloads} ${t("asset_downloads")}`;

      left.appendChild(name);
      left.appendChild(meta);

      const right = document.createElement("div");
      right.className = "asset__right";

      const count = document.createElement("span");
      count.className = "asset__count";
      count.textContent = typeof a.download_count === "number" ? `${fmtCompactNumber(a.download_count)}` : "—";

      const btn = document.createElement("a");
      btn.className = "asset__btn";
      btn.href = a.browser_download_url || `https://github.com/${REPO.owner}/${REPO.repo}/releases`;
      btn.target = "_blank";
      btn.rel = "noreferrer noopener";
      btn.textContent = t("get_asset");

      right.appendChild(count);
      right.appendChild(btn);

      row.appendChild(left);
      row.appendChild(right);
      els.assetList.appendChild(row);
    }
  }

  function updateReleaseCopy(release) {
    if (!release) return;
    const tag = release.tag_name || release.name || "—";
    const date = release.published_at ? new Date(release.published_at) : null;
    const pretty = date
      ? date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "2-digit" })
      : "";

    if (els.latestValue) els.latestValue.textContent = tag;
    if (els.releaseHint) {
      els.releaseHint.textContent = `${t("latest_release_label")}: ${tag}${
        pretty ? ` • ${t("updated_label")}: ${pretty}` : ""
      }`;
    }
    if (els.dlHeadline) els.dlHeadline.textContent = `OnionHop ${tag}`;
  }

  function setPrimaryDownload(asset, release) {
    const fallback = `https://github.com/${REPO.owner}/${REPO.repo}/releases`;
    const tag = release?.tag_name || release?.name || "";

    const url = asset?.browser_download_url || fallback;
    const label = asset?.name || "";
    const size = asset?.size ? fmtBytes(asset.size) : "";

    if (els.downloadLatest) {
      els.downloadLatest.href = url;
      els.downloadLatest.target = "_blank";
      els.downloadLatest.rel = "noreferrer noopener";
      if (els.downloadMeta) els.downloadMeta.textContent = tag ? `(${tag})` : "";
    }

    if (els.dlPrimary) {
      els.dlPrimary.href = url;
      els.dlPrimary.target = "_blank";
      els.dlPrimary.rel = "noreferrer noopener";
    }
    if (els.dlSecondary) els.dlSecondary.href = fallback;

    if (els.dlMeta) {
      const parts = [];
      if (label) parts.push(label);
      if (size) parts.push(size);
      els.dlMeta.textContent = parts.length ? parts.join(" • ") : "—";
    }
  }

  async function fetchAllReleaseDownloads({ pageLimit = 3 } = {}) {
    let total = 0;
    for (let page = 1; page <= pageLimit; page += 1) {
      const url = `https://api.github.com/repos/${REPO.owner}/${REPO.repo}/releases?per_page=100&page=${page}`;
      const releases = await fetchJson(url, { timeoutMs: 9000 });
      if (!Array.isArray(releases) || releases.length === 0) break;
      for (const r of releases) {
        for (const a of r.assets || []) {
          if (typeof a.download_count === "number") total += a.download_count;
        }
      }
      if (releases.length < 100) break;
    }
    return total;
  }

  function setCounts({ stars, downloads, latestTag }) {
    if (els.starsValue) els.starsValue.textContent = fmtCompactNumber(stars);
    if (els.downloadsValue) els.downloadsValue.textContent = fmtCompactNumber(downloads);
    if (els.latestValue && latestTag) els.latestValue.textContent = latestTag;
  }

  let lastRelease = null;

  async function loadGithub() {
    const cacheKey = "onionhop.github.cache.v1";
    const cachedRaw = localStorage.getItem(cacheKey);
    if (cachedRaw) {
      try {
        const cached = JSON.parse(cachedRaw);
        if (cached?.ts && Date.now() - cached.ts < 15 * 60 * 1000) {
          if (typeof cached.stars === "number") els.starsValue.textContent = fmtCompactNumber(cached.stars);
          if (typeof cached.downloads === "number") els.downloadsValue.textContent = fmtCompactNumber(cached.downloads);
          if (cached.latestTag) els.latestValue.textContent = cached.latestTag;
        }
      } catch {
        // ignore
      }
    }

    const repoUrl = `https://api.github.com/repos/${REPO.owner}/${REPO.repo}`;
    const latestUrl = `https://api.github.com/repos/${REPO.owner}/${REPO.repo}/releases/latest`;

    try {
      const [repo, latest] = await Promise.all([fetchJson(repoUrl), fetchJson(latestUrl)]);

      const stars = typeof repo?.stargazers_count === "number" ? repo.stargazers_count : null;
      lastRelease = latest;

      updateReleaseCopy(latest);
      renderAssets(latest?.assets || []);

      const primary = pickBestAsset(latest?.assets || []);
      setPrimaryDownload(primary, latest);

      let downloads = null;
      try {
        downloads = await fetchAllReleaseDownloads({ pageLimit: 3 });
      } catch {
        let latestTotal = 0;
        for (const a of latest?.assets || []) {
          if (typeof a.download_count === "number") latestTotal += a.download_count;
        }
        downloads = latestTotal;
      }

      if (els.apiNote) {
        els.apiNote.style.display = 'none';
      }

      setCounts({
        stars: typeof stars === "number" ? stars : undefined,
        downloads: typeof downloads === "number" ? downloads : undefined,
        latestTag: latest?.tag_name || latest?.name || undefined,
      });

      localStorage.setItem(
        cacheKey,
        JSON.stringify({
          ts: Date.now(),
          stars: typeof stars === "number" ? stars : null,
          downloads: typeof downloads === "number" ? downloads : null,
          latestTag: latest?.tag_name || latest?.name || null,
        })
      );
    } catch (e) {
      console.warn(e);
      if (els.apiNote) els.apiNote.style.display = 'none';
      
      // Try to use cached data even if stale
      if (cachedRaw) {
        try {
          const cached = JSON.parse(cachedRaw);
          if (typeof cached.stars === "number") els.starsValue.textContent = fmtCompactNumber(cached.stars);
          if (typeof cached.downloads === "number") els.downloadsValue.textContent = fmtCompactNumber(cached.downloads);
          if (cached.latestTag) els.latestValue.textContent = cached.latestTag;
        } catch {}
      }
      
      renderAssets([]);
    }
  }

  function init() {
    applyI18n();
    setupLangToggle();
    setupReveal();
    renderScreenshots();
    loadGithub();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
