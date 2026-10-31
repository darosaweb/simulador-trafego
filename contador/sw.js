// Guarda o contador no aparelho para abrir sem internet (contagem de campo costuma ser sem sinal).
// Ao mudar qualquer arquivo do contador, aumente a VERSAO para os aparelhos baixarem a nova.
const VERSAO = "contador-v1";
const ARQUIVOS = [
  "./",
  "index.html",
  "manifest.webmanifest",
  "icones/icone-192.png",
  "icones/icone-512.png",
  "icones/maskable-512.png",
  "icones/apple-touch-icon.png",
  "icones/favicon.png",
  "fontes/Barlow-400.woff2",
  "fontes/Barlow-500.woff2",
  "fontes/Barlow-600.woff2",
  "fontes/BarlowCondensed-500.woff2",
  "fontes/BarlowCondensed-600.woff2",
  "fontes/BarlowCondensed-700.woff2",
];

self.addEventListener("install", e => {
  e.waitUntil(caches.open(VERSAO).then(c => c.addAll(ARQUIVOS)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", e => {
  e.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k.startsWith("contador-") && k !== VERSAO).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

// Página: rede primeiro (pega a versão nova quando há sinal), cache se estiver offline.
// Demais arquivos: cache primeiro.
self.addEventListener("fetch", e => {
  const req = e.request;
  if (req.method !== "GET" || new URL(req.url).origin !== location.origin) return;
  if (req.mode === "navigate") {
    e.respondWith(fetch(req)
      .then(res => { const copy = res.clone(); caches.open(VERSAO).then(c => c.put("index.html", copy)); return res; })
      .catch(() => caches.match("index.html")));
    return;
  }
  e.respondWith(caches.match(req).then(hit => hit || fetch(req)));
});
