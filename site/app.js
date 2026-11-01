/* ViaLX — front-end do site. Dados públicos GTFS/GTFS-RT da Carris. */
"use strict";

const ROOT = location.protocol === "file:" ? "http://127.0.0.1:8080" : location.origin;
const API_BASE = ROOT + "/mock";
const API_ROOT = API_BASE + "/api/v2.10";
const NS = "http://www.w3.org/2000/svg";
const CIRC = 2 * Math.PI * 24;
const numberFmt = new Intl.NumberFormat("pt-PT");
const timeFmt = new Intl.DateTimeFormat("pt-PT", {
  timeZone: "Europe/Lisbon",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});
const hmFmt = new Intl.DateTimeFormat("pt-PT", {
  timeZone: "Europe/Lisbon",
  hour: "2-digit",
  minute: "2-digit",
});
const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

const state = {
  stops: [],
  routes: [],
  feed: null,
  tab: "stops",
  cardLimit: 60,
  selectedStop: "",
  selectedRoute: "",
  boardTimer: 0,
  boardSeq: 0,
  boardLoaded: false,
  mapTimer: 0,
  mapSeq: 0,
  vehicleNodes: new Map(),
  projection: null,
  plans: [],
  planFrom: null,
  planTo: null,
  planIndex: 0,
};

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null) continue;
    if (key === "class") node.className = value;
    else if (key === "text") node.textContent = value;
    else if (key === "dataset") Object.assign(node.dataset, value);
    else if (key.startsWith("on") && typeof value === "function") {
      node.addEventListener(key.slice(2).toLowerCase(), value);
    } else node.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child === undefined || child === null) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function svg(tag, attrs = {}, ...children) {
  const node = document.createElementNS(NS, tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null) continue;
    if (key === "class") node.setAttribute("class", value);
    else if (key === "text") node.textContent = value;
    else node.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child === undefined || child === null) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function normalize(value) {
  return String(value || "")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .trim();
}

function fmtClock(date = new Date()) {
  return timeFmt.format(date);
}

function fmtHm(iso) {
  if (!iso) return "";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "" : hmFmt.format(date);
}

function etaParts(seconds) {
  const total = Math.max(0, Math.round(Number(seconds) || 0));
  if (total >= 3600) {
    return {
      value: `${Math.floor(total / 3600)}h${String(Math.floor((total % 3600) / 60)).padStart(2, "0")}`,
      unit: "",
    };
  }
  if (total >= 60) return { value: String(Math.floor(total / 60)), unit: "min" };
  return { value: String(total), unit: "s" };
}

function toast(message) {
  const node = $("#toast");
  if (!node) return;
  node.textContent = message;
  node.classList.add("show");
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => node.classList.remove("show"), 3400);
}

function connected(ok, title) {
  const dot = $("#mock-dot");
  if (!dot) return;
  dot.classList.toggle("on", !!ok);
  if (title) dot.title = title;
}

async function api(path, options = {}) {
  const response = await fetch(API_ROOT + path, {
    cache: "no-store",
    headers: { Accept: "application/json" },
    ...options,
  });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.json();
}

async function rootFetch(path, options = {}) {
  const response = await fetch(ROOT + path, {
    cache: "no-store",
    headers: { Accept: "application/json" },
    ...options,
  });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.json();
}

/* ---------- arranque ---------- */

document.addEventListener("DOMContentLoaded", () => {
  startClock();
  initProgress();
  initReveal();
  initCanvas();
  initTabs();
  initBoardControls();
  initMapControls();
  initPlanner();
  initApiTable();
  initCopyBase();
  setTimeout(hidePreloader, 2800);
  loadCore();
});

function hidePreloader() {
  $("#preloader")?.classList.add("done");
}

async function loadCore() {
  const status = $("#board-status");
  if (status) status.textContent = "a ligar…";
  try {
    const [stopsResult, routesResult, alertsResult, feedResult] = await Promise.allSettled([
      api("/busstops"),
      api("/routes"),
      api("/alerts"),
      rootFetch("/feed"),
    ]);
    if (stopsResult.status !== "fulfilled") throw stopsResult.reason;
    if (routesResult.status !== "fulfilled") throw routesResult.reason;
    state.stops = stopsResult.value || [];
    state.routes = routesResult.value || [];
    state.feed = feedResult.status === "fulfilled" ? feedResult.value : null;
    state.alerts = alertsResult.status === "fulfilled" ? alertsResult.value || [] : [];
    connected(true, `GTFS ${state.feed?.feedVersion || "público"} · ${state.stops.length} paragens`);
    updateStats();
    updateTicker();
    populateStops();
    populateRoutes();
    renderCards();
    await loadBoard();
    await loadMap();
  } catch (error) {
    console.error(error);
    connected(false, "sem ligação à API");
    if (status) status.textContent = "sem ligação à API";
    renderCoreError(error);
    toast("API indisponível. Corre o servidor ViaLX na porta 8080.");
  } finally {
    hidePreloader();
  }
}

function renderCoreError(error) {
  const board = $("#board");
  if (board) {
    board.replaceChildren(
      el(
        "div",
        { class: "dep" },
        el("div", { class: "route", text: "!" }),
        el("div", { class: "head", text: "Sem ligação ao servidor" }),
        el("div", { class: "count" }, el("b", { text: "—" }), el("small", { text: "API" }))
      )
    );
  }
  const cards = $("#cards");
  if (cards) {
    cards.replaceChildren(
      el(
        "article",
        { class: "card" },
        el("span", { class: "badge", text: "Servidor" }),
        el("h3", { text: "Arranca o ViaLX" }),
        el("p", { text: "O site precisa do servidor local para ler o GTFS público da Carris." }),
        el("div", { class: "meta" }, el("code", { text: "rebuild/.venv/bin/python rebuild/server/carris_server.py" }))
      )
    );
  }
}

function updateStats() {
  const values = [
    state.stops.length,
    state.routes.length,
    10,
    Number(state.feed?.counts?.stop_times) || 2472277,
  ];
  const labels = ["paragens reais", "carreiras", "segundos de polling", "horários no feed"];
  $$(".stat").forEach((stat, index) => {
    const strong = $("strong", stat);
    const span = $("span", stat);
    if (strong) animateNumber(strong, values[index] || 0);
    if (span) span.textContent = labels[index] || "";
  });
}

function animateNumber(node, target) {
  if (reduced || target > 100000) {
    node.textContent = numberFmt.format(target);
    return;
  }
  const started = performance.now();
  const duration = 1100;
  function frame(now) {
    const progress = Math.min(1, (now - started) / duration);
    const eased = 1 - Math.pow(1 - progress, 3);
    node.textContent = numberFmt.format(Math.round(target * eased));
    if (progress < 1) requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
}

function updateTicker() {
  const track = $("#ticker-track");
  if (track && state.routes.length) {
    const chosen = state.routes.slice(0, 14);
    track.replaceChildren();
    for (const route of [...chosen, ...chosen]) {
      track.append(
        el("span", { text: `${route.number} ${route.name.split(" - ").slice(-1)[0]}` }),
        el("i", { text: "◆" })
      );
    }
  }
  const marquee = $(".marquee-track");
  if (marquee && state.routes.length) {
    const chosen = state.routes.slice(0, 8);
    marquee.replaceChildren();
    for (const route of [...chosen, ...chosen]) {
      marquee.append(
        el("span", { text: `${route.number} ${route.name.split(" - ").slice(-1)[0]}` }),
        el("i", { text: "◆" })
      );
    }
  }
}

function populateStops() {
  const select = $("#stop-select");
  if (!select) return;
  const sorted = [...state.stops].sort((a, b) => a.name.localeCompare(b.name, "pt"));
  select.replaceChildren();
  for (const stop of sorted) {
    select.append(el("option", { value: stop.id, text: `${stop.name} · ${stop.number}` }));
  }
  const preferred =
    sorted.find((stop) => stop.id === "1801") ||
    sorted.find((stop) => normalize(stop.name) === "saldanha") ||
    sorted.find((stop) => normalize(stop.name).includes("saldanha")) ||
    sorted[0];
  state.selectedStop = preferred?.id || "";
  if (state.selectedStop) select.value = state.selectedStop;
  populateDatalist();
}

function populateDatalist() {
  const datalist = $("#stops-datalist");
  if (!datalist) return;
  datalist.replaceChildren();
  const seen = new Set();
  for (const stop of state.stops) {
    if (seen.has(stop.name)) continue;
    seen.add(stop.name);
    datalist.append(el("option", { value: stop.name }));
  }
}

function populateRoutes() {
  const select = $("#route-select");
  if (!select) return;
  const sorted = [...state.routes].sort((a, b) =>
    a.number.localeCompare(b.number, "pt", { numeric: true })
  );
  select.replaceChildren();
  for (const route of sorted) {
    select.append(el("option", { value: route.number, text: `${route.number} · ${route.name}` }));
  }
  const preferred =
    sorted.find((route) => route.number === "726") ||
    sorted.find((route) => normalize(route.name).includes("cais sodre")) ||
    sorted[0];
  state.selectedRoute = preferred?.number || "";
  if (state.selectedRoute) select.value = state.selectedRoute;
}

/* ---------- relógio, progresso, reveal ---------- */

function startClock() {
  const node = $("#clock");
  if (!node) return;
  const tick = () => {
    node.textContent = fmtClock();
  };
  tick();
  setInterval(tick, 1000);
}

function initProgress() {
  const bar = $("#progress");
  if (!bar) return;
  const update = () => {
    const max = document.documentElement.scrollHeight - window.innerHeight;
    const ratio = max > 0 ? window.scrollY / max : 0;
    bar.style.width = `${Math.min(100, Math.max(0, ratio * 100))}%`;
  };
  window.addEventListener("scroll", update, { passive: true });
  update();
}

function initReveal() {
  const items = $$(".reveal");
  if (!("IntersectionObserver" in window) || reduced) {
    items.forEach((item) => item.classList.add("in"));
    return;
  }
  const observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          entry.target.classList.add("in");
          observer.unobserve(entry.target);
        }
      }
    },
    { threshold: 0.12 }
  );
  items.forEach((item) => observer.observe(item));
}

/* ---------- painel de chegadas ---------- */

function initBoardControls() {
  const select = $("#stop-select");
  select?.addEventListener("change", () => {
    state.selectedStop = select.value;
    state.boardLoaded = false;
    loadBoard();
  });
  $("#poll-toggle")?.addEventListener("change", () => {
    if ($("#poll-toggle").checked) loadBoard();
    else {
      clearTimeout(state.boardTimer);
      state.boardTimer = 0;
    }
  });
  setInterval(tickCountdowns, 1000);
}

function scheduleBoard(delay = 10000) {
  clearTimeout(state.boardTimer);
  state.boardTimer = 0;
  if (!$("#poll-toggle")?.checked) return;
  state.boardTimer = window.setTimeout(() => loadBoard(), delay);
}

async function loadBoard() {
  if (!state.selectedStop) return;
  const sequence = ++state.boardSeq;
  const status = $("#board-status");
  if (status) status.textContent = "a atualizar…";
  if (!state.boardLoaded) renderBoardSkeleton();
  try {
    const items = await api(
      `/estimations/estimationbusstop/${encodeURIComponent(state.selectedStop)}/top/10`
    );
    if (sequence !== state.boardSeq) return;
    state.boardLoaded = true;
    renderBoard(items || []);
    const stop = state.stops.find((item) => item.id === state.selectedStop);
    if (status) {
      const live = (items || []).filter((item) => item.realtime).length;
      status.textContent = `${stop?.name || "paragem"} · ${items.length} chegadas${live ? ` · ${live} em tempo real` : ""} · ${fmtClock()}`;
    }
    connected(true, `GTFS ${state.feed?.feedVersion || "público"} · ${state.stops.length} paragens`);
    scheduleBoard();
  } catch (error) {
    if (sequence !== state.boardSeq) return;
    console.error(error);
    if (status) status.textContent = "falha ao atualizar · nova tentativa";
    connected(false, "falha na API");
    if (!state.boardLoaded) renderBoardError();
    scheduleBoard(15000);
  }
}

function renderBoardSkeleton() {
  const board = $("#board");
  if (!board) return;
  board.replaceChildren();
  for (let index = 0; index < 4; index += 1) board.append(el("div", { class: "skel" }));
}

function renderBoardError() {
  const board = $("#board");
  if (!board) return;
  board.replaceChildren(
    el(
      "div",
      { class: "dep" },
      el("div", { class: "route", text: "!" }),
      el("div", { class: "head", text: "Não foi possível ler as chegadas" }),
      el("div", { class: "count" }, el("b", { text: "—" }), el("small", { text: "sem rede" }))
    )
  );
}

function renderBoard(items) {
  const board = $("#board");
  if (!board) return;
  board.replaceChildren();
  if (!items.length) {
    board.append(
      el(
        "div",
        { class: "dep" },
        el("div", { class: "route", text: "—" }),
        el("div", { class: "head", text: "Sem chegadas previstas nas próximas horas" }),
        el("div", { class: "count" }, el("b", { text: "0" }), el("small", { text: "min" }))
      )
    );
    return;
  }
  const stopName = items[0]?.stopName || "paragem";
  for (const item of items) {
    const eta = Number(item.etaSeconds ?? item.minutes * 60 + (item.seconds || 0));
    const deadline = Date.now() + Math.max(0, eta) * 1000;
    const total = Math.max(eta, 30);
    const row = el("div", { class: "dep", title: `Ver ${item.routeNumber} no mapa` });
    row.dataset.deadline = String(deadline);
    row.dataset.total = String(total);
    row.dataset.route = item.routeNumber || "";
    row.append(el("div", { class: "route", text: item.routeNumber || "—" }));
    const head = el("div", { class: "head", text: item.headsign || item.destination || stopName });
    head.append(
      el("small", {
        text: `${item.destination || stopName} · ${item.realtime ? "tempo real" : "horário"}${
          item.vehicleId ? ` · viatura ${item.vehicleId}` : ""
        }`,
      })
    );
    row.append(head);
    const count = el("div", { class: "count" });
    const parts = etaParts(eta);
    const value = el("b", { text: parts.value });
    const unit = el("small", { text: parts.unit });
    count.append(value, unit, ring());
    row.append(count);
    row.addEventListener("click", () => {
      if (item.routeNumber) selectRoute(item.routeNumber);
    });
    board.append(row);
  }
  tickCountdowns();
}

function ring() {
  return svg(
    "svg",
    { class: "ring", viewBox: "0 0 60 60", "aria-hidden": "true" },
    svg("circle", { class: "bg", cx: 30, cy: 30, r: 24 }),
    svg("circle", {
      class: "fg",
      cx: 30,
      cy: 30,
      r: 24,
      "stroke-dasharray": CIRC.toFixed(2),
      "stroke-dashoffset": "0",
    })
  );
}

function tickCountdowns() {
  const now = Date.now();
  for (const row of $$("#board .dep[data-deadline]")) {
    const deadline = Number(row.dataset.deadline);
    const total = Number(row.dataset.total) || 60;
    const remaining = Math.max(0, Math.round((deadline - now) / 1000));
    const value = $(".count b", row);
    const unit = $(".count small", row);
    const parts = etaParts(remaining);
    if (value) value.textContent = parts.value;
    if (unit) unit.textContent = parts.unit;
    row.classList.toggle("urgent", remaining <= 60);
    const fg = $(".ring .fg", row);
    if (fg) {
      const ratio = Math.max(0, Math.min(1, remaining / total));
      fg.style.strokeDashoffset = String(CIRC * (1 - ratio));
    }
  }
}

/* ---------- cartões ---------- */

function initTabs() {
  $$(".tab").forEach((tab) => {
    tab.addEventListener("click", () => {
      $$(".tab").forEach((item) => item.classList.toggle("active", item === tab));
      state.tab = tab.dataset.tab || "stops";
      state.cardLimit = 60;
      renderCards();
    });
  });
  $("#search")?.addEventListener("input", () => {
    state.cardLimit = 60;
    clearTimeout(renderCards._timer);
    renderCards._timer = setTimeout(renderCards, 120);
  });
}

function renderCards() {
  const container = $("#cards");
  if (!container) return;
  container.replaceChildren();
  const query = normalize($("#search")?.value);
  if (state.tab === "alerts") {
    renderAlerts(container);
    return;
  }
  if (state.tab === "routes") {
    let routes = state.routes;
    if (query) {
      routes = routes.filter(
        (route) =>
          normalize(route.number).includes(query) ||
          normalize(route.name).includes(query)
      );
    }
    routes = [...routes].sort((a, b) =>
      a.number.localeCompare(b.number, "pt", { numeric: true })
    );
    const visible = routes.slice(0, state.cardLimit);
    for (const route of visible) container.append(routeCard(route));
    if (routes.length > visible.length) {
      container.append(
        moreCard(routes.length - visible.length, () => {
          state.cardLimit += 60;
          renderCards();
        })
      );
    }
    if (!routes.length) container.append(emptyCard("Sem carreiras", "Tenta outro número ou nome."));
    return;
  }
  let stops = state.stops;
  if (query) {
    stops = stops.filter(
      (stop) =>
        normalize(stop.name).includes(query) ||
        normalize(stop.number).includes(query) ||
        normalize(stop.id).includes(query)
    );
  }
  const visible = stops.slice(0, state.cardLimit);
  for (const stop of visible) container.append(stopCard(stop));
  if (stops.length > visible.length) {
    container.append(
      moreCard(stops.length - visible.length, () => {
        state.cardLimit += 60;
        renderCards();
      })
    );
  }
  if (!stops.length) container.append(emptyCard("Sem paragens", "Tenta outro nome ou número."));
}

function stopCard(stop) {
  const card = el("article", {
    class: "card",
    tabindex: "0",
    role: "button",
    "aria-label": `Ver chegadas de ${stop.name}`,
  });
  card.append(
    el("span", { class: "badge", text: `Paragem ${stop.number}` }),
    el("h3", { text: stop.name }),
    el("p", { text: `${stop.lat.toFixed(5)}, ${stop.lng.toFixed(5)}` }),
    el("div", { class: "meta" }, el("span", { text: "chegadas em direto →" }))
  );
  const activate = () => selectStop(stop.id);
  card.addEventListener("click", activate);
  card.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      activate();
    }
  });
  addTilt(card);
  return card;
}

function routeCard(route) {
  const card = el("article", {
    class: "card",
    tabindex: "0",
    role: "button",
    "aria-label": `Ver ${route.number} no mapa`,
  });
  card.append(
    el("span", { class: "badge", text: "Carreira" }),
    el("h3", { text: route.number }),
    el("p", { text: route.name }),
    el("div", { class: "meta" }, el("span", { text: route.direction || "Ida/Volta" }), el("span", { text: "mapa vivo →" }))
  );
  const activate = () => selectRoute(route.number);
  card.addEventListener("click", activate);
  card.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      activate();
    }
  });
  addTilt(card);
  return card;
}

function renderAlerts(container) {
  const alerts = state.alerts || [];
  if (!alerts.length) {
    container.append(
      el(
        "article",
        { class: "card" },
        el("span", { class: "badge", text: "GTFS-RT" }),
        el("h3", { text: "Sem alertas no feed público" }),
        el("p", {
          text: "O feed GTFS-RT da Carris publica posições de veículos, mas não um feed de alertas. Consulta os avisos oficiais da Carris para desvios e obras.",
        }),
        el("div", { class: "meta" }, el("span", { text: "fonte: gateway.carris.pt" }))
      )
    );
    return;
  }
  for (const alert of alerts) {
    container.append(
      el(
        "article",
        { class: "card" },
        el("span", { class: "badge", text: alert.routeNumber || "Rede" }),
        el("h3", { text: "Aviso de serviço" }),
        el("p", { text: alert.description || "" })
      )
    );
  }
}

function emptyCard(title, text) {
  return el(
    "article",
    { class: "card" },
    el("span", { class: "badge", text: "ViaLX" }),
    el("h3", { text: title }),
    el("p", { text })
  );
}

function moreCard(remaining, action) {
  const card = el("article", { class: "card", tabindex: "0", role: "button" });
  card.append(
    el("span", { class: "badge", text: "Mais" }),
    el("h3", { text: `${remaining} por mostrar` }),
    el("p", { text: "Continua a explorar a rede." })
  );
  card.addEventListener("click", action);
  card.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      action();
    }
  });
  return card;
}

function addTilt(card) {
  if (reduced) return;
  card.addEventListener("mousemove", (event) => {
    const rect = card.getBoundingClientRect();
    const dx = (event.clientX - rect.left) / rect.width - 0.5;
    const dy = (event.clientY - rect.top) / rect.height - 0.5;
    card.style.transform = `perspective(900px) rotateX(${(-dy * 5).toFixed(2)}deg) rotateY(${(dx * 5).toFixed(2)}deg) translateY(-3px)`;
  });
  card.addEventListener("mouseleave", () => {
    card.style.transform = "";
  });
}

function selectStop(id) {
  state.selectedStop = id;
  const select = $("#stop-select");
  if (select) select.value = id;
  state.boardLoaded = false;
  loadBoard();
  $("#direto")?.scrollIntoView({ behavior: reduced ? "auto" : "smooth", block: "start" });
}

function selectRoute(number) {
  state.selectedRoute = number;
  const select = $("#route-select");
  if (select) select.value = number;
  loadMap();
  $("#mapa")?.scrollIntoView({ behavior: reduced ? "auto" : "smooth", block: "start" });
}

/* ---------- mapa ---------- */

function initMapControls() {
  const grid = $(".grid");
  if (grid) {
    grid.replaceChildren();
    for (let index = 1; index < 9; index += 1) {
      const x = (800 / 9) * index;
      grid.append(svg("line", { x1: x, y1: 0, x2: x, y2: 420 }));
    }
    for (let index = 1; index < 5; index += 1) {
      const y = (420 / 5) * index;
      grid.append(svg("line", { x1: 0, y1: y, x2: 800, y2: y }));
    }
  }
  $("#route-select")?.addEventListener("change", (event) => {
    state.selectedRoute = event.target.value;
    loadMap();
  });
}

function scheduleMap(delay = 10000) {
  clearTimeout(state.mapTimer);
  state.mapTimer = window.setTimeout(() => loadMap(), delay);
}

async function loadMap() {
  const route = state.selectedRoute;
  if (!route) return;
  const sequence = ++state.mapSeq;
  const status = $("#veh-status");
  if (status) status.textContent = "a carregar viaturas…";
  try {
    const [vehiclesResult, shapeResult] = await Promise.allSettled([
      api(`/vehicleStatuses/routeNumber/${encodeURIComponent(route)}`),
      api(`/routes/${encodeURIComponent(route)}/shape`),
    ]);
    if (sequence !== state.mapSeq) return;
    const vehicles = vehiclesResult.status === "fulfilled" ? vehiclesResult.value || [] : [];
    const shape = shapeResult.status === "fulfilled" ? shapeResult.value || {} : {};
    drawMap(shape, vehicles, route);
    if (status) {
      const live = vehicles.filter(
        (vehicle) => Number.isFinite(Number(vehicle.lat)) && Number.isFinite(Number(vehicle.lng))
      ).length;
      status.textContent = `${live} viatura${live === 1 ? "" : "s"} · ${fmtClock()}`;
    }
    connected(true, `GTFS ${state.feed?.feedVersion || "público"}`);
    scheduleMap();
  } catch (error) {
    if (sequence !== state.mapSeq) return;
    console.error(error);
    if (status) status.textContent = "falha ao ler o tempo real";
    connected(false, "falha no GTFS-RT");
    scheduleMap(15000);
  }
}

function drawMap(shape, vehicles, route) {
  const points = Array.isArray(shape.points) ? shape.points : [];
  const projectedVehicles = vehicles
    .map((vehicle) => ({
      ...vehicle,
      lat: Number(vehicle.lat),
      lng: Number(vehicle.lng),
    }))
    .filter((vehicle) => Number.isFinite(vehicle.lat) && Number.isFinite(vehicle.lng));

  const all = [
    ...points.map((point) => [Number(point[0]), Number(point[1])]),
    ...projectedVehicles.map((vehicle) => [vehicle.lat, vehicle.lng]),
  ];
  const projection = makeProjection(all);
  state.projection = projection;

  const path = $("#route-path");
  if (path && points.length > 1) {
    const sampled = sample(points, 260);
    path.setAttribute(
      "d",
      sampled
        .map((point, index) => {
          const [x, y] = projection(Number(point[0]), Number(point[1]));
          return `${index ? "L" : "M"}${x.toFixed(1)},${y.toFixed(1)}`;
        })
        .join(" ")
    );
  }

  const stopGroup = $("#map-stops");
  if (stopGroup) {
    stopGroup.replaceChildren();
    const seen = new Set();
    for (const stop of shape.stops || []) {
      if (seen.has(stop.id)) continue;
      seen.add(stop.id);
      const [x, y] = projection(Number(stop.lat), Number(stop.lng));
      const node = svg("circle", { class: "stop-dot", cx: x.toFixed(1), cy: y.toFixed(1), r: 3.4 });
      node.append(svg("title", { text: `${stop.name} · ${stop.id}` }));
      stopGroup.append(node);
    }
  }

  const group = $("#vehicles");
  if (!group) return;
  const seen = new Set();
  for (const vehicle of projectedVehicles) {
    const id = String(vehicle.vehicleId || vehicle.tripId || `${vehicle.lat},${vehicle.lng}`);
    const [x, y] = projection(vehicle.lat, vehicle.lng);
    let node = state.vehicleNodes.get(id);
    if (!node) {
      node = svg("g", { class: "veh" });
      node.style.transition = "transform 8s linear";
      node.append(svg("circle", { cx: 0, cy: 0, r: 12 }), svg("text", { x: 0, y: 4, "text-anchor": "middle" }));
      node.append(svg("title"));
      group.append(node);
      state.vehicleNodes.set(id, node);
    }
    node.setAttribute("transform", `translate(${x.toFixed(1)},${y.toFixed(1)})`);
    const label = $("text", node);
    if (label) label.textContent = vehicle.routeNumber || route;
    const title = $("title", node);
    if (title) {
      const heading = vehicle.heading || vehicle.direction || "";
      title.textContent = `Viatura ${vehicle.vehicleId || "?"} · ${vehicle.routeNumber || route}${
        heading ? ` · ${heading}` : ""
      }`;
    }
    seen.add(id);
  }
  for (const [id, node] of state.vehicleNodes) {
    if (!seen.has(id)) {
      node.remove();
      state.vehicleNodes.delete(id);
    }
  }
}

function makeProjection(points) {
  let minLat = Infinity;
  let maxLat = -Infinity;
  let minLng = Infinity;
  let maxLng = -Infinity;
  for (const point of points) {
    const [lat, lng] = point;
    if (!Number.isFinite(lat) || !Number.isFinite(lng)) continue;
    minLat = Math.min(minLat, lat);
    maxLat = Math.max(maxLat, lat);
    minLng = Math.min(minLng, lng);
    maxLng = Math.max(maxLng, lng);
  }
  if (!Number.isFinite(minLat) || !Number.isFinite(minLng)) {
    minLat = 38.68;
    maxLat = 38.8;
    minLng = -9.23;
    maxLng = -9.09;
  }
  let latRange = maxLat - minLat;
  let lngRange = maxLng - minLng;
  if (latRange < 0.002) latRange = 0.002;
  if (lngRange < 0.002) lngRange = 0.002;
  const padLat = latRange * 0.12;
  const padLng = lngRange * 0.12;
  minLat -= padLat;
  maxLat += padLat;
  minLng -= padLng;
  maxLng += padLng;
  latRange = maxLat - minLat;
  lngRange = maxLng - minLng;
  return (lat, lng) => [
    34 + ((lng - minLng) / lngRange) * 732,
    26 + ((maxLat - lat) / latRange) * 368,
  ];
}

function sample(points, limit) {
  if (points.length <= limit) return points;
  const step = Math.ceil(points.length / limit);
  const out = [];
  for (let index = 0; index < points.length; index += step) out.push(points[index]);
  if (out[out.length - 1] !== points[points.length - 1]) out.push(points[points.length - 1]);
  return out;
}

/* ---------- planeador ---------- */

function initPlanner() {
  const when = $("#p-when");
  if (when) when.value = nextTimeValue();
  const datalist = el("datalist", { id: "stops-datalist" });
  for (const stop of state.stops) datalist.append(el("option", { value: stop.name }));
  document.body.append(datalist);
  for (const id of ["p-from", "p-to"]) {
    const input = $(`#${id}`);
    if (input) input.setAttribute("list", "stops-datalist");
  }
  $("#planner-form")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    await planTrip();
  });
}

function nextTimeValue() {
  const date = new Date(Date.now() + 15 * 60000);
  date.setMinutes(Math.ceil(date.getMinutes() / 5) * 5, 0, 0);
  if (date.getMinutes() >= 60) date.setHours(date.getHours() + 1, 0, 0, 0);
  return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
}

async function resolvePlace(query) {
  const cleaned = String(query || "").trim();
  if (!cleaned) throw new Error("Indica a origem e o destino.");
  const results = await api(`/geocoding/suggest/${encodeURIComponent(cleaned)}`);
  if (!results.length) throw new Error(`Não encontrei "${cleaned}". Tenta o nome de uma paragem.`);
  return results[0];
}

async function planTrip() {
  const resultNode = $("#plan-result");
  if (resultNode) resultNode.replaceChildren(el("div", { class: "tstep" }, el("div", { class: "tdot", text: "…" }), el("div", { class: "tbody" }, el("div", { text: "A calcular a melhor ligação…" }))));
  try {
    const fromText = $("#p-from")?.value || "";
    const toText = $("#p-to")?.value || "";
    const when = $("#p-when")?.value || "now";
    const [from, to] = await Promise.all([resolvePlace(fromText), resolvePlace(toText)]);
    state.planFrom = from;
    state.planTo = to;
    const plans = await api(
      `/planner/startLat/${from.lat}/startLon/${from.lng}/endLat/${to.lat}/endLon/${to.lng}/start/${encodeURIComponent(
        when || "now"
      )}/language/pt`
    );
    state.plans = Array.isArray(plans) ? plans : [];
    state.planIndex = 0;
    renderPlans();
  } catch (error) {
    console.error(error);
    if (resultNode) {
      resultNode.replaceChildren(
        el(
          "div",
          { class: "tstep" },
          el("div", { class: "tdot", text: "ℹ" }),
          el("div", { class: "tbody" }, el("div", { text: error.message || "Não foi possível calcular a viagem." }))
        )
      );
    }
    toast(error.message || "Falha no planeador");
  }
}

function renderPlans() {
  const resultNode = $("#plan-result");
  if (!resultNode) return;
  resultNode.replaceChildren();
  if (!state.plans.length) {
    resultNode.append(
      el(
        "div",
        { class: "tstep" },
        el("div", { class: "tdot", text: "ℹ" }),
        el("div", { class: "tbody" }, el("div", { text: "Sem soluções para este par." }))
      )
    );
    return;
  }
  const plan = state.plans[state.planIndex] || state.plans[0];
  const summary = el("div", { class: "tbody" });
  summary.append(
    el("strong", { text: plan.summary || "Solução" }),
    el("br"),
    el("small", {
      text: `${state.planFrom?.name || ""} → ${state.planTo?.name || ""}${
        plan.departure ? ` · partida ${fmtHm(plan.departure)}` : ""
      }${plan.arrival ? ` · chegada ${fmtHm(plan.arrival)}` : ""}`,
    })
  );
  resultNode.append(el("div", { class: "tstep" }, el("div", { class: "tdot", text: "◎" }), summary));

  for (const step of plan.steps || []) {
    const icons = { walk: "🚶", bus: "🚌", wait: "⏱", transfer: "⇄", info: "ℹ" };
    const body = el("div", { class: "tbody" }, el("div", { text: step.instruction || "" }));
    const meta = [];
    if (step.routeNumber) meta.push(`carreira ${step.routeNumber}`);
    if (step.durationMinutes) meta.push(`~${step.durationMinutes} min`);
    if (meta.length) body.append(el("small", { text: meta.join(" · ") }));
    resultNode.append(
      el("div", { class: "tstep" }, el("div", { class: "tdot", text: icons[step.mode] || "•" }), body)
    );
  }

  if (state.plans.length > 1) {
    const alternatives = el("div", { class: "tstep" });
    alternatives.append(el("div", { class: "tdot", text: "↺" }));
    const body = el("div", { class: "tbody" }, el("strong", { text: "Alternativas" }));
    const row = el("div", { class: "meta", style: "display:flex;gap:8px;flex-wrap:wrap;margin-top:10px" });
    state.plans.forEach((candidate, index) => {
      const button = el("button", {
        class: "btn small",
        type: "button",
        text: `${index + 1}. ${candidate.summary}`,
      });
      button.disabled = index === state.planIndex;
      button.addEventListener("click", () => {
        state.planIndex = index;
        renderPlans();
      });
      row.append(button);
    });
    body.append(row);
    alternatives.append(body);
    resultNode.append(alternatives);
  }
}

/* ---------- API ---------- */

function initApiTable() {
  const base = $("#api-base");
  if (base) base.textContent = `${API_BASE}/api/v2.10`;
  const body = $("#api-table");
  if (!body) return;
  const endpoints = [
    ["GET", "/busstops", "Todas as paragens do GTFS"],
    ["GET", "/routes", "Carreiras e variantes"],
    ["GET", "/Routes/busStop/{stop}", "Carreiras que servem a paragem"],
    ["GET", "/estimations/estimationbusstop/{stop}/top/10", "Chegadas + tempo real"],
    ["GET", "/stoptimes/routeNumber/{route}/direction/{dir}/stop/{stop}/date/{date}", "Horários programados"],
    ["GET", "/vehicleStatuses/routeNumber/{route}", "Posições GTFS-RT"],
    ["GET", "/routes/{route}/shape", "Traçado e paragens da carreira"],
    ["GET", "/planner/startLat/{lat}/startLon/{lng}/endLat/{lat}/endLon/{lng}/start/{hora}/language/pt", "Planeador origem→destino"],
    ["GET", "/geocoding/suggest/{q}", "Sugestões de paragens"],
    ["GET", "/feed", "Versão e estado do feed"],
  ];
  for (const [method, path, description] of endpoints) {
    const row = el("tr");
    const full = `${API_BASE}/api/v2.10${path}`;
    const copy = el("button", { class: "btn small", type: "button", text: "copiar" });
    copy.addEventListener("click", () => copyText(full));
    row.append(
      el("td", { text: method }),
      el("td", {}, el("code", { text: path })),
      el("td", { text: description }),
      el("td", {}, copy)
    );
    body.append(row);
  }
}

function initCopyBase() {
  $("#copy-base")?.addEventListener("click", () => copyText(`${API_BASE}/api/v2.10`));
}

async function copyText(value) {
  try {
    await navigator.clipboard.writeText(value);
    toast("Copiado para a área de transferência");
  } catch (error) {
    window.prompt("Copia o URL:", value);
  }
}

/* ---------- canvas do hero ---------- */

function initCanvas() {
  const canvas = $("#routes-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  let width = 0;
  let height = 0;
  let nodes = [];
  let edges = [];
  let pulses = [];
  let frameId = 0;

  function resize() {
    const rect = canvas.getBoundingClientRect();
    width = Math.max(1, rect.width);
    height = Math.max(1, rect.height);
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    build();
  }

  function build() {
    nodes = Array.from({ length: 34 }, () => ({
      x: Math.random() * width,
      y: Math.random() * height,
      r: 1.2 + Math.random() * 2.2,
    }));
    edges = [];
    for (let index = 0; index < nodes.length; index += 1) {
      const origin = nodes[index];
      const nearest = nodes
        .map((node, other) => ({ node, other, distance: Math.hypot(node.x - origin.x, node.y - origin.y) }))
        .filter((item) => item.other !== index)
        .sort((a, b) => a.distance - b.distance)
        .slice(0, 3);
      for (const item of nearest) {
        if (item.distance > Math.min(width, height) * 0.45) continue;
        if (edges.some((edge) => (edge.a === index && edge.b === item.other) || (edge.a === item.other && edge.b === index))) continue;
        edges.push({ a: index, b: item.other });
      }
    }
    pulses = Array.from({ length: 14 }, () => ({
      edge: Math.floor(Math.random() * Math.max(1, edges.length)),
      t: Math.random(),
      speed: 0.0015 + Math.random() * 0.0025,
    }));
  }

  function draw() {
    ctx.clearRect(0, 0, width, height);
    ctx.lineWidth = 1;
    ctx.strokeStyle = "rgba(45,212,191,0.18)";
    for (const edge of edges) {
      const a = nodes[edge.a];
      const b = nodes[edge.b];
      if (!a || !b) continue;
      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
      ctx.stroke();
    }
    ctx.fillStyle = "rgba(244,239,230,0.55)";
    for (const node of nodes) {
      ctx.beginPath();
      ctx.arc(node.x, node.y, node.r, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.shadowBlur = 12;
    ctx.shadowColor = "#C9F24B";
    ctx.fillStyle = "#C9F24B";
    for (const pulse of pulses) {
      const edge = edges[pulse.edge];
      if (!edge) continue;
      const a = nodes[edge.a];
      const b = nodes[edge.b];
      if (!a || !b) continue;
      const x = a.x + (b.x - a.x) * pulse.t;
      const y = a.y + (b.y - a.y) * pulse.t;
      ctx.beginPath();
      ctx.arc(x, y, 2.4, 0, Math.PI * 2);
      ctx.fill();
      pulse.t += pulse.speed;
      if (pulse.t > 1) {
        pulse.t = 0;
        pulse.edge = Math.floor(Math.random() * Math.max(1, edges.length));
      }
    }
    ctx.shadowBlur = 0;
    if (!reduced) frameId = requestAnimationFrame(draw);
  }

  resize();
  draw();
  window.addEventListener("resize", () => {
    cancelAnimationFrame(frameId);
    resize();
    draw();
  });
}
