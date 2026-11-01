"""Servidor ViaLX: GTFS público da Carris + GTFS-RT, sem autenticação.

Correr:
    rebuild/.venv/bin/python rebuild/server/carris_server.py
Abrir:
    http://127.0.0.1:8080/          (site)
    http://127.0.0.1:8080/feed     (estado do feed)
    http://127.0.0.1:8080/mock/api/v2.10/busstops
"""
from __future__ import annotations

import argparse
import json
import mimetypes
import re
import sqlite3
import threading
import time
import traceback
import urllib.parse
import urllib.request
from contextlib import contextmanager
from datetime import date, datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

import gtfs_store as gtfs
import planner

RT_URL = gtfs.GTFS_RT_URL
STATIC_DIR = Path(__file__).resolve().parent.parent / "site"


class RTClient:
    """Cache do feed GTFS-RT de posições de veículos."""

    def __init__(self, routes_by_id: dict[str, str], enabled: bool = True, min_interval: float = 5.0) -> None:
        self.routes_by_id = routes_by_id
        self.enabled = enabled
        self.min_interval = max(1.0, float(min_interval))
        self.vehicles: list[dict[str, Any]] = []
        self.by_trip: dict[str, dict[str, Any]] = {}
        self.feed_timestamp: int | None = None
        self.last_fetch = 0.0
        self.last_success = 0.0
        self.error: str | None = None
        self._lock = threading.Lock()
        self._stop = threading.Event()

    def refresh(self, force: bool = False) -> None:
        if not self.enabled:
            return
        if not force and time.time() - self.last_fetch < self.min_interval:
            return
        with self._lock:
            if not force and time.time() - self.last_fetch < self.min_interval:
                return
            self.last_fetch = time.time()
            try:
                from google.transit import gtfs_realtime_pb2

                request = urllib.request.Request(RT_URL, headers={"User-Agent": gtfs.USER_AGENT})
                with urllib.request.urlopen(request, timeout=30) as response:
                    payload = response.read()
                feed = gtfs_realtime_pb2.FeedMessage()
                feed.ParseFromString(payload)
                feed_updated = int(feed.header.timestamp or 0)
                vehicles: list[dict[str, Any]] = []
                by_trip: dict[str, dict[str, Any]] = {}
                now_epoch = time.time()
                for entity in feed.entity:
                    if not entity.HasField("vehicle"):
                        continue
                    v = entity.vehicle
                    route_id = v.trip.route_id or ""
                    route_number = self.routes_by_id.get(route_id, route_id)
                    vehicle_id = v.vehicle.id or v.vehicle.license_plate or ""
                    direction = int(v.trip.direction_id) if v.trip.HasField("direction_id") else 0
                    vehicle_ts = int(v.timestamp) if v.HasField("timestamp") else feed_updated
                    # Entidades antigas ficam no feed mas já não representam viaturas
                    # em serviço; não as mostrar como se fossem posições ao vivo.
                    if vehicle_ts and now_epoch - vehicle_ts > 300:
                        continue
                    item = {
                        "vehicleId": vehicle_id,
                        "routeNumber": route_number,
                        "routeId": route_id,
                        "tripId": v.trip.trip_id or "",
                        "lat": float(v.position.latitude) if v.HasField("position") else None,
                        "lng": float(v.position.longitude) if v.HasField("position") else None,
                        "bearing": float(v.position.bearing) if v.HasField("position") and v.position.HasField("bearing") else None,
                        "speed": float(v.position.speed) if v.HasField("position") and v.position.HasField("speed") else None,
                        "nextStop": v.stop_id or "",
                        "currentStopSequence": int(v.current_stop_sequence) if v.HasField("current_stop_sequence") else None,
                        "status": int(v.current_status) if v.HasField("current_status") else 0,
                        "direction": "Ida" if direction == 0 else "Volta",
                        "timestamp": vehicle_ts,
                        "feedUpdated": feed_updated,
                    }
                    vehicles.append(item)
                    if item["tripId"]:
                        by_trip[item["tripId"]] = item
                self.vehicles = vehicles
                self.by_trip = by_trip
                self.feed_timestamp = int(feed.header.timestamp or 0)
                self.last_success = time.time()
                self.error = None
            except Exception as exc:  # mantém a última cache boa
                self.error = f"{type(exc).__name__}: {exc}"

    def start_background(self) -> None:
        """Mantém a cache quente para a API responder sem esperar pela Carris."""
        if not self.enabled:
            return
        thread = threading.Thread(target=self._refresh_loop, name="vialx-rt", daemon=True)
        thread.start()

    def _refresh_loop(self) -> None:
        while not self._stop.wait(self.min_interval):
            self.refresh(force=True)

    def status(self) -> dict[str, Any]:
        age = int(time.time() - self.last_success) if self.last_success else None
        return {
            "enabled": self.enabled,
            "vehicles": len(self.vehicles),
            "feedTimestamp": self.feed_timestamp,
            "lastSuccess": int(self.last_success) if self.last_success else None,
            "ageSeconds": age,
            "error": self.error,
        }


class App:
    def __init__(self, db_path: Path, stops: list[dict[str, Any]], routes: dict[str, dict[str, Any]],
                 routes_by_id: dict[str, str], rt: RTClient, meta: dict[str, Any]) -> None:
        self.db_path = db_path
        self.stops = stops
        self.stops_by_id = {s["id"]: s for s in stops}
        self.routes = routes
        self.routes_by_id = routes_by_id
        self.rt = rt
        self.meta = meta
        self._directions_cache: dict[str, list[dict[str, Any]]] = {}

    @contextmanager
    def db(self):
        con = gtfs.connect(self.db_path)
        try:
            yield con
        finally:
            con.close()

    def public_stops(self) -> list[dict[str, Any]]:
        return [
            {
                "id": s["id"],
                "number": s["number"],
                "name": s["name"],
                "lat": s["lat"],
                "lng": s["lng"],
            }
            for s in self.stops
        ]

    def directions_for(self, number: str) -> list[dict[str, Any]]:
        cached = self._directions_cache.get(number)
        if cached is not None:
            return cached
        with self.db() as con:
            directions = gtfs.route_directions(con, number)
        self._directions_cache[number] = directions
        return directions

    def feed(self) -> dict[str, Any]:
        return {
            "feedVersion": self.meta.get("feed_version"),
            "feedStart": self.meta.get("feed_start_date"),
            "feedEnd": self.meta.get("feed_end_date"),
            "importedAt": self.meta.get("imported_at"),
            "counts": self.meta.get("counts", {}),
            "stops": len(self.stops),
            "routes": len(self.routes),
            "realtime": self.rt.status(),
        }


class Handler(BaseHTTPRequestHandler):
    app: App

    server_version = "ViaLX/0.1"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: Any) -> None:
        # log curto, sem ruído de cada pedido
        if "/vehicleStatuses" in (args[0] if args else "") or self.path.startswith("/health"):
            return
        print(f"  {self.address_string()} {fmt % args}", flush=True)

    # ---------- infra ----------
    def _cors(self) -> None:
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, Authorization")
        self.send_header("Access-Control-Expose-Headers", "X-Feed-Timestamp")

    def send_json(self, value: Any, code: int = 200) -> None:
        body = json.dumps(value, ensure_ascii=False, default=str).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        timestamp = self.app.rt.feed_timestamp or 0
        if timestamp:
            self.send_header("X-Feed-Timestamp", str(timestamp))
        self._cors()
        self.end_headers()
        self.wfile.write(body)

    def send_error_json(self, message: str, code: int = 400) -> None:
        self.send_json({"error": message, "status": code}, code)

    def send_file(self, path: Path) -> None:
        try:
            resolved = path.resolve()
            if not str(resolved).startswith(str(STATIC_DIR.resolve())):
                return self.send_error_json("not found", 404)
            data = resolved.read_bytes()
        except FileNotFoundError:
            return self.send_error_json("not found", 404)
        mime, _ = mimetypes.guess_type(str(resolved))
        self.send_response(200)
        self.send_header("Content-Type", mime or "application/octet-stream")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-cache")
        self._cors()
        self.end_headers()
        self.wfile.write(data)

    def do_OPTIONS(self) -> None:
        self.send_response(204)
        self._cors()
        self.end_headers()

    def do_POST(self) -> None:
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)
        path = urllib.parse.urlparse(self.path).path
        if path.endswith("/authorization/sign"):
            # O GTFS público não usa autenticação; mantém compatibilidade de forma.
            return self.send_json({"token": "public-gtfs", "tokenType": "Bearer", "expires": None})
        return self.send_error_json("not found", 404)

    # ---------- routing ----------
    def do_GET(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        path = urllib.parse.unquote(parsed.path)
        # Aceita /mock/api/v2.10/..., /api/v2.10/..., /mock/... e /api/...
        for prefix in ("/mock/api/v2.10", "/api/v2.10", "/mock/api", "/mock", "/api"):
            if path == prefix or path.startswith(prefix + "/"):
                path = path[len(prefix):] or "/"
                break

        try:
            if path in ("/", "/index.html"):
                return self.send_file(STATIC_DIR / "index.html")
            if path.startswith("/site/"):
                return self.send_file(STATIC_DIR / path[len("/site/"):])
            if path in ("/app.js", "/styles.css", "/favicon.ico"):
                return self.send_file(STATIC_DIR / path.lstrip("/"))
            if path == "/health":
                return self.send_json({"ok": True, "service": "ViaLX", "feed": self.app.feed()})
            if path == "/feed":
                return self.send_json(self.app.feed())
            if path == "/alerts":
                return self.send_json([])
            if re.fullmatch(r"/busstops(/timestamp/[^/]+)?", path):
                return self.send_json(self.app.public_stops())
            if path == "/routes":
                return self.send_json([
                    {
                        "number": r["number"],
                        "name": r["name"],
                        "direction": r["direction"],
                        "color": r.get("color", ""),
                        "routeId": r.get("routeId", ""),
                    }
                    for r in self.app.routes.values()
                ])
            m = re.fullmatch(r"/routes/([^/]+)/shape", path)
            if m:
                direction = (urllib.parse.parse_qs(parsed.query).get("direction") or [""])[0]
                with self.app.db() as con:
                    return self.send_json(gtfs.shape_for_route(con, m.group(1), direction))
            m = re.fullmatch(r"/routes/([^/]+)", path)
            if m:
                route = self.app.routes.get(m.group(1))
                if not route:
                    return self.send_error_json("route not found", 404)
                return self.send_json({
                    "number": route["number"],
                    "name": route["name"],
                    "direction": route["direction"],
                    "directions": self.app.directions_for(route["number"]),
                    "variants": [
                        {"id": v["id"], "headsign": v["headsign"], "direction": v["direction"]}
                        for v in route["variants"]
                    ],
                })
            m = re.fullmatch(r"/Routes/busStop/([^/]+)", path, re.IGNORECASE)
            if m:
                with self.app.db() as con:
                    return self.send_json(gtfs.stop_routes(con, m.group(1), gtfs.now_lisbon().date()))
            m = re.fullmatch(r"/estimations/estimationbusstop/([^/]+)/top/(\d+)", path)
            if m:
                stop = self.app.stops_by_id.get(m.group(1))
                if not stop:
                    return self.send_error_json("stop not found", 404)
                self.app.rt.refresh()
                with self.app.db() as con:
                    items = gtfs.stop_departures(
                        con, stop, gtfs.now_lisbon(), self.app.rt.by_trip, limit=int(m.group(2))
                    )
                return self.send_json(items)
            m = re.fullmatch(r"/stoptimes/routeNumber/([^/]+)/direction/([^/]+)/stop/([^/]+)/date/([^/]+)", path)
            if m:
                try:
                    day = datetime.strptime(m.group(4), "%Y-%m-%d").date()
                except ValueError:
                    day = gtfs.now_lisbon().date()
                with self.app.db() as con:
                    return self.send_json(gtfs.stop_times_for(con, m.group(1), m.group(2), m.group(3), day))
            m = re.fullmatch(r"/vehicleStatuses/routeNumber/([^/]+)", path)
            if m:
                self.app.rt.refresh()
                route = m.group(1)
                return self.send_json([v for v in self.app.rt.vehicles if v["routeNumber"] == route])
            if path == "/vehicleStatuses":
                self.app.rt.refresh()
                return self.send_json(self.app.rt.vehicles)
            m = re.fullmatch(
                r"/planner/startLat/([^/]+)/startLon/([^/]+)/endLat/([^/]+)/endLon/([^/]+)/start/([^/]+)/language/([^/]+)",
                path,
            )
            if m:
                try:
                    slat, slon, elat, elon = map(float, m.group(1, 2, 3, 4))
                except ValueError:
                    return self.send_error_json("invalid coordinates", 400)
                with self.app.db() as con:
                    result = planner.plan(
                        con, self.app.stops, (slat, slon), (elat, elon),
                        m.group(5), gtfs.now_lisbon(), m.group(6),
                    )
                return self.send_json(result)
            m = re.fullmatch(r"/geocoding/suggest/(.+)", path)
            if m:
                return self.send_json(gtfs.suggest_stops(self.app.stops, m.group(1)))
            m = re.fullmatch(r"/geocoding/reverse/lat/([^/]+)/lng/([^/]+)", path)
            if m:
                try:
                    lat, lng = float(m.group(1)), float(m.group(2))
                except ValueError:
                    return self.send_error_json("invalid coordinates", 400)
                nearest = gtfs.nearest_stops(self.app.stops, lat, lng, limit=1, max_m=50000)
                return self.send_json([
                    {"id": s["id"], "name": s["name"], "lat": s["lat"], "lng": s["lng"], "type": "stop"}
                    for s in nearest
                ])
            return self.send_error_json("not found", 404)
        except Exception as exc:
            traceback.print_exc()
            return self.send_error_json(f"{type(exc).__name__}: {exc}", 500)


def load_or_import(args: argparse.Namespace) -> tuple[Path, dict[str, Any]]:
    cache = Path(args.cache).resolve()
    cache.mkdir(parents=True, exist_ok=True)
    db_path = Path(args.db).resolve() if args.db else cache / "carris.db"
    meta: dict[str, Any] = {}
    need_import = args.refresh_gtfs or not db_path.exists()
    if need_import:
        zip_path = gtfs.ensure_gtfs_zip(cache, refresh=args.refresh_gtfs)
        meta = gtfs.import_gtfs(zip_path, db_path, force=args.refresh_gtfs)
    else:
        meta = gtfs.load_meta(db_path)
    return db_path, meta


def main() -> None:
    parser = argparse.ArgumentParser(description="ViaLX — servidor GTFS Carris")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--cache", default=str(Path(__file__).resolve().parent / "cache"))
    parser.add_argument("--db", default="")
    parser.add_argument("--refresh-gtfs", action="store_true", help="descarrega e reimporta o GTFS")
    parser.add_argument("--no-rt", action="store_true", help="não consulta o GTFS-RT")
    parser.add_argument("--rt-interval", type=float, default=5.0, help="segundos entre leituras do feed GTFS-RT")
    args = parser.parse_args()

    db_path, meta = load_or_import(args)
    con = gtfs.connect(db_path)
    stops = gtfs.load_stops(con)
    routes, routes_by_id = gtfs.route_summaries(con)
    con.close()

    rt = RTClient(routes_by_id, enabled=not args.no_rt, min_interval=args.rt_interval)
    rt.refresh(force=True)
    rt.start_background()
    app = App(db_path, stops, routes, routes_by_id, rt, meta)
    Handler.app = app

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print()
    print("ViaLX a correr")
    print(f"  site:  http://{args.host}:{args.port}/")
    print(f"  feed:  http://{args.host}:{args.port}/feed")
    print(f"  api:   http://{args.host}:{args.port}/mock/api/v2.10/busstops")
    print(f"  gtfs:  {meta.get('feed_version')} · {len(stops)} paragens · {len(routes)} carreiras")
    print(f"  rt:    {len(rt.vehicles)} veículos · cada {rt.min_interval:g}s · {rt.status().get('error') or 'ok'}")
    print("  Ctrl+C para parar")
    print()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n  a parar…")
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
